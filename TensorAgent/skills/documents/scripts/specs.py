#!/usr/bin/env python3
"""Reading the JSON spec the four writers take, and failing legibly when it is wrong.

A model produces these specs, and the two ways it gets one wrong -- naming a file
that is not there, and emitting JSON with a trailing comma in it -- both surface
as a Python traceback if nobody catches them. A traceback is a poor error: the
model that has to fix the spec has to read the interpreter's stack to find out
that its problem is a missing comma on line 6.
"""

from __future__ import annotations

import json
import sys


def load(source: str, tool: str) -> dict:
    """The spec at `source` (or stdin for "-"), as a dict, or exit saying why not."""
    try:
        if source == "-":
            raw = sys.stdin.read()
        else:
            with open(source, encoding="utf-8") as handle:
                raw = handle.read()
    except OSError as exc:
        raise SystemExit(f"{tool}: cannot read the spec {source!r}: {exc.strerror}")
    except UnicodeDecodeError:
        raise SystemExit(f"{tool}: the spec {source!r} is not UTF-8 text; it has to be a JSON file")

    if not raw.strip():
        raise SystemExit(f"{tool}: the spec {source!r} is empty")

    try:
        spec = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise SystemExit(f"{tool}: the spec is not valid JSON — {exc.msg} at line {exc.lineno}, "
                         f"column {exc.colno}")

    if not isinstance(spec, dict):
        raise SystemExit(f"{tool}: the spec has to be a JSON object, not a {type(spec).__name__}")
    return spec


def check_keys(spec: dict, tool: str, known: set, required: str) -> None:
    """Refuse a spec whose keys this writer does not understand.

    The third way a model gets a spec wrong is the one that used to be silent: it
    invents a plausible shape. A document spec with `sections` instead of `blocks`
    parses, validates, and produces a file containing only its title -- exit 0, no
    warning, and a model that has no way to know the work did not happen. Naming the
    unknown key and the accepted ones turns that into one correctable message.
    """
    unknown = sorted(k for k in spec if k not in known)
    if unknown:
        raise SystemExit(
            f"{tool}: the spec has {'a key' if len(unknown) == 1 else 'keys'} this writer does "
            f"not understand: {', '.join(repr(k) for k in unknown)}. "
            f"It accepts: {', '.join(sorted(known))}."
        )
    if required not in spec or not spec[required]:
        raise SystemExit(
            f"{tool}: the spec has no {required!r}, so there is nothing to write. "
            f"It accepts: {', '.join(sorted(known))}."
        )
