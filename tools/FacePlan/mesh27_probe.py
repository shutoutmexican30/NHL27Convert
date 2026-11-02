"""Read-only NHL 27 PS5 head MeshSet/LOD0 diagnostic.

Accepts resources/chunks exported from the user's game. Supports only the
observed (0xc0, 0xb8, 0x170) layout and validates every buffer/index before
exporting geometry-only GLBs. These GLBs have NO animation/skeleton binding
and are not an import format or a game-ready face conversion.
"""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import struct
import uuid


def parse_head(resource_path):
    header = resource_path.read_bytes()
    def need(ok, message):
        if not ok:
            raise ValueError(f'{resource_path.name}: {message}')
    def unpack(fmt, at):
        need(0 <= at <= len(header) - struct.calcsize(fmt), 'header read out of bounds')
        return struct.unpack_from(fmt, header, at)
    need(len(header) >= 208, 'resource too short')
    need(unpack('<3I', 0) == (0xc0, 0xb8, 0x170), 'unsupported MeshSet layout')
    base = 16
    def pointer(at):
        rel = unpack('<Q', at)[0]
        need(rel > 0 and rel < len(header)-base, 'invalid relative pointer')
        return base + rel
    lod = pointer(base + 32)
    need(unpack('<I', lod)[0] == 1, 'expected skinned LOD')
    section_count = unpack('<I', lod + 8)[0]
    need(1 <= section_count <= 16, 'invalid section count')
    sections_at = pointer(lod + 12)
    index_bytes, vertex_bytes = unpack('<2I', lod + 0x58)
    ident = uuid.UUID(bytes_le=header[lod+0x70:lod+0x80])
    chunk_path = resource_path.with_name(resource_path.stem + '.' + str(ident) + '.chunk')
    chunk = chunk_path.read_bytes()
    need(vertex_bytes + index_bytes <= len(chunk) < vertex_bytes + index_bytes + 16, 'chunk size disagrees with buffers')
    bone_count = unpack('<I', lod + 0xac)[0]
    need(1 <= bone_count <= 2048, 'invalid bone count')
    bones_at = pointer(lod + 0xb0)
    bone_map = unpack('<' + 'H' * bone_count, bones_at)
    result = []
    for number in range(section_count):
        section = sections_at + number * 0x170
        name_at = pointer(section + 8)
        name_end = header.find(b'\0', name_at, name_at + 256)
        need(name_end > name_at, 'invalid section name')
        name = header[name_at:name_end].decode('ascii')
        triangles, start_index, vertex_offset, count = unpack('<4I', section + 32)
        need(0 < triangles <= 100000 and 0 < count <= 65535, 'implausible section counts')
        need((start_index + triangles*3)*2 <= index_bytes, 'index range exceeds buffer')
        strides = list(header[section+0xb0:section+0xd0:2])
        need(sum(strides) == header[section+0x1e], 'declared vertex stride disagrees with streams')
        need(vertex_offset + sum(strides)*count <= vertex_bytes, 'vertex streams exceed buffer')
        bases = []; cursor = vertex_offset
        for stride in strides:
            bases.append(cursor); cursor += stride*count
        elements = {}
        for n in range(16):
            usage, fmt, offset, stream = unpack('<4B', section+0x70+n*4)
            if stream == 0xff:
                continue
            if usage == 0:
                continue
            need(stream < 16 and strides[stream] > 0, 'element refers to missing stream')
            need(usage not in elements, 'duplicate vertex element usage')
            elements[usage] = (fmt, offset, stream)
        def read(usage, expected, fmt):
            need(usage in elements, f'vertex usage {usage} absent')
            actual, offset, stream = elements[usage]
            need(actual == expected and offset+struct.calcsize(fmt) <= strides[stream], f'unsupported vertex format for usage {usage}')
            return [struct.unpack_from(fmt, chunk, bases[stream]+i*strides[stream]+offset) for i in range(count)]
        positions = read(1, 3, '<3f')
        uv = read(33, 2, '<2f')
        j1, j2 = read(2, 23, '<4H'), read(3, 23, '<4H')
        w1, w2 = read(4, 13, '<4B'), read(5, 13, '<4B')
        indices = list(struct.unpack_from('<'+'H'*(triangles*3), chunk, vertex_bytes+start_index*2))
        need(all(i < count for i in indices), 'index references missing vertex')
        need(all(math.isfinite(v) and abs(v) < 100 for p in positions for v in p), 'invalid vertex coordinate')
        need(all(math.isfinite(v) and abs(v) < 100 for p in uv for v in p), 'invalid UV coordinate')
        max_influences = 0; over_six = 0; weight_sums = collections.Counter(); weighted_bones = set()
        for a, b, c, d in zip(j1, j2, w1, w2):
            joints, weights = a+b, c+d
            influences = [(j,w) for j,w in zip(joints,weights) if w]
            need(bool(influences), 'vertex has no skinning weights')
            need(all(j < bone_count for j,w in influences), 'weighted bone exceeds bone map')
            max_influences = max(max_influences, len(influences)); over_six += len(influences) > 6
            weight_sums[sum(weights)] += 1
            weighted_bones.update(bone_map[j] for j,w in influences)
        need(all(253 <= s <= 257 for s in weight_sums), 'skinning weights do not approximately sum to 255')
        result.append(dict(name=name, positions=positions, uv=uv, indices=indices,
                           source_vertices=count, triangles=triangles, vertex_stride=sum(strides),
                           unique_positions=len(set(positions)), max_influences=max_influences,
                           vertices_over_six_influences=over_six, weight_sums=dict(weight_sums),
                           used_bones=sorted(weighted_bones),
                           min=[min(p[k] for p in positions) for k in range(3)],
                           max=[max(p[k] for p in positions) for k in range(3)]))
    need(sum(s['triangles']*6 for s in result) == index_bytes, 'sections do not account for whole index buffer')
    return dict(resource=resource_path.name, chunk_id=str(ident), header_bytes=len(header), chunk_bytes=len(chunk),
                vertex_bytes=vertex_bytes, index_bytes=index_bytes, bone_count=bone_count,
                bone_map=list(bone_map), sections=result, game_ready=False)


