#!/usr/bin/env python3
"""The OOXML package machinery the .docx and .pptx writers share, and the
structural validator that decides whether what they wrote is a package at all.

Why this file exists: the .docx and .pptx writers predate this app compiling
lxml for iOS, so they build the OOXML with the standard library and validate
their own output; python-docx and python-pptx (and lxml) are bundled too, for
what those writers do not do -- see Limits in SKILL.md. An OOXML file is a ZIP
of XML parts plus two kinds of index -- `[Content_Types].xml`, which gives every
part a MIME type, and the `.rels` parts, which say which part points at which --
so the standard library can write one. That is what this does, and
`validate_package` checks any OOXML file, whichever library wrote it.

What it deliberately does NOT do is claim the result renders. Nothing on this
device can open a .docx, so `validate_package` checks the only thing that can be
checked here: that the package is internally consistent. See SKILL.md.
"""

from __future__ import annotations

import posixpath
import re
import zipfile
from dataclasses import dataclass, field
from datetime import datetime, timezone
from xml.etree.ElementTree import ParseError

# Parsing XML that came from outside the app goes through defusedxml: an
# uploaded .docx is a stranger's XML, and xml.etree will expand a billion-laughs
# entity without complaint. Writing goes through the stdlib, which is ours.
from defusedxml.ElementTree import fromstring as safe_fromstring

EMU_PER_INCH = 914400
EMU_PER_POINT = 12700

NS = {
    "ct": "http://schemas.openxmlformats.org/package/2006/content-types",
    "pr": "http://schemas.openxmlformats.org/package/2006/relationships",
    "w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    "s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
    "wp": "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",
    "pic": "http://schemas.openxmlformats.org/drawingml/2006/picture",
}

RT = {  # relationship types, by the short name used at the call site
    "officeDocument": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
    "core": "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties",
    "app": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties",
    "styles": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
    "numbering": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering",
    "image": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
    "slideMaster": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster",
    "slideLayout": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout",
    "slide": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide",
    "theme": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme",
    "presProps": "http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps",
}

CONTENT_TYPES = {
    "docx.document": "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
    "docx.styles": "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml",
    "docx.numbering": "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml",
    "pptx.presentation": "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
    "pptx.slideMaster": "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml",
    "pptx.slideLayout": "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml",
    "pptx.slide": "application/vnd.openxmlformats-officedocument.presentationml.slide+xml",
    "pptx.presProps": "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml",
    "theme": "application/vnd.openxmlformats-officedocument.theme+xml",
    "core": "application/vnd.openxmlformats-package.core-properties+xml",
    "app": "application/vnd.openxmlformats-officedocument.extended-properties+xml",
}

IMAGE_TYPES = {"png": "image/png", "jpeg": "image/jpeg", "jpg": "image/jpeg", "gif": "image/gif"}

XML_DECL = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'

# Codepoints XML 1.0 cannot carry even escaped. A tab, newline or return is
# fine; the rest of C0, the surrogate block and the two noncharacters at the top
# of the BMP are not, and one of them turns a document into an unopenable file
# or ends the write in a UnicodeEncodeError two frames away from the cause.
_ILLEGAL_XML = re.compile("[^\\t\\n\\r\\u0020-\\ud7ff\\ue000-\\ufffd\\U00010000-\\U0010ffff]")


def esc(text: object, attribute: bool = False) -> str:
    """XML-escape a value on its way into hand-built markup.

    Every part here is built by string formatting, so this is the only thing
    standing between a heading called `A & B <draft>` and a corrupt package.
    """
    s = "" if text is None else str(text)
    s = _ILLEGAL_XML.sub("", s)
    s = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    if attribute:
        s = s.replace('"', "&quot;").replace("\n", "&#10;")
    return s


def emu(inches: float) -> int:
    return int(round(inches * EMU_PER_INCH))


@dataclass
class Relationship:
    rid: str
    type: str
    target: str


