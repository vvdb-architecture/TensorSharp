#!/usr/bin/env python3
"""Turn a question into sources to read, with no API key and nothing to configure.

    python3 discover.py "why did the Kessler syndrome get its name"
    python3 discover.py "ggml quantisation formats" --sources papers,code --count 8
    python3 discover.py "battery degradation" --site nature.com
    python3 discover.py "rust async runtimes" --json hits.json

The skill this replaces had a search script that was a client for an endpoint the
user was expected to supply, and one unauthenticated fallback that answers a phone
with a challenge page more often than with results. In practice that meant the
model had to be handed URLs, which is precisely what a person asking for research
does not have.

So this asks SEVERAL open, keyless, machine-readable services at once and merges
what they say:

    wikipedia     the MediaWiki search API           encyclopaedic grounding
    duckduckgo    the HTML endpoint, by POST         the open web
    marginalia    an independent crawler, plain HTML  the open web, a different index
    hackernews    the Algolia search API             discussion, and the links in it
    arxiv         the export API (Atom)              preprints
    crossref      the works API                      published papers, with DOIs
    github        the code search API                implementations
    stackexchange the 2.3 search API                 how people actually do it
    news          Google News' RSS search            recent coverage

None of them needs a key. Several will fail on any given run -- rate limits,
challenge pages, an endpoint having a bad day -- and that is designed for rather
than hidden: every provider is asked independently, failures are reported by name,
and a result found by TWO independent indexes is ranked above one found by a
single provider, because agreement between indexes is the only quality signal
available here.

Exit codes: 0 results, 3 the network switch is off, 4 every provider failed or
returned nothing (the message says which, and why).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.parse
from dataclasses import dataclass, field

import webtext

# Groups a caller picks by name, so a question about papers does not spend a
# rate-limited GitHub call and a question about code does not read Crossref.
GROUPS = {
    "auto": ("wikipedia", "duckduckgo", "marginalia", "hackernews"),
    "web": ("duckduckgo", "marginalia"),
    "encyclopedia": ("wikipedia",),
    "papers": ("arxiv", "crossref"),
    "code": ("github", "stackexchange"),
    "forums": ("hackernews", "stackexchange"),
    "news": ("news",),
    "all": ("wikipedia", "duckduckgo", "marginalia", "hackernews", "arxiv", "crossref",
            "github", "stackexchange", "news"),
}

# What a hit from each provider is worth before agreement is counted. Not a claim
# about truth: it is about how often the provider's top result is ABOUT the
# question at all, which for a curated index is far more often than for a
# full-text web search.
WEIGHT = {
    "wikipedia": 1.3,
    "arxiv": 1.2,
    "crossref": 1.2,
    "duckduckgo": 1.0,
    "marginalia": 1.0,
    "stackexchange": 1.0,
    "github": 0.9,
    "hackernews": 0.9,
    "news": 0.9,
}


@dataclass
class Hit:
    url: str
    title: str
    snippet: str = ""
    date: str = ""
    providers: list[str] = field(default_factory=list)
    score: float = 0.0
    relevance: float = 0.0

    def as_dict(self) -> dict:
        return {
            "url": self.url, "title": self.title, "snippet": self.snippet,
            "date": self.date, "providers": self.providers,
            "score": round(self.score, 3), "relevance": round(self.relevance, 3),
        }


class ProviderFailed(Exception):
    """One service could not be used. Never fatal: the others are still asked."""


def _quote(query: str) -> str:
    return urllib.parse.quote_plus(query)


def _clean(text: str, limit: int = 300) -> str:
    """Snippets arrive as HTML fragments from half of these APIs."""
    text = re.sub(r"<[^>]+>", "", text or "")
    text = re.sub(r"&[a-z]+;|&#\d+;", " ", text)
    text = " ".join(text.split())
    return text[:limit]


# =====================================================================================
# providers
# =====================================================================================

def from_wikipedia(query: str, count: int, timeout: float) -> list[Hit]:
    url = ("https://en.wikipedia.org/w/api.php?action=query&list=search&format=json"
           f"&srsearch={_quote(query)}&srlimit={count}&srprop=snippet%7Ctimestamp")
    document = webtext.fetch_json(url, timeout=timeout)
    results = (document or {}).get("query", {}).get("search", [])
    hits = []
    for record in results:
        title = record.get("title", "")
        if not title:
            continue
        hits.append(Hit(
            url="https://en.wikipedia.org/wiki/" + urllib.parse.quote(title.replace(" ", "_")),
            title=title,
            snippet=_clean(record.get("snippet", "")),
            date=(record.get("timestamp") or "")[:10],
        ))
    return hits


def _unwrap_duckduckgo(url: str) -> str:
    """DuckDuckGo hands results out through a redirector; this is the real target."""
    parsed = urllib.parse.urlsplit(url)
    if parsed.netloc.endswith("duckduckgo.com") and parsed.path.startswith("/l/"):
        target = urllib.parse.parse_qs(parsed.query).get("uddg")
        if target:
            return target[0]
    return url


# What a page says when it has decided we are a robot. Detected rather than parsed
# around: the alternative is a run that reports the engine's own navigation as
# results, which is worse than reporting nothing -- the model then reads them.
_CHALLENGE = (
    "javascript is required", "enable javascript", "unusual traffic", "are you a robot",
    "captcha", "verify you are human", "access denied", "rate limit",
)


def _refuse_if_challenged(page: webtext.Page, name: str) -> None:
    head = page.text[:1500].lower()
    for phrase in _CHALLENGE:
        if phrase in head:
            raise ProviderFailed(f"answered with a challenge page ({phrase!r}); {name} is rate-limiting")


def _from_links(page: webtext.Page, count: int, skip_hosts: tuple[str, ...]) -> list[Hit]:
    """Result links out of a search engine's own HTML, with its furniture dropped."""
    hits: list[Hit] = []
    seen: set[str] = set()
    for url, label in page.links:
        url = _unwrap_duckduckgo(url)
        host = urllib.parse.urlsplit(url).netloc.lower()
        if not label or len(label) < 3 or any(host.endswith(skip) for skip in skip_hosts):
            continue
        if url in seen:
            continue
        seen.add(url)
        hits.append(Hit(url=url, title=" ".join(label.split())[:200]))
        if len(hits) >= count:
            break
    return hits


