#!/usr/bin/env python3
"""A small spreadsheet formula evaluator, and the A1 address arithmetic it needs.

Why a spreadsheet needs an evaluator on this device: a formula in an .xlsx is
only text. The number a reader sees is the *cached* value the last application
to save the file left behind, and openpyxl writes no cached value at all. There
is no Excel here and no LibreOffice -- the whole reason the published xlsx skill
cannot be bundled -- so if this app writes `=SUM(B2:B9)` and stops there, the
user's spreadsheet reads zero, or blank, until they open it somewhere else.

So the numbers are computed in Python and written alongside the formulas. This
module is that computation. It is a deliberately small subset of the function
library, and it raises `FormulaError` naming the formula rather than returning a
plausible-looking zero for something it did not understand.
"""

from __future__ import annotations

import math
import re
import statistics
from dataclasses import dataclass

CELL_REF = re.compile(r"^\$?([A-Za-z]{1,3})\$?([0-9]{1,7})$")
TOKEN = re.compile(
    r"""\s*(?:
        (?P<range>\$?[A-Za-z]{1,3}\$?[0-9]{1,7}\s*:\s*\$?[A-Za-z]{1,3}\$?[0-9]{1,7})
      | (?P<ref>\$?[A-Za-z]{1,3}\$?[0-9]{1,7})(?![A-Za-z0-9_(])
      | (?P<func>[A-Za-z][A-Za-z0-9_.]*)\s*(?=\()
      | (?P<name>[A-Za-z][A-Za-z0-9_.]*)
      | (?P<number>[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)
      | (?P<string>"(?:[^"]|"")*")
      | (?P<op><>|<=|>=|[-+*/^&<>=(),%])
    )""",
    re.VERBOSE,
)


class FormulaError(Exception):
    """A formula this evaluator cannot compute, with the reason in the message."""


@dataclass(frozen=True)
class Ref:
    row: int
    col: int


def column_index(letters: str) -> int:
    """'A' -> 1, 'AA' -> 27."""
    value = 0
    for char in letters.upper():
        value = value * 26 + (ord(char) - 64)
    return value


def column_letters(index: int) -> str:
    letters = ""
    while index > 0:
        index, remainder = divmod(index - 1, 26)
        letters = chr(65 + remainder) + letters
    return letters


def parse_ref(text: str) -> Ref:
    match = CELL_REF.match(text.strip())
    if not match:
        raise FormulaError(f"{text!r} is not an A1 cell reference")
    return Ref(int(match.group(2)), column_index(match.group(1)))


def ref_name(ref: Ref) -> str:
    return f"{column_letters(ref.col)}{ref.row}"


def expand_range(text: str) -> list:
    left, _, right = text.partition(":")
    start, end = parse_ref(left), parse_ref(right)
    rows = range(min(start.row, end.row), max(start.row, end.row) + 1)
    cols = range(min(start.col, end.col), max(start.col, end.col) + 1)
    return [Ref(r, c) for r in rows for c in cols]


def _numbers(values: list) -> list:
    return [v for v in values if isinstance(v, (int, float)) and not isinstance(v, bool)]


def _one(values: list, name: str) -> object:
    if len(values) != 1:
        raise FormulaError(f"{name} takes exactly one argument, got {len(values)}")
    return values[0]


def _number(value: object, name: str) -> float:
    if isinstance(value, bool):
        return 1.0 if value else 0.0
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str) and value.strip():
        try:
            return float(value)
        except ValueError:
            pass
    raise FormulaError(f"{name} needs a number, got {value!r}")


