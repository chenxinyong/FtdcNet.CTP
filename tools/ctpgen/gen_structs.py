#!/usr/bin/env python3
"""Incrementally update FtdcNet.CTP/Struct.cs from CTP 6.7.13 C++ headers.

The updater is intentionally delta based: it keeps all existing C# text intact
and only appends/inserts declarations that are present in the new C++ headers.
This mirrors how CTP evolves its ABI (new structs and appended fields), while
making review diffs small.

Usage:
  python tools/ctpgen/gen_structs.py            # write Struct.cs
  python tools/ctpgen/gen_structs.py --check    # write to a temp file and report
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import tempfile
from collections import OrderedDict
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from parse_ctp import (
    REPO_ROOT,
    Struct,
    Typedef,
    enum_name_for,
    load,
    parse_structs,
    read_text,
    resolve_typedef,
)

STRUCT_CS = REPO_ROOT / "FtdcNet.CTP" / "Struct.cs"
SUBMODULE = REPO_ROOT / "ftdc2c_ctp"
OLD_REF = "origin/v6.7.2"
REMOVED_OLD_STRUCTS = {"ThostFtdcReqUserLoginSCField"}


# ---------------------------------------------------------------------------
# C# model
# ---------------------------------------------------------------------------

FIELD_RE = re.compile(
    r"(?:(?P<attr>\[MarshalAs\(UnmanagedType\.(?:ByValTStr|ByValArray), SizeConst\s*=\s*(?P<size>\d+)\)\])\s*)?"
    r"public\s+(?P<type>[\w\.]+(?:\[\])?)\s+(?P<name>\w+)\s*;"
)


class CSharpField:
    def __init__(self, name: str, cs_type: str, size: Optional[int], start: int, end: int):
        self.name = name
        self.cs_type = cs_type
        self.size = size
        self.start = start
        self.end = end

    @property
    def desc(self):
        if self.cs_type == "string":
            return ("string", self.size)
        if self.cs_type == "byte[]":
            return ("bytearray", self.size)
        if self.cs_type.startswith("Enum"):
            return ("enum", self.cs_type)
        return ("scalar", self.cs_type)


class CSharpStruct:
    def __init__(
        self,
        name: str,
        doc_start: int,
        body_start: int,
        body_end: int,
        body_start_line: int,
        body_end_line: int,
        fields: List[CSharpField],
    ):
        self.name = name
        self.doc_start = doc_start
        self.body_start = body_start
        self.body_end = body_end
        self.body_start_line = body_start_line
        self.body_end_line = body_end_line
        self.fields = fields

    @property
    def field_map(self) -> Dict[str, CSharpField]:
        return {f.name: f for f in self.fields}


CLASS_RE = re.compile(r"^    public class (ThostFtdc\w+)\s*$", re.M)


def parse_csharp_structs(text: str) -> "OrderedDict[str, CSharpStruct]":
    lines = text.splitlines(keepends=True)
    out: "OrderedDict[str, CSharpStruct]" = OrderedDict()

    for m in CLASS_RE.finditer(text):
        name = m.group(1)
        class_line = text.count("\n", 0, m.start())

        # Class opening brace is the next non-empty line.
        i = class_line + 1
        while i < len(lines) and lines[i].strip() == "":
            i += 1
        if i >= len(lines) or lines[i].strip() != "{":
            raise ValueError(f"Cannot find opening brace for {name}")
        body_start_line = i + 1

        j = body_start_line
        while j < len(lines) and lines[j].rstrip("\r\n") != "    };":
            j += 1
        if j >= len(lines):
            raise ValueError(f"Cannot find closing brace for {name}")

        # Include the XML doc / StructLayout attribute above the class.
        doc_start_line = class_line
        while doc_start_line > 0:
            prev = lines[doc_start_line - 1].strip()
            if prev.startswith("///") or prev.startswith("[StructLayout") or prev == "":
                doc_start_line -= 1
                continue
            break
        # Keep exactly one preceding blank line in the block range.
        while doc_start_line < class_line and lines[doc_start_line].strip() == "":
            doc_start_line += 1

        body_start = sum(len(x) for x in lines[:body_start_line])
        body_end = sum(len(x) for x in lines[:j])
        body = text[body_start:body_end]

        fields: List[CSharpField] = []
        for fm in FIELD_RE.finditer(body):
            fields.append(
                CSharpField(
                    name=fm.group("name"),
                    cs_type=fm.group("type"),
                    size=int(fm.group("size")) if fm.group("size") else None,
                    start=body_start + fm.start(),
                    end=body_start + fm.end(),
                )
            )
        out[name] = CSharpStruct(
            name,
            sum(len(x) for x in lines[:doc_start_line]),
            body_start,
            body_end,
            body_start_line,
            j,
            fields,
        )
    return out


# ---------------------------------------------------------------------------
# Type mapping
# ---------------------------------------------------------------------------

SCALAR_MAP = {
    "int": "int",
    "unsigned int": "int",
    "short": "short",
    "unsigned short": "short",
    "long": "int",
    "long long": "long",
    "unsigned long long": "long",
    "double": "double",
    "float": "float",
    "bool": "int",
    "char": "byte",
    "signed char": "byte",
    "unsigned char": "byte",
}


def normalize_field(name: str) -> str:
    m = re.match(r"^(CombOffsetFlag|CombHedgeFlag)_[0-4]$", name)
    return m.group(1) if m else name


def build_type_mapping(
    old_structs: "OrderedDict[str, Struct]",
    cs_structs: "OrderedDict[str, CSharpStruct]",
) -> Tuple[Dict[str, tuple], List[str]]:
    mapping: Dict[str, tuple] = {}
    errors: List[str] = []
    for sname, s in old_structs.items():
        cs = cs_structs.get(sname)
        if not cs:
            if sname not in REMOVED_OLD_STRUCTS:
                errors.append(f"old struct not found in C#: {sname}")
            continue
        fmap = cs.field_map
        for f in s.fields:
            csf = fmap.get(f.name)
            if not csf:
                if f.name in ("CombOffsetFlag", "CombHedgeFlag"):
                    continue
                errors.append(f"field not found in C#: {sname}.{f.name}")
                continue
            prev = mapping.get(f.ctype)
            if prev is not None and prev != csf.desc:
                errors.append(f"inconsistent mapping for {f.ctype}: {prev} vs {csf.desc} ({sname}.{f.name})")
            else:
                mapping[f.ctype] = csf.desc
    return mapping, errors


def resolve_mapping(ctype: str, mapping: Dict[str, tuple], types: Dict[str, Typedef]) -> tuple:
    if ctype in mapping:
        return mapping[ctype]
    td = resolve_typedef(ctype, types)
    if td.kind == "char_array":
        return ("string", td.array_len)
    if td.kind == "char_enum":
        return ("enum", enum_name_for(ctype))
    if td.kind == "scalar":
        base = td.base.strip()
        if base in SCALAR_MAP:
            return ("scalar", SCALAR_MAP[base])
        # Alias to a scalar typedef already seen in the mapping.
        if base in mapping:
            return mapping[base]
        raise ValueError(f"Unknown scalar base '{base}' for {ctype}")
    if td.kind == "alias":
        return resolve_mapping(td.base, mapping, types)
    if td.kind == "char":
        return ("scalar", "byte")
    raise ValueError(f"Unknown typedef kind {td.kind} for {ctype}")


def doc_lines(comment: str, indent: str) -> List[str]:
    lines = [indent + "/// <summary>"]
    for line in (comment or "").splitlines() or [""]:
        lines.append(indent + "/// " + line.strip())
    lines.append(indent + "/// </summary>")
    return lines


def field_snippet(f: "Field", mapping: Dict[str, tuple], types: Dict[str, Typedef]) -> List[str]:
    """Return C# source lines (with 8-space indentation) for one header field."""
    out: List[str] = []
    desc = resolve_mapping(f.ctype, mapping, types)

    if f.ctype in (
        "TThostFtdcCombOffsetFlagType",
        "TThostFtdcCombHedgeFlagType",
    ) and f.name in ("CombOffsetFlag", "CombHedgeFlag"):
        enum_td = resolve_typedef(
            "TThostFtdcOffsetFlagType" if "Offset" in f.ctype else "TThostFtdcHedgeFlagType", types
        )
        enum_name = enum_name_for(enum_td.name)
        for i in range(5):
            out.extend(doc_lines(f.comment if i == 0 else "", "        "))
            out.append(f"        public {enum_name} {f.name}_{i};")
            out.append("")
        return out

    out.extend(doc_lines(f.comment, "        "))
    kind, value = desc
    if kind == "string":
        out.append(f"        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = {value})]")
        out.append(f"        public string {f.name};")
    elif kind == "bytearray":
        out.append(f"        [MarshalAs(UnmanagedType.ByValArray, SizeConst = {value})]")
        out.append(f"        public byte[] {f.name};")
    elif kind == "enum":
        out.append(f"        public {value} {f.name};")
    else:
        out.append(f"        public {value} {f.name};")
    out.append("")
    return out