def write_glb(mesh, path):
    binary=bytearray(); views=[]; accessors=[]; primitives=[]; materials=[]
    def view(data,target):
        offset=len(binary);binary.extend(data)
        while len(binary)%4:binary.append(0)
        views.append(dict(buffer=0,byteOffset=offset,byteLength=len(data),target=target));return len(views)-1
    for section in mesh['sections']:
        p=section['positions']; uv=section['uv']; idx=section['indices']
        pi=view(b''.join(struct.pack('<3f',*x) for x in p),34962)
        accessors.append(dict(bufferView=pi,componentType=5126,count=len(p),type='VEC3',min=section['min'],max=section['max']));pa=len(accessors)-1
        ui=view(b''.join(struct.pack('<2f',*x) for x in uv),34962)
        accessors.append(dict(bufferView=ui,componentType=5126,count=len(uv),type='VEC2'));ua=len(accessors)-1
        ii=view(struct.pack('<'+'H'*len(idx),*idx),34963)
        accessors.append(dict(bufferView=ii,componentType=5123,count=len(idx),type='SCALAR'));ia=len(accessors)-1
        materials.append(dict(name=section['name'],doubleSided=True,pbrMetallicRoughness=dict(baseColorFactor=[0.72,0.75,0.8,1],metallicFactor=0,roughnessFactor=0.8)))
        primitives.append(dict(attributes={'POSITION':pa,'TEXCOORD_0':ua},indices=ia,material=len(materials)-1))
    doc=dict(asset=dict(version='2.0',generator='CIMS NHL27 head geometry diagnostic; no skeleton binding'),
             scene=0,scenes=[dict(nodes=[0])],nodes=[dict(mesh=0)],meshes=[dict(name=path.stem,primitives=primitives)],
             materials=materials,bufferViews=views,accessors=accessors,buffers=[dict(byteLength=len(binary))])
    js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4)
    total=12+8+len(js)+8+len(binary)
    path.write_bytes(struct.pack('<III',0x46546c67,2,total)+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(binary),0x004e4942)+binary)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resources',type=Path);parser.add_argument('output',type=Path)
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    reports=[]
    for path in sorted(args.resources.glob('*.meshset')):
        mesh=parse_head(path);out=args.output/(path.stem+'.geometry-only.glb');write_glb(mesh,out)
        summary={k:v for k,v in mesh.items() if k!='sections'}
        summary['sections']=[{k:v for k,v in s.items() if k not in ['positions','uv','indices']} for s in mesh['sections']]
        summary['geometry_glb']=str(out);summary['sha256']=hashlib.sha256(out.read_bytes()).hexdigest()
        reports.append(summary)
        print(path.stem, 'triangles',sum(s['triangles'] for s in mesh['sections']), 'bones',mesh['bone_count'],
              'max influences',max(s['max_influences'] for s in mesh['sections']))
    (args.output/'geometry-validation.json').write_text(json.dumps(reports,indent=2),encoding='utf-8')


if __name__=='__main__':main()
