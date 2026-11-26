import sys
from pathlib import Path

BASE = Path(__file__).parent
sys.path.insert(0, str(BASE / "pydeps"))
import pefile
import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.error import MethodBodyFormatError
from dncil.clr.token import Token, StringToken, InvalidToken
from dncil.cil.body.reader import CilMethodBodyReaderBase

path = BASE / "embedded_EAAntiCheat.GameServiceLauncher.dll"
data = path.read_bytes()
pe = pefile.PE(data=data)
print("PE entrypoint RVA:", hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint))
print("Sections:")
for s in pe.sections:
    print(" ", s.Name.rstrip(b"\0").decode(), hex(s.VirtualAddress), hex(s.Misc_VirtualSize), hex(s.SizeOfRawData))
print("Imports:")
for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
    print(" ", entry.dll.decode())
    for imp in entry.imports:
        print("   ", imp.name.decode() if imp.name else imp.ordinal)
print("Exports:")
for exp in getattr(getattr(pe, "DIRECTORY_ENTRY_EXPORT", None), "symbols", []):
    print(" ", exp.name, hex(exp.address))

net = dnfile.dnPE(str(path))
print("Managed:", bool(net.net))
if not net.net:
    sys.exit()
print("Metadata version:", net.net.metadata.struct.Version)
print("Runtime version:", net.net.struct.MajorRuntimeVersion, net.net.struct.MinorRuntimeVersion)
print("Assembly:", net.net.mdtables.Assembly.rows if net.net.mdtables.Assembly else None)

lines = []
for t in net.net.mdtables.TypeDef.rows:
    lines.append(f"\nTYPE {t.TypeNamespace}.{t.TypeName}\n")
    for m in t.MethodList:
        try:
            row = m.row
            lines.append(f" METHOD {row.Name} RVA={row.Rva:#x} token={row.token if hasattr(row, 'token') else ''}\n")
        except Exception as e:
            lines.append(f" METHOD <error {e}>\n")
(BASE / "managed_types.txt").write_text("".join(lines), encoding="utf-8")

# The standard dncil reader expects a PE object with get_data and an image base.
class Reader(CilMethodBodyReaderBase):
    def __init__(self, pe):
        self.pe = pe
    def read(self, rva, size):
        return self.pe.get_data(rva, size)
    def get_method_name(self, token):
        return ""

reader = Reader(pe)
output = []
for t in net.net.mdtables.TypeDef.rows:
    type_name = f"{t.TypeNamespace}.{t.TypeName}"
    for m in t.MethodList:
        row = m.row
        output.append(f"\n// {type_name}::{row.Name} RVA={row.Rva:#x}\n")
        if not row.Rva:
            output.append("// no body\n")
            continue
        try:
            body = CilMethodBody(row, reader)
        except Exception as e:
            output.append(f"// body parse error: {e}\n")
            continue
        output.append(f"// maxstack={body.max_stack} locals={body.local_var_sig_tok} size={body.size}\n")
        for ins in body.instructions:
            operand = str(ins.operand)
            if isinstance(ins.operand, StringToken):
                try:
                    operand = repr(net.net.user_strings.get(ins.operand.rid).value)
                except Exception:
                    pass
            elif isinstance(ins.operand, Token):
                try:
                    table = net.net.mdtables.tables.get(ins.operand.table)
                    if table and 0 < ins.operand.rid <= len(table.rows):
                        r = table.rows[ins.operand.rid - 1]
                        operand = f"{table.name}[{ins.operand.rid}] {r}"
                except Exception:
                    pass
            output.append(f"IL_{ins.offset:04x}: {ins.opcode.name:<12} {operand}\n")
(BASE / "managed_cil.txt").write_text("".join(output), encoding="utf-8")
print("Wrote managed_types.txt and managed_cil.txt")