def class_snippet(s: Struct, mapping: Dict[str, tuple], types: Dict[str, Typedef]) -> List[str]:
    out: List[str] = []
    out.extend(doc_lines(s.comment, "    "))
    out.append("    [StructLayout(LayoutKind.Sequential)]")
    out.append(f"    public class {s.name}")
    out.append("    {")
    for f in s.fields:
        out.extend(field_snippet(f, mapping, types))
    out.append("    };")
    out.append("")
    return out


# ---------------------------------------------------------------------------
# Apply deltas
# ---------------------------------------------------------------------------

def find_field_insert_line(
    lines: List[str], body_start_line: int, body_end_line: int, field_name: str
) -> Optional[int]:
    """Find the line index of the doc/attribute block for a field declaration."""
    decl_re = re.compile(r"^\s*public\s+[\w\.]+\s+" + re.escape(field_name) + r"\s*;")
    for i in range(body_start_line, body_end_line):
        if decl_re.match(lines[i]):
            j = i
            # Include the MarshalAs attribute line.
            if j > body_start_line and lines[j - 1].strip().startswith("[MarshalAs"):
                j -= 1
            # Include the XML doc block.
            if j > body_start_line and lines[j - 1].strip() == "/// </summary>":
                j -= 1
                while j > body_start_line and lines[j].strip().startswith("///"):
                    j -= 1
                j += 1
            return j
    return None


