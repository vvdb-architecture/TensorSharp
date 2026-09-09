#!/usr/bin/env python3
"""Current share prices, from a structured endpoint rather than a web page.

    python3 market_movers.py --movers gainers --count 10
    python3 market_movers.py --movers losers --count 5 --json movers.json
    python3 market_movers.py --quote AAPL MSFT NVDA
    python3 market_movers.py --movers actives --count 10 --spec deck_spec.json

Why a script and not a search: the open web answers "top gainers today" with
pages built for a browser. Reading one gives you a number rendered for a human
next to four adverts, and the research skill did exactly that here -- it came
back with a page reading "Oops, something went wrong" and an article about
capital gains tax. This asks a JSON endpoint the same site publishes for its own
front end, so every field is typed and the rows arrive already ranked.

What it will not do is guess. Every row must carry a symbol, a name, a price, a
change, a percentage and a volume, with numbers where numbers belong; a row
missing any of them is dropped rather than printed with a blank, and if fewer
rows survive than were asked for, the count printed is the count found. Nothing
here estimates, back-fills or explains a move.

Output is a markdown table on stdout with the source and the data's own
timestamp, ready to paste into an answer. `--json` writes the same rows as
objects for a follow-up script. `--spec` writes a spec that the documents
skill's make_pptx.py accepts as-is, which is the whole "and put it in a deck"
path in one more command.

Exit codes: 0 rows were printed, 2 bad arguments, 3 the sandbox refused the
request (network switch off, or host not on the allow-list), 4 the endpoint
answered with no usable row, 5 the rows were printed but a file could not be
written.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

SOURCE = "Yahoo Finance"
SCREENER = "https://query1.finance.yahoo.com/v1/finance/screener/predefined/saved"
# One symbol per request. The batch quote endpoint this used to call now answers
# 401 without a session crumb; the chart endpoint carries the same regular-session
# fields in its `meta` and needs no credential, so a quote is N small requests.
CHART = "https://query1.finance.yahoo.com/v8/finance/chart/{symbol}"
MAX_SYMBOLS = 10

# The screener ids the endpoint publishes, under the words a person uses.
MOVERS = {"gainers": "day_gainers", "losers": "day_losers", "actives": "most_actives"}

# Every field a printed row needs, and the ones that must be numbers. An
# incomplete row printed with a gap is worse than a shorter table: it reads as
# a fact.
FIELDS = (
    "symbol", "shortName", "currency", "regularMarketPrice",
    "regularMarketChange", "regularMarketChangePercent", "regularMarketVolume",
)
NUMERIC = (
    "regularMarketPrice", "regularMarketChange",
    "regularMarketChangePercent", "regularMarketVolume",
)

HEADERS = {
    # Identify the app rather than impersonate a browser: the endpoint serves this
    # perfectly well (verified), so there is nothing to gain by pretending.
    "User-Agent": "TensorAgent (+https://github.com/zhongkaifu/TensorSharp)",
    "Accept": "application/json",
}
TIMEOUT = 20
# The host kills a tool call at its own timeout (120 s by default) and --quote makes
# one request per symbol, so ten symbols each stalling for TIMEOUT would be 200 s: the
# call would be killed with NOTHING printed, even though most of the symbols had
# already answered. The run therefore keeps a budget well inside that, spends what is
# left of it as each request's timeout, and prints the rows it did get.
BUDGET = 90.0
_deadline = None


# The endpoint serves at most 100 screener rows per request, and incomplete rows are
# dropped — so the most that can be ASKED for is half of that, leaving 2x headroom.
# Allowing --count 100 would have meant asking for 100 and needing all 100 to be perfect.
MAX_COUNT = 50
REQUEST_CEILING = 100


def start_budget(seconds=BUDGET):
    global _deadline
    _deadline = time.monotonic() + seconds


def budget_left():
    """Seconds still available, or None when no budget was started."""
    return None if _deadline is None else _deadline - time.monotonic()



def fail(code, message):
    print(message, file=sys.stderr)
    raise SystemExit(code)


def fetch(url, fatal=True):
    """The payload, or None when `fatal` is false and this one request failed.

    A per-symbol loop must not die because one symbol is a typo -- a 404 there
    means "no such ticker", which is a row to skip and mention, not the end of
    the run. Losing the network is still fatal either way: every remaining
    request would fail the same way, and saying so once is the useful answer.
    """
    request = urllib.request.Request(url, headers=HEADERS)
    left = budget_left()
    # Past the deadline the remaining requests still go out, but with a second each:
    # a symbol that answers instantly is worth having, and one that stalls is not
    # worth the wall clock the caller no longer has.
    timeout = TIMEOUT if left is None else max(1.0, min(float(TIMEOUT), left))
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if not fatal:
            # The caller prints the status: a 404 means "no such symbol" and a 429 or a
            # 5xx means "ask again later", and reporting both as "no data" told the model
            # a temporary outage was a permanent fact about the ticker.
            return {"__status__": error.code}
        fail(4, f"{SOURCE} answered {error.code} for this request.")
    except PermissionError as error:
        # The app's sandbox, not the network. Its own message already says whether the
        # Network switch is off or the host is not on the allow-list, so pass it through
        # rather than guessing — guessing produced "turn the switch on" for a user whose
        # switch was already on and whose allow-list simply did not include this host.
        # This branch is the DEFAULT configuration: the app ships with networking off,
        # and before it existed that shipped state produced a raw traceback.
        fail(3, f"this app's sandbox refused the request: {error}")
    except urllib.error.URLError as error:
        # A refusal from the audit hook can also arrive wrapped, because urllib turns an
        # OSError raised inside the opener into a URLError.
        reason = error.reason
        if isinstance(reason, PermissionError):
            fail(3, f"this app's sandbox refused the request: {reason}")
        fail(3, f"could not reach {SOURCE}: {reason}")
    except (ValueError, TimeoutError) as error:
        if not fatal:
            return {"__status__": 0}
        fail(4, f"{SOURCE} did not answer with JSON: {error}")
    return {}


def is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def usable(row, equities_only):
    if not isinstance(row, dict):
        return False
    if equities_only and row.get("quoteType") != "EQUITY":
        return False
    if any(row.get(field) is None for field in FIELDS):
        return False
    return all(is_number(row.get(field)) for field in NUMERIC)


def clean(row):
    """Only the fields this prints, never whatever else the endpoint sent."""
    stamp = row.get("regularMarketTime")
    state = row.get("marketState")
    return {
        "symbol": str(row["symbol"]),
        "name": str(row["shortName"]),
        "currency": str(row["currency"]),
        "price": float(row["regularMarketPrice"]),
        "change": float(row["regularMarketChange"]),
        "change_percent": float(row["regularMarketChangePercent"]),
        "volume": int(row["regularMarketVolume"]),
        "as_of": epoch_to_utc(stamp) if is_number(stamp) else "",
        "market_state": str(state) if isinstance(state, str) else "",
    }


def epoch_to_utc(seconds):
    try:
        return datetime.fromtimestamp(float(seconds), timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    except (OverflowError, OSError, ValueError):
        return ""


def rows_from(payload, equities_only, count):
    try:
        quotes = payload["finance"]["result"][0]["quotes"]
    except (KeyError, IndexError, TypeError):
        return []
    if not isinstance(quotes, list):
        return []
    return [clean(row) for row in quotes if usable(row, equities_only)][:count]


def movers(kind, count, equities_only):
    query = urllib.parse.urlencode({
        "formatted": "false",
        "scrIds": MOVERS[kind],
        # Ask for more than is wanted: incomplete rows are dropped, and the
        # endpoint's own ranking is kept for the ones that survive.
        "count": min(REQUEST_CEILING, max(count * 3, 25)),
        "start": 0,
    })
    return rows_from(fetch(f"{SCREENER}?{query}"), equities_only, count)


def quotes(symbols):
    """One row per symbol, from each symbol's own chart meta.

    A symbol that does not resolve is reported and skipped rather than guessed
    at, so a typo cannot come back looking like a price.
    """
    query = urllib.parse.urlencode({"range": "1d", "interval": "1d"})
    rows = []
    skipped = []
    for symbol in symbols:
        left = budget_left()
        if rows and left is not None and left <= 1.0:
            # Something already worked, so the honest thing is to print it and name
            # what was left out, rather than spend the caller's whole timeout.
            skipped.append(symbol)
            continue
        url = CHART.format(symbol=urllib.parse.quote(symbol, safe="")) + "?" + query
        payload = fetch(url, fatal=False)
        status = payload.get("__status__") if isinstance(payload, dict) else None
        if status is not None:
            print(f"note: {SOURCE} answered {status or 'unparseable JSON'} for {symbol}; "
                  + ("no such symbol." if status == 404 else "this is temporary, not a fact about it.")
                  + " It is not in this table.", file=sys.stderr)
            continue
        try:
            meta = payload["chart"]["result"][0]["meta"]
        except (KeyError, IndexError, TypeError):
            print(f"note: {SOURCE} returned no data for {symbol}; it is not in this table.",
                  file=sys.stderr)
            continue
        row = quote_row(symbol, meta)
        if row is None:
            print(f"note: {SOURCE}'s row for {symbol} was missing a required field; "
                  "it is not in this table.", file=sys.stderr)
            continue
        rows.append(row)
    if skipped:
        print(f"note: this run's {int(BUDGET)}s budget ran out before "
              + ", ".join(skipped)
              + "; those are not in the table. Ask for them in a separate run.",
              file=sys.stderr)
    return rows


def quote_row(symbol, meta):
    """The chart meta reshaped into the same row shape the screener produces."""
    if not isinstance(meta, dict):
        return None
    price = meta.get("regularMarketPrice")
    previous = meta.get("chartPreviousClose")
    if previous is None:
        previous = meta.get("previousClose")
    volume = meta.get("regularMarketVolume")
    currency = meta.get("currency")
    if not (is_number(price) and is_number(previous) and is_number(volume)) or previous == 0:
        return None
    # Required here exactly as the screener path requires it, so the printed Currency
    # column can never be blank. A blank unit beside a number reads as dollars.
    if not isinstance(currency, str) or not currency:
        return None
    change = float(price) - float(previous)
    stamp = meta.get("regularMarketTime")
    return {
        "symbol": str(meta.get("symbol") or symbol),
        "name": str(meta.get("longName") or meta.get("shortName") or symbol),
        "currency": currency,
        "price": float(price),
        "change": change,
        "change_percent": change / float(previous) * 100.0,
        "volume": int(volume),
        "as_of": epoch_to_utc(stamp) if is_number(stamp) else "",
        "market_state": str(meta.get("marketState")) if isinstance(meta.get("marketState"), str) else "",
    }


def describe_session(rows):
    """A parenthetical when the market is not open, or "" when it is.

    The default heading says "today", and outside the regular session the price it
    prints is the last close — still true, but "today" implies live. Naming the
    session costs a few words and removes the implication.
    """
    states = {row.get("market_state", "") for row in rows if row.get("market_state")}
    if not states or states == {"REGULAR"}:
        return ""
    if len(states) == 1:
        state = states.pop()
        friendly = {"CLOSED": "market closed", "PRE": "pre-market", "PREPRE": "pre-market",
                    "POST": "after hours", "POSTPOST": "after hours"}.get(state, state.lower())
        return f"last {friendly} price"
    return "mixed session states"


def describe_stamps(rows):
    """How to date a table whose rows need not share a timestamp.

    Printing the FIRST row's stamp as the table's own was wrong twice over: it is
    arbitrary, and across exchanges the rows genuinely differ, so a Tokyo close was
    being presented as the moment a New York row was priced. Equal stamps read as
    one moment; unequal ones say so and name the range.
    """
    stamps = sorted({row["as_of"] for row in rows if row["as_of"]})
    if not stamps:
        return ""
    if len(stamps) == 1:
        return f"as of {stamps[0]}"
    return f"rows stamped between {stamps[0]} and {stamps[-1]}"


def table(rows, ranked):
    head = (["Rank"] if ranked else []) + [
        "Symbol", "Company", "Price", "Currency", "Change", "Change %", "Volume"]
    align = (["---:"] if ranked else []) + ["---", "---", "---:", "---", "---:", "---:", "---:"]
    lines = ["| " + " | ".join(head) + " |", "|" + "|".join(align) + "|"]
    for index, row in enumerate(rows, 1):
        cells = ([str(index)] if ranked else []) + [
            row["symbol"],
            row["name"].replace("|", "/"),
            f"{row['price']:,.2f}",
            # The unit, always. A London row is priced in GBp — pence — so a bare
            # "1,574.80" beside a dollar row is a hundredfold error read as a fact.
            row["currency"],
            f"{row['change']:+,.2f}",
            f"{row['change_percent']:+.2f}%",
            f"{row['volume']:,}",
        ]
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def deck_spec(rows, title, ranked):
    head = (["Rank"] if ranked else []) + [
        "Symbol", "Company", "Price", "Currency", "Change %", "Volume"]
    body = []
    for index, row in enumerate(rows, 1):
        body.append(([str(index)] if ranked else []) + [
            row["symbol"],
            row["name"].replace("|", "/"),
            f"{row['price']:,.2f}",
            row["currency"],
            f"{row['change_percent']:+.2f}%",
            f"{row['volume']:,}",
        ])
    stamped = describe_stamps(rows)
    detail = " · ".join(part for part in (stamped, describe_session(rows)) if part)
    subtitle = f"Source: {SOURCE}" + (f" · {detail}" if detail else "")
    # A table slide holds about 12 rows before it runs off the bottom, which is
    # make_pptx.py's own limit; split anything longer across slides.
    slides = [{"layout": "title", "title": title, "subtitle": subtitle}]
    for start in range(0, len(body), 12):
        slides.append({
            "layout": "table",
            "title": title if start == 0 else f"{title} (continued)",
            "columns": head,
            "rows": body[start:start + 12],
        })
    return {"title": title, "author": "TensorAgent", "slides": slides}


def write_json(path, payload):
    try:
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
    except PermissionError as error:
        fail(5, f"the table above is correct, but {path} could not be written: {error}. "
                "Write inside this turn's working directory.")
    except OSError as error:
        fail(5, f"the table above is correct, but {path} could not be written: {error}")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Current share prices from a structured endpoint.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--movers", choices=sorted(MOVERS),
                        help="the day's biggest gainers, losers or most-traded")
    parser.add_argument("--quote", nargs="+", metavar="SYMBOL",
                        help=f"quote these symbols instead (at most {MAX_SYMBOLS})")
    parser.add_argument("--count", type=int, default=10, help="how many rows (default 10)")
    parser.add_argument("--any-instrument", action="store_true",
                        help="do not restrict --movers to ordinary shares")
    parser.add_argument("--json", metavar="FILE", help="also write the rows as JSON")
    parser.add_argument("--spec", metavar="FILE",
                        help="also write a make_pptx.py spec for these rows")
    parser.add_argument("--title", default=None, help="title for --spec")
    args = parser.parse_args(argv)

    if bool(args.movers) == bool(args.quote):
        parser.error("pass exactly one of --movers or --quote")
    if args.count < 1 or args.count > MAX_COUNT:
        parser.error(f"--count must be between 1 and {MAX_COUNT}")

    start_budget()

    if args.movers:
        rows = movers(args.movers, args.count, not args.any_instrument)
        ranked = True
        default_title = f"Top {len(rows)} {args.movers} today"
    else:
        symbols = [s.strip().upper() for s in args.quote if s.strip()]
        if not symbols:
            parser.error("--quote needs at least one symbol")
        if len(symbols) > MAX_SYMBOLS:
            parser.error(f"--quote takes at most {MAX_SYMBOLS} symbols; it makes one request each")
        rows = quotes(symbols)
        ranked = False
        default_title = "Quotes"

    if not rows:
        fail(4, f"{SOURCE} returned no row carrying every field this prints "
                f"({', '.join(FIELDS)}); nothing was written. Do not fill the gap from memory.")

    title = args.title or default_title
    stamped = describe_stamps(rows)
    note = ", ".join(part for part in (stamped, describe_session(rows)) if part)
    print(f"{title} (Source: {SOURCE}" + (f", {note}" if note else "") + ")")
    print()
    print(table(rows, ranked))

    # The table is already printed, so a write that fails must not bury it under a
    # traceback: the sandbox confines writes to the session workspace, and a path
    # outside it raises PermissionError here and nowhere else.
    if args.json:
        write_json(args.json, {"source": SOURCE, "as_of": stamped, "rows": rows})
        print(f"\nwrote {args.json} ({len(rows)} rows)")
    if args.spec:
        write_json(args.spec, deck_spec(rows, title, ranked))
        print(f"wrote {args.spec} — run documents/scripts/make_pptx.py --spec {args.spec} --out deck.pptx")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
