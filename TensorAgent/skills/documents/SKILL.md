---
name: documents
description: Read and write real documents on the device - PDF, XLSX, DOCX, PPTX and CSV. Turn a photo or picture into a PDF, generate a PDF report or a slide deck from structured input, build a spreadsheet whose formulas carry computed values, extract text and tables out of a PDF or an Office file, and compute sums, means and groupings over a CSV or XLSX. Use whenever the user asks to convert an image to a PDF, to analyse a document or a table, or to produce a report, deck, spreadsheet or Word document.
---

# Documents

Six scripts. Every one runs under the interpreter bundled in this app, with the
packages that are already there: reportlab, pypdf, openpyxl, Pillow and
defusedxml, plus the standard library. Nothing here needs the network, a child
process, or a package the user has to install. python-pptx, python-docx and
lxml are bundled too (see **Limits**) for the things these writers do not do.

Read **Limits** before promising the user anything. Two of them change what you
should say in your answer.

## Choosing a script

| The user wants | Run |
| --- | --- |
| A photo or a picture turned into a PDF | `make_pdf.py --image` |
| A report, a memo, anything to print or share as a PDF | `make_pdf.py --spec` |
| A CSV turned into a spreadsheet, as it stands | `make_xlsx.py --csv` |
| A spreadsheet built from figures you computed | `make_xlsx.py --spec` |
| A slide deck | `make_pptx.py` |
| A Word document | `make_docx.py` |
| Numbers out of a CSV or spreadsheet | `analyze_table.py` |
| Text or tables out of a document they gave you | `read_document.py` |
| To check a file you produced | `validate_document.py` |

A typical request — "analyse this spreadsheet and make me a deck" — is
`analyze_table.py` to get the numbers, then `make_pptx.py` with those numbers in
the spec. Do not put a number in a document that you did not compute.

**Starting from a CSV.** Three different requests, three different answers, and
picking the wrong one wastes the turn:

* *"Turn this CSV into a spreadsheet"* — one command:
  `make_xlsx.py --csv sales.csv --out sales.xlsx`. Do not build a spec by hand
  for this, and do not reach for `analyze_table.py`: it profiles columns and
  refuses `--out` on a workbook without `--group-by`, because a profile is not
  a table.
* *"Total the revenue in this CSV"* — `analyze_table.py`, which computes it.
  Add `--total revenue` to the command above if you want the workbook to carry
  the sum as a live formula as well.
* *"Make a PDF/deck/Word report of this CSV"* — there is no one-shot for this,
  because a report is a document you design rather than a table you convert.
  Read the file (`read_document.py`, or the shell), then write a spec whose
  `table` block holds those rows, then run `make_pdf.py` / `make_pptx.py` /
  `make_docx.py`. Put the real rows in the spec; do not summarise them away.

## make_pdf.py — a PDF report, or a picture

```
python3 scripts/make_pdf.py --image photo.jpg --out photo.pdf
python3 scripts/make_pdf.py --image a.png --image b.png --out album.pdf --title Album
python3 scripts/make_pdf.py --spec spec.json --out report.pdf
python3 scripts/make_pdf.py --spec - --out report.pdf     # spec on stdin
```

**"Turn this photo into a PDF" is `--image`, and it is one command.** Do not write
a spec for it. The user's attached files are already in your working directory
under the names you were told — pass that name straight to `--image`. Each picture
gets a page of its own, scaled to fit with its proportions kept; `--title` adds a
title page. Pass either `--image` or `--spec`, never both.

An iPhone photo is HEIC, and the interpreter here has no HEIC decoder. The app
stages a `.png` beside it for exactly this reason — if you were told about both
`IMG_0004.heic` and `IMG_0004.png`, the `.png` is the one to pass.

Writes the PDF, reopens it with pypdf, and prints JSON: `file`, `bytes`,
`pages`, `blocks`, `extractable_characters`. Non-zero exit means it failed.

