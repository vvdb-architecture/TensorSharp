#!/usr/bin/env python3
"""Fetching and reading web pages with nothing but the standard library.

This module exists because of what the app it runs in does not have. There is no
`requests`, no `beautifulsoup4` and no way to install one that needs a compiler:
the interpreter is staged into an iOS app bundle at build time. So the fetching is
`urllib` and the parsing is `html.parser`, and both are enough.

Two rules shape everything here:

* **The network is the user's switch.** Every socket the interpreter opens goes
  through an audit hook, and with the switch off that hook raises `PermissionError`
  before a connection is attempted. `fetch` turns that into `Refused`, whose message
  names the switch and what to do about it, rather than into a traceback that reads
  like a bug.
* **A fetched page is data, never instruction.** `extract` returns text and links.
  It is deliberately incapable of executing anything, and the caller must treat what
  comes back as something a stranger wrote.
"""

from __future__ import annotations

import gzip
import html
import json
import re
import urllib.error
import urllib.parse
import urllib.request
import zlib
from dataclasses import dataclass, field
from html.parser import HTMLParser

# Named so an operator reading a server log can tell what this is and stop it.
USER_AGENT = "TensorAgent-research/1.0 (on-device agent; +https://github.com/zhongkaifu/TensorSharp)"

# Big enough for an article, small enough that a 200 MB download cannot fill a
# phone's cache while the model waits for it.
MAX_BYTES = 4 * 1024 * 1024

# Tags whose CONTENT is not page text. <script> and <style> are the ones that
# matter: their bodies are code, and a naive extractor emits them as prose.
_SILENT = {"script", "style", "noscript", "template", "svg", "canvas", "head"}

# Tags that end a line of text, so that a stripped page reads as paragraphs rather
# than as one run-on sentence.
_BREAKS = {
    "p", "div", "br", "li", "tr", "section", "article", "header", "footer", "nav",
    "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table", "ul", "ol", "dl", "dd", "dt",
}


# The exact sentence ExecutionPolicy.HostNotAllowedSuffix puts in the refusal when a
# host is off the session's allow-list. Matched as a substring rather than reproduced,
# so the two stay in step: this file only has to recognise it, never phrase it.
HOST_NOT_ALLOWED = "is not in this session's allowed hosts"


class NetworkOff(Exception):
    """The user's network switch is off. Distinct from a fetch that failed."""


class Refused(Exception):
    """The fetch was refused or failed, with a sentence naming why."""


@dataclass
class Page:
    """One fetched page, already reduced to what a reader would see."""

    url: str
    status: int
    title: str
    text: str
    links: list[tuple[str, str]] = field(default_factory=list)
    """(absolute url, anchor text) in document order, duplicates removed."""
    meta: dict[str, str] = field(default_factory=dict)
    """<meta> names and properties, lowercased. Where a publication date lives."""
    raw: str = ""
    """The document as fetched, for the table reader. Empty for non-HTML."""

    @property
    def words(self) -> int:
        return len(self.text.split())

    @property
    def published(self) -> str:
        """The page's own publication date, or "" when it does not state one.

        Recency decides whether half of what a research answer says is worth
        repeating, and almost nothing else on a page tells you. This reads the
        handful of tags that actually carry it -- Open Graph, schema.org, the
        Dublin Core ones -- and never guesses from the text, because a wrong date
        stated confidently is worse than no date.
        """
        for key in ("article:published_time", "article:modified_time", "datepublished",
                    "date", "dc.date", "dc.date.issued", "og:updated_time",
                    "citation_publication_date", "pubdate", "sailthru.date"):
            value = self.meta.get(key, "").strip()
            if value:
                found = re.search(r"\d{4}-\d{2}(-\d{2})?", value) or re.search(r"\b(19|20)\d{2}\b", value)
                if found:
                    return found.group(0)
        return ""