@dataclass
class Package:
    """One OOXML package under construction: parts, per-part relationships and
    content types, written out in the order a consumer expects to find them."""

    parts: dict[str, bytes] = field(default_factory=dict)
    rels: dict[str, list[Relationship]] = field(default_factory=dict)
    overrides: dict[str, str] = field(default_factory=dict)
    defaults: dict[str, str] = field(default_factory=lambda: {
        "rels": "application/vnd.openxmlformats-package.relationships+xml",
        "xml": "application/xml",
    })

    def add_part(self, name: str, data: "str | bytes", content_type: "str | None" = None) -> str:
        """Add a part by its package name (`word/document.xml`, no leading slash)."""
        self.parts[name] = data.encode("utf-8") if isinstance(data, str) else data
        if content_type:
            self.overrides["/" + name] = content_type
        return name

    def add_image(self, name: str, data: bytes, extension: str) -> str:
        """Add a binary image part, declaring its extension as a package Default.

        Images are typed by extension rather than per part because that is what
        Word and PowerPoint themselves write, and a missing Default is one of
        the two ways a package with a picture in it fails to open.
        """
        ext = extension.lower().lstrip(".")
        self.defaults.setdefault(ext, IMAGE_TYPES.get(ext, "application/octet-stream"))
        self.parts[name] = data
        return name

    def relate(self, source: str, rel_type: str, target: str) -> str:
        """Point one part at another and return the r:id the source must cite.

        `source` is "" for the package root (`_rels/.rels`). Targets are stored
        relative to the source part's own directory, which is what a consumer
        resolves them against.
        """
        bucket = self.rels.setdefault(source, [])
        rid = f"rId{len(bucket) + 1}"
        base = posixpath.dirname(source)
        relative = posixpath.relpath(target, base) if base else target
        bucket.append(Relationship(rid, RT.get(rel_type, rel_type), relative))
        return rid

    def _content_types(self) -> str:
        lines = [XML_DECL, f'<Types xmlns="{NS["ct"]}">']
        for ext, ctype in sorted(self.defaults.items()):
            lines.append(f'<Default Extension="{esc(ext, True)}" ContentType="{esc(ctype, True)}"/>')
        for part, ctype in sorted(self.overrides.items()):
            lines.append(f'<Override PartName="{esc(part, True)}" ContentType="{esc(ctype, True)}"/>')
        lines.append("</Types>")
        return "".join(lines)

    def _rels_part(self, source: str) -> str:
        lines = [XML_DECL, f'<Relationships xmlns="{NS["pr"]}">']
        for rel in self.rels[source]:
            lines.append(
                f'<Relationship Id="{rel.rid}" Type="{esc(rel.type, True)}" Target="{esc(rel.target, True)}"/>'
            )
        lines.append("</Relationships>")
        return "".join(lines)

    def core_properties(self, title: str = "", author: str = "", subject: str = "") -> None:
        stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        core = (
            XML_DECL
            + '<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"'
            ' xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/"'
            ' xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
            f"<dc:title>{esc(title)}</dc:title><dc:subject>{esc(subject)}</dc:subject>"
            f"<dc:creator>{esc(author)}</dc:creator><cp:lastModifiedBy>{esc(author)}</cp:lastModifiedBy>"
            f'<dcterms:created xsi:type="dcterms:W3CDTF">{stamp}</dcterms:created>'
            f'<dcterms:modified xsi:type="dcterms:W3CDTF">{stamp}</dcterms:modified>'
            "</cp:coreProperties>"
        )
        app = (
            XML_DECL
            + '<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"'
            ' xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">'
            "<Application>TensorAgent</Application></Properties>"
        )
        self.add_part("docProps/core.xml", core, CONTENT_TYPES["core"])
        self.add_part("docProps/app.xml", app, CONTENT_TYPES["app"])
        self.relate("", "core", "docProps/core.xml")
        self.relate("", "app", "docProps/app.xml")

    def write(self, path: str) -> None:
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.writestr("[Content_Types].xml", self._content_types())
            for source in sorted(self.rels):
                base = posixpath.dirname(source)
                name = posixpath.join(base, "_rels", posixpath.basename(source) + ".rels") if source else "_rels/.rels"
                zf.writestr(name, self._rels_part(source))
            for name in sorted(self.parts):
                zf.writestr(name, self.parts[name])


