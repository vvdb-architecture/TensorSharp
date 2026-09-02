#!/usr/bin/env python3
"""Write an .xlsx from a JSON spec, with every formula's value computed here.

    make_xlsx.py --spec spec.json --out book.xlsx
    make_xlsx.py --spec - --out book.xlsx         # spec on stdin

The spec is documented in SKILL.md. On success this prints a JSON report to
stdout listing every formula cell and the value it cached, and it reopens the
workbook with `data_only=True` to prove the cached values are readable before
saying it succeeded.

The point of this script, and the thing openpyxl does not do on its own: a
formula cell is written with BOTH the formula and a cached value. openpyxl
writes `<f>SUM(B2:B9)</f>` and no `<v>`, which is correct only if something
recalculates the sheet afterwards. Nothing on this device can: there is no Excel
and no LibreOffice, which is exactly why the published xlsx skill cannot be
bundled here. So the values are evaluated by scripts/sheetcalc.py and injected
into the sheet XML, and `fullCalcOnLoad` is left set so a real Excel still
recomputes everything the moment the user opens the file.

A formula sheetcalc cannot evaluate is reported, loudly, and its cell goes into
the file with the formula and no cached value -- never with a made-up zero.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import zipfile

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

import analyze_table
import specs
from sheetcalc import Evaluator, FormulaError, column_index, expand_range, parse_ref, ref_name

HEADER_FILL = PatternFill("solid", fgColor="DEEAF6")
HEADER_FONT = Font(bold=True, color="1F3864")
THIN = Side(style="thin", color="9AA0A6")
BORDER = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)

# openpyxl emits `<c r="D2" s="3"><f>B2*C2</f><v/></c>` -- an EMPTY value
# element, which is the whole problem: a reader finds a `<v>`, reads nothing out
# of it, and reports the cell as blank. Cells never nest, so matching one open
# tag to the next close tag is unambiguous.
CELL_ELEMENT = re.compile(rb"<c\b([^>]*)>(.*?)</c>", re.S)
CELL_REF_ATTR = re.compile(rb'\br="([A-Z]{1,3}[0-9]{1,7})"')
TYPE_ATTR = re.compile(rb'\st="[^"]*"')
EMPTY_VALUE = re.compile(rb"<v\s*/>|<v\s*>\s*</v>")


def xml_escape(text: str) -> bytes:
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")).encode("utf-8")


def serialise(value: object) -> "tuple[bytes, bytes] | None":
    """A cached value as (type attribute, `<v>` body), or None if it cannot be cached."""
    if value is None:
        return None
    if isinstance(value, bool):
        return b' t="b"', b"1" if value else b"0"
    if isinstance(value, (int, float)):
        if isinstance(value, float) and (value != value or value in (float("inf"), float("-inf"))):
            return None
        return b"", repr(float(value)).encode("ascii") if isinstance(value, float) else str(value).encode("ascii")
    # t="str" is the type for a formula that returns text; t="s" would mean an
    # index into the shared string table, which a cached value is not.
    return b' t="str"', xml_escape(str(value))


def inject_cached_values(path: str, values_by_sheet: dict) -> int:
    """Rewrite the sheet parts so every formula cell also carries its value.

    A zip cannot be edited in place, so the package is rebuilt entry by entry
    with the sheet XML substituted; everything else is copied byte for byte.
    """
    with zipfile.ZipFile(path) as source:
        entries = [(item, source.read(item.filename)) for item in source.infolist()]

    injected = 0
    rewritten = []
    for item, data in entries:
        values = values_by_sheet.get(item.filename)
        if values:
            data, count = _inject(data, values)
            injected += count
        rewritten.append((item, data))

    temporary = path + ".tmp"
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as target:
        for item, data in rewritten:
            target.writestr(item, data)
    shutil.move(temporary, path)
    return injected


def _inject(data: bytes, values: dict) -> "tuple[bytes, int]":
    count = 0

    def replace(match: "re.Match") -> bytes:
        nonlocal count
        attrs, inner = match.group(1), match.group(2)
        if b"<f" not in inner:
            return match.group(0)
        inner = EMPTY_VALUE.sub(b"", inner)
        if b"<v" in inner:
            # Something already cached a real value here; do not overwrite it.
            return match.group(0)
        ref_match = CELL_REF_ATTR.search(attrs)
        if not ref_match:
            return match.group(0)
        ref = ref_match.group(1).decode("ascii")
        if ref not in values:
            return match.group(0)
        serialised = serialise(values[ref])
        if serialised is None:
            return match.group(0)
        type_attr, body = serialised
        attrs = TYPE_ATTR.sub(b"", attrs)
        count += 1
        return b"<c" + attrs + type_attr + b">" + inner + b"<v>" + body + b"</v></c>"

    return CELL_ELEMENT.sub(replace, data), count


def apply_column_widths(sheet, columns: list, rows: list, explicit: dict) -> None:
    """Size columns to their content, because a phone screen has no room for
    a column of ####."""
    width = len(columns) if columns else (len(rows[0]) if rows else 0)
    for index in range(1, width + 1):
        letter = get_column_letter(index)
        if letter in explicit:
            sheet.column_dimensions[letter].width = float(explicit[letter])
            continue
        widest = len(str(columns[index - 1])) if index <= len(columns) else 0
        for row in rows[:200]:
            if index <= len(row):
                widest = max(widest, len(f"{row[index - 1]:,.2f}" if isinstance(row[index - 1], float)
                                        else str(row[index - 1] if row[index - 1] is not None else "")))
        sheet.column_dimensions[letter].width = min(max(widest + 3, 9), 48)