def from_duckduckgo(query: str, count: int, timeout: float) -> list[Hit]:
    # POST, and the lite endpoint as the second try. A GET to the html endpoint is
    # what returns a challenge page; between the two of these, one usually answers.
    attempts = (
        ("https://html.duckduckgo.com/html/", {"q": query}),
        ("https://lite.duckduckgo.com/lite/", {"q": query}),
    )
    last = ""
    for endpoint, form in attempts:
        try:
            page = webtext.fetch(endpoint, timeout=timeout, form=form)
            _refuse_if_challenged(page, "duckduckgo")
        except (webtext.Refused, ProviderFailed) as refused:
            last = str(refused)
            continue
        hits = _from_links(page, count, ("duckduckgo.com",))
        if hits:
            return hits
        last = "answered with no result links (a challenge page or a rate limit)"
    raise ProviderFailed(last or "no answer")


MARGINALIA = "https://old-search.marginalia.nu"


def from_marginalia(query: str, count: int, timeout: float) -> list[Hit]:
    """A second, genuinely independent index of the open web.

    Marginalia crawls what the big engines rank last -- documentation, personal
    sites, papers people put on their own servers -- which is much of what a
    research question is actually about. It is here because one web index is a
    single point of failure: DuckDuckGo rate-limits, and the moment it does, a run
    with only one general provider has nothing to say.

    Two things make it awkward and neither is fatal. It answers the first request
    with a one-second interstitial carrying the real query link, which is followed
    here rather than reported as an empty result; and its results are laid out for
    a reader rather than for a parser, so they are read from the TEXT -- a bare URL
    on its own line, its title on the next -- which is also the shape least likely
    to change under us.

    (Mojeek was the first choice here and had to go: it now answers a plain fetch
    with "JavaScript is required to complete this challenge", and there is no
    browser engine in this app and cannot be one.)
    """
    page = webtext.fetch(f"{MARGINALIA}/search?query={_quote(query)}", timeout=timeout)
    if "refresh on its own" in page.text or "seconds" in page.text[:400]:
        follow = next((url for url, _ in page.links if "sst=" in url), "")
        if not follow:
            raise ProviderFailed("answered with a wait page and no link to follow")
        page = webtext.fetch(follow, timeout=timeout)

    _refuse_if_challenged(page, "marginalia")

    hits: list[Hit] = []
    lines = [line.strip() for line in page.text.splitlines()]
    for index, line in enumerate(lines):
        if " " in line or not line.lower().startswith(("http://", "https://")):
            continue
        title = next((later for later in lines[index + 1:index + 4] if later), "")
        if not title or title.lower().startswith(("http://", "https://")):
            continue
        hits.append(Hit(url=line, title=title[:200]))
        if len(hits) >= count:
            break
    if not hits:
        raise ProviderFailed("answered with no results")
    return hits


