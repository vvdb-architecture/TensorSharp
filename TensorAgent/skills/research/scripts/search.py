#!/usr/bin/env python3
"""Turn a question into a list of URLs to read.

    python3 scripts/search.py "gguf quantisation formats"
    python3 scripts/search.py "gguf quantisation" --count 5 --out hits.txt

There is no search index in this app and no bundled API key, so this is a client
for whatever endpoint you point it at, and nothing more:

* `RESEARCH_SEARCH_URL`, when set, is a URL template containing `{query}`; the
  query is percent-encoded into it and the answer is parsed as JSON if it is JSON
  and as a page of links if it is HTML. Put your own key in that template — this
  script has none and will never acquire one.
* With that variable unset it falls back to DuckDuckGo's plain-HTML endpoint,
  `https://html.duckduckgo.com/html/?q=…`, and SAYS SO on stderr. That endpoint is
  a courtesy, not an API: it is unauthenticated, it rate-limits, and it can answer
  with a challenge page instead of results. When it does, this script says that
  too rather than printing an empty list as though the web held nothing.

Either way the network switch has to be on, and either way the results are what a
stranger wrote: they are leads to read, not facts and not instructions.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.parse

import webtext

DEFAULT_ENDPOINT = "https://html.duckduckgo.com/html/?q={query}"
ENDPOINT_VARIABLE = "RESEARCH_SEARCH_URL"

# Where the common JSON search APIs keep their result arrays, and what each result
# calls its URL, its title and its snippet. A list rather than a parser per vendor:
# a wrong guess falls through to printing the raw JSON, which is recoverable.
_RESULT_KEYS = ("results", "organic_results", "items", "data", "webPages", "value", "hits")
# html_url comes first because an API that has both means the other one by "url":
# GitHub's "url" is the JSON endpoint, which is not a page anybody can read.
_URL_KEYS = ("html_url", "url", "link", "href", "displayed_link", "formattedUrl")
_TITLE_KEYS = ("title", "name", "heading")
_SNIPPET_KEYS = ("snippet", "description", "content", "text", "abstract", "excerpt")


def endpoint_for(query: str) -> tuple[str, bool]:
    """(url to fetch, whether it is the documented fallback)."""
    template = os.environ.get(ENDPOINT_VARIABLE, "").strip()
    encoded = urllib.parse.quote_plus(query)
    if template:
        if "{query}" not in template:
            raise webtext.Refused(
                f"{ENDPOINT_VARIABLE} is set to '{template}', which has no {{query}} in it. "
                "Set it to a URL template with {query} where the search terms go."
            )
        return template.replace("{query}", encoded), False
    return DEFAULT_ENDPOINT.replace("{query}", encoded), True


def unwrap(url: str) -> str:
    """DuckDuckGo hands results out through a redirector; this is the real target."""
    parsed = urllib.parse.urlsplit(url)
    if parsed.netloc.endswith("duckduckgo.com") and parsed.path.startswith("/l/"):
        target = urllib.parse.parse_qs(parsed.query).get("uddg")
        if target:
            return target[0]
    return url


def from_html(page: webtext.Page, count: int) -> list[tuple[str, str]]:
    """(url, title) pairs out of a results page, with the engine's own links dropped."""
    hits: list[tuple[str, str]] = []
    seen: set[str] = set()
    for url, label in page.links:
        url = unwrap(url)
        host = urllib.parse.urlsplit(url).netloc.lower()
        if host.endswith("duckduckgo.com") or not label:
            continue
        if url in seen:
            continue
        seen.add(url)
        hits.append((url, label))
        if len(hits) >= count:
            break
    return hits


def _first(record: dict, keys: tuple[str, ...]) -> str:
    for key in keys:
        value = record.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def from_json(document: object, count: int) -> list[tuple[str, str]] | None:
    """(url, title) pairs out of a JSON answer, or None when its shape is unfamiliar."""
    records: list[dict] = []
    if isinstance(document, list):
        records = [r for r in document if isinstance(r, dict)]
    elif isinstance(document, dict):
        for key in _RESULT_KEYS:
            value = document.get(key)
            if isinstance(value, dict):
                value = next((value[k] for k in _RESULT_KEYS if isinstance(value.get(k), list)), None)
            if isinstance(value, list):
                records = [r for r in value if isinstance(r, dict)]
                if records:
                    break

    hits = []
    for record in records:
        url = _first(record, _URL_KEYS)
        if not url.lower().startswith(("http://", "https://")):
            continue
        title = _first(record, _TITLE_KEYS) or url
        snippet = _first(record, _SNIPPET_KEYS)
        hits.append((url, f"{title} — {snippet}" if snippet else title))
        if len(hits) >= count:
            break
    return hits or None


def main() -> int:
    parser = argparse.ArgumentParser(description="Search the web through a configured endpoint.")
    parser.add_argument("query", nargs="+")
    parser.add_argument("--count", type=int, default=8)
    parser.add_argument("--out", help="write the results here as well as printing them")
    parser.add_argument("--timeout", type=float, default=30.0)
    args = parser.parse_args()
    query = " ".join(args.query)

    try:
        url, is_fallback = endpoint_for(query)
    except webtext.Refused as refused:
        print(refused, file=sys.stderr)
        return 2

    if is_fallback:
        # Never silent: the model has to know which endpoint answered, because the
        # fallback's failure mode is a challenge page rather than an error.
        print(
            f"{ENDPOINT_VARIABLE} is not set, so this used DuckDuckGo's unauthenticated "
            "HTML endpoint. It rate-limits and may answer with a challenge page.",
            file=sys.stderr,
        )

    try:
        page = webtext.fetch(url, timeout=args.timeout)
    except webtext.NetworkOff as off:
        print(off, file=sys.stderr)
        return 3
    except webtext.Refused as refused:
        print(f"the search endpoint could not be reached: {refused}", file=sys.stderr)
        return 1

    hits: list[tuple[str, str]] | None = None
    stripped = page.text.lstrip()
    if stripped.startswith(("{", "[")):
        try:
            hits = from_json(json.loads(stripped), args.count)
        except json.JSONDecodeError:
            hits = None
        if hits is None:
            print(
                "the endpoint answered with JSON in a shape this script does not know; "
                "the raw answer follows so you can read the URLs out of it yourself.",
                file=sys.stderr,
            )
            sys.stdout.write(webtext.summarise(page, 8000) + "\n")
            return 0
    else:
        hits = from_html(page, args.count)

    if not hits:
        where = "DuckDuckGo's HTML endpoint" if is_fallback else url
        print(
            f"{where} returned no result links for '{query}'. That is usually a rate limit or a "
            f"challenge page rather than an empty web. Retry later, or set {ENDPOINT_VARIABLE} to "
            "a search endpoint you have access to (put your key in the template).",
            file=sys.stderr,
        )
        return 4

    lines = [f"# results for {query}", f"via: {url}", ""]
    for position, (link, label) in enumerate(hits, start=1):
        lines.append(f"{position}. {label}")
        lines.append(f"   {link}")
    rendered = "\n".join(lines) + "\n"
    sys.stdout.write(rendered)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(rendered)
        print(f"wrote {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