def _text(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def _empty(name: str):
    """Refuse an aggregate that had no numbers to work with."""
    raise FormulaError(f"{name} had no numeric values to work with")


# Functions are (name -> callable over the flattened argument list). Anything not
# here is refused by name rather than approximated.
FUNCTIONS = {
    "SUM": lambda a: float(sum(_numbers(a))),
    # An aggregate over nothing has no value. Excel answers #DIV/0! or #NUM! here;
    # returning 0.0 would be cached into the sheet as a real number, and a reader has
    # no way to tell that zero from a measured one. Refusing puts the cell in
    # uncomputable_formulas, which is the honest half of make_xlsx's contract.
    "PRODUCT": lambda a: float(math.prod(_numbers(a))) if _numbers(a) else _empty("PRODUCT"),
    "AVERAGE": lambda a: statistics.fmean(_numbers(a)) if _numbers(a) else _empty("AVERAGE"),
    "AVG": lambda a: statistics.fmean(_numbers(a)) if _numbers(a) else _empty("AVG"),
    "MEDIAN": lambda a: statistics.median(_numbers(a)) if _numbers(a) else _empty("MEDIAN"),
    "MIN": lambda a: float(min(_numbers(a))) if _numbers(a) else _empty("MIN"),
    "MAX": lambda a: float(max(_numbers(a))) if _numbers(a) else _empty("MAX"),
    "COUNT": lambda a: float(len(_numbers(a))),
    "COUNTA": lambda a: float(len([v for v in a if v not in (None, "")])),
    "STDEV": lambda a: statistics.stdev(_numbers(a)) if len(_numbers(a)) > 1 else _empty("STDEV"),
    "ABS": lambda a: abs(_number(_one(a, "ABS"), "ABS")),
    "SQRT": lambda a: math.sqrt(_number(_one(a, "SQRT"), "SQRT")),
    "INT": lambda a: float(math.floor(_number(_one(a, "INT"), "INT"))),
    "LEN": lambda a: float(len(_text(_one(a, "LEN")))),
    "UPPER": lambda a: _text(_one(a, "UPPER")).upper(),
    "LOWER": lambda a: _text(_one(a, "LOWER")).lower(),
    "TRIM": lambda a: _text(_one(a, "TRIM")).strip(),
    "NOT": lambda a: not _truth(_one(a, "NOT")),
    "AND": lambda a: all(_truth(v) for v in a),
    "OR": lambda a: any(_truth(v) for v in a),
    "CONCAT": lambda a: "".join(_text(v) for v in a),
    "CONCATENATE": lambda a: "".join(_text(v) for v in a),
}


def _truth(value: object) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    return bool(value)


def _round(args: list) -> float:
    if not 1 <= len(args) <= 2:
        raise FormulaError(f"ROUND takes one or two arguments, got {len(args)}")
    digits = int(_number(args[1], "ROUND")) if len(args) == 2 else 0
    value = _number(args[0], "ROUND")
    # Excel rounds half away from zero; Python rounds half to even, so 2.5 would
    # become 2 and a total would be one off from the one the user expects.
    factor = 10.0 ** digits
    scaled = value * factor
    rounded = math.floor(abs(scaled) + 0.5) * (1 if scaled >= 0 else -1)
    return rounded / factor


class Evaluator:
    """Evaluates one sheet's formulas against its literal values.

    `cells` maps an (row, col) tuple to either a literal value or a formula
    string beginning with '='. Results are memoised, and a reference cycle is
    reported as one rather than recursing until the stack gives out.
    """

    def __init__(self, cells: dict):
        self.cells = cells
        self._cache: dict = {}
        self._active: set = set()

    def value_of(self, ref: Ref) -> object:
        key = (ref.row, ref.col)
        if key in self._cache:
            return self._cache[key]
        raw = self.cells.get(key)
        if isinstance(raw, str) and raw.startswith("="):
            if key in self._active:
                raise FormulaError(f"{ref_name(ref)} takes part in a circular reference")
            self._active.add(key)
            try:
                value = self.evaluate(raw)
            finally:
                self._active.discard(key)
        else:
            value = raw
        self._cache[key] = value
        return value

    def evaluate(self, formula: str) -> object:
        text = formula[1:] if formula.startswith("=") else formula
        # A whole-column range is the single most common thing a generated
        # formula reaches for, and the tokenizer's own message for it ("cannot
        # read ':B,2)'") tells the reader nothing about how to fix it.
        whole_column = re.search(r"\$?[A-Za-z]{1,3}\$?:\$?[A-Za-z]{1,3}(?![A-Za-z0-9])", text)
        if whole_column:
            raise FormulaError(
                f"the whole-column range {whole_column.group(0)!r} is not supported; "
                "write an explicit range such as A2:A500"
            )
        tokens = self._tokenize(text)
        self._tokens, self._position = tokens, 0
        value = self._comparison()
        if self._position != len(self._tokens):
            kind, raw = self._tokens[self._position]
            raise FormulaError(f"unexpected {raw!r} in {formula!r}")
        return value

    def _tokenize(self, text: str) -> list:
        tokens, position = [], 0
        while position < len(text):
            if text[position].isspace():
                position += 1
                continue
            match = TOKEN.match(text, position)
            if not match or match.end() == position:
                raise FormulaError(f"cannot read {text[position:position + 12]!r}")
            position = match.end()
            for kind in ("range", "ref", "func", "name", "number", "string", "op"):
                raw = match.group(kind)
                if raw is not None:
                    tokens.append((kind, raw))
                    break
        return tokens

    def _peek(self) -> tuple:
        return self._tokens[self._position] if self._position < len(self._tokens) else ("end", "")

    def _take(self) -> tuple:
        token = self._peek()
        self._position += 1
        return token

    def _expect(self, raw: str) -> None:
        kind, value = self._take()
        if value != raw:
            raise FormulaError(f"expected {raw!r}, found {value or 'end of formula'!r}")

    def _comparison(self) -> object:
        left = self._concat()
        kind, raw = self._peek()
        if kind == "op" and raw in ("=", "<>", "<", "<=", ">", ">="):
            self._take()
            right = self._concat()
            if raw == "=":
                return left == right
            if raw == "<>":
                return left != right
            a, b = _number(left, "comparison"), _number(right, "comparison")
            return {"<": a < b, "<=": a <= b, ">": a > b, ">=": a >= b}[raw]
        return left

    def _concat(self) -> object:
        value = self._sum()
        while self._peek() == ("op", "&"):
            self._take()
            value = _text(value) + _text(self._sum())
        return value

    def _sum(self) -> object:
        value = self._product()
        while self._peek()[0] == "op" and self._peek()[1] in "+-":
            _, raw = self._take()
            right = _number(self._product(), "arithmetic")
            value = _number(value, "arithmetic") + right if raw == "+" else _number(value, "arithmetic") - right
        return value

    def _product(self) -> object:
        value = self._unary()
        while self._peek()[0] == "op" and self._peek()[1] in "*/":
            _, raw = self._take()
            right = _number(self._unary(), "arithmetic")
            if raw == "/":
                if right == 0:
                    raise FormulaError("division by zero")
                value = _number(value, "arithmetic") / right
            else:
                value = _number(value, "arithmetic") * right
        return value

    def _unary(self) -> object:
        kind, raw = self._peek()
        if kind == "op" and raw in "+-":
            self._take()
            value = _number(self._unary(), "arithmetic")
            return -value if raw == "-" else value
        return self._power()

    def _power(self) -> object:
        value = self._postfix()
        if self._peek() == ("op", "^"):
            self._take()
            return _number(value, "arithmetic") ** _number(self._unary(), "arithmetic")
        return value

    def _postfix(self) -> object:
        value = self._atom()
        while self._peek() == ("op", "%"):
            self._take()
            value = _number(value, "arithmetic") / 100.0
        return value

    def _atom(self) -> object:
        kind, raw = self._take()
        if kind == "number":
            number = float(raw)
            return number
        if kind == "string":
            return raw[1:-1].replace('""', '"')
        if kind == "range":
            return [self.value_of(ref) for ref in expand_range(raw)]
        if kind == "ref":
            return self.value_of(parse_ref(raw))
        if kind == "name":
            upper = raw.upper()
            if upper == "TRUE":
                return True
            if upper == "FALSE":
                return False
            raise FormulaError(f"unknown name {raw!r}")
        if kind == "func":
            name = raw.upper()
            self._expect("(")
            args: list = []
            errors: list = []
            if self._peek() != ("op", ")"):
                args.append(self._parse_argument(errors))
                while self._peek() == ("op", ","):
                    self._take()
                    args.append(self._parse_argument(errors))
            self._expect(")")
            # IF is the one function whose arguments must not all have to succeed.
            # This evaluator computes as it parses, so `=IF(B2=0,0,C2/B2)` -- the
            # canonical divide-by-zero guard, and the reason anyone writes IF at all --
            # evaluated the division before it ever looked at the condition, and refused
            # the whole formula. The untaken branch's failure is carried instead of
            # raised, and only surfaces if that branch turns out to be the one wanted.
            if name == "IF":
                return self._if(args, errors)
            for failure in errors:
                if failure is not None:
                    raise failure
            return self._call(name, args)
        if kind == "op" and raw == "(":
            value = self._comparison()
            self._expect(")")
            return value
        raise FormulaError(f"unexpected {raw or 'end of formula'!r}")

    def _parse_argument(self, errors: list) -> object:
        """One argument, with a failure recorded rather than raised.

        The caller re-raises for every function except IF, so nothing else changes
        behaviour; IF alone gets to look at the condition first.
        """
        try:
            value = self._comparison()
            errors.append(None)
            return value
        except FormulaError as failed:
            errors.append(failed)
            return None

    def _if(self, args: list, errors: list) -> object:
        if not 2 <= len(args) <= 3:
            raise FormulaError("IF takes two or three arguments")
        if errors[0] is not None:
            raise errors[0]
        taken = 1 if _truth(args[0]) else 2
        if taken == 2 and len(args) == 2:
            return False
        if errors[taken] is not None:
            raise errors[taken]
        return args[taken]

    def _call(self, name: str, args: list) -> object:
        if name == "ROUND":
            return _round(args)
        if name == "POWER":
            if len(args) != 2:
                raise FormulaError("POWER takes two arguments")
            return _number(args[0], "POWER") ** _number(args[1], "POWER")
        if name not in FUNCTIONS:
            raise FormulaError(
                f"{name} is not one of the functions this evaluator implements "
                f"({', '.join(sorted(set(FUNCTIONS) | {'IF', 'ROUND', 'POWER'}))}); "
                "the formula would go into the file with no cached value"
            )
        flat: list = []
        for arg in args:
            flat.extend(arg) if isinstance(arg, list) else flat.append(arg)
        return FUNCTIONS[name](flat)
