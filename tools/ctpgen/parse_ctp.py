#!/usr/bin/env python3
"""Parse CTP C++ headers into a small model used by the C# generators.

The CTP SDK headers are plain data declarations:
  - typedefs (scalars, char arrays, char enums defined via THOST_FTDC_* macros)
  - structs with fixed fields in declaration order

This module deliberately avoids a full C++ parser. It only understands the
declaration subset used by ThostFtdcUserApiDataType.h and
ThostFtdcUserApiStruct.h.
"""

from __future__ import annotations

import re
from collections import OrderedDict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_API_DIR = REPO_ROOT / "ftdc2c_ctp" / "api"


def read_text(path: Path) -> str:
    raw = path.read_bytes()
    for enc in ("utf-8-sig", "utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


@dataclass
class Define:
    name: str
    value: str
    comment: str = ""


@dataclass
class Typedef:
    name: str
    decl: str                 # e.g. "char", "int", "TThostFtdcFooType", "char[5]"
    array_len: Optional[int]
    base: str                 # element type after resolving aliases
    kind: str                 # "char_array" | "char_enum" | "char" | "scalar" | "alias"
    defines: List[Define] = field(default_factory=list)
    summary: str = ""


@dataclass
class Field:
    ctype: str
    name: str
    comment: str = ""


@dataclass
class Struct:
    name: str                 # without C prefix (ThostFtdcXxxField)
    comment: str
    fields: List[Field]

    @property
    def c_name(self) -> str:
        return "C" + self.name


_TYPEDEF_RE = re.compile(
    r"typedef\s+([A-Za-z_][\w\s]*?)\s+(TThostFtdc\w+)\s*(?:\[(\d+)\])?\s*;"
)
_DEFINE_RE = re.compile(r"#define\s+(THOST_FTDC_\w+)\s+(.+?)\s*$", re.M)
_FIELD_RE = re.compile(r"^\s*(TThostFtdc\w+)\s+(\w+)\s*;\s*$")
_STRUCT_RE = re.compile(r"struct\s+(CThostFtdc\w+)\s*\{(.*?)\n\};", re.S)


def _comments_before(text: str, pos: int) -> str:
    """Collect the consecutive /// lines immediately preceding pos."""
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


def _block_comment(lines: Iterable[str]) -> str:
    return "\n".join(line[3:].strip() for line in lines if line.strip().startswith("///"))


def parse_data_types(path: Optional[Path] = None) -> "OrderedDict[str, Typedef]":
    path = path or (DEFAULT_API_DIR / "ThostFtdcUserApiDataType.h")
    text = read_text(path).replace("\r\n", "\n")

    matches = list(_TYPEDEF_RE.finditer(text))
    result: "OrderedDict[str, Typedef]" = OrderedDict()
    prev_end = 0
    for i, m in enumerate(matches):
        block_start = prev_end
        block = text[block_start:m.start()]
        prev_end = m.end()

        decl = re.sub(r"\s+", " ", m.group(1).strip())
        name = m.group(2)
        array_len = int(m.group(3)) if m.group(3) else None

        defines: List[Define] = []
        last_comment = ""
        for line in block.splitlines():
            s = line.strip()
            if s.startswith("///"):
                last_comment = s[3:].strip()
                continue
            dm = _DEFINE_RE.match(line)
            if dm:
                defines.append(Define(dm.group(1), dm.group(2).strip().strip("'"), last_comment))
                last_comment = ""
            elif s == "":
                last_comment = ""

        summary = ""
        for line in block.splitlines():
            s = line.strip()
            if s.startswith("///") and "Type" in s:
                summary = s[3:].strip()
                break

        if decl == "char" and array_len is None and defines:
            kind = "char_enum"
        elif decl == "char" and array_len is not None:
            kind = "char_array"
        elif decl in ("char", "signed char", "unsigned char") and array_len is None:
            kind = "char"
        elif decl.startswith("TThostFtdc") and array_len is None:
            kind = "alias"
        else:
            kind = "scalar"

        result[name] = Typedef(
            name=name,
            decl=decl + (f"[{array_len}]" if array_len is not None else ""),
            array_len=array_len,
            base=decl,
            kind=kind,
            defines=defines,
            summary=summary,
        )
    return result


def parse_structs(path: Optional[Path] = None) -> "OrderedDict[str, Struct]":
    path = path or (DEFAULT_API_DIR / "ThostFtdcUserApiStruct.h")
    text = read_text(path).replace("\r\n", "\n")

    result: "OrderedDict[str, Struct]" = OrderedDict()
    for m in _STRUCT_RE.finditer(text):
        cname = m.group(1)
        body = m.group(2)
        struct_comment = _comments_before(text, m.start())
        fields: List[Field] = []
        pending: List[str] = []
        for line in body.splitlines():
            s = line.strip()
            if s.startswith("///"):
                pending.append(s[3:].strip())
                continue
            fm = _FIELD_RE.match(line)
            if fm:
                fields.append(Field(fm.group(1), fm.group(2), "\n".join(pending)))
                pending = []
            elif s == "":
                continue
            else:
                # Any unexpected declaration is a parser error, not silently ignored.
                raise ValueError(f"Unparsed field in {cname}: {line!r}")
        result[cname[1:]] = Struct(cname[1:], struct_comment, fields)
    return result


def enum_name_for(typedef_name: str) -> str:
    """TThostFtdcDirectionType -> EnumDirectionType"""
    short = typedef_name
    if short.startswith("TThostFtdc"):
        short = short[len("TThostFtdc"):]
    return "Enum" + short


def enum_member_name(define_name: str) -> str:
    """THOST_FTDC_PRS_Init -> Init (the last macro component)."""
    m = re.match(r"THOST_FTDC_[A-Z0-9]+_(.+)$", define_name)
    return m.group(1) if m else define_name


def resolve_typedef(name: str, typedefs: Dict[str, Typedef], seen=None) -> Typedef:
    seen = seen or set()
    if name in seen:
        raise ValueError(f"Cyclic typedef: {name}")
    seen.add(name)
    td = typedefs[name]
    if td.kind == "alias":
        target = resolve_typedef(td.base, typedefs, seen)
        # Preserve the original name but expose the resolved shape.
        return Typedef(
            name=td.name,
            decl=target.decl,
            array_len=target.array_len,
            base=target.base,
            kind=target.kind,
            defines=td.defines or target.defines,
            summary=td.summary or target.summary,
        )
    return td


def csharp_enum_members(td: Typedef) -> List[Tuple[str, str, str]]:
    """Return (member, char_value, comment) for a char enum typedef."""
    out = []
    for d in td.defines:
        out.append((enum_member_name(d.name), d.value, d.comment))
    return out


def load(api_dir: Optional[Path] = None):
    api_dir = api_dir or DEFAULT_API_DIR
    types = parse_data_types(api_dir / "ThostFtdcUserApiDataType.h")
    structs = parse_structs(api_dir / "ThostFtdcUserApiStruct.h")
    return types, structs


if __name__ == "__main__":
    types, structs = load()
    print(f"typedefs: {len(types)}")
    print(f"char enums: {sum(1 for t in types.values() if t.kind == 'char_enum')}")
    print(f"structs: {len(structs)}")
