#!/usr/bin/env python3
"""Write a .docx from a JSON spec, with the standard library and nothing else.

    make_docx.py --spec spec.json --out report.docx
    make_docx.py --spec - --out report.docx      # spec on stdin

The spec is documented in SKILL.md. On success this prints a JSON report to
stdout naming the file, its size and the blocks it wrote, and it validates the
package it just produced before saying it succeeded.

python-docx is not available here (it needs lxml, which has no iOS wheel), so
the WordprocessingML is built by hand. Everything it emits -- headings,
paragraphs, real numbered and bulleted lists, tables with borders, inline
images -- is written against ECMA-376, but no Word has opened the result. See
"What is not verified" in SKILL.md.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import specs
from ooxml import CONTENT_TYPES, NS, Package, emu, esc, validate_package

# Half-points, which is how WordprocessingML sizes text: w:sz is 2x the point size.
STYLE_SIZES = {"Title": 56, "Heading1": 32, "Heading2": 26, "Heading3": 24}

STYLES_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f'<w:styles xmlns:w="{NS["w"]}">'
    "<w:docDefaults><w:rPrDefault><w:rPr>"
    '<w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/>'
    "</w:rPr></w:rPrDefault>"
    '<w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault>'
    "</w:docDefaults>"
    '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>'
    '<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:qFormat/>'
    '<w:pPr><w:spacing w:before="0" w:after="240"/></w:pPr>'
    '<w:rPr><w:b/><w:sz w:val="56"/><w:szCs w:val="56"/></w:rPr></w:style>'
    '<w:style w:type="paragraph" w:styleId="Subtitle"><w:name w:val="Subtitle"/><w:basedOn w:val="Normal"/><w:qFormat/>'
    '<w:pPr><w:spacing w:after="320"/></w:pPr>'
    '<w:rPr><w:i/><w:color w:val="595959"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>'
    + "".join(
        f'<w:style w:type="paragraph" w:styleId="Heading{level}"><w:name w:val="heading {level}"/>'
        '<w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>'
        f'<w:pPr><w:keepNext/><w:spacing w:before="240" w:after="120"/><w:outlineLvl w:val="{level - 1}"/></w:pPr>'
        f'<w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="{STYLE_SIZES[f"Heading{level}"]}"/>'
        f'<w:szCs w:val="{STYLE_SIZES[f"Heading{level}"]}"/></w:rPr></w:style>'
        for level in (1, 2, 3)
    )
    + '<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/>'
    '<w:basedOn w:val="Normal"/><w:qFormat/>'
    '<w:pPr><w:spacing w:after="0"/><w:contextualSpacing/></w:pPr></w:style>'
    '<w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/>'
    '<w:pPr><w:spacing w:before="0" w:after="200"/></w:pPr>'
    '<w:rPr><w:i/><w:color w:val="595959"/><w:sz w:val="18"/></w:rPr></w:style>'
    '<w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/>'
    '<w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar>'
    '<w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/>'
    '<w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/>'
    "</w:tblCellMar></w:tblPr></w:style>"
    '<w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/>'
    "<w:tblPr><w:tblBorders>"
    + "".join(
        f'<w:{edge} w:val="single" w:sz="4" w:space="0" w:color="808080"/>'
        for edge in ("top", "left", "bottom", "right", "insideH", "insideV")
    )
    + "</w:tblBorders></w:tblPr></w:style>"
    "</w:styles>"
)

# Real list formatting needs a numbering part; without it w:numPr points at
# nothing and Word renders flat paragraphs. Two abstract definitions are enough:
# a three-level bullet and a three-level decimal.
BULLET_GLYPHS = [("\uf0b7", "Symbol"), ("o", "Courier New"), ("\uf0a7", "Wingdings")]
DECIMAL_FORMATS = ["decimal", "lowerLetter", "lowerRoman"]

NUMBERING_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f'<w:numbering xmlns:w="{NS["w"]}">'
    '<w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="hybridMultilevel"/>'
    + "".join(
        f'<w:lvl w:ilvl="{i}"><w:start w:val="1"/><w:numFmt w:val="bullet"/>'
        f'<w:lvlText w:val="{esc(glyph, True)}"/><w:lvlJc w:val="left"/>'
        f'<w:pPr><w:ind w:left="{720 * (i + 1)}" w:hanging="360"/></w:pPr>'
        f'<w:rPr><w:rFonts w:ascii="{font}" w:hAnsi="{font}" w:hint="default"/></w:rPr></w:lvl>'
        for i, (glyph, font) in enumerate(BULLET_GLYPHS)
    )
    + "</w:abstractNum>"
    '<w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="hybridMultilevel"/>'
    + "".join(
        f'<w:lvl w:ilvl="{i}"><w:start w:val="1"/><w:numFmt w:val="{fmt}"/>'
        f'<w:lvlText w:val="%{i + 1}."/><w:lvlJc w:val="left"/>'
        f'<w:pPr><w:ind w:left="{720 * (i + 1)}" w:hanging="360"/></w:pPr></w:lvl>'
        for i, fmt in enumerate(DECIMAL_FORMATS)
    )
    + "</w:abstractNum>"
    '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>'
    '<w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>'
    "</w:numbering>"
)

BULLET_NUM_ID = 1
NUMBERED_NUM_ID = 2

# US Letter in twentieths of a point, with one-inch margins.
SECTION_XML = (
    '<w:sectPr><w:pgSz w:w="12240" w:h="15840"/>'
    '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" '
    'w:header="720" w:footer="720" w:gutter="0"/></w:sectPr>'
)

USABLE_WIDTH_TWIPS = 12240 - 1440 - 1440


def run(text: str, bold: bool = False, italic: bool = False, size: "int | None" = None,
        color: "str | None" = None, mono: bool = False) -> str:
    props = []
    if mono:
        props.append('<w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/>')
    if bold:
        props.append("<w:b/>")
    if italic:
        props.append("<w:i/>")
    if color:
        props.append(f'<w:color w:val="{esc(color, True)}"/>')
    if size:
        props.append(f'<w:sz w:val="{int(size * 2)}"/><w:szCs w:val="{int(size * 2)}"/>')
    rpr = f"<w:rPr>{''.join(props)}</w:rPr>" if props else ""
    # xml:space is not optional: without it a run of " " collapses away.
    return f'<w:r>{rpr}<w:t xml:space="preserve">{esc(text)}</w:t></w:r>'


def paragraph(runs: str, style: "str | None" = None, num_id: "int | None" = None,
              level: int = 0, align: "str | None" = None, page_break_before: bool = False) -> str:
    props = []
    if style:
        props.append(f'<w:pStyle w:val="{esc(style, True)}"/>')
    if page_break_before:
        props.append("<w:pageBreakBefore/>")
    if num_id is not None:
        props.append(f'<w:numPr><w:ilvl w:val="{level}"/><w:numId w:val="{num_id}"/></w:numPr>')
    if align:
        props.append(f'<w:jc w:val="{esc(align, True)}"/>')
    ppr = f"<w:pPr>{''.join(props)}</w:pPr>" if props else ""
    return f"<w:p>{ppr}{runs}</w:p>"


def table(columns: list, rows: list, header: bool = True) -> str:
    width = len(columns) if columns else (len(rows[0]) if rows else 1)
    width = max(width, 1)
    col_twips = USABLE_WIDTH_TWIPS // width
    grid = "".join(f'<w:gridCol w:w="{col_twips}"/>' for _ in range(width))

    def cell(value: object, bold: bool, shade: "str | None") -> str:
        shading = f'<w:shd w:val="clear" w:color="auto" w:fill="{shade}"/>' if shade else ""
        numeric = isinstance(value, (int, float)) and not isinstance(value, bool)
        align = '<w:jc w:val="right"/>' if numeric else ""
        return (
            f'<w:tc><w:tcPr><w:tcW w:w="{col_twips}" w:type="dxa"/>{shading}'
            '<w:vAlign w:val="center"/></w:tcPr>'
            f"<w:p><w:pPr><w:spacing w:after=\"40\"/>{align}</w:pPr>{run(_text(value), bold=bold)}</w:p></w:tc>"
        )

    body = ""
    if columns:
        cells = "".join(cell(c, bold=True, shade="DEEAF6") for c in columns)
        # tblHeader repeats the row when the table spans a page break.
        body += f"<w:tr><w:trPr><w:tblHeader/></w:trPr>{cells}</w:tr>" if header else f"<w:tr>{cells}</w:tr>"
    for row in rows:
        padded = list(row) + [""] * (width - len(row))
        body += "<w:tr>" + "".join(cell(v, bold=False, shade=None) for v in padded[:width]) + "</w:tr>"

    return (
        '<w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/>'
        f'<w:tblW w:w="{USABLE_WIDTH_TWIPS}" w:type="dxa"/><w:tblLayout w:type="fixed"/>'
        "<w:tblBorders>"
        + "".join(
            f'<w:{edge} w:val="single" w:sz="4" w:space="0" w:color="808080"/>'
            for edge in ("top", "left", "bottom", "right", "insideH", "insideV")
        )
        + f"</w:tblBorders></w:tblPr><w:tblGrid>{grid}</w:tblGrid>{body}</w:tbl>"
        # A table must be followed by a paragraph or Word treats the document as
        # ending mid-table and repairs the file on open.
        "<w:p/>"
    )


def _text(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"
    if isinstance(value, float):
        return f"{value:g}"
    return str(value)


def image_size(path: str, width_in: "float | None") -> "tuple[int, int]":
    """Pixel size of an image, scaled to a target width in inches.

    Pillow is bundled, so the real dimensions are available and the aspect
    ratio is preserved instead of guessed.
    """
    from PIL import Image

    with Image.open(path) as im:
        pixel_w, pixel_h = im.size
        dpi = im.info.get("dpi", (96, 96))
    dpi_x = dpi[0] or 96
    dpi_y = (dpi[1] if len(dpi) > 1 else dpi[0]) or 96
    natural_w = pixel_w / dpi_x
    natural_h = pixel_h / dpi_y
    target_w = width_in if width_in else min(natural_w, 6.0)
    scale = target_w / natural_w if natural_w else 1.0
    return emu(target_w), emu(natural_h * scale)


def drawing(rid: str, cx: int, cy: int, index: int, name: str) -> str:
    return (
        "<w:r><w:drawing>"
        f'<wp:inline distT="0" distB="0" distL="0" distR="0">'
        f'<wp:extent cx="{cx}" cy="{cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>'
        f'<wp:docPr id="{index}" name="{esc(name, True)}"/>'
        '<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>'
        f'<a:graphic><a:graphicData uri="{NS["pic"]}">'
        "<pic:pic>"
        f'<pic:nvPicPr><pic:cNvPr id="{index}" name="{esc(name, True)}"/><pic:cNvPicPr/></pic:nvPicPr>'
        f'<pic:blipFill><a:blip r:embed="{rid}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>'
        '<pic:spPr><a:xfrm><a:off x="0" y="0"/>'
        f'<a:ext cx="{cx}" cy="{cy}"/></a:xfrm>'
        '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>'
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"
    )


def build(spec: dict, out_path: str) -> dict:
    package = Package()
    package.core_properties(
        title=str(spec.get("title", "")),
        author=str(spec.get("author", "")),
        subject=str(spec.get("subject", "")),
    )
    package.relate("", "officeDocument", "word/document.xml")
    package.add_part("word/styles.xml", STYLES_XML, CONTENT_TYPES["docx.styles"])
    package.add_part("word/numbering.xml", NUMBERING_XML, CONTENT_TYPES["docx.numbering"])
    package.relate("word/document.xml", "styles", "word/styles.xml")
    package.relate("word/document.xml", "numbering", "word/numbering.xml")

    body: list = []
    written: dict = {}
    images = 0

    if spec.get("title"):
        body.append(paragraph(run(str(spec["title"])), style="Title"))
        written["title"] = 1
    if spec.get("subtitle"):
        body.append(paragraph(run(str(spec["subtitle"])), style="Subtitle"))
        written["subtitle"] = 1

    for block in spec.get("blocks", []):
        kind = str(block.get("type", "")).lower()
        written[kind] = written.get(kind, 0) + 1
        if kind == "heading":
            level = min(max(int(block.get("level", 1)), 1), 3)
            body.append(paragraph(run(str(block.get("text", ""))), style=f"Heading{level}"))
        elif kind == "paragraph":
            body.append(paragraph(
                run(str(block.get("text", "")), bold=bool(block.get("bold")),
                    italic=bool(block.get("italic")), mono=bool(block.get("mono"))),
                align=block.get("align"),
            ))
        elif kind in ("bullets", "numbers"):
            num_id = BULLET_NUM_ID if kind == "bullets" else NUMBERED_NUM_ID
            for item in block.get("items", []):
                if isinstance(item, dict):
                    text, level = str(item.get("text", "")), min(int(item.get("level", 0)), 2)
                else:
                    text, level = str(item), 0
                body.append(paragraph(run(text), style="ListParagraph", num_id=num_id, level=level))
        elif kind == "table":
            body.append(table(list(block.get("columns", [])), list(block.get("rows", []))))
        elif kind == "image":
            path = block.get("path")
            if not path:
                raise SystemExit("make_docx: an image block needs a 'path'")
            if not os.path.isfile(path):
                raise SystemExit(f"make_docx: image {path!r} does not exist")
            extension = os.path.splitext(path)[1].lstrip(".").lower() or "png"
            images += 1
            with open(path, "rb") as handle:
                data = handle.read()
            name = f"word/media/image{images}.{extension}"
            package.add_image(name, data, extension)
            rid = package.relate("word/document.xml", "image", name)
            cx, cy = image_size(path, block.get("width_in"))
            body.append(paragraph(drawing(rid, cx, cy, 100 + images, os.path.basename(path)), align="center"))
            if block.get("caption"):
                body.append(paragraph(run(str(block["caption"])), style="Caption", align="center"))
        elif kind == "pagebreak":
            body.append(paragraph("", page_break_before=True))
        else:
            raise SystemExit(f"make_docx: unknown block type {kind!r}; see SKILL.md for the list")

    document = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        f'<w:document xmlns:w="{NS["w"]}" xmlns:r="{NS["r"]}" xmlns:wp="{NS["wp"]}" '
        f'xmlns:a="{NS["a"]}" xmlns:pic="{NS["pic"]}">'
        f"<w:body>{''.join(body)}{SECTION_XML}</w:body></w:document>"
    )
    package.add_part("word/document.xml", document, CONTENT_TYPES["docx.document"])
    package.write(out_path)

    report = validate_package(out_path, "docx")
    return {
        "file": os.path.abspath(out_path),
        "bytes": os.path.getsize(out_path),
        "blocks": written,
        "images": images,
        "valid": report["ok"],
        "validation": report,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Write a .docx from a JSON spec.")
    parser.add_argument("--spec", required=True, help="path to the JSON spec, or - for stdin")
    parser.add_argument("--out", required=True, help="path of the .docx to write")
    args = parser.parse_args()

    spec = specs.load(args.spec, "make_docx")
    specs.check_keys(spec, "make_docx", {"title", "subtitle", "author", "subject", "blocks"}, "blocks")
    result = build(spec, args.out)
    print(json.dumps(result, indent=2))
    if not result["valid"]:
        print("make_docx: the package it wrote did not validate; see 'validation' above", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