def from_hackernews(query: str, count: int, timeout: float) -> list[Hit]:
    url = f"https://hn.algolia.com/api/v1/search?query={_quote(query)}&hitsPerPage={count}"
    document = webtext.fetch_json(url, timeout=timeout)
    hits = []
    for record in (document or {}).get("hits", []):
        link = record.get("url") or (
            f"https://news.ycombinator.com/item?id={record.get('objectID')}" if record.get("objectID") else "")
        title = record.get("title") or record.get("story_title") or ""
        if not link or not title:
            continue
        points = record.get("points") or 0
        comments = record.get("num_comments") or 0
        hits.append(Hit(
            url=link, title=title,
            snippet=f"{points} points, {comments} comments on Hacker News",
            date=(record.get("created_at") or "")[:10],
        ))
    return hits


def from_arxiv(query: str, count: int, timeout: float) -> list[Hit]:
    url = ("http://export.arxiv.org/api/query?search_query=all:"
           f"{_quote(query)}&start=0&max_results={count}&sortBy=relevance")
    page = webtext.fetch(url, timeout=timeout, accept="application/atom+xml")
    # Atom, parsed by hand rather than by an XML library: the document is small,
    # regular, and read only for four fields. defusedxml is staged and would be the
    # right answer for a document with structure; this one has none worth walking.
    hits = []
    body = page.raw or page.text
    for entry in re.findall(r"<entry\b(.*?)</entry>", body, re.S):
        link = re.search(r"<id>\s*([^<]+)</id>", entry)
        title = re.search(r"<title>\s*(.*?)\s*</title>", entry, re.S)
        summary = re.search(r"<summary>\s*(.*?)\s*</summary>", entry, re.S)
        published = re.search(r"<published>\s*([^<]+)</published>", entry)
        if not link or not title:
            continue
        hits.append(Hit(
            url=link.group(1).strip(),
            title=" ".join(title.group(1).split()),
            snippet=_clean(summary.group(1) if summary else ""),
            date=(published.group(1)[:10] if published else ""),
        ))
    if not hits:
        raise ProviderFailed("the export API returned no entries")
    return hits


def from_crossref(query: str, count: int, timeout: float) -> list[Hit]:
    url = (f"https://api.crossref.org/works?query={_quote(query)}&rows={count}"
           "&select=title,URL,abstract,issued,container-title")
    document = webtext.fetch_json(url, timeout=timeout)
    hits = []
    for record in (document or {}).get("message", {}).get("items", []):
        link = record.get("URL")
        titles = record.get("title") or []
        if not link or not titles:
            continue
        parts = (record.get("issued") or {}).get("date-parts") or [[]]
        date = "-".join(f"{p:02d}" if i else str(p) for i, p in enumerate(parts[0])) if parts[0] else ""
        journal = (record.get("container-title") or [""])[0]
        hits.append(Hit(
            url=link, title=" ".join(titles[0].split()),
            snippet=_clean(record.get("abstract", "")) or journal, date=date,
        ))
    return hits


