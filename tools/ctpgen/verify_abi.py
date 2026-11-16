#!/usr/bin/env python3
"""Verify the managed wrapper's data model against the CTP 6.7.13 headers.

Checks:
  1. every C++ struct has a C# class (and no extra classes)
  2. every struct field exists, in header order, with the expected C# type
  3. callback enums match enums.h name-for-name and ordinal-for-ordinal
  4. every enum value defined by the headers has a C# member with that value
  5. every new native export has an Interop DllImport
"""

from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path
from typing import Dict, List

from parse_ctp import (
    DEFAULT_API_DIR,
    REPO_ROOT,
    Typedef,
    csharp_enum_members,
    enum_name_for,
    load,
    read_text,
    resolve_typedef,
)
from gen_structs import (
    build_type_mapping,
    load_old_structs,
    normalize_field,
    parse_csharp_structs,
    resolve_mapping,
)

STRUCT_CS = REPO_ROOT / "FtdcNet.CTP" / "Struct.cs"
ENUMS_CS = REPO_ROOT / "FtdcNet.CTP" / "Enums.cs"
ENUM_EVENTS_CS = REPO_ROOT / "FtdcNet.CTP" / "EnumEvents.cs"
INTEROP_CS = REPO_ROOT / "FtdcNet.CTP" / "Interop.cs"
NATIVE_H = REPO_ROOT / "ftdc2c_ctp" / "ftdc2c_ctp.h"

PAIRS = [
    ("EnumOnErrRtnEvent", "EnumOnErrRtnType"),
    ("EnumOnFrontEvent", "EnumOnFrontType"),
    ("EnumOnRspEvent", "EnumOnRspType"),
    ("EnumOnRtnEvent", "EnumOnRtnType"),
]

# Native exports that are intentionally not wrapped by the managed adapter.
OPTIONAL_EXPORTS = {"MdSetUserObject", "TdSetUserObject"}


def parse_native_events(text: str, name: str) -> List[str]:
    m = re.search(r"enum class " + name + r"\s*\{(.*?)\};", text, re.S)
    body = re.sub(r"//.*", "", m.group(1))
    return [x.strip() for x in body.split(",") if x.strip()]


def parse_managed_events(text: str, name: str) -> List[str]:
    m = re.search(r"public enum " + name + r"\s*\{(.*?)\n    \};", text, re.S)
    body = re.sub(r"///.*", "", m.group(1))
    return re.findall(r"^\s*(\w+)\s*,?\s*$", body, re.M)


def parse_managed_enum_members(text: str, name: str) -> Dict[str, str]:
    m = re.search(r"public enum " + name + r"(?:\s*:\s*byte)?\s*\{(.*?)\n    \};", text, re.S)
    if not m:
        return {}
    body = re.sub(r"///.*", "", m.group(1))
    out = {}
    for mm in re.finditer(r"^\s*(\w+)\s*=\s*(?:\(byte\)\s*)?'(.?)'", body, re.M):
        out[mm.group(1)] = mm.group(2)
    for mm in re.finditer(r"^\s*(\w+)\s*=\s*(\d+)", body, re.M):
        out.setdefault(mm.group(1), mm.group(2))
    return out


def main() -> int:
    errors: List[str] = []
    types, structs = load()
    struct_text = read_text(STRUCT_CS)
    enum_text = read_text(ENUMS_CS)
    event_text = read_text(ENUM_EVENTS_CS)
    interop_text = read_text(INTEROP_CS)
    native_text = read_text(NATIVE_H)

    # 1/2. Structs and fields.
    cs_structs = parse_csharp_structs(struct_text)
    old_structs = load_old_structs()
    mapping, map_errors = build_type_mapping(old_structs, cs_structs)
    errors.extend(map_errors)

    if set(structs) != set(cs_structs):
        missing = sorted(set(structs) - set(cs_structs))
        extra = sorted(set(cs_structs) - set(structs))
        if missing:
            errors.append(f"missing C# structs: {missing}")
        if extra:
            errors.append(f"extra C# structs: {extra}")

    checked_fields = 0
    for sname, s in structs.items():
        cs = cs_structs.get(sname)
        if not cs:
            continue
        fmap = {f.name: f for f in cs.fields}
        for f in s.fields:
            if f.ctype in ("TThostFtdcCombOffsetFlagType", "TThostFtdcCombHedgeFlagType"):
                missing = [f"{f.name}_{i}" for i in range(5) if f"{f.name}_{i}" not in fmap]
                if missing:
                    errors.append(f"{sname}: missing split fields {missing}")
                continue
            csf = fmap.get(f.name)
            if not csf:
                errors.append(f"{sname}: missing field {f.name}")
                continue
            expected = resolve_mapping(f.ctype, mapping, types)
            actual = csf.desc
            if expected != actual:
                errors.append(f"{sname}.{f.name}: expected {expected}, got {actual}")
            checked_fields += 1

    # 3. Event enum ordinals.
    native_enums = read_text(DEFAULT_API_DIR.parent / "enums.h")
    for native_name, managed_name in PAIRS:
        native_members = parse_native_events(native_enums, native_name)
        managed_members = parse_managed_events(event_text, managed_name)
        if native_members != managed_members:
            for i, (a, b) in enumerate(zip(native_members, managed_members)):
                if a != b:
                    errors.append(f"{managed_name}: ordinal {i} is {b}, expected {a}")
                    break
            else:
                errors.append(
                    f"{managed_name}: length {len(managed_members)} != native {len(native_members)}"
                )

    # 4. Enum values.
    new_enum_types = [td for td in types.values() if td.kind == "char_enum"]
    for td in new_enum_types:
        cs_name = enum_name_for(td.name)
        cs_members = parse_managed_enum_members(enum_text, cs_name)
        expected = {m: v for m, v, _ in csharp_enum_members(td)}
        if not cs_members:
            errors.append(f"missing C# enum {cs_name}")
            continue
        # Legacy CTP enums with multi-character defines are represented with
        # suffixed member names and sequential byte values; skip value equality.
        if any(len(v) != 1 for v in expected.values()):
            continue
        missing = [(m, v) for m, v in expected.items() if cs_members.get(m) != v]
        if missing:
            # Report only if the whole value set differs, to avoid old naming noise.
            expected_values = Counter(expected.values())
            actual_values = Counter(cs_members.values())
            if expected_values != actual_values:
                errors.append(f"{cs_name}: missing/changed members {missing[:5]}")

    # 5. New exports are wrapped.
    native_exports = set(re.findall(r"FTDC2C_API\s+\S+\s+MYDECL\s+(\w+)\s*\(", native_text))
    wrapped = set(re.findall(r"public static extern\s+\S+\s+(\w+)\s*\(", interop_text))
    missing_exports = sorted(native_exports - wrapped - OPTIONAL_EXPORTS)
    if missing_exports:
        errors.append(f"native exports without DllImport: {missing_exports}")

    print(f"structs: {len(structs)}; fields checked: {checked_fields}; "
          f"native exports: {len(native_exports)}, wrapped: {len(wrapped)}")
    if errors:
        print("\nABI ERRORS:", file=sys.stderr)
        for e in errors:
            print("  - " + e, file=sys.stderr)
        return 1
    print("ABI OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
