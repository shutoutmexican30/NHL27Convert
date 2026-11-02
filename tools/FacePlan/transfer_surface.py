"""Stage a geometry-only face-surface prototype; never edits a game.

Maps the NHL 27 mat_head surface into an exported NHL 24 LOD through shared
UV triangle topology. Keeps NHL 24 indices, skinning, UVs and other sections.
Eyes, lashes, textures, animation bindings and a dedicated destination slot
remain separate requirements. Output is explicitly NOT a complete face mod.
"""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import re
import struct
from mesh27_probe import parse_head, write_glb


def template_sections(report, raw):
    result=[]; vb=int(report['lodMetadata']['VertexBufferSize'])
    for section in report['sections']:
        if section.get('skipped'):continue
        fields={};strides=collections.defaultdict(int);count=section['VertexCount']
        for text in section['layout']:
            m=re.fullmatch(r's(\d+)\+(\d+) (\w+) (\w+) \((\d+)\)',text)
            if not m:raise ValueError('Unsupported exported vertex declaration')
            stream,offset,usage,fmt,size=int(m[1]),int(m[2]),m[3],m[4],int(m[5])
            fields[usage]=dict(stream=stream,offset=offset,format=fmt,size=size)
            strides[stream]=max(strides[stream],offset+size)
        if sum(strides.values())!=int(section['metadata']['VertexStride']):raise ValueError('Vertex stream strides disagree')
        bases={};cursor=int(section['metadata']['VertexOffset'])
        for stream,stride in sorted(strides.items()):bases[stream]=cursor;cursor+=stride*count
        if cursor>vb:raise ValueError('Template vertex range exceeds buffer')
        def read(usage,fmt,expected):
            f=fields[usage]
            if f['format']!=expected:raise ValueError('Unsupported template format: '+usage)
            return [struct.unpack_from(fmt,raw,bases[f['stream']]+i*strides[f['stream']]+f['offset']) for i in range(count)]
        pos=read('Pos','<3f','Float3');uv=read('TexCoord0','<2e','Half2')
        at=vb+int(section['metadata']['StartIndex'])*2;n=section['PrimitiveCount']*3
        indices=struct.unpack_from('<'+'H'*n,raw,at)
        if any(i>=count for i in indices):raise ValueError('Template index out of range')
        result.append(dict(name=section['Name'],positions=pos,uv=uv,indices=indices,fields=fields,bases=bases,strides=strides,
                           min=[min(p[k] for p in pos) for k in range(3)],max=[max(p[k] for p in pos) for k in range(3)]))
    return result


def map_surface(target, source):
    quant=lambda uv:struct.pack('<2e',*uv)
    key=lambda part,tri:tuple(sorted(quant(part['uv'][v]) for v in tri))
    lookup=collections.defaultdict(list)
    for i in range(0,len(source['indices']),3):
        tri=source['indices'][i:i+3];lookup[key(source,tri)].append(tri)
    assigned=collections.defaultdict(set)
    for i in range(0,len(target['indices']),3):
        tri=target['indices'][i:i+3];candidates=lookup.get(key(target,tri),[])
        if not candidates:raise ValueError('Source lacks a template UV triangle')
        for vertex in tri:
            values={tuple(source['positions'][v]) for src in candidates for v in src if quant(source['uv'][v])==quant(target['uv'][vertex])}
            if len(values)==1:assigned[vertex].update(values)
    if len(assigned)!=len(target['positions']) or any(len(v)!=1 for v in assigned.values()):
        raise ValueError('UV mapping is incomplete or conflicting; refusing prototype')
    return [next(iter(assigned[i])) for i in range(len(target['positions']))]


def subtract(a,b):return tuple(x-y for x,y in zip(a,b))
def add(a,b):return tuple(x+y for x,y in zip(a,b))
def scale(v,s):return tuple(x*s for x in v)
def dot(a,b):return sum(x*y for x,y in zip(a,b))
def cross(a,b):return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def normalized(v):
    length=math.sqrt(dot(v,v))
    return scale(v,1/length) if length>1e-12 else None