def from_github(query: str, count: int, timeout: float) -> list[Hit]:
    url = f"https://api.github.com/search/repositories?q={_quote(query)}&per_page={count}"
    document = webtext.fetch_json(url, timeout=timeout)
    if isinstance(document, dict) and document.get("message") and not document.get("items"):
        raise ProviderFailed(str(document["message"]))
    hits = []
    for record in (document or {}).get("items", []):
        link = record.get("html_url")
        if not link:
            continue
        hits.append(Hit(
            url=link, title=record.get("full_name") or link,
            snippet=_clean(record.get("description", "")) + f" ({record.get('stargazers_count', 0)} stars)",
            date=(record.get("pushed_at") or "")[:10],
        ))
    return hits


def from_stackexchange(query: str, count: int, timeout: float) -> list[Hit]:
    url = ("https://api.stackexchange.com/2.3/search/advanced?order=desc&sort=relevance"
           f"&q={_quote(query)}&site=stackoverflow&pagesize={count}")
    document = webtext.fetch_json(url, timeout=timeout)
    if isinstance(document, dict) and document.get("error_message"):
        raise ProviderFailed(str(document["error_message"]))
    hits = []
    for record in (document or {}).get("items", []):
        link = record.get("link")
        if not link:
            continue
        hits.append(Hit(
            url=link, title=_clean(record.get("title", "")) or link,
            snippet=f"score {record.get('score', 0)}, "
                    + ("answered" if record.get("is_answered") else "unanswered"),
        ))
    return hits


def from_news(query: str, count: int, timeout: float) -> list[Hit]:
    url = f"https://news.google.com/rss/search?q={_quote(query)}&hl=en-US&gl=US&ceid=US:en"
    page = webtext.fetch(url, timeout=timeout, accept="application/rss+xml, application/xml")
    body = page.raw or page.text
    hits = []
    for item in re.findall(r"<item\b(.*?)</item>", body, re.S)[:count]:
        link = re.search(r"<link>\s*([^<]+)</link>", item)
        title = re.search(r"<title>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</title>", item, re.S)
        date = re.search(r"<pubDate>\s*([^<]+)</pubDate>", item)
        source = re.search(r"<source[^>]*>\s*([^<]+)</source>", item)
        if not link or not title:
            continue
        hits.append(Hit(
            url=link.group(1).strip(),
            title=_clean(title.group(1)),
            snippet=(source.group(1).strip() if source else ""),
            date=_rfc822_date(date.group(1) if date else ""),
        ))
    if not hits:
        raise ProviderFailed("the feed carried no items")
    return hits


def _rfc822_date(value: str) -> str:
    """"Tue, 02 Sep 2026 10:00:00 GMT" as 2026-09-02, or "" when it is not that."""
    months = {m: i for i, m in enumerate(
        "Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec".split(), start=1)}
    found = re.search(r"(\d{1,2})\s+([A-Z][a-z]{2})\s+(\d{4})", value or "")
    if not found:
        return ""
    day, month, year = found.groups()
    return f"{year}-{months.get(month, 1):02d}-{int(day):02d}"


PROVIDERS = {
    "wikipedia": from_wikipedia,
    "duckduckgo": from_duckduckgo,
    "marginalia": from_marginalia,
    "hackernews": from_hackernews,
    "arxiv": from_arxiv,
    "crossref": from_crossref,
    "github": from_github,
    "stackexchange": from_stackexchange,
    "news": from_news,
}


# =====================================================================================
# merging
# =====================================================================================