class _Reader(HTMLParser):
    """Collects the title, the visible text and the links of one document."""

    def __init__(self, base: str) -> None:
        super().__init__(convert_charrefs=True)
        self.base = base
        self.title = ""
        self._chunks: list[str] = []
        self._silent = 0
        self._in_title = False
        self._anchor: list[str] | None = None
        self._href: str | None = None
        self._links: list[tuple[str, str]] = []
        self._seen: set[str] = set()
        self.meta: dict[str, str] = {}

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag in _SILENT:
            self._silent += 1
            return
        if tag == "title":
            self._in_title = True
        if tag in _BREAKS:
            self._chunks.append("\n")
        if tag == "meta":
            pairs = {k.lower(): (v or "") for k, v in attrs}
            name = (pairs.get("property") or pairs.get("name") or pairs.get("itemprop") or "").lower()
            content = pairs.get("content", "")
            if name and content and name not in self.meta:
                self.meta[name] = content
        if tag == "time":
            when = dict(attrs).get("datetime")
            if when and "datepublished" not in self.meta:
                self.meta["datepublished"] = when
        if tag == "a":
            href = dict(attrs).get("href")
            self._href = href
            self._anchor = []

    def handle_endtag(self, tag: str) -> None:
        if tag in _SILENT:
            self._silent = max(0, self._silent - 1)
            return
        if tag == "title":
            self._in_title = False
        if tag in _BREAKS:
            self._chunks.append("\n")
        if tag == "a":
            self._close_anchor()

    def handle_data(self, data: str) -> None:
        # The title is read before the silence test, and has to be: <title> lives
        # inside <head>, which is silent precisely so that its meta and link tags do
        # not become prose. Testing silence first is how a page loses its title.
        if self._in_title:
            self.title += data
        if self._silent:
            return
        self._chunks.append(data)
        if self._anchor is not None:
            self._anchor.append(data)

    def _close_anchor(self) -> None:
        text = " ".join("".join(self._anchor or []).split())
        href, self._href, self._anchor = self._href, None, None
        if not href:
            return
        url = absolute(self.base, href)
        # Fragments and javascript: hrefs go nowhere a fetch could follow.
        if not url or url in self._seen:
            return
        self._seen.add(url)
        self._links.append((url, text))

    def result(self) -> tuple[str, str, list[tuple[str, str]]]:
        # Anchors are closed here too: an unbalanced </a> is ordinary on the web.
        self._close_anchor()
        text = "".join(self._chunks)
        text = re.sub(r"[ \t\r\f\v]+", " ", text)
        text = re.sub(r" ?\n ?", "\n", text)
        text = re.sub(r"\n{3,}", "\n\n", text)
        return " ".join(self.title.split()), text.strip(), self._links


def absolute(base: str, href: str) -> str:
    """An absolute http(s) URL for `href` as written on `base`, or "" for one nothing can fetch."""
    href = html.unescape(href.strip())
    if not href or href.startswith("#"):
        return ""
    try:
        url = urllib.parse.urljoin(base, href)
    except ValueError:
        return ""
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        return ""
    return urllib.parse.urlunsplit(parsed._replace(fragment=""))


def extract(body: str, url: str) -> tuple[str, str, list[tuple[str, str]]]:
    """(title, readable text, links) for one HTML document."""
    title, text, links, _ = extract_all(body, url)
    return title, text, links


def extract_all(body: str, url: str) -> tuple[str, str, list[tuple[str, str]], dict[str, str]]:
    """(title, readable text, links, meta) for one HTML document."""
    reader = _Reader(url)
    try:
        reader.feed(body)
        reader.close()
    except AssertionError:
        # html.parser asserts rather than raises on a few malformed documents.
        # Whatever it collected before giving up is still worth returning.
        pass
    title, text, links = reader.result()
    return title, text, links, reader.meta


