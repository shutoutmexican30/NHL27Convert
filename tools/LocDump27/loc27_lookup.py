"""Read-only lookup in an NHL 27 language DB (two T3DB tables; NHL 24 has one). Same row/pool/Huffman layout as
Suite.Rosters.LanguageStrings: 16-byte rows (text offset, CRC key hash, key offset, 0) after a 36-byte table header and
3 field descriptors; the pool holds a Huffman tree then 2-byte-length-prefixed MSB-first strings.
Usage: python -I loc27_lookup.py <eng_us.db> KEY [KEY ...]   (prints KEY<TAB>text, or KEY<TAB><missing>)
"""
import struct
import sys


def crc(key):
    c = 0
    for ch in key.encode("ascii"):
        c ^= ch & 0xDF
        for _ in range(8):
            c = (c >> 1) ^ 0xEDB88320 if c & 1 else c >> 1
    return c


class Table:
    def __init__(self, d, start):
        self.d = d
        u = lambda o: struct.unpack_from("<I", d, o)[0]
        self.count = u(start + 16) >> 16
        self.pool_len = u(start + 12)
        self.rows_at = start + 36 + 48
        self.rows = {}
        for i in range(self.count):
            k, t, h = struct.unpack_from("<3I", d, self.rows_at + 16 * i)  # NHL 27 order: key, text, hash (NHL 24: text, hash, key)
            self.rows.setdefault(h, t)
        self.pool_at = self.rows_at + 16 * self.count

    def text(self, off):
        d, p = self.d, self.pool_at
        n = d[p + off] << 8 | d[p + off + 1]
        out, bit, node = [], 0, 0
        while len(out) < n:
            side = d[p + off + 2 + bit // 8] >> (7 - bit % 8) & 1
            bit += 1
            a, b = d[p + node * 4 + side * 2], d[p + node * 4 + side * 2 + 1]
            if a == 0 and b != 0:
                out.append(chr(b)); node = 0
            else:
                node = a | b << 8
        return "".join(out)

    def find(self, key):
        t = self.rows.get(crc(key))
        return None if t is None or t >= self.pool_len else self.text(t)


d = open(sys.argv[1], "rb").read()
ntab = struct.unpack_from("<I", d, 16)[0]
dir_end = 24 + 8 * ntab
first = 44 if ntab == 2 else 36  # verified on NHL 27 (2 tables) and NHL 24 (1 table)
offsets = [struct.unpack_from("<I", d, 24 + 8 * i + 4)[0] for i in range(ntab)]
tables = []
for off in offsets:
    try:
        tables.append(Table(d, first + off))
    except Exception as e:  # noqa: BLE001
        print(f"# table at {first + off}: {e}", file=sys.stderr)
for t in tables:
    print(f"# table rows {t.count} pool {t.pool_len}", file=sys.stderr)
for key in sys.argv[2:]:
    text = None
    for t in tables:
        try:
            text = t.find(key)
        except Exception:  # noqa: BLE001
            text = None
        if text is not None:
            break
    print(f"{key}\t{text if text is not None else '<missing>'}")
