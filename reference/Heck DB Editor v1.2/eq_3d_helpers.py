"""
eq_3d_helpers.py — self-contained 3-D rendering helpers for the equipment preview.

Ported from texture_editor.py; no dependency on that project.
Requires: PyOpenGL, pyopengltk, Pillow, numpy  (all installed by the launcher bat).
"""

import io
import re
import struct
import zlib

try:
    from pyopengltk import OpenGLFrame as _OpenGLFrame
    _GL_OK = True
except Exception:
    _OpenGLFrame = object
    _GL_OK = False


# ---------------------------------------------------------------------------
# EA chunkzip decompressor
# ---------------------------------------------------------------------------

def _decompress_chunkzip(data: bytes) -> bytes:
    off = 8
    dummy,      = struct.unpack_from(">I", data, off); off += 4
    chunk_size, = struct.unpack_from(">I", data, off); off += 4
    if dummy <= 2:
        fullsize,   = struct.unpack_from(">I", data, 12)
        chunk_size, = struct.unpack_from(">I", data, 16)
        n_chunks,   = struct.unpack_from(">I", data, 20)
        off = 40
    else:
        fullsize = dummy
        n_chunks = (fullsize + chunk_size - 1) // chunk_size
    out = bytearray()
    for _ in range(n_chunks):
        if dummy <= 2:
            while off % 16 != 8:
                off += 8
        zsize, = struct.unpack_from(">I", data, off); off += 4
        if dummy <= 2:
            off += 4
        chunk = data[off:off + zsize]; off += zsize
        try:
            out.extend(zlib.decompress(chunk, -15))
        except zlib.error:
            out.extend(zlib.decompress(chunk))
    return bytes(out[:fullsize])


def _load_file(path: str) -> bytes:
    with open(path, "rb") as f:
        magic = f.read(8)
        f.seek(0)
        data = f.read()
    if magic == b"chunkzip":
        return _decompress_chunkzip(data)
    if magic == b"chunklzx":
        raise RuntimeError("chunklzx (LZX) compression not supported.")
    return data


# ---------------------------------------------------------------------------
# Texture name scanner
# ---------------------------------------------------------------------------

_NAME_RE = re.compile(
    rb'[A-Za-z0-9_.\\-]+'
    rb'(?:\.Raster|\.Texture|\.RasterData|\.RasterTexture)'
    rb'(?=[^A-Za-z0-9_.\\-]|$)'
)


def _scan_texture_names(data: bytes) -> list:
    seen, names = set(), []
    for m in _NAME_RE.finditer(data):
        s = m.group().decode("ascii", errors="replace")
        if s not in seen:
            seen.add(s)
            names.append(s)
    return names


# ---------------------------------------------------------------------------
# Texture parser — PS3 texlib (.rpsgl)
# ---------------------------------------------------------------------------

_TEXLIB_GPU_START  = 0x0500
_TEXLIB_REC_BASE   = 0x03A0
_TEXLIB_REC_SIZE   = 0x30
_TEXLIB_REC_MAGIC  = b'\x00\x01\x00\x30'
_TEXLIB_DESC_DELTA = 0x18

_PS3_FMT = {
    0x83F1: "DXT1",
    0x83F2: "DXT3",
    0x83F3: "DXT5",
    0x6007: "A8R8G8B8",
}

_DDS_TEMPLATE = bytearray(128)
struct.pack_into('<4s', _DDS_TEMPLATE,   0, b'DDS ')
struct.pack_into('<I',  _DDS_TEMPLATE,   4, 124)
struct.pack_into('<I',  _DDS_TEMPLATE,   8, 0x000A1007)
struct.pack_into('<I',  _DDS_TEMPLATE,  76, 32)
struct.pack_into('<I',  _DDS_TEMPLATE,  80, 0x4)
struct.pack_into('<4s', _DDS_TEMPLATE,  72, b'\x08\x00\x02\x00')
struct.pack_into('<I',  _DDS_TEMPLATE, 108, 0x00401008)


class TextureInfo:
    __slots__ = ("name", "width", "height", "fmt", "mip_count", "pixel_data",
                 "gpu_offset", "gpu_size")

    def __init__(self, name, width, height, fmt, mip_count, pixel_data,
                 gpu_offset=0, gpu_size=0):
        self.name       = name
        self.width      = width
        self.height     = height
        self.fmt        = fmt
        self.mip_count  = mip_count
        self.pixel_data = pixel_data
        self.gpu_offset = gpu_offset
        self.gpu_size   = gpu_size

    def to_dds(self) -> bytes:
        fmt = self.fmt
        if   "DXT1" in fmt: fourcc = b"DXT1"
        elif "DXT3" in fmt: fourcc = b"DXT3"
        elif "DXT5" in fmt: fourcc = b"DXT5"
        else:               fourcc = b"DXT5"
        hdr = bytearray(_DDS_TEMPLATE)
        struct.pack_into('<I',  hdr, 12, self.height)
        struct.pack_into('<I',  hdr, 16, self.width)
        struct.pack_into('<I',  hdr, 28, max(1, self.mip_count))
        struct.pack_into('<4s', hdr, 84, fourcc)
        return bytes(hdr) + self.pixel_data