# =====================================================================================
# validation
# =====================================================================================

# What each package kind must contain to be that kind at all: the part every
# consumer opens first, its root element as (prefix, local name), and the
# children without which the file is structurally empty.
KIND_RULES = {
    "docx": {
        "primary": "word/document.xml",
        "root": ("w", "document"),
        "required": ["w:body"],
        "content_type": CONTENT_TYPES["docx.document"],
    },
    "pptx": {
        "primary": "ppt/presentation.xml",
        "root": ("p", "presentation"),
        "required": ["p:sldMasterIdLst", "p:sldIdLst", "p:sldSz"],
        "content_type": CONTENT_TYPES["pptx.presentation"],
    },
    "xlsx": {
        "primary": "xl/workbook.xml",
        "root": ("s", "workbook"),
        "required": ["s:sheets"],
        "content_type": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
    },
}


def _qname(spec: str) -> str:
    prefix, _, local = spec.partition(":")
    return f"{{{NS[prefix]}}}{local}"


def validate_package(path: str, kind: "str | None" = None) -> dict:
    """Check that an OOXML file is a self-consistent package.

    This is the strongest claim that can be made on a device with no Office
    application on it: every part `[Content_Types].xml` names exists, every part
    in the zip has a content type, every relationship resolves to a part that is
    present, every XML part parses, and the primary part has the root element
    and the children its kind requires. It does not say the file renders, and
    nothing here can.
    """
    report: dict = {"file": path, "kind": kind, "ok": False, "parts": 0, "errors": [], "checks": []}
    errors: list = report["errors"]
    checks: list = report["checks"]

    if not zipfile.is_zipfile(path):
        errors.append(f"{path} is not a ZIP archive; an OOXML file always is")
        return report

    with zipfile.ZipFile(path) as zf:
        names = [n for n in zf.namelist() if not n.endswith("/")]
        report["parts"] = len(names)
        if "[Content_Types].xml" not in names:
            errors.append("[Content_Types].xml is missing; no consumer can type a single part")
            return report

        try:
            types_root = safe_fromstring(zf.read("[Content_Types].xml"))
        except ParseError as exc:
            errors.append(f"[Content_Types].xml does not parse: {exc}")
            return report

        defaults = {
            e.get("Extension", "").lower(): e.get("ContentType", "")
            for e in types_root.findall(f'{{{NS["ct"]}}}Default')
        }
        overrides = {
            e.get("PartName", ""): e.get("ContentType", "")
            for e in types_root.findall(f'{{{NS["ct"]}}}Override')
        }
        checks.append(f"[Content_Types].xml: {len(defaults)} default(s), {len(overrides)} override(s)")

        for part in sorted(overrides):
            if part.lstrip("/") not in names:
                errors.append(f"[Content_Types].xml overrides {part}, which is not in the package")

        for name in names:
            if name == "[Content_Types].xml":
                continue
            # OPC takes everything after the last dot, so `/_rels/.rels` has the
            # extension "rels". posixpath.splitext calls that a dotfile with no
            # extension and would fail every well-formed package here.
            basename = posixpath.basename(name)
            extension = basename.rsplit(".", 1)[-1].lower() if "." in basename else ""
            if "/" + name not in overrides and extension not in defaults:
                errors.append(f"{name} has no content type: no Override for it and no Default for '.{extension}'")

        # Parse every XML part once and keep the trees. Parsing on demand instead
        # would report the same failure twice and, worse, would raise the second
        # time -- turning a bad file into a traceback with no verdict in it.
        trees: dict = {}
        for name in sorted(names):
            if not (name.endswith(".xml") or name.endswith(".rels")):
                continue
            try:
                trees[name] = safe_fromstring(zf.read(name))
            except ParseError as exc:
                errors.append(f"{name} does not parse: {exc}")
        checks.append(f"{len(trees)} XML part(s) parsed")

        rels_parts = [n for n in names if n.endswith(".rels")]
        checks.append(f"{len(rels_parts)} relationship part(s)")
        targets_seen = 0
        for rels_name in sorted(rels_parts):
            rels_root = trees.get(rels_name)
            if rels_root is None:
                continue
            # word/_rels/document.xml.rels resolves its targets against word/.
            source_dir = posixpath.dirname(posixpath.dirname(rels_name))
            for rel in rels_root.findall(f'{{{NS["pr"]}}}Relationship'):
                if rel.get("TargetMode") == "External":
                    continue
                target = rel.get("Target", "")
                targets_seen += 1
                if target.startswith("/"):
                    resolved = target.lstrip("/")
                else:
                    resolved = posixpath.normpath(posixpath.join(source_dir, target))
                if resolved not in names:
                    errors.append(f"{rels_name}: relationship {rel.get('Id')} points at {target!r} "
                                  f"(resolves to {resolved!r}), which is not in the package")
        checks.append(f"{targets_seen} internal relationship target(s) resolved")

        if kind is None:
            for candidate, rule in KIND_RULES.items():
                if rule["primary"] in names:
                    kind = candidate
                    break
            report["kind"] = kind
        if kind is None:
            errors.append("no primary part found: this is not a .docx, .pptx or .xlsx package")
        elif kind not in KIND_RULES:
            errors.append(f"unknown package kind {kind!r}")
        else:
            rule = KIND_RULES[kind]
            primary = rule["primary"]
            if primary not in names:
                errors.append(f"a .{kind} must contain {primary}")
            elif primary not in trees:
                pass  # its parse failure is already reported above
            else:
                declared = overrides.get("/" + primary)
                if declared != rule["content_type"]:
                    errors.append(f"{primary} is typed {declared!r}, expected {rule['content_type']!r}")
                root = trees[primary]
                want = _qname(":".join(rule["root"]))
                if root.tag != want:
                    errors.append(f"{primary} root is {root.tag}, expected {want}")
                else:
                    checks.append(f"{primary} root is {rule['root'][1]} in the {rule['root'][0]} namespace")
                for child in rule["required"]:
                    if root.find(_qname(child)) is None:
                        errors.append(f"{primary} has no {child}")
                    else:
                        checks.append(f"{primary} has {child}")
                if "_rels/.rels" not in trees:
                    errors.append("_rels/.rels is missing or unreadable; nothing tells a consumer "
                                  "where the document starts")
                else:
                    root_rel_root = trees["_rels/.rels"]
                    office = [
                        r for r in root_rel_root.findall(f'{{{NS["pr"]}}}Relationship')
                        if r.get("Type") == RT["officeDocument"]
                    ]
                    if not office:
                        errors.append("_rels/.rels declares no officeDocument relationship")
                    elif posixpath.normpath(office[0].get("Target", "").lstrip("/")) != primary:
                        errors.append(f"_rels/.rels points the officeDocument relationship at "
                                      f"{office[0].get('Target')!r}, not {primary}")
                    else:
                        checks.append(f"_rels/.rels points officeDocument at {primary}")

        if kind == "pptx":
            slides = sorted(n for n in names if re.fullmatch(r"ppt/slides/slide\d+\.xml", n))
            if not slides:
                errors.append("the deck has no slides")
            for slide in slides:
                slide_root = trees.get(slide)
                if slide_root is None:
                    continue  # its parse failure is already reported above
                if slide_root.tag != _qname("p:sld"):
                    errors.append(f"{slide} root is {slide_root.tag}, expected {_qname('p:sld')}")
                elif slide_root.find(f'{_qname("p:cSld")}/{_qname("p:spTree")}') is None:
                    errors.append(f"{slide} has no p:cSld/p:spTree, so it has no shapes")
            checks.append(f"{len(slides)} slide(s) carry a shape tree")

    report["ok"] = not errors
    return report
