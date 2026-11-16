#!/usr/bin/env python3
"""Regenerate the four callback enums in FtdcNet.CTP/EnumEvents.cs.

The numeric ordinals in these enums are an ABI contract with
ftdc2c_ctp/enums.h. This script rewrites the enum bodies from the native
header so they can never drift. Existing XML doc comments are preserved;
new callbacks get docs derived from ThostFtdcTraderApi.h / MdApi.h.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from parse_ctp import DEFAULT_API_DIR, REPO_ROOT, read_text

ENUM_EVENTS_CS = REPO_ROOT / "FtdcNet.CTP" / "EnumEvents.cs"
TRADER_API_H = DEFAULT_API_DIR / "ThostFtdcTraderApi.h"
MD_API_H = DEFAULT_API_DIR / "ThostFtdcMdApi.h"

PAIRS = [
    ("EnumOnErrRtnEvent", "EnumOnErrRtnType"),
    ("EnumOnFrontEvent", "EnumOnFrontType"),
    ("EnumOnRspEvent", "EnumOnRspType"),
    ("EnumOnRtnEvent", "EnumOnRtnType"),
]


def parse_native_members(text: str, enum_name: str) -> List[str]:
    m = re.search(r"enum class " + enum_name + r"\s*\{(.*?)\};", text, re.S)
    if not m:
        raise ValueError(f"native enum not found: {enum_name}")
    body = re.sub(r"//.*", "", m.group(1))
    return [x.strip() for x in body.split(",") if x.strip()]


def parse_managed_enum(text: str, enum_name: str):
    """Return (body_start_line, body_end_line, {member: [raw doc lines]})."""
    pat = re.compile(r"^    public enum " + enum_name + r"\s*$", re.M)
    m = pat.search(text)
    if not m:
        raise ValueError(f"managed enum not found: {enum_name}")
    lines = text.splitlines(keepends=True)
    class_line = text.count("\n", 0, m.start())
    i = class_line + 1
    while i < len(lines) and lines[i].strip() == "":
        i += 1
    if lines[i].strip() != "{":
        raise ValueError(f"opening brace not found for {enum_name}")
    body_start = i + 1
    j = body_start
    while j < len(lines) and lines[j].rstrip("\r\n") != "    };":
        j += 1
    if j >= len(lines):
        raise ValueError(f"closing brace not found for {enum_name}")

    docs: Dict[str, List[str]] = {}
    pending: List[str] = []
    member_re = re.compile(r"^\s*(\w+)\s*,?\s*$")
    for line in lines[body_start:j]:
        s = line.strip()
        if s.startswith("///"):
            pending.append(line.rstrip("\r\n"))
            continue
        mm = member_re.match(line)
        if mm:
            docs[mm.group(1)] = pending
            pending = []
        elif s == "":
            continue
    return body_start, j, docs


def comments_before(text: str, pos: int) -> str:
    lines = text[:pos].splitlines()
    out: List[str] = []
    for line in reversed(lines):
        s = line.strip()
        if s.startswith("///"):
            out.append(s[3:].strip())
        elif s == "":
            if out:
                break
            continue
        else:
            break
    return "\n".join(reversed(out))


def callback_info(member: str, header_texts: List[str]) -> Tuple[str, Optional[str]]:
    if member == "OnRtnPrivateSeqNo":
        return "私有流序号通知, 整数; 使用 e.Param.ToInt32() 获取序号", None
    pat = re.compile(r"virtual\s+void\s+" + re.escape(member) + r"\s*\((.*?)\)\s*(?:=\s*0\s*)?\{", re.S)
    for text in header_texts:
        m = pat.search(text)
        if not m:
            continue
        comment = comments_before(text, m.start())
        sm = re.search(r"CThostFtdc(\w+Field)", m.group(1))
        struct_name = "ThostFtdc" + sm.group(1) if sm else None
        return comment, struct_name
    return "", None


def generated_doc(member: str, comment: str, struct_name: Optional[str]) -> str:
    if comment:
        text = comment
    elif struct_name:
        text = f"{member}, {struct_name}"
    else:
        text = member
    if struct_name and struct_name not in text:
        text += f", {struct_name}"
    return text


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    native = read_text(DEFAULT_API_DIR.parent / "enums.h")
    managed = read_text(ENUM_EVENTS_CS)
    header_texts = [read_text(TRADER_API_H), read_text(MD_API_H)]
    eol = "\r\n" if "\r\n" in managed else "\n"
    lines = managed.splitlines(keepends=True)

    replacements: List[Tuple[int, int, List[str]]] = []
    for native_name, managed_name in PAIRS:
        native_members = parse_native_members(native, native_name)
        body_start, body_end, existing = parse_managed_enum(managed, managed_name)

        new_body: List[str] = []
        for member in native_members:
            if member.startswith("MaxValue"):
                doc = existing.get("MaxValue") or [
                    "        /// <summary>",
                    "        /// 未使用,保留",
                    "        /// </summary>",
                ]
            else:
                if member in existing and existing[member]:
                    doc = existing[member]
                else:
                    comment, struct_name = callback_info(member, header_texts)
                    text = generated_doc(member, comment, struct_name)
                    doc = ["        /// <summary>"]
                    doc += ["        /// " + x for x in text.splitlines()]
                    doc.append("        /// </summary>")
            new_body.extend(doc)
            new_body.append(f"        {member},")
        replacements.append((body_start, body_end, new_body))

    for body_start, body_end, new_body in sorted(replacements, key=lambda x: x[0], reverse=True):
        lines[body_start:body_end] = [line + eol for line in new_body]

    out = "".join(lines)
    print("events updated:", ", ".join(m for _, m in PAIRS))
    if args.check:
        import tempfile

        with tempfile.NamedTemporaryFile(
            delete=False, suffix=".cs", mode="w", encoding="utf-8-sig", newline=""
        ) as tmp:
            tmp.write(out)
            print("check output:", tmp.name)
        return 0

    ENUM_EVENTS_CS.write_bytes(out.encode("utf-8-sig"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