def _mip_chain_size(w: int, h: int, fmt_id: int, mips: int) -> int:
    bpb = 8 if fmt_id == 0x83F1 else 16
    total = 0
    for m in range(max(1, mips)):
        mw = max(1, w >> m)
        mh = max(1, h >> m)
        total += max(1, (mw + 3) // 4) * max(1, (mh + 3) // 4) * bpb
    return total


def _parse_textures(data: bytes, raw_names: list, filename: str = "") -> list:
    if data[:4] == b'\x89RW4':
        _rw4_gpu_start = struct.unpack_from('>I', data, 0x44)[0]
    else:
        _rw4_gpu_start = None

    rec_base = _TEXLIB_REC_BASE
    if len(data) < rec_base + 4 or data[rec_base:rec_base + 4] != _TEXLIB_REC_MAGIC:
        rec_base = None
        for scan in range(0x100, min(len(data), 0x2000), 4):
            if data[scan:scan + 4] == _TEXLIB_REC_MAGIC:
                rec_base = scan
                break
    if rec_base is None:
        return []

    rec_count, pos = 0, rec_base
    while pos + _TEXLIB_REC_SIZE <= len(data):
        if data[pos:pos + 4] == _TEXLIB_REC_MAGIC:
            rec_count += 1
            pos += _TEXLIB_REC_SIZE
        else:
            break

    name_idx, textures, prev_cumul = 0, [], 0
    for i in range(rec_count):
        rec_off   = rec_base + i * _TEXLIB_REC_SIZE
        desc_ptr  = struct.unpack_from(">I", data, rec_off + 0x04)[0]
        cumul_end = struct.unpack_from(">I", data, rec_off + 0x1C)[0]

        desc_off = desc_ptr + _TEXLIB_DESC_DELTA
        if desc_off + 0x20 > len(data):
            prev_cumul = cumul_end; continue

        desc   = data[desc_off:desc_off + 0x20]
        fmt_id = struct.unpack_from(">H", desc, 0x0A)[0]
        width  = struct.unpack_from(">H", desc, 0x18)[0]
        height = struct.unpack_from(">H", desc, 0x1A)[0]
        mips   = desc[0x1C]

        if width == 0 or height == 0 or fmt_id not in _PS3_FMT:
            prev_cumul = cumul_end; continue

        fmt      = _PS3_FMT[fmt_id]
        mips     = max(1, mips)
        tex_size = _mip_chain_size(width, height, fmt_id, mips)
        rec_block = cumul_end - prev_cumul if cumul_end > prev_cumul else tex_size
        if _rw4_gpu_start is not None and rec_block < tex_size // 4:
            file_start = _rw4_gpu_start + prev_cumul
            block_size = tex_size
        else:
            file_start = _TEXLIB_GPU_START + prev_cumul
            block_size = rec_block
        actual = min(tex_size, block_size, max(0, len(data) - file_start))

        if actual == 0:
            prev_cumul = cumul_end; continue

        pixel_data = data[file_start:file_start + actual]
        name = raw_names[name_idx] if name_idx < len(raw_names) else f"texture_{i:04d}"
        name_idx += 1
        textures.append(TextureInfo(name, width, height, fmt, mips, pixel_data,
                                    gpu_offset=prev_cumul, gpu_size=actual))
        prev_cumul = cumul_end
    return textures


# ---------------------------------------------------------------------------
# RX2 model parser
# ---------------------------------------------------------------------------

def _half_be(b0: int, b1: int) -> float:
    h = (b0 << 8) | b1
    s = (h >> 15) & 1
    e = (h >> 10) & 0x1f
    m = h & 0x3ff
    if e == 0:
        v = (m / 1024.0) * (2.0 ** -14)
    elif e == 31:
        v = float("inf")
    else:
        v = (1.0 + m / 1024.0) * (2.0 ** (e - 15))
    return -v if s else v


def _strip_to_triangles(indices: list) -> list:
    tris = []
    for k in range(len(indices) - 2):
        a, b, c = indices[k], indices[k + 1], indices[k + 2]
        if a == b or b == c or a == c:
            continue
        tris.append((a, b, c) if k % 2 == 0 else (a, c, b))
    return tris


def _parse_rx2(data: bytes):
    import numpy as np
    _empty = (np.empty((0, 3), np.float32),
              np.empty((0, 2), np.float32),
              np.empty((0, 3), np.int32),
              np.empty((0, 2), np.float32))

    MAGIC = b"\x89\x52\x57\x34\x78\x62\x32"
    if data[:7] != MAGIC:
        return _empty

    def ri(off):
        return struct.unpack_from(">i", data, off)[0]

    file_count = ri(0x20)
    file_table = ri(0x30)
    hdr_size   = ri(0x44)
    data_size  = ri(0x54)
    if data_size == 0:
        hdr_size = 0

    file_list = []
    for i in range(file_count):
        base = file_table + i * 24
        e = struct.unpack_from(">iiiiii", data, base)
        file_list.append(list(e))

    FXRS_TYPE = 15663108
    UV1_USAGE = 327686
    UV2_USAGE = 327943
    UV_USHORT = 2891865
    UV_SHORT  = 2892121

    all_verts, all_uvs, all_tris, all_uv2s = [], [], [], []

    for i in range(file_count):
        if file_list[i][5] != FXRS_TYPE:
            continue

        rs_off = file_list[i][0]
        mesh_idx, vb_desc_idx, prim_type = struct.unpack_from(">iii", data, rs_off)

        vbd_off = file_list[vb_desc_idx][0]
        vb_info = struct.unpack_from(">iiBBBBi", data, vbd_off)
        n_elems = vb_info[2]
        stride  = vb_info[3]

        pos_off, uv_off, uv_type = 0, -1, UV_USHORT
        uv2_off = -1
        elem_ptr = vbd_off + 16
        for _ in range(n_elems):
            byte_off, d_type, usage = struct.unpack_from(">iii", data, elem_ptr)
            elem_ptr += 12
            if usage == 1:
                pos_off = byte_off
            elif usage == UV1_USAGE:
                uv_off  = byte_off
                uv_type = d_type
            elif usage == UV2_USAGE:
                uv2_off = byte_off

        mesh_off = file_list[mesh_idx][0]
        om = struct.unpack_from(">iiiiii", data, mesh_off)

        f_info      = struct.unpack_from(">iii", data, file_list[om[4]][0] + 24)
        ib_section  = f_info[0]
        idx_count   = f_info[2]
        ib_data_off = file_list[ib_section][0] + hdr_size

        v_info      = struct.unpack_from(">iiiii", data, file_list[om[5]][0] + 24)
        vb_section  = v_info[0] >> 2
        n_verts     = v_info[3]
        vb_data_off = file_list[vb_section][0] + hdr_size

        if n_verts == 0 or idx_count == 0:
            continue
        if vb_data_off + n_verts * stride > len(data):
            continue
        if ib_data_off + idx_count * 2 > len(data):
            continue

        verts, uvs, uv2s = [], [], []
        for v in range(n_verts):
            off = vb_data_off + v * stride
            x = _half_be(data[off + pos_off],     data[off + pos_off + 1])
            y = _half_be(data[off + pos_off + 2], data[off + pos_off + 3])
            z = _half_be(data[off + pos_off + 4], data[off + pos_off + 5])
            verts.append((x, y, z))
            if uv_off >= 0:
                if uv_type == UV_SHORT:
                    u_raw = struct.unpack_from(">h", data, off + uv_off)[0]
                    v_raw = struct.unpack_from(">h", data, off + uv_off + 2)[0]
                    u = (u_raw + 32768) / 65535.0
                    v = (v_raw + 32768) / 65535.0
                else:
                    u_raw = struct.unpack_from(">H", data, off + uv_off)[0]
                    v_raw = struct.unpack_from(">H", data, off + uv_off + 2)[0]
                    u = u_raw / 65535.0
                    v = v_raw / 65535.0
                uvs.append((u, v))
            else:
                uvs.append((0.0, 0.0))
            if uv2_off >= 0:
                u2_raw = struct.unpack_from(">H", data, off + uv2_off)[0]
                v2_raw = struct.unpack_from(">H", data, off + uv2_off + 2)[0]
                uv2s.append((u2_raw / 65535.0, v2_raw / 65535.0))
            else:
                uv2s.append((0.0, 0.0))

        idxs = struct.unpack_from(f">{idx_count}H", data, ib_data_off)
        tris = []
        if prim_type == 6:
            strip: list = []
            for idx in idxs:
                if idx == 0xFFFF:
                    tris.extend(_strip_to_triangles(strip))
                    strip = []
                else:
                    strip.append(idx)
            tris.extend(_strip_to_triangles(strip))
        elif prim_type == 4:
            for k in range(0, idx_count - 2, 3):
                tris.append((idxs[k], idxs[k + 1], idxs[k + 2]))

        base = len(all_verts)
        all_verts.extend(verts)
        all_uvs.extend(uvs)
        all_uv2s.extend(uv2s)
        all_tris.extend((a + base, b + base, c + base) for a, b, c in tris)

    if not all_verts:
        return _empty
    return (np.array(all_verts,  dtype=np.float32),
            np.array(all_uvs,    dtype=np.float32),
            np.array(all_tris,   dtype=np.int32),
            np.array(all_uv2s,   dtype=np.float32))


def _parse_rpsgl_mesh(data: bytes):
    """Extract vertex geometry from an RW4 PS3 render-piece set (.rpsgl) file.

    The GPU data encodes vertices at stride 16 with positions as big-endian
    half-floats at bytes 0-5.  Coordinates are in a [0,1]-normalised space;
    callers should transform them into the target mesh's world space.
    """
    import numpy as np
    _empty = (np.empty((0, 3), np.float32),
              np.empty((0, 3), np.int32))

    MAGIC = b'\x89RW4ps3\x00'
    if len(data) < 0x50 or data[:8] != MAGIC:
        return _empty

    gpu_off  = struct.unpack_from('>I', data, 0x44)[0]
    gpu_size = struct.unpack_from('>I', data, 0x4C)[0]

    if gpu_off == 0 or gpu_off + gpu_size > len(data) or gpu_size == 0:
        return _empty

    STRIDE = 16
    n_verts = gpu_size // STRIDE
    if n_verts < 3:
        return _empty

    verts = np.empty((n_verts, 3), dtype=np.float32)
    for i in range(n_verts):
        off = gpu_off + i * STRIDE
        verts[i, 0] = _half_be(data[off + 0], data[off + 1])
        verts[i, 1] = _half_be(data[off + 2], data[off + 3])
        verts[i, 2] = _half_be(data[off + 4], data[off + 5])

    # Build sequential triangle list, filtering degenerate triangles.
    # The GPU buffer has vertices across multiple Z-levels (the logo face at
    # z≈-0.2 plus other model regions at z>0.1).  Only keep triangles where:
    #   1. All vertices share the same Z-band (z < 0.05 = the near face)
    #   2. No edge exceeds 0.12 in normalised units (rejects cross-region links)
    MAX_EDGE_SQ = 0.12 * 0.12
    Z_MAX = 0.05
    good_idx = []
    for t in range(n_verts // 3):
        i0, i1, i2 = t * 3, t * 3 + 1, t * 3 + 2
        v0, v1, v2 = verts[i0], verts[i1], verts[i2]
        if float(v0[2]) > Z_MAX or float(v1[2]) > Z_MAX or float(v2[2]) > Z_MAX:
            continue
        d01 = float(np.sum((v0 - v1) ** 2))
        d12 = float(np.sum((v1 - v2) ** 2))
        d02 = float(np.sum((v0 - v2) ** 2))
        if max(d01, d12, d02) <= MAX_EDGE_SQ:
            good_idx.extend([i0, i1, i2])

    if len(good_idx) < 3:
        return _empty

    tris = np.array(good_idx, dtype=np.int32).reshape(-1, 3)
    return (verts, tris)


def _compute_smooth_normals(verts, tris):
    import numpy as np
    norms = np.zeros_like(verts, dtype=np.float64)
    v0 = verts[tris[:, 0]]; v1 = verts[tris[:, 1]]; v2 = verts[tris[:, 2]]
    face_n = np.cross(v1 - v0, v2 - v0)
    np.add.at(norms, tris[:, 0], face_n)
    np.add.at(norms, tris[:, 1], face_n)
    np.add.at(norms, tris[:, 2], face_n)
    lengths = np.linalg.norm(norms, axis=1, keepdims=True)
    lengths[lengths == 0] = 1.0
    return (norms / lengths).astype(np.float32)


def _compute_tangents(verts, uvs, tris):
    import numpy as np
    N    = len(verts)
    tans = np.zeros((N, 3), dtype=np.float64)
    i0, i1, i2 = tris[:, 0], tris[:, 1], tris[:, 2]
    dv1 = verts[i1] - verts[i0]; dv2 = verts[i2] - verts[i0]
    du1 = uvs[i1]   - uvs[i0];   du2 = uvs[i2]   - uvs[i0]
    det = du1[:, 0] * du2[:, 1] - du2[:, 0] * du1[:, 1]
    det = np.where(np.abs(det) < 1e-10, 1e-10, det)
    f   = 1.0 / det
    tan = f[:, None] * (du2[:, 1:2] * dv1 - du1[:, 1:2] * dv2)
    np.add.at(tans, i0, tan)
    np.add.at(tans, i1, tan)
    np.add.at(tans, i2, tan)
    lengths = np.linalg.norm(tans, axis=1, keepdims=True)
    lengths[lengths == 0] = 1.0
    return (tans / lengths).astype(np.float32)


# ---------------------------------------------------------------------------
# GLSL shaders
# ---------------------------------------------------------------------------

_VERT_SRC = b"""
#version 110
attribute vec3 a_tangent;
varying vec3 v_pos_eye;
varying vec3 v_norm_eye;
varying vec3 v_tan_eye;
varying vec2 v_uv;
void main() {
    gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
    v_pos_eye   = vec3(gl_ModelViewMatrix * gl_Vertex);
    v_norm_eye  = normalize(gl_NormalMatrix * gl_Normal);
    v_tan_eye   = normalize(gl_NormalMatrix * a_tangent);
    v_uv        = gl_MultiTexCoord0.xy;
}
"""

_FRAG_SRC = b"""
#version 110
uniform sampler2D u_cm;
uniform sampler2D u_sm;
uniform sampler2D u_nm;
uniform float f_sm;
uniform float f_nm;
uniform vec2 u_uv_offset;
varying vec3 v_pos_eye;
varying vec3 v_norm_eye;
varying vec3 v_tan_eye;
varying vec2 v_uv;
void main() {
    vec2 uv = mod(v_uv + u_uv_offset, 1.0);
    vec4 albedo = texture2D(u_cm, uv);
    float spec_t = dot(texture2D(u_sm, uv).rgb, vec3(0.333)) * f_sm;
    vec3 N = normalize(v_norm_eye);
    vec3 T = normalize(v_tan_eye - dot(v_tan_eye, N) * N);
    vec3 B = cross(N, T);
    vec4 ns = texture2D(u_nm, uv);
    vec3 tn;
    tn.x = ns.a * 2.0 - 1.0;
    tn.y = ns.g * 2.0 - 1.0;
    tn.z = sqrt(max(0.001, 1.0 - tn.x*tn.x - tn.y*tn.y));
    vec3 nm_N = normalize(T * tn.x + B * tn.y + N * tn.z);
    vec3 final_N = normalize(mix(N, nm_N, f_nm));
    vec3 V  = normalize(-v_pos_eye);
    vec3 L0 = normalize(vec3(1.0, 2.0, 2.0));
    vec3 L1 = normalize(vec3(-1.5, 0.5, 1.0));
    vec3 L2 = normalize(vec3(0.0, -1.0, -1.5));
    float d0 = max(dot(final_N, L0), 0.0);
    float d1 = max(dot(final_N, L1), 0.0);
    float d2 = max(dot(final_N, L2), 0.0);
    vec3  R0 = reflect(-L0, final_N);
    float s0 = pow(max(dot(R0, V), 0.0), 32.0);
    vec3 col = albedo.rgb * (0.40 + d0 * 0.70 + d1 * 0.45 + d2 * 0.20)
             + vec3(s0 * spec_t * 1.2);
    gl_FragColor = vec4(col, albedo.a);
}
"""


# ---------------------------------------------------------------------------
# OpenGL model viewer widget
# ---------------------------------------------------------------------------

class ModelGLView(_OpenGLFrame):
    """pyopengltk OpenGL widget — textured 3-D model viewer."""

    def __init__(self, master, **kw):
        super().__init__(master, **kw)
        self.animate    = 33
        self._gl_ready  = False
        self._objects   = []
        self._center    = (0.0, 0.0, 0.0)
        self._span      = 1.0
        self._zoom      = 1.0
        self._rot_x     = 10.0
        self._rot_y     = 0.0
        self._shader_prog = 0
        self._shader_err  = ""
        self._tan_attr    = -1
        self._u_cm = self._u_sm = self._u_nm = -1
        self._u_use_sm = self._u_use_nm = -1
        self._u_uv_offset = -1
        self._uv_du = 0.0
        self._uv_dv = 0.0
        self._drag = None
        self.bind("<Button-1>",   self._on_btn1)
        self.bind("<B1-Motion>",  self._on_drag)
        self.bind("<MouseWheel>", self._on_scroll)

    def initgl(self):
        import OpenGL.GL as gl
        gl.glClearColor(0.22, 0.22, 0.22, 1.0)
        gl.glEnable(gl.GL_DEPTH_TEST)
        gl.glEnable(gl.GL_NORMALIZE)
        gl.glShadeModel(gl.GL_SMOOTH)
        gl.glEnable(gl.GL_LIGHTING)
        gl.glLightModelfv(gl.GL_LIGHT_MODEL_AMBIENT, [0.40, 0.40, 0.40, 1.0])
        gl.glEnable(gl.GL_LIGHT0)
        gl.glLightfv(gl.GL_LIGHT0, gl.GL_POSITION, [ 1.0,  2.0,  2.0, 0.0])
        gl.glLightfv(gl.GL_LIGHT0, gl.GL_DIFFUSE,  [0.70, 0.70, 0.70, 1.0])
        gl.glLightfv(gl.GL_LIGHT0, gl.GL_SPECULAR, [0.60, 0.60, 0.60, 1.0])
        gl.glLightfv(gl.GL_LIGHT0, gl.GL_AMBIENT,  [0.0,  0.0,  0.0,  1.0])
        gl.glEnable(gl.GL_LIGHT1)
        gl.glLightfv(gl.GL_LIGHT1, gl.GL_POSITION, [-1.5, 0.5,  1.0, 0.0])
        gl.glLightfv(gl.GL_LIGHT1, gl.GL_DIFFUSE,  [0.45, 0.45, 0.50, 1.0])
        gl.glLightfv(gl.GL_LIGHT1, gl.GL_SPECULAR, [0.0,  0.0,  0.0,  1.0])
        gl.glLightfv(gl.GL_LIGHT1, gl.GL_AMBIENT,  [0.0,  0.0,  0.0,  1.0])
        gl.glEnable(gl.GL_LIGHT2)
        gl.glLightfv(gl.GL_LIGHT2, gl.GL_POSITION, [0.0, -1.0, -1.5, 0.0])
        gl.glLightfv(gl.GL_LIGHT2, gl.GL_DIFFUSE,  [0.20, 0.20, 0.20, 1.0])
        gl.glLightfv(gl.GL_LIGHT2, gl.GL_SPECULAR, [0.0,  0.0,  0.0,  1.0])
        gl.glLightfv(gl.GL_LIGHT2, gl.GL_AMBIENT,  [0.0,  0.0,  0.0,  1.0])
        gl.glEnable(gl.GL_COLOR_MATERIAL)
        gl.glColorMaterial(gl.GL_FRONT_AND_BACK, gl.GL_AMBIENT_AND_DIFFUSE)
        gl.glMaterialfv(gl.GL_FRONT_AND_BACK, gl.GL_SPECULAR,  [0.25, 0.25, 0.25, 1.0])
        gl.glMateriali(gl.GL_FRONT_AND_BACK,  gl.GL_SHININESS, 40)
        gl.glEnable(gl.GL_TEXTURE_2D)
        gl.glTexEnvf(gl.GL_TEXTURE_ENV, gl.GL_TEXTURE_ENV_MODE, gl.GL_MODULATE)
        if not self._gl_ready:
            import numpy as np
            tex = gl.glGenTextures(1)
            gl.glBindTexture(gl.GL_TEXTURE_2D, tex)
            gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_MIN_FILTER, gl.GL_LINEAR)
            gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_MAG_FILTER, gl.GL_LINEAR)
            white = np.array([255, 255, 255, 255], dtype=np.uint8)
            gl.glTexImage2D(gl.GL_TEXTURE_2D, 0, gl.GL_RGBA, 1, 1, 0,
                            gl.GL_RGBA, gl.GL_UNSIGNED_BYTE, white.tobytes())
            self._tex_cm = tex
            try:
                from OpenGL.GL import shaders as _sh
                vert = _sh.compileShader(_VERT_SRC, gl.GL_VERTEX_SHADER)
                frag = _sh.compileShader(_FRAG_SRC, gl.GL_FRAGMENT_SHADER)
                prog = _sh.compileProgram(vert, frag)
                self._shader_prog = int(prog)
                self._u_cm        = gl.glGetUniformLocation(prog, "u_cm")
                self._u_sm        = gl.glGetUniformLocation(prog, "u_sm")
                self._u_nm        = gl.glGetUniformLocation(prog, "u_nm")
                self._u_use_sm    = gl.glGetUniformLocation(prog, "f_sm")
                self._u_use_nm    = gl.glGetUniformLocation(prog, "f_nm")
                self._u_uv_offset = gl.glGetUniformLocation(prog, "u_uv_offset")
                self._tan_attr    = gl.glGetAttribLocation(prog, "a_tangent")
                self._shader_err  = "shader OK"
            except Exception as e:
                self._shader_prog = 0
                self._shader_err  = f"shader FAILED: {e}"
            self._gl_ready = True

    def set_mesh(self, verts, norms, uvs, tris, center, span):
        import numpy as np
        import OpenGL.GL as gl
        self.tkMakeCurrent()
        for obj in self._objects:
            for key in ('tex_cm', 'tex_sm', 'tex_nm'):
                if obj.get(key):
                    try: gl.glDeleteTextures([obj[key]])
                    except Exception: pass
        self._objects = [{
            'verts':    np.ascontiguousarray(verts,        dtype=np.float32),
            'norms':    np.ascontiguousarray(norms,        dtype=np.float32),
            'uvs':      np.ascontiguousarray(uvs,          dtype=np.float32),
            'tris':     np.ascontiguousarray(tris.ravel(), dtype=np.uint32),
            'tangents': np.ascontiguousarray(_compute_tangents(verts, uvs, tris), dtype=np.float32),
            'tex_cm': 0, 'tex_sm': 0, 'tex_nm': 0,
        }]
        self._center = tuple(float(c) for c in center)
        self._span   = float(span)
        self._zoom   = 1.0
        self._rot_x  = 10.0
        self._rot_y  = 0.0

    def _upload_tex(self, gl, rgba_arr, w: int, h: int):
        import numpy as np
        max_sz = int(gl.glGetIntegerv(gl.GL_MAX_TEXTURE_SIZE))
        if w > max_sz or h > max_sz:
            from PIL import Image as _PI
            scale = max_sz / max(w, h)
            nw, nh = max(1, int(w * scale)), max(1, int(h * scale))
            img_tmp = _PI.fromarray(rgba_arr, "RGBA").resize((nw, nh), _PI.LANCZOS)
            rgba_arr = np.ascontiguousarray(np.array(img_tmp, dtype=np.uint8))
            w, h = nw, nh
        tex = gl.glGenTextures(1)
        gl.glBindTexture(gl.GL_TEXTURE_2D, tex)
        gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_MIN_FILTER, gl.GL_LINEAR)
        gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_MAG_FILTER, gl.GL_LINEAR)
        gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_WRAP_S, gl.GL_REPEAT)
        gl.glTexParameteri(gl.GL_TEXTURE_2D, gl.GL_TEXTURE_WRAP_T, gl.GL_REPEAT)
        gl.glTexImage2D(gl.GL_TEXTURE_2D, 0, gl.GL_RGBA, w, h, 0,
                        gl.GL_RGBA, gl.GL_UNSIGNED_BYTE, rgba_arr.tobytes())
        return tex

    def set_textures(self, textures: dict):
        if not self._objects:
            return
        import OpenGL.GL as gl
        self.tkMakeCurrent()
        obj = self._objects[0]
        for key in ('cm', 'sm', 'nm'):
            if key not in textures:
                continue
            old = obj.get(f'tex_{key}', 0)
            if old:
                try: gl.glDeleteTextures([old])
                except Exception: pass
            obj[f'tex_{key}'] = 0
            rgba, w, h = textures[key]
            try:
                obj[f'tex_{key}'] = self._upload_tex(gl, rgba, w, h)
            except Exception:
                pass

    def redraw(self):
        import OpenGL.GL  as gl
        import OpenGL.GLU as glu
        w = max(self.winfo_width(),  1)
        h = max(self.winfo_height(), 1)
        gl.glMatrixMode(gl.GL_PROJECTION)
        gl.glLoadIdentity()
        glu.gluPerspective(40.0, w / h, 0.01, 50.0)
        gl.glMatrixMode(gl.GL_MODELVIEW)
        gl.glLoadIdentity()
        dist = self._span * 2.5 * self._zoom
        cx, cy, cz = self._center
        glu.gluLookAt(0, 0, dist, 0, 0, 0, 0, 1, 0)
        gl.glRotatef(self._rot_x, 1, 0, 0)
        gl.glRotatef(self._rot_y, 0, 1, 0)
        gl.glTranslatef(-cx, -cy, -cz)
        gl.glClear(gl.GL_COLOR_BUFFER_BIT | gl.GL_DEPTH_BUFFER_BIT)
        for obj in self._objects:
            if self._shader_prog:
                self._draw_shaded(gl, obj)
            else:
                self._draw_fixed(gl, obj)

    def _draw_shaded(self, gl, obj):
        tex_cm = obj['tex_cm']
        tex_sm = obj['tex_sm']
        tex_nm = obj['tex_nm']
        sm_id = tex_sm if tex_sm else tex_cm
        nm_id = tex_nm if tex_nm else tex_cm
        gl.glUseProgram(self._shader_prog)
        gl.glActiveTexture(gl.GL_TEXTURE0)
        gl.glBindTexture(gl.GL_TEXTURE_2D, tex_cm)
        gl.glUniform1i(self._u_cm, 0)
        gl.glActiveTexture(gl.GL_TEXTURE1)
        gl.glBindTexture(gl.GL_TEXTURE_2D, sm_id)
        gl.glUniform1i(self._u_sm, 1)
        gl.glUniform1f(self._u_use_sm, 1.0 if tex_sm else 0.0)
        gl.glActiveTexture(gl.GL_TEXTURE2)
        gl.glBindTexture(gl.GL_TEXTURE_2D, nm_id)
        gl.glUniform1i(self._u_nm, 2)
        gl.glUniform1f(self._u_use_nm, 1.0 if tex_nm else 0.0)
        if self._u_uv_offset >= 0:
            gl.glUniform2f(self._u_uv_offset, self._uv_du, self._uv_dv)
        gl.glActiveTexture(gl.GL_TEXTURE0)
        gl.glEnableClientState(gl.GL_VERTEX_ARRAY)
        gl.glEnableClientState(gl.GL_NORMAL_ARRAY)
        gl.glEnableClientState(gl.GL_TEXTURE_COORD_ARRAY)
        gl.glVertexPointer(3, gl.GL_FLOAT, 0, obj['verts'])
        gl.glNormalPointer(gl.GL_FLOAT, 0, obj['norms'])
        gl.glTexCoordPointer(2, gl.GL_FLOAT, 0, obj['uvs'])
        tangents = obj.get('tangents')
        if self._tan_attr >= 0 and tangents is not None:
            gl.glEnableVertexAttribArray(self._tan_attr)
            gl.glVertexAttribPointer(self._tan_attr, 3, gl.GL_FLOAT, False, 0, tangents)
        gl.glDrawElements(gl.GL_TRIANGLES, len(obj['tris']),
                          gl.GL_UNSIGNED_INT, obj['tris'])
        if self._tan_attr >= 0 and tangents is not None:
            gl.glDisableVertexAttribArray(self._tan_attr)
        gl.glDisableClientState(gl.GL_VERTEX_ARRAY)
        gl.glDisableClientState(gl.GL_NORMAL_ARRAY)
        gl.glDisableClientState(gl.GL_TEXTURE_COORD_ARRAY)
        gl.glActiveTexture(gl.GL_TEXTURE2); gl.glBindTexture(gl.GL_TEXTURE_2D, 0)
        gl.glActiveTexture(gl.GL_TEXTURE1); gl.glBindTexture(gl.GL_TEXTURE_2D, 0)
        gl.glActiveTexture(gl.GL_TEXTURE0)
        gl.glUseProgram(0)

    def _draw_fixed(self, gl, obj):
        gl.glBindTexture(gl.GL_TEXTURE_2D, obj['tex_cm'])
        gl.glColor3f(1.0, 1.0, 1.0)
        gl.glEnableClientState(gl.GL_VERTEX_ARRAY)
        gl.glEnableClientState(gl.GL_NORMAL_ARRAY)
        gl.glEnableClientState(gl.GL_TEXTURE_COORD_ARRAY)
        gl.glVertexPointer(3, gl.GL_FLOAT, 0, obj['verts'])
        gl.glNormalPointer(gl.GL_FLOAT, 0, obj['norms'])
        gl.glTexCoordPointer(2, gl.GL_FLOAT, 0, obj['uvs'])
        gl.glDrawElements(gl.GL_TRIANGLES, len(obj['tris']),
                          gl.GL_UNSIGNED_INT, obj['tris'])
        gl.glDisableClientState(gl.GL_VERTEX_ARRAY)
        gl.glDisableClientState(gl.GL_NORMAL_ARRAY)
        gl.glDisableClientState(gl.GL_TEXTURE_COORD_ARRAY)

    def set_uv_offset(self, du: float, dv: float):
        self._uv_du = du
        self._uv_dv = dv

    def _on_btn1(self, event):
        self._drag = (event.x, event.y)

    def _on_drag(self, event):
        if self._drag is None:
            return
        dx = event.x - self._drag[0]
        dy = event.y - self._drag[1]
        self._rot_y += dx * 0.4
        self._rot_x += dy * 0.4
        self._drag   = (event.x, event.y)

    def _on_scroll(self, event):
        self._zoom *= 0.9 if event.delta > 0 else 1.1
        self._zoom  = max(0.05, min(10.0, self._zoom))