def fetch(url: str, timeout: float = 30.0, form: dict | None = None, accept: str | None = None) -> Page:
    """Fetch one URL and reduce it to readable text.

    `form`, when given, makes this a POST of those fields as a normal HTML form.
    That is not a convenience: DuckDuckGo's HTML endpoint answers a GET with a
    challenge page far more often than it answers a POST, and a search that only
    works from a laptop is the difference between this skill working and not.

    Raises `NetworkOff` only when the user's switch is actually off, and `Refused`
    for everything else -- including a host the session may not reach, which looks
    identical to the caller and needs the opposite advice.
    """
    if not url.lower().startswith(("http://", "https://")):
        raise Refused(f"{url}: only http and https URLs can be fetched")

    body_bytes = urllib.parse.urlencode(form).encode("utf-8") if form else None
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": accept or "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5",
        "Accept-Encoding": "gzip, deflate, identity",
        "Accept-Language": "en",
    }
    if body_bytes is not None:
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    request = urllib.request.Request(url, data=body_bytes, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status = getattr(response, "status", 200) or 200
            raw = response.read(MAX_BYTES + 1)
            encoding = response.headers.get("Content-Encoding", "").lower()
            if encoding == "gzip":
                raw = gzip.decompress(raw)
            elif encoding == "deflate":
                # Stack Exchange's API is gzip-only; other JSON endpoints send
                # deflate, and a body nothing decompresses is a parse error whose
                # message says nothing about compression.
                try:
                    raw = zlib.decompress(raw)
                except zlib.error:
                    raw = zlib.decompress(raw, -zlib.MAX_WBITS)
            charset = response.headers.get_content_charset() or "utf-8"
            content_type = (response.headers.get_content_type() or "").lower()
            final = response.geturl()
    except PermissionError as denied:
        # The audit hook, not the network -- but it refuses for two different reasons
        # and they need different advice. Telling someone to turn on a switch that is
        # already on, because their host was not on the session's allow-list, sends the
        # model round a loop it cannot get out of.
        detail = str(denied)
        if HOST_NOT_ALLOWED in detail:
            raise Refused(
                f"{url} was not fetched: {detail}. The network is on; this host is not "
                "one this session may reach, and no setting on this screen changes that."
            ) from denied
        raise NetworkOff(
            f"{url} was not fetched: {detail}. Turn Network on in the app's Settings "
            "and run this again; nothing here can work around it."
        ) from denied
    except urllib.error.HTTPError as failed:
        raise Refused(f"{url}: the server answered {failed.code} {failed.reason}") from failed
    except urllib.error.URLError as failed:
        raise Refused(f"{url}: {failed.reason}") from failed
    except (OSError, ValueError) as failed:
        raise Refused(f"{url}: {failed}") from failed

    truncated = len(raw) > MAX_BYTES
    body = raw[:MAX_BYTES].decode(charset, errors="replace")
    meta: dict[str, str] = {}
    markup = ""
    if content_type.startswith("text/html") or content_type.endswith("+xml") or "<html" in body[:2048].lower():
        title, text, links, meta = extract_all(body, final)
        markup = body
    else:
        # Plain text, JSON, CSV: already readable, and running it through an HTML
        # parser would eat every < it contains.
        title, text, links = "", body.strip(), []

    if truncated:
        text += f"\n\n[truncated: only the first {MAX_BYTES} bytes of this page were read]"
    return Page(url=final, status=status, title=title, text=text, links=links, meta=meta, raw=markup)


def fetch_json(url: str, timeout: float = 30.0, form: dict | None = None) -> object:
    """A JSON API's answer, parsed.

    Separate from `fetch` because the failure is different: an endpoint that
    answers with a challenge page, a rate-limit notice or an HTML error returns
    perfectly good text that is not the document the caller asked for, and
    `json.loads` says only "Expecting value". This says which URL, and shows the
    beginning of what actually came back.
    """
    page = fetch(url, timeout=timeout, form=form, accept="application/json, text/plain;q=0.8, */*;q=0.5")
    body = page.text.strip()
    try:
        return json.loads(body)
    except json.JSONDecodeError as broken:
        preview = " ".join(body[:200].split())
        raise Refused(f"{url} did not answer with JSON ({broken}); it began: {preview!r}") from broken


class _Tables(HTMLParser):
    """Every <table> in a document, as rows of cell text."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.tables: list[list[list[str]]] = []
        self._table: list[list[str]] | None = None
        self._row: list[str] | None = None
        self._cell: list[str] | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag == "table":
            self._table = []
        elif tag == "tr" and self._table is not None:
            self._row = []
        elif tag in ("td", "th") and self._row is not None:
            self._cell = []

    def handle_endtag(self, tag: str) -> None:
        if tag in ("td", "th") and self._cell is not None and self._row is not None:
            self._row.append(" ".join("".join(self._cell).split()))
            self._cell = None
        elif tag == "tr" and self._row is not None and self._table is not None:
            if self._row:
                self._table.append(self._row)
            self._row = None
        elif tag == "table" and self._table is not None:
            if self._table:
                self.tables.append(self._table)
            self._table = None

    def handle_data(self, data: str) -> None:
        if self._cell is not None:
            self._cell.append(data)


def tables(markup: str) -> list[list[list[str]]]:
    """The tables in one HTML document, biggest first.

    A research answer is often a number in a table on a page, and a table read as
    prose is a run of words with the columns gone. Biggest first because the one a
    reader means is almost never the two-cell layout table at the top.
    """
    reader = _Tables()
    try:
        reader.feed(markup)
        reader.close()
    except AssertionError:
        pass
    return sorted(reader.tables, key=lambda t: sum(len(r) for r in t), reverse=True)


def summarise(page: Page, limit: int = 4000) -> str:
    """The first `limit` characters of a page's text, cut at a line boundary."""
    if len(page.text) <= limit:
        return page.text
    cut = page.text.rfind("\n", 0, limit)
    return page.text[: cut if cut > limit // 2 else limit] + "\n[…]"


# =====================================================================================
# reading text as words, shared by everything downstream
# =====================================================================================

# The words that carry no subject. One list, in the library, because three copies of
# it in three scripts drift and then a term is a stop word in the search and a
# keyword in the analysis of the same run.
STOP = frozenset("""a an the and or but if then than that this these those of in on at to for from by
with without about into over under again further is are was were be been being have has had do does
did not no nor so such can could may might will would shall should must it its it's as we you your
they them he she her his i what which who whom when where why how all any both each few more most
other some only own same too very just also there their our us one two new like get make use using
used may per via out up down off between while during after before because does doing done
page pages site home search menu click here read more privacy cookie cookies terms contact""".split())


def content_words(text: str) -> list[str]:
    """The words of `text` that are about something, in order, deduplicated."""
    words = re.findall(r"[A-Za-z][A-Za-z0-9_.+-]{1,}", (text or "").lower())
    return [w for w in dict.fromkeys(words) if w not in STOP and len(w) > 2]


def sentences(text: str) -> list[str]:
    """A rough sentence split. Rough is the right amount: this feeds a search, not a parser."""
    parts = re.split(r"(?<=[.!?])\s+|\n{2,}", text or "")
    return [" ".join(p.split()) for p in parts if len(p.strip()) > 30]


def overlap(question_terms: list[str], text: str) -> float:
    """The fraction of a question's words that appear in `text`.

    Used to rank a search hit by whether it is ABOUT the question, which no index
    here reports and several get badly wrong: ask MediaWiki "what is the Kessler
    syndrome and is it happening" and its first answer is the article on mental
    disorders, because the query is a sentence and its index is not.
    """
    if not question_terms:
        return 0.0
    low = (text or "").lower()
    return sum(1 for term in question_terms if term in low) / len(question_terms)


def same_site(a: str, b: str) -> bool:
    """True when two URLs share a registrable-looking host, for bounding a crawl."""
    ha = urllib.parse.urlsplit(a).netloc.lower().removeprefix("www.")
    hb = urllib.parse.urlsplit(b).netloc.lower().removeprefix("www.")
    return ha == hb
