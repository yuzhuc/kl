from __future__ import annotations

from pathlib import Path
import re
import struct


RID_LAYOUT = {
    "win-x64": {"kind": "pe", "machine": 0x8664, "ext": ".zip", "exe": "kl.exe", "core": "kl-core.exe"},
    "win-arm64": {"kind": "pe", "machine": 0xAA64, "ext": ".zip", "exe": "kl.exe", "core": "kl-core.exe"},
    "linux-x64": {"kind": "elf", "machine": 0x003E, "ext": ".tar.gz", "exe": "kl", "core": "kl-core"},
    "linux-arm64": {"kind": "elf", "machine": 0x00B7, "ext": ".tar.gz", "exe": "kl", "core": "kl-core"},
    "osx-arm64": {"kind": "macho", "cpu": 0x0100000C, "ext": ".tar.gz", "exe": "kl", "core": "kl-core"},
}

DIAGNOSTIC_FILE_PATTERNS = [
    re.compile(r"\.pdb$", re.IGNORECASE),
    re.compile(r"\.dbg$", re.IGNORECASE),
    re.compile(r"^createdump(\.exe)?$", re.IGNORECASE),
    re.compile(r"^Microsoft\.DiaSymReader\.Native\..*\.dll$", re.IGNORECASE),
    re.compile(r"^mscordaccore.*\.dll$", re.IGNORECASE),
    re.compile(r"^mscordbi.*\.dll$", re.IGNORECASE),
    re.compile(r"^sos\.dll$", re.IGNORECASE),
    re.compile(r"^libsosplugin", re.IGNORECASE),
    re.compile(r"^libmscordaccore", re.IGNORECASE),
    re.compile(r"^libmscordbi", re.IGNORECASE),
]


def layout_for(rid: str) -> dict[str, object]:
    try:
        return RID_LAYOUT[rid]
    except KeyError as error:
        raise ValueError(f"unsupported rid: {rid}") from error


def is_diagnostic_file(name: str) -> bool:
    return any(pattern.search(name) for pattern in DIAGNOSTIC_FILE_PATTERNS)


def _read_bytes(path: Path, length: int, position: int = 0) -> bytes:
    with path.open("rb") as handle:
        handle.seek(position)
        return handle.read(length)


def _pe_machine(path: Path) -> int | None:
    head = _read_bytes(path, 0x40)
    if len(head) < 0x40 or head[:2] != b"MZ":
        return None
    pe_offset = struct.unpack_from("<I", head, 0x3C)[0]
    pe = _read_bytes(path, 6, pe_offset)
    if len(pe) < 6 or pe[:4] != b"PE\0\0":
        return None
    return struct.unpack_from("<H", pe, 4)[0]


def _elf_machine(path: Path) -> int | None:
    head = _read_bytes(path, 0x14)
    if len(head) < 0x14 or head[:4] != b"\x7fELF":
        return None
    return struct.unpack_from("<H", head, 0x12)[0]


def _macho_cpu(path: Path) -> set[int] | None:
    head = _read_bytes(path, 4096)
    if len(head) < 8:
        return None
    magic = struct.unpack_from("<I", head, 0)[0]
    if magic in (0xFEEDFACF, 0xFEEDFACE):
        return {struct.unpack_from("<I", head, 4)[0]}
    if magic in (0xCFFAEDFE, 0xCEFAEDFE):
        return {struct.unpack_from(">I", head, 4)[0]}

    fat_stride = 20 if magic == 0xBEBAFECA else 32 if magic == 0xBFBAFECA else 0
    if fat_stride == 0:
        return None
    count = struct.unpack_from(">I", head, 4)[0]
    cpus: set[int] = set()
    for index in range(count):
        offset = 8 + index * fat_stride
        if offset + 4 > len(head):
            break
        cpus.add(struct.unpack_from(">I", head, offset)[0])
    return cpus


def assert_binary_format(path: Path, rid: str) -> None:
    layout = layout_for(rid)
    kind = layout["kind"]
    if kind == "pe":
        actual: int | set[int] | None = _pe_machine(path)
    elif kind == "elf":
        actual = _elf_machine(path)
    else:
        actual = _macho_cpu(path)

    expected = layout["cpu"] if kind == "macho" else layout["machine"]
    if actual is None:
        raise ValueError(f"{path}: not a {kind} binary")
    if kind == "macho":
        if expected not in actual:
            found = ",".join(f"0x{cpu:x}" for cpu in sorted(actual))
            raise ValueError(f"{path}: expected {rid} (0x{expected:x}), found {found}")
    elif actual != expected:
        raise ValueError(f"{path}: expected {rid} (0x{expected:x}), found 0x{actual:x}")
