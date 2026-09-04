#!/usr/bin/env python3
"""Read a dossier's notes.json and say what the sources actually agree on.

    python3 analyze.py notes.json                       # the summary: terms, numbers, dates
    python3 analyze.py notes.json --claim "space debris collisions cascade"
    python3 analyze.py notes.json --terms --top 25
    python3 analyze.py notes.json --numbers
    python3 analyze.py notes.json --timeline
    python3 analyze.py notes.json --claim "..." --out support.md

Collecting five pages is the easy half. The half that decides whether an answer is
worth giving is what they say TOGETHER: which of them support a claim, which
contradict it, which never mention it at all, and whether the numbers everyone
quotes are the same number.

Nothing here decides anything. It finds and counts, and it prints the sentence it
counted along with the source it came from, so the judgement stays with the reader
who can see the evidence. A tool that returned "3 sources agree" and no sentences
would be asking to be believed.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.parse
from collections import Counter

import webtext

# Words that flip the meaning of a sentence they appear in. A sentence containing
# every term of a claim AND one of these is the most interesting sentence in a
# dossier, and reporting it as support would be the worst possible mistake.
NEGATIONS = ("not", "no ", "never", "cannot", "can't", "isn't", "aren't", "wasn't", "doesn't",
             "didn't", "false", "myth", "debunk", "disput", "contrary", "however", "unlikely",
             "overstat", "exaggerat", "incorrect", "wrong", "refut")

NUMBER = re.compile(
    r"(?<![\w.])(\d[\d,]*(?:\.\d+)?)\s*"
    r"(%|percent|per cent|billion|million|thousand|trillion|km|kilometres|kilometers|miles|"
    r"kg|tonnes|tons|GB|MB|TB|GHz|MHz|watts|W|kW|MW|years|months|days|hours|minutes|seconds|"
    r"USD|dollars|EUR|euros|£|\$|€)?",
    re.IGNORECASE)

YEAR = re.compile(r"\b(1[89]\d{2}|20\d{2})\b")


def load(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as handle:
            notes = json.load(handle)
    except FileNotFoundError:
        raise SystemExit(f"analyze: {path} does not exist. research.py writes it beside the dossier.")
    except json.JSONDecodeError as broken:
        raise SystemExit(f"analyze: {path} is not the JSON research.py writes ({broken}).")
    if "records" not in notes:
        raise SystemExit(f"analyze: {path} has no 'records'; it is not a research dossier's notes.")
    return notes


sentences = webtext.sentences


def words_of(text: str) -> list[str]:
    """Content words, lowercased. The same list the search side uses, deliberately."""
    return [w for w in re.findall(r"[a-z][a-z0-9'-]{2,}", (text or "").lower())
            if w not in webtext.STOP]


def host(url: str) -> str:
    return urllib.parse.urlsplit(url).netloc.lower().removeprefix("www.")


def read_records(notes: dict) -> list[dict]:
    return [r for r in notes.get("records", []) if r.get("ok") and r.get("text")]


# =====================================================================================
# what the corpus is about
# =====================================================================================

def terms(records: list[dict], top: int) -> list[str]:
    """The words this corpus is made of, and how many separate sources use each.

    Frequency alone rewards one long page; the source count is what says a word is
    the subject rather than one author's habit, so both are printed and the sort is
    on sources first.
    """
    total = Counter()
    per_source = Counter()
    for record in records:
        seen = set()
        for word in words_of(record["text"]):
            total[word] += 1
            seen.add(word)
        per_source.update(seen)

    ranked = sorted(total, key=lambda w: (-per_source[w], -total[w], w))[:top]
    lines = ["## What the sources are about", "",
             "| term | sources | mentions |", "| --- | --- | --- |"]
    lines += [f"| {word} | {per_source[word]} of {len(records)} | {total[word]} |" for word in ranked]
    return lines + [""]


# =====================================================================================
# does the corpus support a claim
# =====================================================================================

def claim(records: list[dict], statement: str, limit: int) -> list[str]:
    key = [w for w in words_of(statement)]
    if not key:
        raise SystemExit("analyze: --claim needs some content words in it")

    lines = [f"## Sources on: {statement}", "",
             f"Key terms: {', '.join(key)}", ""]
    supporting, qualified, silent = [], [], []

    for record in records:
        best: list[tuple[int, bool, str]] = []
        # A heading appears in the navigation and again in the body of most pages,
        # and quoting it twice makes one source look like two.
        already: set[str] = set()
        for sentence in sentences(record["text"]):
            low = sentence.lower()
            if low in already:
                continue
            already.add(low)
            hits = sum(1 for word in key if word in low)
            if hits < max(2, (len(key) + 1) // 2):
                continue
            negated = any(mark in low for mark in NEGATIONS)
            best.append((hits, negated, sentence))
        best.sort(key=lambda item: (-item[0], item[1]))
        if not best:
            silent.append(record)
            continue
        (qualified if any(n for _, n, _ in best[:2]) else supporting).append((record, best[:limit]))

    def block(title: str, group: list) -> list[str]:
        out = [f"### {title} ({len(group)})", ""]
        if not group:
            out.append("_none_")
            out.append("")
            return out
        for record, found in group:
            out.append(f"**[{record['title']}]({record['url']})** — {host(record['url'])}"
                       + (f" · {record['date']}" if record.get("date") else ""))
            for hits, negated, sentence in found:
                out.append(f"> {'⚠ ' if negated else ''}{sentence}")
            out.append("")
        return out

    lines += block("Say it, plainly", supporting)
    lines += block("Say it with a qualification or a denial nearby", qualified)
    lines += [f"### Do not mention it ({len(silent)})", ""]
    for record in silent:
        lines.append(f"- [{record['title']}]({record['url']})")
    lines += ["",
              "A source that does not mention a claim is not a source that disagrees with it. "
              "Neither is a ⚠ line: it means the sentence carries a negation or a hedge, and it "
              "is there to be read, not counted.", ""]
    return lines


# =====================================================================================
# the numbers, and when things happened
# =====================================================================================

def numbers(records: list[dict], top: int) -> list[str]:
    """Every figure the corpus states, grouped by the figure.

    The point is the grouping. Two sources quoting 36,500 and one quoting 3,650 is
    a typo somewhere, and it is invisible while the numbers are spread across five
    pages of prose.
    """
    found: dict[str, list[tuple[str, str]]] = {}
    for record in records:
        for sentence in sentences(record["text"]):
            for match in NUMBER.finditer(sentence):
                value, unit = match.group(1), (match.group(2) or "").lower()
                if len(value.replace(",", "").replace(".", "")) < 2 and not unit:
                    continue
                # A bare year is a date, and --timeline already has it. Leaving it
                # here fills the table with 1978 and 2009 and buries the quantities.
                if not unit and YEAR.fullmatch(value):
                    continue
                key = f"{value} {unit}".strip()
                entries = found.setdefault(key, [])
                if len(entries) < 3 and all(e[0] != record["url"] for e in entries):
                    entries.append((record["url"], sentence))

    ranked = sorted(found.items(), key=lambda item: (-len(item[1]), item[0]))[:top]
    lines = ["## Figures the sources state", ""]
    if not ranked:
        return lines + ["_none found_", ""]
    for key, entries in ranked:
        lines.append(f"**{key}** — stated by {len(entries)} source(s)")
        for url, sentence in entries:
            lines.append(f"> {sentence[:300]}  \n  — {host(url)}")
        lines.append("")
    return lines


def timeline(records: list[dict], top: int) -> list[str]:
    """Years the corpus mentions, with the sentence each came from."""
    found: dict[str, list[tuple[str, str]]] = {}
    for record in records:
        for sentence in sentences(record["text"]):
            for year in dict.fromkeys(YEAR.findall(sentence)):
                entries = found.setdefault(year, [])
                if len(entries) < 2:
                    entries.append((record["url"], sentence))

    lines = ["## Dates the sources mention", ""]
    if not found:
        return lines + ["_none found_", ""]
    for year in sorted(found)[:top]:
        lines.append(f"**{year}**")
        for url, sentence in found[year]:
            lines.append(f"> {sentence[:250]}  \n  — {host(url)}")
        lines.append("")
    return lines


def coverage(notes: dict, records: list[dict]) -> list[str]:
    total = notes.get("records", [])
    lost = [r for r in total if not r.get("ok")]
    lines = ["## Coverage", "",
             f"- {len(records)} source(s) read, {len(lost)} could not be read",
             f"- {len({host(r['url']) for r in records})} distinct site(s)",
             f"- {sum(r.get('words', 0) for r in records):,} words collected"]
    if lost:
        lines.append("- unread: " + ", ".join(host(r["url"]) for r in lost))
    dated = [r["date"] for r in records if r.get("date")]
    if dated:
        lines.append(f"- publication dates found: {min(dated)} to {max(dated)}")
    else:
        lines.append("- no source states a publication date, so nothing here is known to be current")
    return lines + [""]


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyse the sources a dossier collected.")
    parser.add_argument("notes", help="the notes.json research.py wrote")
    parser.add_argument("--claim", help="a statement to look for support and contradiction of")
    parser.add_argument("--terms", action="store_true", help="what the corpus is about")
    parser.add_argument("--numbers", action="store_true", help="figures, grouped by figure")
    parser.add_argument("--timeline", action="store_true", help="years, with their sentences")
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--quotes", type=int, default=3, help="sentences per source for --claim")
    parser.add_argument("--out", help="write the report here as well as printing it")
    args = parser.parse_args()

    notes = load(args.notes)
    records = read_records(notes)
    if not records:
        print(f"analyze: {args.notes} has no source that was read successfully; "
              "there is nothing to analyse.", file=sys.stderr)
        return 4

    lines = [f"# Analysis of {args.notes}",
             (f"Question: {notes['question']}" if notes.get("question") else ""), ""]
    lines += coverage(notes, records)

    chose = args.claim or args.terms or args.numbers or args.timeline
    if args.claim:
        lines += claim(records, args.claim, args.quotes)
    if args.terms or not chose:
        lines += terms(records, args.top)
    if args.numbers or not chose:
        lines += numbers(records, min(args.top, 10))
    if args.timeline or not chose:
        lines += timeline(records, min(args.top, 8))

    report = "\n".join(line for line in lines if line is not None) + "\n"
    sys.stdout.write(report)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(report)
        print(f"wrote {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
