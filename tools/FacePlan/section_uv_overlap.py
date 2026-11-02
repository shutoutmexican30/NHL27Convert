"""Per-section UV overlap between an NHL 24 head and an NHL 27 head.

The head surface was confirmed as a 1:1 quantized-UV correspondence (3,146 of 3,146). This asks the same question of
every section - eyes, eyelashes - because those are the parts still unmapped. Read only.

usage: section_uv_overlap.py <template-export-report.json> <template.lod0.bin> <source.meshset>
"""
import argparse, json, struct, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import transfer_surface as ts
import mesh27_probe as probe


def quant(uv):
    return struct.pack('<2e', float(uv[0]), float(uv[1]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('template_report', type=Path)
    ap.add_argument('template_lod', type=Path)
    ap.add_argument('source_resource', type=Path)
    a = ap.parse_args()

    reports = json.loads(a.template_report.read_text(encoding='utf-8-sig'))
    report = next(r for r in reports if Path(r['name']).name + '.lod0.bin' == a.template_lod.name)
    old = {s['name']: s for s in ts.template_sections(report, a.template_lod.read_bytes())}
    new = {s['name']: s for s in probe.parse_head(a.source_resource)['sections']}

    print('template : %s  (%d sections)' % (Path(report['name']).name, len(old)))
    print('source   : %s  (%d sections)' % (a.source_resource.name, len(new)))
    print()
    print('%-14s %7s %7s %7s %7s %7s %7s' % ('section', 'tri24', 'tri27', 'uv24', 'uv27', 'shared', 'missing'))
    for name in sorted(set(old) | set(new)):
        o, n = old.get(name), new.get(name)
        if o is None or n is None:
            side = 'only in 24' if o is not None else 'only in 27'
            print('%-14s %7s %7s %7s %7s %7s   %s' % (
                name,
                len(o['indices']) // 3 if o else '-', len(n['triangles']) if n else '-',
                len({quant(u) for u in o['uv']}) if o else '-', len({quant(u) for u in n['uv']}) if n else '-',
                '-', side))
            continue
        u24 = {quant(u) for u in o['uv']}
        u27 = {quant(u) for u in n['uv']}
        shared = len(u24 & u27)
        print('%-14s %7d %7d %7d %7d %7d %7d' % (
            name, len(o['indices']) // 3, n['triangles'], len(u24), len(u27), shared, len(u24 - u27)))
    print()
    print('shared == uv24 means every template UV exists in the source: the surface transfer applies unchanged.')


if __name__ == '__main__':
    main()