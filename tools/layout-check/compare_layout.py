#!/usr/bin/env python3
"""Compare native sizeof/offsetof output with C# Marshal output."""

from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Dict, Tuple


def load(path: Path):
    sizes: Dict[str, int] = {}
    offsets: Dict[Tuple[str, str], int] = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("|")
        if parts[0] == "S" and len(parts) == 3:
            name = parts[1][1:] if parts[1].startswith("CThostFtdc") else parts[1]
            sizes[name] = int(parts[2])
        elif parts[0] == "F" and len(parts) == 4:
            name = parts[1][1:] if parts[1].startswith("CThostFtdc") else parts[1]
            field = parts[2]
            # The managed side expands char[5] comb flags into five fields.
            # Compare only the first element against the native field.
            m = re.match(r"^(CombOffsetFlag|CombHedgeFlag)_([0-4])$", field)
            if m:
                if m.group(2) != "0":
                    continue
                field = m.group(1)
            offsets[(name, field)] = int(parts[3])
    return sizes, offsets


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: compare_layout.py <cpp-output> <csharp-output>", file=sys.stderr)
        return 2
    # argv[1] = native C++ dump, argv[2] = managed C# dump
    cpp_sizes, cpp_offsets = load(Path(sys.argv[1]))
    cs_sizes, cs_offsets = load(Path(sys.argv[2]))

    errors = []
    for name in sorted(set(cs_sizes) | set(cpp_sizes)):
        a, b = cs_sizes.get(name), cpp_sizes.get(name)
        if a != b:
            errors.append(f"sizeof {name}: C# {a} vs C++ {b}")
    for key in sorted(set(cs_offsets) | set(cpp_offsets)):
        a, b = cs_offsets.get(key), cpp_offsets.get(key)
        if a != b:
            errors.append(f"offsetof {key[0]}.{key[1]}: C# {a} vs C++ {b}")

    print(f"structs compared: {len(cs_sizes)}; fields compared: {len(cs_offsets)}")
    if errors:
        print(f"MISMATCHES: {len(errors)}", file=sys.stderr)
        for e in errors[:100]:
            print("  - " + e, file=sys.stderr)
        return 1
    print("LAYOUT OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