```json
{
  "title": "Q3 Revenue Review",
  "subtitle": "Prepared on device",
  "author": "TensorAgent",
  "page_size": "letter",
  "landscape": false,
  "margin_in": 0.9,
  "blocks": [
    { "type": "heading", "level": 1, "text": "Summary" },
    { "type": "paragraph", "text": "Revenue rose 18%.", "mono": false },
    { "type": "bullets", "items": ["South overtook East", "Costs flat"] },
    { "type": "numbers", "items": ["Load the CSV", "Group by region"] },
    { "type": "table",
      "columns": ["Region", "Units", "Revenue"],
      "rows": [["West", 120, 420.0], ["East", 200, 700.0]],
      "column_widths": [2, 1, 1],
      "zebra": true },
    { "type": "image", "path": "chart.png", "width_in": 5.0, "caption": "Figure 1." },
    { "type": "pagebreak" },
    { "type": "spacer", "height": 12 }
  ]
}
```

`page_size` is `letter` or `a4`. `level` is 1-3. Numeric table cells are
right-aligned and thousands-separated automatically; the header row repeats when
a table crosses a page. `column_widths` are relative, not absolute. An unknown
block type is an error, not a skipped block.

There is no chart type. Draw one with Pillow into a `.png` and add it as an
`image` block — that is what the image block is for.

## make_xlsx.py — a workbook whose formulas show their numbers

```
python3 scripts/make_xlsx.py --csv sales.csv --out sales.xlsx
python3 scripts/make_xlsx.py --csv sales.csv --out sales.xlsx --total revenue
python3 scripts/make_xlsx.py --spec spec.json --out book.xlsx
```

