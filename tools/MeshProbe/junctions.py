import struct
paths = [r"C:\Users\devin\AppData\Local\CenterIceModdingSuite\nhl24view\Data\Gen4b",
         r"C:\Users\devin\AppData\Local\CenterIceModdingSuite\nhl24view\Patch\Gen4b"]
for p in paths:
    d = open(p, 'rb').read()
    sub_off, sub_len, pr_off, pr_len = struct.unpack_from('<HHHH', d, 8)
    kind = p.split('\\')[-2]
    print(kind, '->', d[16 + pr_off:16 + pr_off + pr_len].decode('utf-16le'))