def apply_struct_delta(text: str, new_structs: "OrderedDict[str, Struct]", mapping, types) -> Tuple[str, dict]:
    cs_structs = parse_csharp_structs(text)
    eol = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    line_offsets = [0]
    for line in lines:
        line_offsets.append(line_offsets[-1] + len(line))

    insertions_by_pos: "OrderedDict[int, List[str]]" = OrderedDict()
    stats = {"added_classes": 0, "added_fields": 0, "removed_classes": 0}

    for sname, s in new_structs.items():
        cs = cs_structs.get(sname)
        if not cs:
            continue
        existing = {normalize_field(f.name) for f in cs.fields}
        # Determine missing fields, preserving new header order.
        for idx, f in enumerate(s.fields):
            if normalize_field(f.name) in existing:
                continue
            snippet = field_snippet(f, mapping, types)
            # Insert before the next header field that already exists in C#, else at end.
            anchor_line: Optional[int] = None
            for nxt in s.fields[idx + 1:]:
                if normalize_field(nxt.name) in existing:
                    anchor_line = find_field_insert_line(
                        lines, cs.body_start_line, cs.body_end_line, nxt.name
                    )
                    break
            pos = cs.body_end if anchor_line is None else line_offsets[anchor_line]
            insertions_by_pos.setdefault(pos, []).extend(snippet)
            stats["added_fields"] += 1

    # New classes appended before the closing namespace.
    existing_class_names = set(cs_structs)
    new_class_lines: List[str] = []
    for sname, s in new_structs.items():
        if sname not in existing_class_names:
            new_class_lines.extend(class_snippet(s, mapping, types))
            stats["added_classes"] += 1

    # Remove structs deleted upstream (doc comment + class body + closing line).
    deletions: List[Tuple[int, int]] = []
    for name, cs in cs_structs.items():
        if name in new_structs:
            continue
        nl = text.find("\n", cs.body_end)
        deletions.append((cs.doc_start, nl + 1 if nl != -1 else cs.body_end))
    stats["removed_classes"] = len(deletions)

    # Apply deletions/insertions from the end to keep offsets valid.
    edits: List[Tuple[int, int, Optional[str]]] = [
        (start, end, None) for start, end in deletions
    ]
    edits += [(pos, pos, eol.join(snip) + eol) for pos, snip in insertions_by_pos.items()]
    edits.sort(key=lambda x: (x[0], x[1]), reverse=True)

    for start, end, snip in edits:
        if snip is None:
            text = text[:start] + text[end:]
        else:
            text = text[:start] + snip + text[start:]

    if new_class_lines:
        idx = text.rfind("}; // end of namespace")
        if idx == -1:
            raise ValueError("cannot find namespace terminator")
        line_start = text.rfind("\n", 0, idx) + 1
        text = text[:line_start] + eol.join(new_class_lines) + eol + text[line_start:]

    return text, stats


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def load_old_structs() -> "OrderedDict[str, Struct]":
    raw = subprocess.check_output(
        ["git", "-C", str(SUBMODULE), "show", f"{OLD_REF}:api/ThostFtdcUserApiStruct.h"]
    )
    with tempfile.NamedTemporaryFile(delete=False, suffix=".h") as tmp:
        tmp.write(raw)
        tmp_path = Path(tmp.name)
    try:
        return parse_structs(tmp_path)
    finally:
        tmp_path.unlink(missing_ok=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="do not write; print the resulting diff summary")
    args = ap.parse_args()

    types, new_structs = load()
    old_structs = load_old_structs()
    text = read_text(STRUCT_CS)
    cs_structs = parse_csharp_structs(text)
    mapping, errors = build_type_mapping(old_structs, cs_structs)
    if errors:
        print("Type mapping errors:", file=sys.stderr)
        for e in errors:
            print("  " + e, file=sys.stderr)
        return 1

    new_text, stats = apply_struct_delta(text, new_structs, mapping, types)
    print(
        f"classes +{stats['added_classes']}, fields +{stats['added_fields']}, "
        f"removed {stats['removed_classes']}"
    )

    if args.check:
        with tempfile.NamedTemporaryFile(
            delete=False, suffix=".cs", mode="w", encoding="utf-8-sig", newline=""
        ) as tmp:
            tmp.write(new_text)
            print("check output:", tmp.name)
        return 0

    STRUCT_CS.write_bytes(new_text.encode("utf-8-sig"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
