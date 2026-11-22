import sys, glob

def magic(p):
    with open(p, 'rb') as f:
        head = f.read(64)
    return head[:4].hex()

for label, pat in [
    ("update-UPDATE Data", r"D:\stock-restore\update-UPDATE\Data\Gen4b\**\*.toc"),
    ("update-UPDATE Patch", r"D:\stock-restore\update-UPDATE\Patch\Gen4b\**\*.toc"),
]:
    files = glob.glob(pat, recursive=True)
    print(label, len(files), "tocs")
    from collections import Counter
    c = Counter(magic(f) for f in files[:40])
    for m, n in c.most_common():
        print("   ", m, n)
    for f in files[:3]:
        print("    e.g.", f)
