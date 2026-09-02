#!/usr/bin/env python3
"""Write a PDF report from a JSON spec, using reportlab.

    make_pdf.py --spec spec.json --out report.pdf
    make_pdf.py --spec - --out report.pdf         # spec on stdin

The spec is documented in SKILL.md. On success this prints a JSON report to
stdout, and it reopens the PDF with pypdf before saying it succeeded -- a file
reportlab wrote but pypdf cannot parse is a failure, not a result.

reportlab is bundled and pure Python, so this is the one document format on this
device that is produced by a real library rather than by hand.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4, LETTER, landscape
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (
    Image,
    KeepTogether,
    ListFlowable,
    ListItem,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)

import specs

PAGE_SIZES = {"letter": LETTER, "a4": A4}

ACCENT = colors.HexColor("#1F3864")
HEADER_FILL = colors.HexColor("#DEEAF6")
GRID = colors.HexColor("#9AA0A6")


def styles() -> dict:
    sheet = getSampleStyleSheet()
    return {
        "title": ParagraphStyle("DocTitle", parent=sheet["Title"], fontSize=24, leading=29,
                                textColor=ACCENT, spaceAfter=6),
        "subtitle": ParagraphStyle("DocSubtitle", parent=sheet["Normal"], fontSize=12, leading=16,
                                   textColor=colors.HexColor("#595959"), alignment=TA_CENTER, spaceAfter=18),
        "h1": ParagraphStyle("H1", parent=sheet["Heading1"], fontSize=16, leading=20,
                             textColor=ACCENT, spaceBefore=14, spaceAfter=6),
        "h2": ParagraphStyle("H2", parent=sheet["Heading2"], fontSize=13, leading=17,
                             textColor=ACCENT, spaceBefore=10, spaceAfter=4),
        "h3": ParagraphStyle("H3", parent=sheet["Heading3"], fontSize=11, leading=15,
                             textColor=ACCENT, spaceBefore=8, spaceAfter=3),
        "body": ParagraphStyle("Body", parent=sheet["BodyText"], fontSize=10, leading=14, spaceAfter=8),
        "mono": ParagraphStyle("Mono", parent=sheet["Code"], fontSize=8.5, leading=11,
                               backColor=colors.HexColor("#F5F5F5"), borderPadding=6, spaceAfter=8),
        "caption": ParagraphStyle("Caption", parent=sheet["Normal"], fontSize=8.5, leading=11,
                                  textColor=colors.HexColor("#595959"), alignment=TA_CENTER, spaceAfter=10),
        "cell": ParagraphStyle("Cell", parent=sheet["BodyText"], fontSize=9, leading=12, spaceAfter=0),
        "cellhead": ParagraphStyle("CellHead", parent=sheet["BodyText"], fontSize=9, leading=12,
                                   spaceAfter=0, textColor=ACCENT, fontName="Helvetica-Bold"),
    }


def cell_text(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"
    if isinstance(value, float):
        return f"{value:,.2f}"
    if isinstance(value, int):
        return f"{value:,}"
    # Paragraph() parses its argument as mini-HTML, so anything from the spec has
    # to be escaped or a stray '&' aborts the build.
    return str(value)


def escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def build_table(block: dict, sty: dict, available: float) -> Table:
    columns = list(block.get("columns", []))
    rows = list(block.get("rows", []))
    width = max(len(columns) or (len(rows[0]) if rows else 1), 1)

    data = []
    if columns:
        data.append([Paragraph(escape(cell_text(c)), sty["cellhead"]) for c in columns])
    numeric_columns = set(range(width))
    for row in rows:
        padded = list(row) + [""] * (width - len(row))
        for i, value in enumerate(padded[:width]):
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                numeric_columns.discard(i)
        data.append([Paragraph(escape(cell_text(v)), sty["cell"]) for v in padded[:width]])
    if not data:
        data = [[Paragraph("", sty["cell"])]]

    widths = block.get("column_widths")
    if widths:
        total = float(sum(widths)) or 1.0
        col_widths = [available * (float(w) / total) for w in widths]
    else:
        col_widths = [available / width] * width

    table = Table(data, colWidths=col_widths, repeatRows=1 if columns else 0, hAlign="LEFT")
    commands = [
        ("GRID", (0, 0), (-1, -1), 0.5, GRID),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]
    if columns:
        commands.append(("BACKGROUND", (0, 0), (-1, 0), HEADER_FILL))
    for column in sorted(numeric_columns):
        commands.append(("ALIGN", (column, 1 if columns else 0), (column, -1), "RIGHT"))
    if block.get("zebra", True) and len(data) > 2:
        for row_index in range(1 if columns else 0, len(data)):
            if row_index % 2 == 0:
                commands.append(("BACKGROUND", (0, row_index), (-1, row_index), colors.HexColor("#F7F9FC")))
    table.setStyle(TableStyle(commands))
    return table


def build(spec: dict, out_path: str) -> dict:
    sty = styles()
    page = PAGE_SIZES.get(str(spec.get("page_size", "letter")).lower(), LETTER)
    if spec.get("landscape"):
        page = landscape(page)
    margin = float(spec.get("margin_in", 0.9)) * inch
    available = page[0] - 2 * margin

    doc = SimpleDocTemplate(
        out_path,
        pagesize=page,
        leftMargin=margin, rightMargin=margin, topMargin=margin, bottomMargin=margin,
        title=str(spec.get("title", "")),
        author=str(spec.get("author", "")),
        subject=str(spec.get("subject", "")),
    )

    story: list = []
    written: dict = {}
    if spec.get("title"):
        story.append(Paragraph(escape(str(spec["title"])), sty["title"]))
        written["title"] = 1
    if spec.get("subtitle"):
        story.append(Paragraph(escape(str(spec["subtitle"])), sty["subtitle"]))
        written["subtitle"] = 1

    for block in spec.get("blocks", []):
        kind = str(block.get("type", "")).lower()
        written[kind] = written.get(kind, 0) + 1
        if kind == "heading":
            level = min(max(int(block.get("level", 1)), 1), 3)
            story.append(Paragraph(escape(str(block.get("text", ""))), sty[f"h{level}"]))
        elif kind == "paragraph":
            style = sty["mono"] if block.get("mono") else sty["body"]
            story.append(Paragraph(escape(str(block.get("text", ""))), style))
        elif kind in ("bullets", "numbers"):
            items = [
                ListItem(Paragraph(escape(str(item if not isinstance(item, dict) else item.get("text", ""))),
                                   sty["body"]), leftIndent=18)
                for item in block.get("items", [])
            ]
            story.append(ListFlowable(items, bulletType="bullet" if kind == "bullets" else "1",
                                      start="circle" if kind == "bullets" else 1, leftIndent=18))
            story.append(Spacer(1, 8))
        elif kind == "table":
            story.append(build_table(block, sty, available))
            story.append(Spacer(1, 10))
        elif kind == "image":
            path = block.get("path")
            if not path:
                raise SystemExit("make_pdf: an image block needs a 'path'")
            if not os.path.isfile(path):
                raise SystemExit(f"make_pdf: image {path!r} does not exist")
            from PIL import Image as PilImage

            with PilImage.open(path) as im:
                pixel_w, pixel_h = im.size
            target_w = float(block.get("width_in", 0)) * inch or min(available, pixel_w * inch / 96)
            target_w = min(target_w, available)
            target_h = target_w * (pixel_h / pixel_w) if pixel_w else target_w
            flowables: list = [Image(path, width=target_w, height=target_h, hAlign="CENTER")]
            if block.get("caption"):
                flowables.append(Spacer(1, 4))
                flowables.append(Paragraph(escape(str(block["caption"])), sty["caption"]))
            # An image and its caption belong on the same page or the caption
            # explains a figure the reader has already turned past.
            story.append(KeepTogether(flowables))
        elif kind == "pagebreak":
            story.append(PageBreak())
        elif kind == "spacer":
            story.append(Spacer(1, float(block.get("height", 12))))
        else:
            raise SystemExit(f"make_pdf: unknown block type {kind!r}; see SKILL.md for the list")

    doc.build(story)

    # Reopening with a different library is the only check available here that
    # the bytes are a PDF and not just a file reportlab was willing to write.
    from pypdf import PdfReader

    reader = PdfReader(out_path)
    pages = len(reader.pages)
    extracted = "".join(page.extract_text() or "" for page in reader.pages)

    return {
        "file": os.path.abspath(out_path),
        "bytes": os.path.getsize(out_path),
        "pages": pages,
        "blocks": written,
        "extractable_characters": len(extracted),
        "reopened_by_pypdf": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Write a PDF report from a JSON spec.")
    parser.add_argument("--spec", required=True, help="path to the JSON spec, or - for stdin")
    parser.add_argument("--out", required=True, help="path of the .pdf to write")
    args = parser.parse_args()

    spec = specs.load(args.spec, "make_pdf")
    specs.check_keys(spec, "make_pdf", {"title", "subtitle", "author", "subject", "page_size", "landscape", "margin_in", "blocks"}, "blocks")
    result = build(spec, args.out)
    print(json.dumps(result, indent=2))
    if result["pages"] == 0:
        print("make_pdf: the file has no pages", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