def basis(positions,uv,indices):
    normals=[(0.,0.,0.) for p in positions];tangents=list(normals);bitangents=list(normals)
    for at in range(0,len(indices),3):
        a,b,c=indices[at:at+3];e1=subtract(positions[b],positions[a]);e2=subtract(positions[c],positions[a])
        n=cross(e1,e2);u1,v1=subtract(uv[b],uv[a]);u2,v2=subtract(uv[c],uv[a]);det=u1*v2-v1*u2
        for i in (a,b,c):normals[i]=add(normals[i],n)
        if abs(det)>1e-12:
            t=scale(subtract(scale(e1,v2),scale(e2,v1)),1/det)
            bt=scale(subtract(scale(e2,u1),scale(e1,u2)),1/det)
            for i in (a,b,c):tangents[i]=add(tangents[i],t);bitangents[i]=add(bitangents[i],bt)
    output=[]
    for n,t,b in zip(normals,tangents,bitangents):
        n=normalized(n)
        t=normalized(subtract(t,scale(n,dot(n,t)))) if n else None
        if n is None or t is None:output.append(None);continue
        b=scale(cross(n,t),-1 if dot(cross(n,t),b)<0 else 1)
        output.append((n,t,b))
    return output


def stage(report, raw, source, output):
    sections=template_sections(report,raw)
    target=next(s for s in sections if s['name']=='mat_head')
    face=next(s for s in source['sections'] if s['name']=='mat_head')
    points=map_surface(target,face);vectors=basis(points,target['uv'],target['indices'])
    result=bytearray(raw);allowed=bytearray(len(raw));fallback=0
    def write(usage,i,data):
        field=target['fields'][usage];at=target['bases'][field['stream']]+i*target['strides'][field['stream']]+field['offset']
        result[at:at+len(data)]=data;allowed[at:at+len(data)]=bytes([1])*len(data)
    for i,p in enumerate(points):
        write('Pos',i,struct.pack('<3f',*p))
        if vectors[i] is None:fallback+=1;continue
        for usage,value in zip(['Normal','Tangent','Binormal'],vectors[i]):
            if target['fields'][usage]['format']!='Half4':raise ValueError('Unsupported tangent basis storage')
            write(usage,i,struct.pack('<3e',*value)) # Preserve the original fourth component.
    if len(result)!=len(raw) or any(a!=b and not allowed[i] for i,(a,b) in enumerate(zip(raw,result))):
        raise ValueError('Prototype changed protected bytes')
    vb=int(report['lodMetadata']['VertexBufferSize'])
    if result[vb:]!=raw[vb:]:raise ValueError('Index buffer changed')
    verified=template_sections(report,result)
    roundtrip=next(s for s in verified if s['name']=='mat_head')['positions']
    if any(a!=b for a,b in zip(points,roundtrip)):raise ValueError('Position read-back differs')
    output.mkdir(parents=True,exist_ok=True)
    blob=output/'demidov-surface-prototype.lod0.bin';blob.write_bytes(result)
    write_glb(dict(sections=verified),output/'demidov-surface-prototype.geometry-only.glb')
    summary=dict(source=source['resource'],template=report['name'],mapped_head_vertices=len(points),conflicting_assignments=0,
                 bytes=len(result),size_unchanged=True,indices_unchanged=True,skinning_and_uv_bytes_unchanged=True,
                 changed_bytes=sum(a!=b for a,b in zip(raw,result)),protected_bytes_unchanged=True,
                 basis_vertices_retaining_old_vectors=fallback,position_readback_exact=True,
                 sha256=hashlib.sha256(result).hexdigest(),game_ready=False,
                 remaining=['Map or rebuild eyes and eyelashes','Convert and validate face normal/color/specmask textures',
                            'Validate animation/rest-pose/material bindings','Reserve and verify an isolated destination head slot',
                            'Perform a backed-up test-copy gameplay check'])
    (output/'surface-transfer.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    return summary


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('template_report',type=Path);p.add_argument('template_lod',type=Path)
    p.add_argument('source_resource',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
    reports=json.loads(a.template_report.read_text(encoding='utf-8-sig'))
    report=next(r for r in reports if Path(r['name']).name+'.lod0.bin'==a.template_lod.name)
    print(json.dumps(stage(report,a.template_lod.read_bytes(),parse_head(a.source_resource),a.output),indent=2))


if __name__=='__main__':main()