def build(spec: dict, out_path: str) -> dict:
    sheets_spec = list(spec.get("sheets", []))
    if not sheets_spec:
        raise SystemExit("make_xlsx: the spec has no 'sheets'")

    workbook = Workbook()
    workbook.remove(workbook.active)
    # Cached values are for readers that do not calculate. Excel is not one of
    # them, and it should recompute rather than trust what this wrote.
    workbook.calculation.fullCalcOnLoad = True

    per_sheet: dict = {}
    failures: list = []
    summary: list = []

    for order, sheet_spec in enumerate(sheets_spec):
        name = str(sheet_spec.get("name") or f"Sheet{order + 1}")
        sheet = workbook.create_sheet(title=name[:31])
        columns = list(sheet_spec.get("columns", []))
        rows = [list(r) for r in sheet_spec.get("rows", [])]

        model: dict = {}
        row_cursor = 1
        if columns:
            for index, heading in enumerate(columns, start=1):
                cell = sheet.cell(row=1, column=index, value=heading)
                cell.fill = HEADER_FILL
                cell.font = HEADER_FONT
                cell.border = BORDER
                cell.alignment = Alignment(horizontal="center", vertical="center")
                model[(1, index)] = heading
            row_cursor = 2
        # A string beginning with "=" inside `rows` IS a formula: openpyxl writes it
        # to the file as one. Counting only the separate `formulas` array meant such a
        # cell was written with no cached value while the run reported success, and
        # every reader that trusts cached values -- including this skill's own
        # analyze_table.py -- then saw it as zero. They are collected here and go
        # through exactly the same evaluate-and-cache path as a declared formula.
        inline: dict = {}
        for row in rows:
            for index, value in enumerate(row, start=1):
                if isinstance(value, str) and value.startswith("=") and len(value) > 1:
                    inline[(row_cursor, index)] = value
                else:
                    sheet.cell(row=row_cursor, column=index, value=value).border = BORDER
                model[(row_cursor, index)] = value
            row_cursor += 1

        # Formulas go into the model before anything is evaluated, so one formula
        # may refer to another regardless of the order they are declared in.
        declared: dict = dict(inline)
        for entry in sheet_spec.get("formulas", []):
            formula = str(entry.get("formula", ""))
            if not formula.startswith("="):
                raise SystemExit(f"make_xlsx: formula {formula!r} on sheet {name!r} must start with '='")
            if entry.get("range"):
                targets = expand_range(str(entry["range"]))
            elif entry.get("cell"):
                targets = [parse_ref(str(entry["cell"]))]
            else:
                raise SystemExit(f"make_xlsx: a formula entry on sheet {name!r} needs 'cell' or 'range'")
            for ref in targets:
                # {r} and {c} let one entry fill a column without repeating it
                # per row, which is what a generated sheet almost always wants.
                text = formula.replace("{r}", str(ref.row)).replace("{c}", ref_name(ref)[:-len(str(ref.row))])
                declared[(ref.row, ref.col)] = text
                model[(ref.row, ref.col)] = text

        evaluator = Evaluator(model)
        cached: dict = {}
        for (row, column), formula in sorted(declared.items()):
            address = f"{get_column_letter(column)}{row}"
            sheet.cell(row=row, column=column, value=formula).border = BORDER
            try:
                cached[address] = evaluator.value_of(parse_ref(address))
            except FormulaError as exc:
                failures.append({"sheet": name, "cell": address, "formula": formula, "reason": str(exc)})

        for letter, number_format in (sheet_spec.get("number_formats") or {}).items():
            index = column_index(letter)
            for row in range(2 if columns else 1, row_cursor):
                sheet.cell(row=row, column=index).number_format = number_format
            for (frow, fcol) in declared:
                if fcol == index:
                    sheet.cell(row=frow, column=fcol).number_format = number_format

        apply_column_widths(sheet, columns, rows, sheet_spec.get("column_widths") or {})
        if columns and sheet_spec.get("freeze_header", True):
            sheet.freeze_panes = "A2"
        if columns and sheet_spec.get("auto_filter", True) and rows:
            sheet.auto_filter.ref = f"A1:{get_column_letter(len(columns))}{row_cursor - 1}"

        # openpyxl names sheet parts in creation order, which is the order here.
        per_sheet[f"xl/worksheets/sheet{order + 1}.xml"] = cached
        summary.append({"name": name, "rows": len(rows), "columns": len(columns), "formulas": len(declared)})

    workbook.save(out_path)
    injected = inject_cached_values(out_path, per_sheet)

    # Prove it: data_only=True returns the cached value and nothing else, so a
    # number here means a reader that does not calculate will see one too.
    verify = load_workbook(out_path, data_only=True)
    read_back: dict = {}
    for order, sheet_spec in enumerate(sheets_spec):
        cached = per_sheet[f"xl/worksheets/sheet{order + 1}.xml"]
        sheet = verify.worksheets[order]
        read_back[sheet.title] = {address: sheet[address].value for address in sorted(cached)}
    verify.close()

    return {
        "file": os.path.abspath(out_path),
        "bytes": os.path.getsize(out_path),
        "sheets": summary,
        "formula_cells": sum(len(v) for v in per_sheet.values()),
        "cached_values_written": injected,
        "cached_values_read_back": read_back,
        "uncomputable_formulas": failures,
        "recalculates_on_open": True,
    }