`--csv` writes the file as it stands: the header becomes the columns, and a
cell that reads as a number is stored as one, so the column can be summed.
`--sheet-name` names the sheet (default: the file's own name), and `--total COL`
adds a SUM row for that column — a real formula with its value cached, like
every other formula here. It is repeatable. Everything below is about `--spec`,
which is what you use when the figures are ones you computed rather than ones
already in a file.

**Read this part.** A formula in an .xlsx is only text; the number a reader sees
is the *cached* value left behind by the last application that saved the file.
There is no Excel and no LibreOffice on this device, so nothing will recalculate
the sheet after you write it. This script therefore evaluates every formula in
Python and writes the value into the cell **alongside** the formula. The file
also carries `fullCalcOnLoad`, so a real Excel recomputes everything anyway the
moment the user opens it.

If a formula cannot be evaluated here, the cell gets the formula and **no**
cached value, the script lists it under `uncomputable_formulas`, and it exits
non-zero. Tell the user which cells those are; do not present the file as
finished.

```json
{
  "sheets": [
    {
      "name": "Sales",
      "columns": ["Region", "Units", "Price", "Revenue"],
      "rows": [["West", 120, 3.5, null], ["East", 200, 3.5, null]],
      "formulas": [
        { "range": "D2:D3", "formula": "=B{r}*C{r}" },
        { "cell": "D4", "formula": "=SUM(D2:D3)" },
        { "cell": "D5", "formula": "=ROUND(AVERAGE(D2:D3),2)" }
      ],
      "number_formats": { "C": "#,##0.00", "D": "#,##0.00" },
      "column_widths": { "A": 18 },
      "freeze_header": true,
      "auto_filter": true
    }
  ]
}
```

`range` fills a block of cells from one entry: `{r}` becomes the row number and
`{c}` the column letter. Header cells are styled, columns are sized to content,
and a formula may refer to another formula cell in any order.

Functions the evaluator implements: `SUM AVERAGE AVG MEDIAN MIN MAX COUNT COUNTA
STDEV PRODUCT ABS SQRT INT ROUND POWER LEN UPPER LOWER TRIM CONCAT CONCATENATE IF
AND OR NOT`, the operators `+ - * / ^ % &`, comparisons, and A1 ranges. It does
**not** implement lookups (`VLOOKUP`, `INDEX`/`MATCH`), whole-column ranges
(`A:B`), cross-sheet references or dates. Compute those in Python and write the
result as a value.

## make_pptx.py — a slide deck

```
python3 scripts/make_pptx.py --spec spec.json --out deck.pptx
```

16:9. Five slide layouts; every slide may have a `title`.

```json
{
  "title": "Q3 Revenue Review",
  "author": "TensorAgent",
  "slides": [
    { "layout": "title", "title": "Q3 Revenue Review", "subtitle": "Prepared on device" },
    { "layout": "bullets", "title": "What happened",
      "bullets": ["South overtook East", { "text": "since 2023", "level": 1 }] },
    { "layout": "table", "title": "By region",
      "columns": ["Region", "Revenue"], "rows": [["West", 420.0]] },
    { "layout": "image", "title": "Revenue", "image": "chart.png", "caption": "Figure 1." },
    { "layout": "text", "title": "Next", "text": "Recheck the North price." }
  ]
}
```

Bullets nest to three levels via `level`. The common tree form
`{ "text": "Heading", "bullets": ["Detail"] }` is also accepted and flattened
to those levels, so do not rewrite an otherwise-correct spec just to expand it.
A citation bullet may add `date` and `url` beside `text`; the writer joins all
three visibly on the slide. Images are scaled to fit and centred.
A table slide holds about 12 rows before it runs off the bottom — split a longer
one across slides. `notes` is refused rather than silently dropped: speaker
notes need a notesMaster part this writer does not build.

Prefer the canonical fields in the example. For compatibility with common deck
specs, a slide with no `layout` is a bullets slide and its `content` array is an
alias for `bullets` (do not provide both). An optional top-level `sources` array
accepts URL strings or `{ "title": "...", "date": "...", "url": "..." }`
objects and becomes a final Sources slide. For compatibility, the same
`sources` array on an individual slide is collected into that one final Sources
slide; a slide whose only body is `sources` acts as that final-slide placeholder
instead of creating an additional empty slide. A `text` slide that also contains a `bullets` array is unambiguously
normalized to a bullets slide, with its introductory `text` retained first.
Unknown fields, ambiguous aliases, and layouts without visible required content
fail before a `.pptx` is written; the diagnostic names the slide and the fields
accepted by its layout.

## make_docx.py — a Word document

```
python3 scripts/make_docx.py --spec spec.json --out report.docx
```

Same block vocabulary as `make_pdf.py`, minus `spacer`:

```json
{
  "title": "Q3 Revenue Review",
  "subtitle": "Prepared on device",
  "author": "TensorAgent",
  "blocks": [
    { "type": "heading", "level": 2, "text": "By region" },
    { "type": "paragraph", "text": "Revenue rose 18%.", "bold": false, "italic": false, "align": "left" },
    { "type": "bullets", "items": ["Top level", { "text": "Nested", "level": 1 }] },
    { "type": "numbers", "items": ["First", "Second"] },
    { "type": "table", "columns": ["Region", "Revenue"], "rows": [["West", 420.0]] },
    { "type": "image", "path": "chart.png", "width_in": 5.0, "caption": "Figure 1." },
    { "type": "pagebreak" }
  ]
}
```

Real Word styles (Title, Heading 1-3, Caption, Table Grid) and real list
numbering, so the document's outline and its lists behave as Word's own.

## analyze_table.py — numbers out of a CSV or XLSX

```
python3 scripts/analyze_table.py sales.csv
python3 scripts/analyze_table.py sales.csv --group-by Region --sum Revenue --mean Price
python3 scripts/analyze_table.py book.xlsx --sheet Q3 --group-by Region --sum Revenue --out summary.xlsx
python3 scripts/analyze_table.py sales.csv --where "Units>=12" --where "Region=West" --sum Revenue --group-by Rep
```

| Option | Effect |
| --- | --- |
| `--sheet NAME` | which sheet of an .xlsx (default: the first) |
| `--delimiter C` | CSV delimiter (default: sniffed) |
| `--where "Col=v"` | keep matching rows; `=` `!=` `>` `<` `>=` `<=`; repeatable |
| `--group-by COL` | aggregate per distinct value of this column |
| `--sum COL` | total this column per group; repeatable |
| `--mean COL` | average this column per group; repeatable |
| `--out FILE` | also write `.json`, `.csv` or `.xlsx` |

With no `--group-by` it profiles every column: filled/empty counts, distinct
values, and for numeric columns count, sum, mean, median, min, max and stdev;
for text columns the five commonest values. JSON always goes to stdout.

`$1,240.00`, `1 240`, `45%` and `(320)` all read as numbers. A blank cell is not
zero: it is left out of sums and means, and `rows_dropped_not_a_number` counts
the rows an ordering filter could not judge. Say that number out loud if it is
not zero — it usually is the finding.

`formula_cells_without_cached_values` is the other one to watch. Reading an
.xlsx gives you the value the file has *cached*, so a workbook whose totals are
formulas that nothing ever calculated reads as blank and counts as nothing. When
that number is not zero, say so rather than reporting a total built out of holes.

## read_document.py — text and tables out of a document

```
python3 scripts/read_document.py report.pdf --tables --json extracted.json
python3 scripts/read_document.py notes.docx
python3 scripts/read_document.py deck.pptx --json deck.json
python3 scripts/read_document.py book.xlsx --sheet Q3
```

Readable text on stdout; `--json` also writes the structured form, which is what
a follow-up script should consume.

* **.pdf** — pypdf in *layout* mode, which keeps each glyph near its column
  instead of reflowing the page. `--tables` then finds runs of lines whose
  fields start at the same character positions. It is a heuristic: it finds the
  tables a report lays out in aligned columns, misses ones drawn with borders
  and no alignment, and sometimes calls a numbered list a table. Look at what it
  returned before quoting it.
* **.docx** — paragraphs (with their style names, so headings are identifiable)
  and tables as rows of cells.
* **.pptx** — text per slide, in slide order.
* **.xlsx** — values via openpyxl. Formula cells hold whatever value was cached
  in the file; a workbook written by a tool that caches none reads as empty
  there. `make_xlsx.py` caches them, which is the point of it.

## validate_document.py — check what you produced

```
python3 scripts/validate_document.py report.docx deck.pptx book.xlsx report.pdf
python3 scripts/validate_document.py deck.pptx --json checks.json
```

Prints PASS/FAIL per file and exits non-zero if any failed. `make_pdf.py`,
`make_xlsx.py`, `make_docx.py` and `make_pptx.py` already run this on their own
output; use it on a file you assembled by hand or edited afterwards.

## Limits

**No Office application has opened these files.** There is no Word, PowerPoint
or Excel on this device, and no headless converter — LibreOffice, which the
published document skills shell out to for exactly this check, cannot be bundled
and could not be launched if it were, because iOS starts no subprocesses. What
`validate_document.py` checks is that the package is internally consistent:
every part `[Content_Types].xml` names exists, every part has a content type,
every relationship resolves, every XML part parses, and the primary part has the
root element and children its kind requires. That is a real check and it catches
the mistakes that make a file unopenable — but a PASS means well formed, not
opened. If the user reports that a file will not open, believe them.

**python-docx and python-pptx ARE here, and so is lxml.** Both import
`lxml.etree` at module scope, and lxml is a C extension that no index publishes
for iOS — so this app compiles it itself and ships it as signed frameworks,
alongside `python-pptx`, `python-docx`, `XlsxWriter` and `typing_extensions`.
Import them directly; `pip install` of any of them by name is a no-op. lxml is
compiled into the app and cannot be replaced by a download; the pure ones could
be shadowed by a pinned install, but nothing here needs that. The writers above
still build the OOXML
themselves with `zipfile` and `xml.etree` (they predate this, and they validate
their own output), which is why their feature set is the list above and not
everything Word can do. For what they lack — charts, speaker notes, headers and
footers, editing a document the user gave you — use python-pptx or python-docx
and run `validate_document.py` on the result, which checks their output the
same way.

**pdfplumber is not here.** It needs pdfminer.six, which imports
`cryptography` at the top of `pdfminer/pdfdocument.py` in every release back to
2022, and `cryptography` is a Rust extension with no pure wheel of any kind.
That is why PDF reading is pypdf's layout mode and the table finder is a
heuristic rather than a ruling-line analysis. An encrypted PDF that needs a real
password cannot be opened here at all.

**Spreadsheet formulas are evaluated by a subset.** See the function list under
`make_xlsx.py`. Anything outside it is refused by name and reported, never
guessed at.
