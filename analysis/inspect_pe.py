import json
import math
import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent / "pydeps"))
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_REG_RIP

ROOT = Path(__file__).parent
EXE = ROOT / "EAAntiCheat.GameServiceLauncher.exe"
data = EXE.read_bytes()
pe = pefile.PE(data=data, fast_load=False)


def entropy(blob):
    if not blob:
        return 0.0
    counts = [0] * 256
    for b in blob:
        counts[b] += 1
    total = len(blob)
    return -sum((c / total) * math.log2(c / total) for c in counts if c)

summary = {
    "sha256": __import__("hashlib").sha256(data).hexdigest(),
    "size": len(data),
    "machine": hex(pe.FILE_HEADER.Machine),
    "timestamp_raw": pe.FILE_HEADER.TimeDateStamp,
    "characteristics": hex(pe.FILE_HEADER.Characteristics),
    "subsystem": pe.OPTIONAL_HEADER.Subsystem,
    "image_base": hex(pe.OPTIONAL_HEADER.ImageBase),
    "entrypoint_rva": hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint),
    "entrypoint_va": hex(pe.OPTIONAL_HEADER.ImageBase + pe.OPTIONAL_HEADER.AddressOfEntryPoint),
    "size_of_image": hex(pe.OPTIONAL_HEADER.SizeOfImage),
    "checksum": hex(pe.OPTIONAL_HEADER.CheckSum),
    "dll_characteristics": hex(pe.OPTIONAL_HEADER.DllCharacteristics),
    "sections": [],
    "imports": {},
    "exports": [],
    "version_info": {},
    "debug": [],
    "tls": bool(getattr(pe, "DIRECTORY_ENTRY_TLS", None)),
    "load_config": bool(getattr(pe, "DIRECTORY_ENTRY_LOAD_CONFIG", None)),
    "resources": [],
}
for s in pe.sections:
    blob = s.get_data()
    summary["sections"].append({
        "name": s.Name.rstrip(b"\0").decode("ascii", "replace"),
        "rva": hex(s.VirtualAddress),
        "va": hex(pe.OPTIONAL_HEADER.ImageBase + s.VirtualAddress),
        "virtual_size": hex(s.Misc_VirtualSize),
        "raw_offset": hex(s.PointerToRawData),
        "raw_size": hex(s.SizeOfRawData),
        "characteristics": hex(s.Characteristics),
        "entropy": round(entropy(blob), 3),
        "md5": s.get_hash_md5(),
        "sha256": s.get_hash_sha256(),
    })
if hasattr(pe, "DIRECTORY_ENTRY_IMPORT"):
    for entry in pe.DIRECTORY_ENTRY_IMPORT:
        summary["imports"][entry.dll.decode("ascii", "replace")] = [
            {"name": imp.name.decode("ascii", "replace") if imp.name else None,
             "ordinal": imp.ordinal,
             "iat_va": hex(imp.address)}
            for imp in entry.imports
        ]
if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
    for exp in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        summary["exports"].append({"name": exp.name.decode("ascii", "replace") if exp.name else None,
                                   "ordinal": exp.ordinal, "rva": hex(exp.address)})
if hasattr(pe, "FileInfo"):
    for group in pe.FileInfo:
        for item in group:
            if item.Key == b"StringFileInfo":
                for st in item.StringTable:
                    for k, v in st.entries.items():
                        summary["version_info"][k.decode("utf-8", "replace")] = v.decode("utf-8", "replace")
            elif item.Key == b"VarFileInfo":
                summary["version_info"]["VarFileInfo"] = str(item.Var)
