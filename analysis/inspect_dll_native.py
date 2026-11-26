import json
import re
import sys
from pathlib import Path

BASE = Path(__file__).parent
sys.path.insert(0, str(BASE / "pydeps"))
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_OP_IMM, X86_REG_RIP

path = BASE / "embedded_EAAntiCheat.GameServiceLauncher.dll"
data = path.read_bytes()
pe = pefile.PE(data=data)
base = pe.OPTIONAL_HEADER.ImageBase
strings = []
for regex, enc in [(rb"[\x20-\x7e]{4,}", "ascii"), (rb"(?:[\x20-\x7e]\x00){4,}", "utf16le")]:
    for m in re.finditer(regex, data):
        off = m.start()
        try:
            va = base + pe.get_rva_from_offset(off)
        except Exception:
            va = None
        text = m.group().decode("ascii" if enc == "ascii" else "utf-16le")
        strings.append({"offset": off, "va": va, "encoding": enc, "text": text})
strings.sort(key=lambda s: s["offset"])
with (BASE / "dll_strings.txt").open("w", encoding="utf-8") as f:
    for s in strings:
        f.write(f"{s['offset']:08x} {s['va'] or 0:016x} {s['encoding']:7s} {s['text']}\n")

imports = {}
import_by_va = {}
for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
    dll = entry.dll.decode("ascii", "replace")
    imports[dll] = []
    for imp in entry.imports:
        name = imp.name.decode("ascii", "replace") if imp.name else str(imp.ordinal)
        imports[dll].append({"name": name, "iat_va": hex(imp.address)})
        import_by_va[imp.address] = f"{dll}!{name}"
summary = {"sha256": __import__('hashlib').sha256(data).hexdigest(), "size": len(data),
           "image_base": hex(base), "entrypoint": hex(base + pe.OPTIONAL_HEADER.AddressOfEntryPoint),
           "imports": imports, "sections": []}
for s in pe.sections:
    summary["sections"].append({"name": s.Name.rstrip(b"\0").decode("ascii", "replace"),
                                "rva": hex(s.VirtualAddress), "raw_offset": hex(s.PointerToRawData),
                                "raw_size": hex(s.SizeOfRawData), "characteristics": hex(s.Characteristics)})
(BASE / "dll_pe_summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
string_by_va = {s["va"]: s["text"] for s in strings if s["va"] is not None}
md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
lines = []
for s in pe.sections:
    if not s.Characteristics & 0x20000000:
        continue
    name = s.Name.rstrip(b"\0").decode("ascii", "replace")
    start = base + s.VirtualAddress
    lines.append(f"\n; SECTION {name} VA={start:#x}\n")
    for ins in md.disasm(s.get_data(), start):
        notes = []
        for op in ins.operands:
            target = None
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                target = ins.address + ins.size + op.mem.disp
            elif op.type == X86_OP_IMM:
                target = op.imm
            if target in import_by_va:
                notes.append(import_by_va[target])
            elif target in string_by_va:
                notes.append(repr(string_by_va[target]))
            elif target is not None and op.type == X86_OP_MEM:
                notes.append(f"target={target:#x}")
        suffix = " ; " + " | ".join(notes) if notes else ""
        lines.append(f"{ins.address:016x}  {ins.bytes.hex(' '):<28} {ins.mnemonic:<9} {ins.op_str}{suffix}\n")
(BASE / "dll_disassembly.txt").write_text("".join(lines), encoding="utf-8")
print(f"Extracted {len(strings)} strings and disassembled executable sections")
