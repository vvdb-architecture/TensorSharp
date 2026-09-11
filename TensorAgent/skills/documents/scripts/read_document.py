#!/usr/bin/env python3
"""Extract text -- and, from a PDF, tables -- out of a document the user gave you.

    read_document.py report.pdf
    read_document.py report.pdf --tables --json extracted.json
    read_document.py notes.docx
    read_document.py deck.pptx --json deck.json
    read_document.py book.xlsx --sheet Q3

Prints readable text to stdout; --json also writes the structured form, which is
what a follow-up script should consume.

How each format is read, and what that costs:

  .pdf    pypdf, in "layout" extraction mode, which keeps each glyph near its
          column instead of reflowing the page into one paragraph. pdfplumber
          would do this better, but it cannot be bundled: it pulls in
          pdfminer.six, which imports `cryptography` at module scope, and
          `cryptography` is a Rust extension with no pure wheel. --tables is
          therefore a whitespace-column heuristic over the layout text, not a
          ruling-line analysis. It finds the tables a report puts in aligned
          columns and misses the ones drawn with borders and no alignment.
  .docx   the zip's word/document.xml, read with defusedxml.
  .pptx   ppt/slides/slideN.xml, in slide order, read with defusedxml.
  .xlsx   openpyxl with data_only=True.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import zipfile

from defusedxml.ElementTree import fromstring as safe_fromstring

from ooxml import NS

W = f'{{{NS["w"]}}}'
A = f'{{{NS["a"]}}}'
P = f'{{{NS["p"]}}}'


def read_pdf(path: str, want_tables: bool) -> dict:
    from pypdf import PdfReader

    reader = PdfReader(path)
    if reader.is_encrypted:
        # An empty-password decrypt is the common case for "protected" PDFs.
        try:
            if not reader.decrypt(""):
                raise SystemExit(f"read_document: {path} is encrypted and needs a password")
        except NotImplementedError as exc:
            raise SystemExit(f"read_document: {path} uses an encryption pypdf cannot open here ({exc}); "
                             "the library that could, pdfminer.six, needs cryptography and is not bundled")

    pages = []
    for number, page in enumerate(reader.pages, start=1):
        try:
            layout = page.extract_text(extraction_mode="layout") or ""
        except Exception as exc:  # a damaged content stream should not lose the other pages
            layout = ""
            print(f"read_document: page {number} did not extract ({exc})", file=sys.stderr)
        plain = page.extract_text() or ""
        entry = {"page": number, "text": plain, "layout": layout, "characters": len(plain)}
        if want_tables:
            entry["tables"] = detect_tables(layout)
        pages.append(entry)

    return {
        "file": os.path.abspath(path),
        "kind": "pdf",
        "pages": len(pages),
        "metadata": {k.lstrip("/"): str(v) for k, v in (reader.metadata or {}).items()},
        "content": pages,
    }


def detect_tables(layout: str) -> list:
    """Find aligned columns in layout-mode text.

    The rule: a run of lines whose fields START at the same character positions
    is a table. Field starts, not gap positions -- in a real table "Region" and
    "West" end in different places but begin in the same one. Two lines is not
    evidence, so three is the minimum, and a single blank line does not end a
    run because a rendered table usually puts one between its rows. This is a
    heuristic and it is named as one wherever its output is used.
    """
    lines = [line.rstrip() for line in layout.splitlines()]
    tables: list = []
    block: list = []

    def gaps(line: str) -> frozenset:
        return frozenset(m.start(1) for m in re.finditer(r"(?:^\s*|\s{2,})(\S)", line))

    def flush() -> None:
        if len(block) >= 3:
            rows = [re.split(r"\s{2,}", line.strip()) for line in block]
            width = max(len(r) for r in rows)
            if width >= 2:
                tables.append({
                    "rows": [r + [""] * (width - len(r)) for r in rows],
                    "columns": width,
                    "heuristic": "aligned whitespace columns in pypdf layout-mode text",
                })
        block.clear()

    previous: frozenset = frozenset()
    blanks = 0
    for line in lines:
        if not line.strip():
            blanks += 1
            if blanks > 1:
                flush()
                previous = frozenset()
            continue
        blanks = 0
        current = gaps(line)
        if len(current) < 2:
            flush()
            previous = frozenset()
            continue
        # Column starts drift by a character or two between rows, so ask for a
        # majority overlap rather than an exact match.
        overlap = len(current & previous) if previous else 0
        if previous and overlap >= max(2, min(len(current), len(previous)) // 2):
            block.append(line)
        else:
            flush()
            block.append(line)
        previous = current
    flush()
    return tables


def _paragraph_text(node) -> str:
    return "".join(t.text or "" for t in node.iter(f"{W}t"))


def read_docx(path: str) -> dict:
    with zipfile.ZipFile(path) as zf:
        if "word/document.xml" not in zf.namelist():
            raise SystemExit(f"read_document: {path} has no word/document.xml; it is not a .docx")
        root = safe_fromstring(zf.read("word/document.xml"))
    body = root.find(f"{W}body")
    blocks: list = []
    if body is not None:
        for node in body:
            if node.tag == f"{W}p":
                style = node.find(f"{W}pPr/{W}pStyle")
                text = _paragraph_text(node)
                if not text.strip():
                    continue
                name = style.get(f"{W}val") if style is not None else None
                blocks.append({"type": "heading" if (name or "").startswith("Heading") else "paragraph",
                               "style": name, "text": text})
            elif node.tag == f"{W}tbl":
                rows = []
                for tr in node.findall(f"{W}tr"):
                    rows.append([_paragraph_text(tc) for tc in tr.findall(f"{W}tc")])
                blocks.append({"type": "table", "rows": rows})
    return {"file": os.path.abspath(path), "kind": "docx", "blocks": blocks,
            "paragraphs": sum(1 for b in blocks if b["type"] != "table"),
            "tables": sum(1 for b in blocks if b["type"] == "table")}


def read_pptx(path: str) -> dict:
    with zipfile.ZipFile(path) as zf:
        names = sorted(
            (n for n in zf.namelist() if re.fullmatch(r"ppt/slides/slide\d+\.xml", n)),
            key=lambda n: int(re.findall(r"\d+", n)[-1]),
        )
        if not names:
            raise SystemExit(f"read_document: {path} has no ppt/slides/slideN.xml; it is not a .pptx")
        slides = []
        for number, name in enumerate(names, start=1):
            root = safe_fromstring(zf.read(name))
            texts = []
            for para in root.iter(f"{A}p"):
                line = "".join(t.text or "" for t in para.iter(f"{A}t"))
                if line.strip():
                    texts.append(line)
            slides.append({"slide": number, "part": name, "text": texts})
    return {"file": os.path.abspath(path), "kind": "pptx", "slides": len(slides), "content": slides}


def read_xlsx(path: str, sheet_name: "str | None") -> dict:
    from openpyxl import load_workbook

    workbook = load_workbook(path, data_only=True, read_only=True)
    try:
        names = [sheet_name] if sheet_name else workbook.sheetnames
        sheets = []
        for name in names:
            if name not in workbook.sheetnames:
                raise SystemExit(f"read_document: {path} has no sheet named {name!r}; "
                                 f"it has {', '.join(workbook.sheetnames)}")
            sheet = workbook[name]
            rows = [list(row) for row in sheet.iter_rows(values_only=True)]
            sheets.append({"name": name, "rows": rows, "row_count": len(rows)})
    finally:
        workbook.close()
    return {"file": os.path.abspath(path), "kind": "xlsx", "sheets": sheets}


def render(result: dict) -> str:
    kind = result["kind"]
    if kind == "pdf":
        out = []
        for page in result["content"]:
            out.append(f"--- page {page['page']} ---")
            out.append(page["layout"] or page["text"])
            for index, table in enumerate(page.get("tables", []), start=1):
                out.append(f"[table {index}: {len(table['rows'])} rows x {table['columns']} columns "
                           f"({table['heuristic']})]")
        return "\n".join(out)
    if kind == "docx":
        out = []
        for block in result["blocks"]:
            if block["type"] == "table":
                for row in block["rows"]:
                    out.append(" | ".join(row))
            elif block["type"] == "heading":
                out.append(f"# {block['text']}")
            else:
                out.append(block["text"])
        return "\n".join(out)
    if kind == "pptx":
        out = []
        for slide in result["content"]:
            out.append(f"--- slide {slide['slide']} ---")
            out.extend(slide["text"])
        return "\n".join(out)
    out = []
    for sheet in result["sheets"]:
        out.append(f"--- sheet {sheet['name']} ({sheet['row_count']} rows) ---")
        for row in sheet["rows"]:
            out.append(" | ".join("" if v is None else str(v) for v in row))
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract text and tables from a PDF, DOCX, PPTX or XLSX.")
    parser.add_argument("input", help="the document to read")
    parser.add_argument("--tables", action="store_true", help="also detect tables (PDF only)")
    parser.add_argument("--sheet", help="a single sheet, for an .xlsx")
    parser.add_argument("--json", dest="json_path", help="write the structured extraction here")
    args = parser.parse_args()

    if not os.path.isfile(args.input):
        raise SystemExit(f"read_document: {args.input} does not exist")
    extension = os.path.splitext(args.input)[1].lower()
    if extension == ".pdf":
        result = read_pdf(args.input, args.tables)
    elif extension == ".docx":
        result = read_docx(args.input)
    elif extension == ".pptx":
        result = read_pptx(args.input)
    elif extension in (".xlsx", ".xlsm"):
        result = read_xlsx(args.input, args.sheet)
    else:
        raise SystemExit(f"read_document: {args.input} has an extension this does not read "
                         "(.pdf, .docx, .pptx and .xlsx are the ones it does)")

    if args.json_path:
        with open(args.json_path, "w", encoding="utf-8") as handle:
            json.dump(result, handle, indent=2, default=str)
        print(f"wrote {os.path.abspath(args.json_path)}", file=sys.stderr)
    print(render(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