def canonical(url: str) -> str:
    """One spelling per page, so two indexes naming it agree rather than compete.

    Host, path, and nothing else. The query string is dropped deliberately: the
    same article reached from three indexes arrives with three different tracking
    parameters, and counting those as three pages destroys the one quality signal
    there is here. The cost is that a genuinely query-addressed page (a search
    result, an ?id= permalink) collapses onto its siblings, which is why the FIRST
    spelling seen is the one kept and fetched.
    """
    parsed = urllib.parse.urlsplit(url)
    host = parsed.netloc.lower().removeprefix("www.")
    path = parsed.path.rstrip("/") or "/"
    query = parsed.query
    if query and not re.fullmatch(r"(utm_[a-z]+=[^&]*&?|ref=[^&]*&?|source=[^&]*&?)+", query):
        return f"{host}{path}?{query}"
    return f"{host}{path}"


def merge(found: dict[str, list[Hit]], count: int, terms: list[str] | None = None) -> list[Hit]:
    """One ranked list out of several.

    Two things decide the order and both are needed. AGREEMENT -- being named by
    two independent indexes -- is the only quality signal available without a
    ranker of our own. RELEVANCE -- how much of the question appears in the title
    and snippet -- is what stops a provider's own mistake being promoted by its
    weight: asked "what is the Kessler syndrome and is it happening", MediaWiki's
    first answer is the article on mental disorders, and nothing about agreement
    would ever have caught that, because only one index said it.
    """
    terms = terms or []
    by_page: dict[str, Hit] = {}
    for provider, hits in found.items():
        for position, hit in enumerate(hits):
            key = canonical(hit.url)
            if not key:
                continue
            kept = by_page.get(key)
            if kept is None:
                kept = Hit(url=hit.url, title=hit.title, snippet=hit.snippet, date=hit.date)
                by_page[key] = kept
            else:
                # Keep the longest title and the first snippet and date offered:
                # a search page's anchor text is often a fragment of the real one.
                if len(hit.title) > len(kept.title):
                    kept.title = hit.title
                kept.snippet = kept.snippet or hit.snippet
                kept.date = kept.date or hit.date
            kept.providers.append(provider)
            # Position matters within a provider, and having been named at all
            # matters more: 1/(position+2) falls off fast on purpose.
            kept.score += WEIGHT.get(provider, 1.0) * (1.0 / (position + 2)) + 0.15

    for hit in by_page.values():
        hit.relevance = webtext.overlap(terms, f"{hit.title} {hit.snippet} {hit.url}")
        hit.score += 2.0 * hit.relevance

    # Relevance leads, because a page that is not about the question is not a source
    # however many indexes named it; agreement breaks the ties, which is most of them.
    ranked = sorted(by_page.values(),
                    key=lambda h: (-round(h.relevance, 2), -len(set(h.providers)), -h.score, h.url))
    return ranked[:count]


# Which providers read a sentence as a sentence. The web engines do; a keyword
# index answers "what is the Kessler syndrome and is it happening" with whatever
# article happens to contain the most of those words, which is not the same thing.
NATURAL_LANGUAGE = ("duckduckgo", "marginalia", "news")


def query_for(name: str, question: str, site: str = "") -> str:
    """The query to send one provider: the sentence, or the words it is about."""
    if name in NATURAL_LANGUAGE:
        return question if not site else f"{question} site:{site}"
    words = webtext.content_words(question)
    return " ".join(words) if words else question


