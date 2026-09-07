#!/usr/bin/env python3
"""Write a .pptx from a JSON spec, with the standard library and nothing else.

    make_pptx.py --spec spec.json --out deck.pptx
    make_pptx.py --spec - --out deck.pptx         # spec on stdin

The spec is documented in SKILL.md. On success this prints a JSON report to
stdout and validates the package it just produced before saying it succeeded.

python-pptx is not available here (it needs lxml, which has no iOS wheel), so
the PresentationML is built by hand. Slides carry explicit geometry rather than
placeholders inherited from a layout, which is why one blank layout and one
master are enough and why what you position is what a renderer draws. No
PowerPoint has opened the result -- see "What is not verified" in SKILL.md.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import specs
from ooxml import CONTENT_TYPES, NS, Package, emu, esc, validate_package

SLIDE_W_IN, SLIDE_H_IN = 13.333, 7.5  # 16:9, the shape every deck since 2013 uses
MARGIN_IN = 0.75

SLIDE_FIELDS = {
    "title": {"layout", "title", "subtitle"},
    "bullets": {"layout", "title", "bullets"},
    "table": {"layout", "title", "columns", "rows"},
    "image": {"layout", "title", "image", "caption"},
    "text": {"layout", "title", "text"},
}
BULLET_FIELDS = {"text", "level", "size", "bold", "bullet", "date", "url"}
SOURCE_FIELDS = {"title", "date", "url"}
SOURCE_SLIDE_TITLES = {
    "source", "sources", "references", "citations", "data sources",
    "来源", "资料来源", "信息来源", "数据来源", "参考资料", "参考来源",
}
MAX_BULLETS = 512

THEME_COLORS = [
    ("dk1", '<a:sysClr val="windowText" lastClr="000000"/>'),
    ("lt1", '<a:sysClr val="window" lastClr="FFFFFF"/>'),
    ("dk2", '<a:srgbClr val="1F3864"/>'),
    ("lt2", '<a:srgbClr val="E7E6E6"/>'),
    ("accent1", '<a:srgbClr val="4472C4"/>'),
    ("accent2", '<a:srgbClr val="ED7D31"/>'),
    ("accent3", '<a:srgbClr val="A5A5A5"/>'),
    ("accent4", '<a:srgbClr val="FFC000"/>'),
    ("accent5", '<a:srgbClr val="5B9BD5"/>'),
    ("accent6", '<a:srgbClr val="70AD47"/>'),
    ("hlink", '<a:srgbClr val="0563C1"/>'),
    ("folHlink", '<a:srgbClr val="954F72"/>'),
]

# A theme is not optional: the master's clrMap resolves scheme colours through
# it, and a deck whose master has no theme relationship fails to open.
THEME_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f'<a:theme xmlns:a="{NS["a"]}" name="TensorAgent">'
    "<a:themeElements>"
    '<a:clrScheme name="TensorAgent">'
    + "".join(f"<a:{name}>{value}</a:{name}>" for name, value in THEME_COLORS)
    + "</a:clrScheme>"
    '<a:fontScheme name="TensorAgent">'
    '<a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>'
    '<a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>'
    "</a:fontScheme>"
    '<a:fmtScheme name="TensorAgent">'
    "<a:fillStyleLst>"
    '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
    '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
    '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
    "</a:fillStyleLst>"
    "<a:lnStyleLst>"
    + "".join(
        f'<a:ln w="{width}" cap="flat" cmpd="sng" algn="ctr">'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '<a:prstDash val="solid"/></a:ln>'
        for width in (6350, 12700, 19050)
    )
    + "</a:lnStyleLst>"
    "<a:effectStyleLst>"
    + "<a:effectStyle><a:effectLst/></a:effectStyle>" * 3
    + "</a:effectStyleLst>"
    "<a:bgFillStyleLst>"
    + '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>' * 3
    + "</a:bgFillStyleLst>"
    "</a:fmtScheme>"
    "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/>"
    "</a:theme>"
)

EMPTY_TREE = (
    '<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>'
    '<p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/>'
    '<a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>'
)

CLR_MAP = (
    '<p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" '
    'accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" '
    'hlink="hlink" folHlink="folHlink"/>'
)

PPT_ROOT_NS = f'xmlns:a="{NS["a"]}" xmlns:r="{NS["r"]}" xmlns:p="{NS["p"]}"'

SLIDE_MASTER_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f"<p:sldMaster {PPT_ROOT_NS}>"
    '<p:cSld><p:bg><p:bgPr><a:solidFill><a:schemeClr val="bg1"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>'
    f"<p:spTree>{EMPTY_TREE}</p:spTree></p:cSld>"
    f"{CLR_MAP}"
    '<p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>'
    "<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>"
    "</p:sldMaster>"
)

SLIDE_LAYOUT_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f'<p:sldLayout {PPT_ROOT_NS} type="blank" preserve="1">'
    f'<p:cSld name="Blank"><p:spTree>{EMPTY_TREE}</p:spTree></p:cSld>'
    "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>"
    "</p:sldLayout>"
)

PRES_PROPS_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    f"<p:presentationPr {PPT_ROOT_NS}/>"
)


def text_body(paragraphs: list, anchor: str = "t", align: str = "l", wrap: bool = True) -> str:
    """A DrawingML text body. `paragraphs` are dicts of text/size/bold/colour/level."""
    body = []
    for para in paragraphs:
        props = []
        level = int(para.get("level", 0))
        if level:
            props.append(f'lvl="{level}"')
        if align != "l":
            props.append(f'algn="{align}"')
        bullet = para.get("bullet")
        ppr_children = ""
        if bullet is False:
            ppr_children = "<a:buNone/>"
        elif bullet:
            ppr_children = (
                '<a:buFont typeface="Arial" panose="020B0604020202020204" pitchFamily="34" charset="0"/>'
                f'<a:buChar char="{esc(bullet, True)}"/>'
            )
        indent = f' marL="{emu(0.3 + 0.35 * level)}" indent="{emu(-0.25)}"' if bullet else ""
        ppr = f"<a:pPr{indent} {' '.join(props)}>{ppr_children}</a:pPr>" if (props or ppr_children or indent) else ""
        rpr = ['lang="en-US" dirty="0"']
        if para.get("size"):
            rpr.append(f'sz="{int(float(para["size"]) * 100)}"')
        if para.get("bold"):
            rpr.append('b="1"')
        if para.get("italic"):
            rpr.append('i="1"')
        fill = f'<a:solidFill><a:srgbClr val="{esc(para["color"], True)}"/></a:solidFill>' if para.get("color") else ""
        text = esc(para.get("text", ""))
        body.append(
            f"<a:p>{ppr}<a:r><a:rPr {' '.join(rpr)}>{fill}</a:rPr>"
            f"<a:t>{text}</a:t></a:r></a:p>"
        )
    if not body:
        body.append("<a:p/>")
    wrap_attr = 'wrap="square"' if wrap else 'wrap="none"'
    return (
        f'<a:bodyPr {wrap_attr} anchor="{anchor}"><a:normAutofit/></a:bodyPr><a:lstStyle/>'
        + "".join(body)
    )


def shape(index: int, name: str, x: float, y: float, cx: float, cy: float, body: str) -> str:
    return (
        "<p:sp>"
        f'<p:nvSpPr><p:cNvPr id="{index}" name="{esc(name, True)}"/>'
        '<p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>'
        f'<p:spPr><a:xfrm><a:off x="{emu(x)}" y="{emu(y)}"/><a:ext cx="{emu(cx)}" cy="{emu(cy)}"/></a:xfrm>'
        '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>'
        f"<p:txBody>{body}</p:txBody></p:sp>"
    )


def picture(index: int, rid: str, name: str, x: float, y: float, cx: float, cy: float) -> str:
    return (
        "<p:pic>"
        f'<p:nvPicPr><p:cNvPr id="{index}" name="{esc(name, True)}"/>'
        '<p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>'
        f'<p:blipFill><a:blip r:embed="{rid}"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>'
        f'<p:spPr><a:xfrm><a:off x="{emu(x)}" y="{emu(y)}"/><a:ext cx="{emu(cx)}" cy="{emu(cy)}"/></a:xfrm>'
        '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>'
        "</p:pic>"
    )


def graphic_table(index: int, columns: list, rows: list, x: float, y: float, cx: float, cy: float) -> str:
    """A DrawingML table inside a graphicFrame.

    The cells carry explicit fills rather than a tableStyleId: a style id names
    a GUID in a tableStyles part this package does not ship, and a renderer that
    cannot resolve it draws an invisible table.
    """
    width = max(len(columns) or (len(rows[0]) if rows else 1), 1)
    col_emu = emu(cx) // width
    row_h = emu(0.4)

    def cell(value: object, header: bool) -> str:
        numeric = isinstance(value, (int, float)) and not isinstance(value, bool)
        para = {
            "text": "" if value is None else (f"{value:g}" if isinstance(value, float) else str(value)),
            "bold": header,
            "size": 14,
            "color": "FFFFFF" if header else "202020",
        }
        fill = "4472C4" if header else "FFFFFF"
        return (
            f'<a:tc><a:txBody>{text_body([para], align="r" if numeric else "l")}</a:txBody>'
            f'<a:tcPr marL="{emu(0.08)}" marR="{emu(0.08)}" anchor="ctr">'
            f'<a:solidFill><a:srgbClr val="{fill}"/></a:solidFill></a:tcPr></a:tc>'
        )

    grid = "".join(f'<a:gridCol w="{col_emu}"/>' for _ in range(width))
    body = ""
    if columns:
        body += f'<a:tr h="{row_h}">' + "".join(cell(c, True) for c in columns) + "</a:tr>"
    for row in rows:
        padded = list(row) + [""] * (width - len(row))
        body += f'<a:tr h="{row_h}">' + "".join(cell(v, False) for v in padded[:width]) + "</a:tr>"

    return (
        "<p:graphicFrame>"
        f'<p:nvGraphicFramePr><p:cNvPr id="{index}" name="Table {index}"/>'
        '<p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>'
        f'<p:xfrm><a:off x="{emu(x)}" y="{emu(y)}"/><a:ext cx="{emu(cx)}" cy="{emu(cy)}"/></p:xfrm>'
        # The table graphic's uri is .../drawingml/2006/table, which is NOT the
        # main DrawingML namespace with "/table" appended.
        '<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table">'
        '<a:tbl><a:tblPr firstRow="1" bandRow="1"/>'
        f"<a:tblGrid>{grid}</a:tblGrid>"
        f"{body}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>"
    )


def fit(path: str, max_w: float, max_h: float) -> "tuple[float, float]":
    """Scale an image to fit a box, keeping its aspect ratio. Pillow is bundled."""
    from PIL import Image

    with Image.open(path) as im:
        pixel_w, pixel_h = im.size
    if not pixel_w or not pixel_h:
        return max_w, max_h
    scale = min(max_w / pixel_w, max_h / pixel_h)
    return pixel_w * scale, pixel_h * scale


def slide_error(number: int, layout: str, message: str) -> None:
    """Fail with enough schema context for an agent to repair one slide."""
    accepted = SLIDE_FIELDS.get(layout, {"layout", "title"})
    raise SystemExit(
        f"make_pptx: slide {number} (layout {layout!r}) {message} "
        f"Accepted fields for this layout: {', '.join(sorted(accepted))}. "
        f"Accepted layouts: {', '.join(sorted(SLIDE_FIELDS))}."
    )


def has_text(value: object) -> bool:
    return value is not None and bool(str(value).strip())


def flatten_bullets(raw: object, slide_number: int) -> object:
    """Normalize the common {text, bullets:[...]} tree into DrawingML levels."""
    if not isinstance(raw, list):
        return raw  # validate_slide owns the canonical type diagnostic

    flattened: list = []

    def append(item: object, inherited_level: "int | None" = None, depth: int = 0) -> None:
        if depth > 8:
            slide_error(slide_number, "bullets", "nests bullets more than 8 levels deep.")
        if len(flattened) >= MAX_BULLETS:
            slide_error(slide_number, "bullets", f"contains more than {MAX_BULLETS} bullet items.")

        if isinstance(item, list):
            slide_error(slide_number, "bullets", "contains a nested array; nest with an object's 'bullets' field.")

        if not isinstance(item, dict):
            if inherited_level is None:
                flattened.append(item)
            else:
                flattened.append({"text": "" if item is None else item, "level": inherited_level})
            return

        normalized = dict(item)
        children = normalized.pop("bullets", None) if "bullets" in normalized else None
        if inherited_level is not None and "level" not in normalized:
            normalized["level"] = inherited_level
        flattened.append(normalized)

        if children is None:
            return
        if not isinstance(children, list):
            slide_error(slide_number, "bullets", "has a bullet object's 'bullets' field that is not an array.")

        try:
            parent_level = int(normalized.get("level", inherited_level or 0))
        except (TypeError, ValueError):
            # validate_slide will issue the canonical parent-level diagnostic; the
            # inherited child value here is never rendered after that failure.
            parent_level = inherited_level or 0
        child_level = min(max(parent_level + 1, 0), 2)
        for child in children:
            append(child, child_level, depth + 1)

    for bullet in raw:
        append(bullet)
    return flattened


def validate_slide(spec: dict, number: int, layout: str) -> None:
    """Reject fields or shapes that would otherwise render as an empty slide."""
    if layout not in SLIDE_FIELDS:
        raise SystemExit(
            f"make_pptx: slide {number} has unknown layout {layout!r}. "
            f"Accepted layouts: {', '.join(sorted(SLIDE_FIELDS))}."
        )

    if "notes" in spec:
        # Speaker notes need a notesSlide part and a notesMaster; this writer has
        # neither, so saying nothing here would silently drop the text.
        slide_error(
            number, layout,
            "has unsupported key 'notes' (a notesSlide needs a notesMaster part that is not built here); "
            "put the text on the slide or remove that key."
        )

    unknown = sorted(key for key in spec if key not in SLIDE_FIELDS[layout])
    if unknown:
        noun = "key" if len(unknown) == 1 else "keys"
        slide_error(
            number, layout,
            f"has unknown {noun} {', '.join(repr(key) for key in unknown)}."
        )

    if layout == "title":
        if not has_text(spec.get("title")):
            slide_error(number, layout, "needs a non-empty 'title'.")
        return

    if layout == "text":
        if not has_text(spec.get("text")):
            slide_error(number, layout, "needs non-empty 'text' body content.")
        return

    if layout == "image":
        image = spec.get("image")
        if not isinstance(image, str) or not image.strip():
            slide_error(number, layout, "needs a non-empty string 'image' path.")
        return

    if layout == "bullets":
        bullets = spec.get("bullets")
        if not isinstance(bullets, list) or not bullets:
            slide_error(number, layout, "needs a non-empty 'bullets' array.")

        visible = False
        for item_number, item in enumerate(bullets, start=1):
            if isinstance(item, dict):
                unknown_item = sorted(key for key in item if key not in BULLET_FIELDS)
                if unknown_item:
                    slide_error(
                        number, layout,
                        f"bullet {item_number} has unknown "
                        f"{'key' if len(unknown_item) == 1 else 'keys'} "
                        f"{', '.join(repr(key) for key in unknown_item)}; bullet objects accept: "
                        f"{', '.join(sorted(BULLET_FIELDS))}."
                    )
                if "level" in item:
                    try:
                        level = int(item["level"])
                    except (TypeError, ValueError):
                        slide_error(number, layout, f"bullet {item_number} has a non-integer 'level'.")
                    if level < 0 or level > 2:
                        slide_error(number, layout, f"bullet {item_number} needs 'level' between 0 and 2.")
                if "size" in item:
                    try:
                        size = float(item["size"])
                    except (TypeError, ValueError):
                        slide_error(number, layout, f"bullet {item_number} has a non-numeric 'size'.")
                    if not math.isfinite(size) or size <= 0:
                        slide_error(number, layout, f"bullet {item_number} needs a finite positive 'size'.")
                if "url" in item and not isinstance(item.get("url"), str):
                    slide_error(number, layout, f"bullet {item_number} needs 'url' to be text when present.")
                visible = visible or any(
                    has_text(item.get(field)) for field in ("text", "date", "url")
                )
            elif isinstance(item, list):
                slide_error(number, layout, f"bullet {item_number} must be text or an object, not an array.")
            else:
                # Empty strings and nulls are accepted as intentional visual separators,
                # but at least one item on the slide must carry visible text.
                visible = visible or has_text(item)
        if not visible:
            slide_error(number, layout, "has no visible text in its 'bullets' array.")
        return

    columns = spec.get("columns", [])
    rows = spec.get("rows", [])
    if not isinstance(columns, list):
        slide_error(number, layout, "needs 'columns' to be an array when present.")
    if not isinstance(rows, list):
        slide_error(number, layout, "needs 'rows' to be an array when present.")
    if not columns and not rows:
        slide_error(number, layout, "needs a non-empty 'columns' or 'rows' array.")
    for row_number, row in enumerate(rows, start=1):
        if not isinstance(row, list):
            slide_error(number, layout, f"row {row_number} must be an array of cells.")
    width = len(columns) if columns else (len(rows[0]) if rows else 0)
    if width == 0:
        slide_error(number, layout, "has zero table columns.")
    too_wide = next((i for i, row in enumerate(rows, start=1) if len(row) > width), None)
    if too_wide is not None:
        slide_error(
            number, layout,
            f"row {too_wide} has more cells than the table's {width} columns; no cell may be silently dropped."
        )
    if not any(has_text(value) for value in columns) and not any(
        has_text(value) for row in rows for value in row
    ):
        slide_error(number, layout, "has no visible table cell content.")


def normalize_slide(raw: object, number: int) -> dict:
    if not isinstance(raw, dict):
        raise SystemExit(
            f"make_pptx: slide {number} must be a JSON object, not a {type(raw).__name__}. "
            f"Accepted layouts: {', '.join(sorted(SLIDE_FIELDS))}."
        )

    spec = dict(raw)
    layout = str(spec.get("layout", "bullets")).strip().lower()

    # The measured live failure used title + content[] on every slide. That shape
    # is unambiguously a bullets slide, so normalize it instead of spending a model
    # round on a mechanical rename. Other uses of `content` remain errors: guessing
    # whether it means table rows, image bytes or title text would lose information.
    if "content" in spec and layout == "bullets":
        if "bullets" in spec:
            slide_error(number, layout, "has both alias 'content' and canonical field 'bullets'; keep only 'bullets'.")
        if not isinstance(spec["content"], list):
            slide_error(number, layout, "uses alias 'content', but that alias must be an array; use 'bullets'.")
        spec["bullets"] = spec.pop("content")

    # Models often choose `text` for a short Sources introduction and then add a
    # canonical bullets array below it. Nothing is ambiguous here: retain the intro
    # as the first bullet and render the slide using the richer bullets layout.
    if layout == "text" and "bullets" in spec:
        if not isinstance(spec["bullets"], list):
            slide_error(number, layout, "has 'bullets', but that field must be an array.")
        bullets = list(spec["bullets"])
        if has_text(spec.get("text")):
            bullets.insert(0, {"text": spec["text"], "bullet": False})
        spec.pop("text", None)
        spec["bullets"] = bullets
        spec["layout"] = layout = "bullets"

    if layout == "bullets" and "bullets" in spec:
        spec["bullets"] = flatten_bullets(spec["bullets"], number)

    spec["layout"] = layout
    validate_slide(spec, number, layout)
    return spec


def source_bullet(raw: object, number: int) -> str:
    if isinstance(raw, str):
        if raw.strip():
            return raw.strip()
        raise SystemExit(f"make_pptx: source {number} is empty; provide a URL or a title/date/url object")
    if not isinstance(raw, dict):
        raise SystemExit(
            f"make_pptx: source {number} must be a URL string or an object with: "
            f"{', '.join(sorted(SOURCE_FIELDS))}"
        )

    unknown = sorted(key for key in raw if key not in SOURCE_FIELDS)
    if unknown:
        raise SystemExit(
            f"make_pptx: source {number} has unknown "
            f"{'key' if len(unknown) == 1 else 'keys'} {', '.join(repr(key) for key in unknown)}. "
            f"Accepted source fields: {', '.join(sorted(SOURCE_FIELDS))}."
        )
    url = raw.get("url")
    if not isinstance(url, str) or not url.strip():
        raise SystemExit(
            f"make_pptx: source {number} needs a non-empty 'url'. "
            f"Accepted source fields: {', '.join(sorted(SOURCE_FIELDS))}."
        )
    parts = [str(raw[key]).strip() for key in ("title", "date") if has_text(raw.get(key))]
    parts.append(url.strip())
    return " — ".join(parts)


def is_sources_only_slide(raw: dict) -> bool:
    """Return whether a slide is only a placeholder for its ``sources`` field.

    Agents commonly spell the documented per-slide compatibility form as a
    bullets slide titled "Sources", but omit ``bullets`` because ``sources`` is
    its body.  Once the caller extracts that field, validating the remainder as
    an authored slide would turn a valid request into an empty-slide error.  An
    explicitly empty body has the same unambiguous meaning.  Non-empty content,
    unsupported layouts, and extra fields remain ordinary authored slides and
    therefore still receive the strict schema diagnostics.
    """
    layout = str(raw.get("layout", "bullets")).strip().lower()
    if layout not in ("bullets", "text"):
        return False

    allowed = {"layout", "title"}
    if layout == "bullets":
        allowed.update(("bullets", "content"))
        for field in ("bullets", "content"):
            if field in raw and raw[field] != []:
                return False
    else:
        allowed.update(("text", "bullets"))
        if has_text(raw.get("text")):
            return False
        if "bullets" in raw and raw["bullets"] != []:
            return False

    return all(key in allowed for key in raw)


def authored_source_entries(raw: dict) -> "list | None":
    """Extract a visibly authored Sources slide without duplicating it later.

    Small models sometimes ignore the canonical root ``sources`` shape and also
    author a localized bullets slide whose every item is a citation.  Keeping
    that slide and appending the normalized root sources produces two Sources
    slides.  Only an unmistakable source title whose every non-empty bullet has
    a URL is folded; ordinary bullets slides are left untouched.
    """
    title = str(raw.get("title", "")).strip()
    if title.casefold() not in SOURCE_SLIDE_TITLES:
        return None
    if str(raw.get("layout", "bullets")).strip().lower() != "bullets":
        return None

    bullets = raw.get("bullets")
    if not isinstance(bullets, list) or not bullets:
        return None

    entries = []
    allowed = BULLET_FIELDS | {"title"}
    for bullet in bullets:
        if isinstance(bullet, str):
            text = bullet.strip()
            if "http://" not in text and "https://" not in text:
                return None
            entries.append(text)
            continue
        if not isinstance(bullet, dict) or any(key not in allowed for key in bullet):
            return None
        url = bullet.get("url")
        if not isinstance(url, str) or not url.strip():
            return None
        source = {"url": url.strip()}
        source_title = bullet.get("title", bullet.get("text"))
        if has_text(source_title):
            source["title"] = str(source_title).strip()
        if has_text(bullet.get("date")):
            source["date"] = str(bullet["date"]).strip()
        entries.append(source)
    return entries


def normalized_slides(spec: dict) -> list:
    raw_slides = spec.get("slides")
    if not isinstance(raw_slides, list) or not raw_slides:
        raise SystemExit("make_pptx: the spec needs a non-empty 'slides' array; a deck with no slides will not open")

    gathered_sources: list = []
    if "sources" in spec:
        sources = spec["sources"]
        if not isinstance(sources, list):
            raise SystemExit(
                "make_pptx: top-level 'sources' must be an array of URL strings or title/date/url objects"
            )
        gathered_sources.extend(sources)

    slides = []
    sources_title = "Sources"
    for number, raw in enumerate(raw_slides, start=1):
        normalized_raw = raw
        if isinstance(raw, dict) and "sources" in raw:
            slide_sources = raw["sources"]
            if not isinstance(slide_sources, list):
                raise SystemExit(
                    f"make_pptx: slide {number} field 'sources' must be an array of URL strings "
                    "or title/date/url objects"
                )
            normalized_raw = dict(raw)
            normalized_raw.pop("sources")
            gathered_sources.extend(slide_sources)
            if is_sources_only_slide(normalized_raw):
                if has_text(normalized_raw.get("title")):
                    sources_title = str(normalized_raw["title"]).strip()
                continue
        if isinstance(normalized_raw, dict):
            authored_sources = authored_source_entries(normalized_raw)
            if authored_sources is not None:
                sources_title = str(normalized_raw["title"]).strip()
                gathered_sources.extend(authored_sources)
                continue
        slides.append(normalize_slide(normalized_raw, number))

    if gathered_sources:
        # Convert and de-duplicate before validation, preserving first-seen order.
        # The same citation is often present both at the root and on its claim
        # slide; rendering it twice wastes space without adding evidence.
        source_bullets = list(dict.fromkeys(
            source_bullet(source, i)
            for i, source in enumerate(gathered_sources, start=1)
        ))
        slides.append(normalize_slide({
            "layout": "bullets",
            "title": sources_title,
            "bullets": source_bullets,
        }, len(slides) + 1))
    elif not slides:
        raise SystemExit(
            "make_pptx: a sources-only slide needs at least one source; "
            "a deck with no visible slides will not open"
        )
    return slides


def build_slide(spec: dict, package: Package, number: int, images: list) -> str:
    layout = str(spec.get("layout", "bullets")).lower()
    slide_part = f"ppt/slides/slide{number}.xml"
    shapes: list = []
    index = 2  # id 1 belongs to the group shape

    title = spec.get("title")
    content_top = MARGIN_IN + 1.2

    if layout == "title":
        shapes.append(shape(
            index, "Title", MARGIN_IN, 2.4, SLIDE_W_IN - 2 * MARGIN_IN, 1.6,
            text_body([{"text": str(title or ""), "size": 44, "bold": True, "color": "1F3864", "bullet": False}],
                      anchor="b", align="ctr"),
        ))
        index += 1
        if spec.get("subtitle"):
            shapes.append(shape(
                index, "Subtitle", MARGIN_IN, 4.1, SLIDE_W_IN - 2 * MARGIN_IN, 1.0,
                text_body([{"text": str(spec["subtitle"]), "size": 20, "color": "595959", "bullet": False}],
                          anchor="t", align="ctr"),
            ))
            index += 1
    else:
        if title:
            shapes.append(shape(
                index, "Title", MARGIN_IN, MARGIN_IN * 0.6, SLIDE_W_IN - 2 * MARGIN_IN, 1.0,
                text_body([{"text": str(title), "size": 30, "bold": True, "color": "1F3864", "bullet": False}],
                          anchor="ctr"),
            ))
            index += 1

    if layout == "bullets":
        paragraphs = []
        for item in spec.get("bullets", []):
            if isinstance(item, dict):
                citation_parts = [
                    str(item[field]).strip()
                    for field in ("text", "date", "url")
                    if has_text(item.get(field))
                ]
                item_text = " — ".join(citation_parts)
                paragraphs.append({
                    "text": item_text,
                    "level": int(item.get("level", 0)),
                    "size": item.get("size", 20),
                    "bold": bool(item.get("bold")),
                    "bullet": item.get("bullet", "•"),
                })
            else:
                # Validation permits null as an intentional visual separator. Keep
                # it empty instead of leaking Python's spelling ("None") into the
                # user's slide.
                paragraphs.append({
                    "text": "" if item is None else str(item),
                    "level": 0,
                    "size": 20,
                    "bullet": "•",
                })
        shapes.append(shape(
            index, "Content", MARGIN_IN, content_top, SLIDE_W_IN - 2 * MARGIN_IN,
            SLIDE_H_IN - content_top - MARGIN_IN, text_body(paragraphs),
        ))
        index += 1
    elif layout == "image":
        path = spec.get("image")
        if not path:
            raise SystemExit(f"make_pptx: slide {number} has layout 'image' but no 'image' path")
        if not os.path.isfile(path):
            raise SystemExit(f"make_pptx: image {path!r} does not exist")
        extension = os.path.splitext(path)[1].lstrip(".").lower() or "png"
        with open(path, "rb") as handle:
            data = handle.read()
        images.append(path)
        name = f"ppt/media/image{len(images)}.{extension}"
        package.add_image(name, data, extension)
        rid = package.relate(slide_part, "image", name)
        caption_h = 0.45 if spec.get("caption") else 0.0
        box_h = SLIDE_H_IN - content_top - MARGIN_IN - caption_h
        box_w = SLIDE_W_IN - 2 * MARGIN_IN
        w, h = fit(path, box_w, box_h)
        shapes.append(picture(index, rid, os.path.basename(path),
                              (SLIDE_W_IN - w) / 2, content_top + (box_h - h) / 2, w, h))
        index += 1
        if spec.get("caption"):
            shapes.append(shape(
                index, "Caption", MARGIN_IN, SLIDE_H_IN - MARGIN_IN - caption_h, box_w, caption_h,
                text_body([{"text": str(spec["caption"]), "size": 13, "italic": True,
                            "color": "595959", "bullet": False}], align="ctr"),
            ))
            index += 1
    elif layout == "table":
        columns = list(spec.get("columns", []))
        rows = list(spec.get("rows", []))
        height = min(0.4 * (len(rows) + 1) + 0.1, SLIDE_H_IN - content_top - MARGIN_IN)
        shapes.append(graphic_table(index, columns, rows, MARGIN_IN, content_top,
                                    SLIDE_W_IN - 2 * MARGIN_IN, height))
        index += 1
    elif layout == "text":
        shapes.append(shape(
            index, "Body", MARGIN_IN, content_top, SLIDE_W_IN - 2 * MARGIN_IN,
            SLIDE_H_IN - content_top - MARGIN_IN,
            text_body([{"text": str(spec.get("text", "")), "size": 20, "bullet": False}]),
        ))
        index += 1
    elif layout != "title":
        raise SystemExit(f"make_pptx: unknown slide layout {layout!r}; see SKILL.md for the list")

    xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        f"<p:sld {PPT_ROOT_NS}>"
        f'<p:cSld><p:spTree>{EMPTY_TREE}{"".join(shapes)}</p:spTree></p:cSld>'
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>"
        "</p:sld>"
    )
    return xml


def build(spec: dict, out_path: str) -> dict:
    slides = normalized_slides(spec)

    package = Package()
    package.core_properties(
        title=str(spec.get("title", "")),
        author=str(spec.get("author", "")),
        subject=str(spec.get("subject", "")),
    )
    package.relate("", "officeDocument", "ppt/presentation.xml")

    package.add_part("ppt/theme/theme1.xml", THEME_XML, CONTENT_TYPES["theme"])
    package.add_part("ppt/slideLayouts/slideLayout1.xml", SLIDE_LAYOUT_XML, CONTENT_TYPES["pptx.slideLayout"])
    package.add_part("ppt/slideMasters/slideMaster1.xml", SLIDE_MASTER_XML, CONTENT_TYPES["pptx.slideMaster"])
    package.add_part("ppt/presProps.xml", PRES_PROPS_XML, CONTENT_TYPES["pptx.presProps"])

    # rId1 on the master must be the layout, because sldLayoutIdLst cites it.
    package.relate("ppt/slideMasters/slideMaster1.xml", "slideLayout", "ppt/slideLayouts/slideLayout1.xml")
    package.relate("ppt/slideMasters/slideMaster1.xml", "theme", "ppt/theme/theme1.xml")
    package.relate("ppt/slideLayouts/slideLayout1.xml", "slideMaster", "ppt/slideMasters/slideMaster1.xml")

    master_rid = package.relate("ppt/presentation.xml", "slideMaster", "ppt/slideMasters/slideMaster1.xml")
    slide_rids = []
    images: list = []
    kinds: dict = {}
    for number, slide_spec in enumerate(slides, start=1):
        part = f"ppt/slides/slide{number}.xml"
        # The layout relationship has to exist before any image relationship so
        # the slide's own rIds stay stable while images are appended.
        package.relate(part, "slideLayout", "ppt/slideLayouts/slideLayout1.xml")
        xml = build_slide(slide_spec, package, number, images)
        package.add_part(part, xml, CONTENT_TYPES["pptx.slide"])
        slide_rids.append(package.relate("ppt/presentation.xml", "slide", part))
        kind = str(slide_spec.get("layout", "bullets")).lower()
        kinds[kind] = kinds.get(kind, 0) + 1
    package.relate("ppt/presentation.xml", "presProps", "ppt/presProps.xml")
    package.relate("ppt/presentation.xml", "theme", "ppt/theme/theme1.xml")

    presentation = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        f'<p:presentation {PPT_ROOT_NS} saveSubsetFonts="1">'
        f'<p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="{master_rid}"/></p:sldMasterIdLst>'
        "<p:sldIdLst>"
        + "".join(f'<p:sldId id="{256 + i}" r:id="{rid}"/>' for i, rid in enumerate(slide_rids))
        + "</p:sldIdLst>"
        f'<p:sldSz cx="{emu(SLIDE_W_IN)}" cy="{emu(SLIDE_H_IN)}"/>'
        f'<p:notesSz cx="{emu(7.5)}" cy="{emu(10.0)}"/>'
        "</p:presentation>"
    )
    package.add_part("ppt/presentation.xml", presentation, CONTENT_TYPES["pptx.presentation"])
    package.write(out_path)

    report = validate_package(out_path, "pptx")
    return {
        "file": os.path.abspath(out_path),
        "bytes": os.path.getsize(out_path),
        "slides": len(slides),
        "layouts": kinds,
        "images": len(images),
        "valid": report["ok"],
        "validation": report,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Write a .pptx from a JSON spec.")
    parser.add_argument("--spec", required=True, help="path to the JSON spec, or - for stdin")
    parser.add_argument("--out", required=True, help="path of the .pptx to write")
    args = parser.parse_args()

    spec = specs.load(args.spec, "make_pptx")
    specs.check_keys(spec, "make_pptx", {"title", "subtitle", "author", "subject", "slides", "sources"}, "slides")
    result = build(spec, args.out)
    print(json.dumps(result, indent=2))
    if not result["valid"]:
        print("make_pptx: the package it wrote did not validate; see 'validation' above", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