def spec_from_csv(path: str, sheet_name: "str | None", total_columns: list) -> dict:
    """A one-sheet spec holding the CSV as it stands.

    The gap this closes: "turn this CSV into a spreadsheet" is the commonest
    thing anyone asks a document tool, and until this existed the skill had no
    answer to it. make_xlsx wanted a JSON spec the caller had to write by hand
    from data it had not read, and analyze_table refuses --out on a workbook
    without --group-by because a column profile is not a table. So a model
    asking the obvious question was told no by both scripts and gave up. This
    is the missing third answer: the file, as a workbook.

    Cells that read as numbers are written as numbers rather than as text,
    because a column of numerals stored as strings does not sum, and a
    spreadsheet whose totals cannot be taken is not a spreadsheet.
    """
    header, rows = analyze_table.read_csv(path, None)
    if not header:
        raise SystemExit(f"make_xlsx: {path} has no header row, so there are no columns to write")

    typed = []
    for row in rows:
        # A short row is padded rather than dropped: a trailing empty field is
        # missing data, not a broken file, and losing the row loses the rest of it.
        padded = list(row) + [None] * (len(header) - len(row))
        number_of = analyze_table.as_number
        typed.append([
            cell if (value := number_of(cell)) is None
            else (int(value) if float(value).is_integer() else value)
            for cell in padded[:len(header)]
        ])

    sheet = {
        "name": sheet_name or os.path.splitext(os.path.basename(path))[0][:31] or "Sheet1",
        "columns": [str(name) for name in header],
        "rows": typed,
    }

    # A totals row is a formula, not a number this script works out and writes
    # down: the point of the whole file is that the reader can see where the
    # figure came from, and the cached value is filled in by the evaluator that
    # every other formula here goes through.
    if total_columns:
        total_row = len(typed) + 2
        formulas = []
        for name in total_columns:
            index = analyze_table.resolve(header, name)
            letter = get_column_letter(index + 1)
            formulas.append({
                "cell": f"{letter}{total_row}",
                "formula": f"=SUM({letter}2:{letter}{total_row - 1})",
            })
        sheet["formulas"] = formulas

    return {"sheets": [sheet]}


def main() -> int:
    parser = argparse.ArgumentParser(description="Write an .xlsx from a JSON spec, or straight from a CSV.")
    parser.add_argument("--spec", help="path to the JSON spec, or - for stdin")
    parser.add_argument("--csv", help="a .csv to write as a workbook as it stands, instead of --spec")
    parser.add_argument("--sheet-name", help="the sheet name when using --csv (default: the file's name)")
    parser.add_argument("--total", action="append", default=[],
                        help="with --csv, add a SUM row for this column; repeatable")
    parser.add_argument("--out", required=True, help="path of the .xlsx to write")
    args = parser.parse_args()

    if bool(args.spec) == bool(args.csv):
        raise SystemExit("make_xlsx: pass exactly one of --spec (a JSON spec) or --csv (a file to convert)")

    if args.csv:
        spec = spec_from_csv(args.csv, args.sheet_name, args.total)
    else:
        spec = specs.load(args.spec, "make_xlsx")
        specs.check_keys(spec, "make_xlsx", {"title", "author", "subject", "sheets"}, "sheets")
    result = build(spec, args.out)
    print(json.dumps(result, indent=2, default=str))
    if result["uncomputable_formulas"]:
        print(f"make_xlsx: {len(result['uncomputable_formulas'])} formula(s) went into the file with no cached "
              "value; a reader that does not recalculate will see them empty. See 'uncomputable_formulas'.",
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
