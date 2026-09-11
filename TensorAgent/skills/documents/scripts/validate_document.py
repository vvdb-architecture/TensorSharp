#!/usr/bin/env python3
"""Check that a document this skill produced is structurally sound.

    validate_document.py report.docx deck.pptx book.xlsx report.pdf
    validate_document.py deck.pptx --json checks.json

Exits non-zero if any file fails, so it can gate a workflow.

For .docx, .pptx and .xlsx it opens the package and checks that it is internally
consistent: every part `[Content_Types].xml` names is present, every part in the
zip has a content type, every relationship resolves to a part that exists, every
XML part parses, and the primary part carries the root element and children its
kind requires. For .pdf it reopens the file with pypdf and reports the page
count and how much text comes back out.

What it does NOT check, and cannot: that Word, PowerPoint or Excel renders the
file. There is no Office application on this device and no headless converter --
LibreOffice, which the published skills shell out to for exactly this, cannot be
bundled and could not be launched if it were, because iOS has no subprocesses.
A PASS here means the package is well formed, not that it has been opened.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

from ooxml import validate_package

OOXML_EXTENSIONS = {".docx": "docx", ".pptx": "pptx", ".xlsx": "xlsx", ".xlsm": "xlsx"}


def check_pdf(path: str) -> dict:
    from pypdf import PdfReader

    report: dict = {"file": os.path.abspath(path), "kind": "pdf", "ok": False, "errors": [], "checks": []}
    try:
        reader = PdfReader(path)
    except Exception as exc:
        report["errors"].append(f"pypdf could not open it: {exc}")
        return report
    pages = len(reader.pages)
    if pages == 0:
        report["errors"].append("the file has no pages")
    else:
        report["checks"].append(f"pypdf opened it and found {pages} page(s)")
    text = "".join(page.extract_text() or "" for page in reader.pages)
    report["pages"] = pages
    report["extractable_characters"] = len(text)
    if pages and not text.strip():
        # Not fatal: a scanned or all-image PDF legitimately has no text layer.
        report["checks"].append("no extractable text; the pages carry no text layer")
    else:
        report["checks"].append(f"{len(text)} character(s) extract back out")
    report["ok"] = not report["errors"]
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description="Structurally validate .docx, .pptx, .xlsx and .pdf files.")
    parser.add_argument("files", nargs="+", help="the documents to check")
    parser.add_argument("--json", dest="json_path", help="write the reports here")
    args = parser.parse_args()

    reports = []
    for path in args.files:
        if not os.path.isfile(path):
            reports.append({"file": path, "ok": False, "errors": [f"{path} does not exist"], "checks": []})
            continue
        extension = os.path.splitext(path)[1].lower()
        if extension == ".pdf":
            reports.append(check_pdf(path))
        elif extension in OOXML_EXTENSIONS:
            reports.append(validate_package(path, OOXML_EXTENSIONS[extension]))
        else:
            reports.append({
                "file": path, "ok": False, "checks": [],
                "errors": [f"{extension or 'a file with no extension'} is not one this validates "
                           "(.docx, .pptx, .xlsx and .pdf are)"],
            })

    for report in reports:
        mark = "PASS" if report["ok"] else "FAIL"
        print(f"[{mark}] {report['file']}")
        for line in report.get("checks", []):
            print(f"       . {line}")
        for line in report.get("errors", []):
            print(f"       ! {line}")

    failed = [r for r in reports if not r["ok"]]
    print(f"\n{len(reports) - len(failed)} of {len(reports)} file(s) are structurally sound.")
    # Only when an OOXML file was actually inspected: printing it after a report
    # on a .png would attach a caveat to a check that never happened.
    if any(r.get("kind") in ("docx", "pptx", "xlsx") for r in reports):
        # Flushed first so the caveat lands after the verdict it qualifies, not
        # ahead of it: the two streams are buffered differently.
        sys.stdout.flush()
        print("Structurally sound is not the same as opens in Office: nothing on this device "
              "can render one of these, so that has not been checked.", file=sys.stderr)

    if args.json_path:
        with open(args.json_path, "w", encoding="utf-8") as handle:
            json.dump(reports, handle, indent=2, default=str)

    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