def discover(query: str, sources: list[str], count: int, timeout: float,
             site: str = "", on_note=None) -> tuple[list[Hit], dict[str, str]]:
    """Ask every named provider and merge. Returns (hits, why each failure failed)."""
    found: dict[str, list[Hit]] = {}
    failures: dict[str, str] = {}
    terms = webtext.content_words(query)
    # Ask for more per provider than the caller wants: merging discards duplicates,
    # and a provider's fourth result is often another's first.
    per_provider = max(count, 8)

    for name in sources:
        provider = PROVIDERS.get(name)
        if provider is None:
            failures[name] = "no such provider"
            continue
        try:
            hits = provider(query_for(name, query, site), per_provider, timeout)
        except webtext.NetworkOff:
            raise
        except (ProviderFailed, webtext.Refused) as failed:
            failures[name] = str(failed)
            if on_note:
                on_note(f"{name}: {failed}")
            continue
        except Exception as unexpected:  # a provider must never take the run down
            failures[name] = f"{type(unexpected).__name__}: {unexpected}"
            if on_note:
                on_note(f"{name}: {unexpected}")
            continue
        if hits:
            found[name] = hits
            if on_note:
                on_note(f"{name}: {len(hits)} results")
        else:
            failures[name] = "no results"

    return merge(found, count, terms), failures


def resolve_sources(names: str) -> list[str]:
    """"auto", "web,papers" or "marginalia,arxiv" -- groups and providers, mixed."""
    chosen: list[str] = []
    for part in (names or "auto").split(","):
        part = part.strip().lower()
        if not part:
            continue
        if part in GROUPS:
            chosen.extend(GROUPS[part])
        elif part in PROVIDERS:
            chosen.append(part)
        else:
            raise SystemExit(
                f"discover: '{part}' is neither a group ({', '.join(sorted(GROUPS))}) "
                f"nor a provider ({', '.join(sorted(PROVIDERS))})")
    ordered: list[str] = []
    for name in chosen:
        if name not in ordered:
            ordered.append(name)
    return ordered


def render(query: str, hits: list[Hit], failures: dict[str, str], sources: list[str]) -> str:
    lines = [f"# sources for: {query}", f"asked: {', '.join(sources)}", ""]
    for position, hit in enumerate(hits, start=1):
        lines.append(f"{position}. {hit.title}")
        lines.append(f"   {hit.url}")
        found_by = ", ".join(sorted(set(hit.providers)))
        detail = f"   found by {found_by} · {int(hit.relevance * 100)}% of the question"
        if hit.date:
            detail += f" · {hit.date}"
        lines.append(detail)
        if hit.snippet:
            lines.append(f"   {hit.snippet[:200]}")
        lines.append("")
    if failures:
        lines.append("could not be used:")
        for name, why in sorted(failures.items()):
            lines.append(f"  {name}: {why}")
        lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Find sources for a question, with no API key.")
    parser.add_argument("query", nargs="+")
    parser.add_argument("--sources", default="auto",
                        help="groups or providers, comma separated: " + ", ".join(sorted(GROUPS)))
    parser.add_argument("--count", type=int, default=8)
    parser.add_argument("--site", default="", help="restrict the web engines to one domain")
    parser.add_argument("--timeout", type=float, default=20.0,
                        help="seconds for one provider's request. Nine providers times thirty "
                             "seconds is longer than a tool call is allowed to take.")
    parser.add_argument("--json", dest="json_out", help="also write the results here as JSON")
    parser.add_argument("--out", help="also write the printed list here")
    args = parser.parse_args()

    query = " ".join(args.query)
    sources = resolve_sources(args.sources)

    try:
        hits, failures = discover(query, sources, args.count, args.timeout, args.site,
                                  on_note=lambda note: print(note, file=sys.stderr))
    except webtext.NetworkOff as off:
        print(off, file=sys.stderr)
        return 3

    if not hits:
        print(f"no source could be found for '{query}'. Every provider asked either failed or "
              "returned nothing:", file=sys.stderr)
        for name, why in sorted(failures.items()):
            print(f"  {name}: {why}", file=sys.stderr)
        print("Try --sources all, different words, or name a site with --site.", file=sys.stderr)
        return 4

    rendered = render(query, hits, failures, sources)
    sys.stdout.write(rendered)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(rendered)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump({"query": query, "sources": sources,
                       "hits": [h.as_dict() for h in hits],
                       "failures": failures}, handle, indent=2)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
