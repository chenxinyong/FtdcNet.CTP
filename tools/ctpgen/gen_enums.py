#!/usr/bin/env python3
"""Incrementally update FtdcNet.CTP/Enums.cs from CTP 6.7.13 data types.

Adds:
  - new char-enum typedefs as `public enum EnumXxxType : byte`
  - new values appended to existing enums
  - THOST_TERT_RESUME_FROM_SEQ_NO to EnumTeResumeType

Existing enum text is preserved; only missing members / new enums are added.
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
    Typedef,
    csharp_enum_members,
    enum_name_for,
    load,
    parse_data_types,
    read_text,
)

ENUMS_CS = REPO_ROOT / "FtdcNet.CTP" / "Enums.cs"
SUBMODULE = REPO_ROOT / "ftdc2c_ctp"
OLD_REF = "origin/v6.7.2"

ENUM_RE = re.compile(r"^    public enum (\w+)(?:\s*:\s*byte)?\s*$", re.M)


class CSharpEnum:
    def __init__(self, name: str, body_start_line: int, body_end_line: int, members: set):
        self.name = name
        self.body_start_line = body_start_line
        self.body_end_line = body_end_line
        self.members = members


def parse_csharp_enums(text: str) -> "OrderedDict[str, CSharpEnum]":
    lines = text.splitlines(keepends=True)
    out: "OrderedDict[str, CSharpEnum]" = OrderedDict()
    for m in ENUM_RE.finditer(text):
        name = m.group(1)
        class_line = text.count("\n", 0, m.start())
        i = class_line + 1
        while i < len(lines) and lines[i].strip() == "":
            i += 1
        if i >= len(lines) or lines[i].strip() != "{":
            raise ValueError(f"Cannot find opening brace for enum {name}")
        body_start = i + 1
        j = body_start
        while j < len(lines) and lines[j].rstrip("\r\n") != "    };":
            j += 1
        if j >= len(lines):
            raise ValueError(f"Cannot find closing brace for enum {name}")
        body = "".join(lines[body_start:j])
        body = re.sub(r"///.*", "", body)
        members = set(re.findall(r"^\s*(\w+)\s*=", body, re.M))
        out[name] = CSharpEnum(name, body_start, j, members)
    return out


def value_expr(value: str) -> str:
    value = value.strip()
    if len(value) == 1:
        return f"(byte)'{value}'"
    return value


def member_block(name: str, value: str, comment: str) -> List[str]:
    out: List[str] = []
    if comment:
        out.append("        /// <summary>")
        for line in comment.splitlines():
            out.append("        /// " + line.strip())
        out.append("        /// </summary>")
    out.append(f"        {name} = {value_expr(value)},")
    out.append("")
    return out


def enum_block(td: Typedef) -> List[str]:
    out: List[str] = []
    out.append("    /// <summary>")
    for line in (td.summary or td.name).splitlines():
        out.append("    /// " + line.strip())
    out.append("    /// </summary>")
    out.append(f"    public enum {enum_name_for(td.name)} : byte")
    out.append("    {")
    members = csharp_enum_members(td)
    for i, (m, v, c) in enumerate(members):
        if c:
            out.append("        /// <summary>")
            for line in c.splitlines():
                out.append("        /// " + line.strip())
            out.append("        /// </summary>")
        comma = "," if i != len(members) - 1 else ""
        out.append(f"        {m} = {value_expr(v)}{comma}")
        if i != len(members) - 1:
            out.append("")
    out.append("    };")
    out.append("")
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    types, _ = load()
    raw_old = subprocess.check_output(
        ["git", "-C", str(SUBMODULE), "show", f"{OLD_REF}:api/ThostFtdcUserApiDataType.h"]
    )
    with tempfile.NamedTemporaryFile(delete=False, suffix=".h") as tmp:
        tmp.write(raw_old)
        old_path = Path(tmp.name)
    try:
        old_types = parse_data_types(old_path)
    finally:
        old_path.unlink(missing_ok=True)

    text = read_text(ENUMS_CS)
    eol = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    line_offsets = [0]
    for line in lines:
        line_offsets.append(line_offsets[-1] + len(line))

    cs_enums = parse_csharp_enums(text)
    edits: List[Tuple[int, str]] = []
    stats = {"new_enums": 0, "new_values": 0}

    def ensure_previous_comma(enum: CSharpEnum) -> Optional[Tuple[int, str]]:
        k = enum.body_end_line - 1
        while k >= enum.body_start_line:
            s = lines[k].strip()
            if s and not s.startswith("///"):
                break
            k -= 1
        if k < enum.body_start_line:
            return None
        content = lines[k].rstrip("\r\n")
        if content.rstrip().endswith(","):
            return None
        # Insert the comma at the end of the previous member line.
        return (line_offsets[k] + len(content), ",")

    # New enum types.
    new_enum_blocks: List[str] = []
    for td in types.values():
        if td.kind != "char_enum" or td.name in old_types:
            continue
        cs_name = enum_name_for(td.name)
        if cs_name in cs_enums:
            continue
        new_enum_blocks.extend(enum_block(td))
        stats["new_enums"] += 1

    # New values in existing enums.
    for td in types.values():
        if td.kind != "char_enum" or td.name not in old_types:
            continue
        cs_name = enum_name_for(td.name)
        cs = cs_enums.get(cs_name)
        if not cs:
            print(f"warning: enum not found in Enums.cs: {cs_name}", file=sys.stderr)
            continue
        old_members = {m for m, _, _ in csharp_enum_members(old_types[td.name])}
        missing = [(m, v, c) for m, v, c in csharp_enum_members(td) if m not in cs.members]
        # Defensive: only append members that were not in the previous header either.
        missing = [x for x in missing if x[0] not in old_members]
        if not missing:
            continue
        block: List[str] = []
        for m, v, c in missing:
            block.extend(member_block(m, v, c))
        comma = ensure_previous_comma(cs)
        if comma:
            edits.append(comma)
        snippet = eol.join(block).rstrip("\r\n")
        edits.append((line_offsets[cs.body_end_line], eol + snippet + eol))
        stats["new_values"] += len(missing)

    # EnumTeResumeType: CTP 6.7.13 adds THOST_TERT_RESUME_FROM_SEQ_NO.
    te = cs_enums.get("EnumTeResumeType")
    if te and "THOST_TERT_RESUME_FROM_SEQ_NO" not in te.members:
        comma = ensure_previous_comma(te)
        if comma:
            edits.append(comma)
        edits.append((
            line_offsets[te.body_end_line],
            "        THOST_TERT_RESUME_FROM_SEQ_NO = 4" + eol,
        ))
        stats["new_values"] += 1

    edits.sort(key=lambda x: x[0], reverse=True)
    for pos, snippet in edits:
        text = text[:pos] + snippet + text[pos:]

    if new_enum_blocks:
        idx = text.rfind("}; // end of namespace")
        if idx == -1:
            raise ValueError("cannot find namespace terminator")
        line_start = text.rfind("\n", 0, idx) + 1
        text = text[:line_start] + eol.join(new_enum_blocks) + eol + text[line_start:]

    print(f"new enums: {stats['new_enums']}, new values: {stats['new_values']}")
    if args.check:
        with tempfile.NamedTemporaryFile(
            delete=False, suffix=".cs", mode="w", encoding="utf-8-sig", newline=""
        ) as tmp:
            tmp.write(text)
            print("check output:", tmp.name)
        return 0

    ENUMS_CS.write_bytes(text.encode("utf-8-sig"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
