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
import re
import urllib.error
import urllib.parse
import urllib.request
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

    @property
    def words(self) -> int:
        return len(self.text.split())


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

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag in _SILENT:
            self._silent += 1
            return
        if tag == "title":
            self._in_title = True
        if tag in _BREAKS:
            self._chunks.append("\n")
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
    reader = _Reader(url)
    try:
        reader.feed(body)
        reader.close()
    except AssertionError:
        # html.parser asserts rather than raises on a few malformed documents.
        # Whatever it collected before giving up is still worth returning.
        pass
    return reader.result()


def fetch(url: str, timeout: float = 30.0) -> Page:
    """Fetch one URL and reduce it to readable text.

    Raises `NetworkOff` only when the user's switch is actually off, and `Refused`
    for everything else -- including a host the session may not reach, which looks
    identical to the caller and needs the opposite advice.
    """
    if not url.lower().startswith(("http://", "https://")):
        raise Refused(f"{url}: only http and https URLs can be fetched")

    request = urllib.request.Request(url, headers={
        "User-Agent": USER_AGENT,
        "Accept": "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5",
        "Accept-Encoding": "gzip, identity",
        "Accept-Language": "en",
    })
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            status = getattr(response, "status", 200) or 200
            raw = response.read(MAX_BYTES + 1)
            if response.headers.get("Content-Encoding", "").lower() == "gzip":
                raw = gzip.decompress(raw)
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
    if content_type.startswith("text/html") or content_type.endswith("+xml") or "<html" in body[:2048].lower():
        title, text, links = extract(body, final)
    else:
        # Plain text, JSON, CSV: already readable, and running it through an HTML
        # parser would eat every < it contains.
        title, text, links = "", body.strip(), []

    if truncated:
        text += f"\n\n[truncated: only the first {MAX_BYTES} bytes of this page were read]"
    return Page(url=final, status=status, title=title, text=text, links=links)


def summarise(page: Page, limit: int = 4000) -> str:
    """The first `limit` characters of a page's text, cut at a line boundary."""
    if len(page.text) <= limit:
        return page.text
    cut = page.text.rfind("\n", 0, limit)
    return page.text[: cut if cut > limit // 2 else limit] + "\n[…]"


def same_site(a: str, b: str) -> bool:
    """True when two URLs share a registrable-looking host, for bounding a crawl."""
    ha = urllib.parse.urlsplit(a).netloc.lower().removeprefix("www.")
    hb = urllib.parse.urlsplit(b).netloc.lower().removeprefix("www.")
    return ha == hb