if hasattr(pe, "DIRECTORY_ENTRY_DEBUG"):
    for d in pe.DIRECTORY_ENTRY_DEBUG:
        item = {"type": d.struct.Type, "size": d.struct.SizeOfData}
        try:
            item["pdb"] = pe.get_debug_directory_data(d).decode("ascii", "replace") if False else None
        except Exception:
            pass
        off = d.struct.PointerToRawData
        raw = data[off:off+d.struct.SizeOfData]
        m = re.search(rb"([A-Za-z]:)?[^\x00]+\.pdb\x00", raw, re.I)
        if m:
            item["pdb"] = m.group(0).rstrip(b"\0").decode("utf-8", "replace")
        item["raw_hex_prefix"] = raw[:64].hex()
        summary["debug"].append(item)
if hasattr(pe, "DIRECTORY_ENTRY_RESOURCE"):
    def walk(entries, path=()):
        for e in entries:
            name = str(e.name) if e.name is not None else str(e.id)
            p = path + (name,)
            if hasattr(e, "directory"):
                walk(e.directory.entries, p)
            else:
                summary["resources"].append({"path": p, "rva": hex(e.data.struct.OffsetToData),
                                             "size": e.data.struct.Size, "codepage": e.data.struct.CodePage})
    walk(pe.DIRECTORY_ENTRY_RESOURCE.entries)

(ROOT / "pe_summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")

# Extract strings with file offsets and virtual addresses.
strings = []
for m in re.finditer(rb"[\x20-\x7e]{4,}", data):
    off = m.start()
    try:
        va = pe.get_rva_from_offset(off) + pe.OPTIONAL_HEADER.ImageBase
    except Exception:
        va = None
    strings.append({"offset": off, "va": va, "encoding": "ascii", "text": m.group().decode("ascii")})
for m in re.finditer(rb"(?:[\x20-\x7e]\x00){4,}", data):
    off = m.start()
    try:
        va = pe.get_rva_from_offset(off) + pe.OPTIONAL_HEADER.ImageBase
    except Exception:
        va = None
    strings.append({"offset": off, "va": va, "encoding": "utf16le", "text": m.group().decode("utf-16le")})
strings.sort(key=lambda x: x["offset"])
with (ROOT / "strings.txt").open("w", encoding="utf-8") as f:
    for s in strings:
        f.write(f"{s['offset']:08x} {s['va'] or 0:016x} {s['encoding']:7s} {s['text']}\n")

# Import-address annotations for disassembly.
import_by_va = {}
for dll, imports in summary["imports"].items():
    for imp in imports:
        import_by_va[int(imp["iat_va"], 16)] = f"{dll}!{imp['name'] or imp['ordinal']}"
string_by_va = {s["va"]: s["text"] for s in strings if s["va"] is not None}

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
lines = []
for s in pe.sections:
    name = s.Name.rstrip(b"\0").decode("ascii", "replace")
    if not (s.Characteristics & 0x20000000):
        continue
    base = pe.OPTIONAL_HEADER.ImageBase + s.VirtualAddress
    code = s.get_data()
    lines.append(f"\n; SECTION {name} VA={base:#x} SIZE={len(code):#x}\n")
    for ins in md.disasm(code, base):
        annotations = []
        for op in ins.operands:
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                target = ins.address + ins.size + op.mem.disp
                if target in import_by_va:
                    annotations.append(import_by_va[target])
                elif target in string_by_va:
                    annotations.append(repr(string_by_va[target]))
                else:
                    annotations.append(f"target={target:#x}")
            elif op.type == 2 and op.imm in import_by_va:  # X86_OP_IMM
                annotations.append(import_by_va[op.imm])
            elif op.type == 2 and op.imm in string_by_va:
                annotations.append(repr(string_by_va[op.imm]))
        suffix = " ; " + " | ".join(annotations) if annotations else ""
        lines.append(f"{ins.address:016x}  {ins.bytes.hex(' '):<28} {ins.mnemonic:<9} {ins.op_str}{suffix}\n")
(ROOT / "disassembly.txt").write_text("".join(lines), encoding="utf-8")
print(json.dumps(summary, indent=2))
print(f"Extracted {len(strings)} strings")
