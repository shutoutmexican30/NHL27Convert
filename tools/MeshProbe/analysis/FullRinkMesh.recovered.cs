using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.FullRink;

/// <summary>
/// The NHL 24 rink ice mesh (content/worlds/rink/ice/components/rink_ice_nhl_mesh), measured from the Gen4b dump
/// (2026-11-18). Its MeshSet resource header holds 20 fixed-size section records; the geometry chunk stores, per
/// section, f32 xyz positions (12 bytes/vertex) at the record's position offset, then half-float attribute streams,
/// then an f32 UV set whose u already equals 0.5 + z / 62.56 on the stock centre logo patch. The centre logo patch
/// (material ice_centerlogo_mat) is a 12.165 x 12.167 m rectangular annulus at centre ice: 6 outer verts, 12 inner
/// verts ringing a full-width strip round the red line (z +-0.74 m) that the centreline sections draw in.
///
/// The full-rink edit moves vertices only (sizes never change): the annulus outer verts stretch to the 62.56 x 31.28 m
/// texture frame, the inner strip collapses to z = 0 so the patch becomes a solid sheet, the patch lifts +5 mm so it
/// wins the depth test over the stock ice and ads it now covers, and the centreline sections collapse to a point below
/// the ice so the red line comes from the painted texture. UVs are rewritten to u = 0.5 + z / 62.56, v = 0.5 - x / 31.28,
/// the same frame the RinkFrame/FullRinkTexture builders squeeze into the square centre-logo slot.
/// </summary>
public static class FullRinkMesh
{
    public const string ResName = "content/worlds/rink/ice/components/rink_ice_nhl_mesh";
    public const int SectionCount = 20;
    public const int FirstSectionAt = 0x150;
    public const int SectionStride = 0xE0;
    public const int NameStringAt = 0x08;   // u32 offset into the header's string table
    public const int TrisAt = 0x20;         // u32 triangle count
    public const int PosOffAt = 0x28;       // u32 byte offset of the positions stream in the chunk
    public const int VertsAt = 0x2C;        // u32 vertex count
    public const int BMinAt = 0xC0;         // 3 floats + pad
    public const int BMaxAt = 0xD0;         // 3 floats + pad

    public const double FrameLength = 62.56;   // metres along z covered by u 0..1 (RinkFrame constant, verified here)
    public const double FrameWidth = 31.28;    // metres along x covered by v 0..1
    public const double LogoHalfZ = 6.0834;    // measured outer annulus half length
    public const double LogoHalfX = 6.0821;    // measured outer annulus half width
    public const float LiftY = 0.005f;         // raised patch height above the stock ice
    public const float CollapseY = -0.01f;     // height collapsed sections sink to

    public sealed record Section(int Index, string Name, int Tris, int PosOff, int Verts,
        float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ, int RecordAt)
    {
        public int PositionsEnd => PosOff + Verts * 12;
    }

    /// <summary>Parses the 20 section records. Throws if the header does not look like the rink ice mesh.</summary>
    public static List<Section> Parse(byte[] res)
    {
        if (res.Length < FirstSectionAt + SectionCount * SectionStride) throw new InvalidDataException("Mesh resource header too small.");
        var sections = new List<Section>(SectionCount);
        for (int i = 0; i < SectionCount; i++)
        {
            int r = FirstSectionAt + i * SectionStride;
            string Name(int at)
            {
                int off = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + at));
                int p = Array.IndexOf(res, (byte)0xFF, 0, 0); // no-op; names resolved below via caller table
                return "";
            }
            // The name string lives at NameStringAt as an offset into the string table that starts at 0x1300 for
            // this asset; resolve directly.
            int strOff = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + NameStringAt));
            string name = strOff >= 0 && strOff < res.Length ? CStr(res, strOff) : "";
            int tris = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + TrisAt));
            int posOff = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + PosOffAt));
            int verts = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + VertsAt));
            float f(int at) => BinaryPrimitives.ReadSingleLittleEndian(res.AsSpan(r + at));
            sections.Add(new(i, name, tris, posOff, verts, f(BMinAt), f(BMinAt + 4), f(BMinAt + 8),
                f(BMaxAt), f(BMaxAt + 4), f(BMaxAt + 8), r));
        }
        if (sections.Count(s => s.Name.EndsWith("_mat", StringComparison.Ordinal)) < SectionCount - 2
            || !sections.Any(s => s.Name == "ice_centerlogo_mat"))
            throw new InvalidDataException("The header's section records do not look like rink_ice_nhl_mesh.");
        return sections;
    }

    static string CStr(byte[] b, int at)
    {
        if (at >= b.Length) return "";
        int end = Array.IndexOf(b, (byte)0, at);
        return end < 0 ? "" : Encoding.ASCII.GetString(b, at, end - at);
    }

    /// <summary>
    /// Rewrites the geometry chunk in place (returns a copy) and patches the resource header's bounding boxes.
    /// <paramref name="res"/> is modified in place as well; both stay the same length. Only runs when switching to
    /// full-rink; switching back restores the backed-up originals instead.
    /// </summary>
    public static byte[] Patch(byte[] res, byte[] chunk, IReadOnlyList<Section> sections, List<string> log)
    {
        var logo = sections.First(s => s.Name == "ice_centerlogo_mat");
        int uvOff = FindLogoUvSet(chunk, logo, out double uMaxErr);
        log.Add($"centre logo: {logo.Verts} verts at chunk +0x{logo.PosOff:X}, {logo.Tris} tris, UV set at +0x{uvOff:X} (stock u formula max err {uMaxErr:F5})");

        var output = (byte[])chunk.Clone();
        double sx = (FrameWidth / 2) / LogoHalfX, sz = (FrameLength / 2) / LogoHalfZ;

        // Stretch the annulus: outer verts (|z| near the patch edge) go to the frame corners/edge midpoints,
        // inner strip verts (|z| < 1 m) collapse to z' = 0 so the sheet is solid across the red line.
        for (int v = 0; v < logo.Verts; v++)
        {
            int at = logo.PosOff + v * 12;
            float x = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at));
            float z = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at + 8));
            bool inner = Math.Abs(z) < 1.0;
            double nx = x * sx, nz = inner ? 0 : z * sz;
            Write(output, at, (float)nx, LiftY, (float)nz);
            WriteUv(output, uvOff + v * 12, 0.5 + nz / FrameLength, 0.5 - nx / FrameWidth);
        }
        var logoBox = ((float)-FrameWidth / 2, LiftY, (float)-FrameLength / 2, (float)FrameWidth / 2, LiftY, (float)FrameLength / 2);
        WriteBox(res, logo.RecordAt, logoBox);
        log.Add($"centre logo stretched to frame {FrameWidth} x {FrameLength} m at y=+{LiftY} m");

        // The red line and its outside strip come from the painted texture now: collapse both sections.
        foreach (var s in sections.Where(s => s.Name is "ice_centerline_mat" or "ice_centerlineoutside_mat"))
        {
            for (int v = 0; v < s.Verts; v++)
                Write(output, s.PosOff + v * 12, 0f, CollapseY, 0f);
            WriteBox(res, s.RecordAt, (0, CollapseY, 0, 0, CollapseY, 0));
            log.Add($"{s.Name}: {s.Verts} verts collapsed below the ice");
        }

        // Whole-mesh bounds grow to the frame so frustum culling keeps the stretched patch.
        WriteBox(res, 0, ((float)-FrameWidth / 2, CollapseY, (float)-FrameLength / 2, (float)FrameWidth / 2, LiftY, (float)FrameLength / 2));
        return output;
    }

    /// <summary>
    /// Locates the f32 UV set of the centre logo section by requiring its u to match the measured stock formula
    /// u = 0.5 + z / 62.56 for every vertex. Throws when no candidate validates (better than writing a wrong stream).
    /// </summary>
    public static int FindLogoUvSet(byte[] chunk, Section logo, out double uMaxErr)
    {
        int start = logo.PositionsEnd;
        int end = start + 2048; // the UV set sits within the section block, well before this
        uMaxErr = double.MaxValue;
        int best = -1;
        for (int o = start; o + logo.Verts * 12 <= Math.Min(chunk.Length, end); o += 4)
        {
            double err = 0;
            for (int v = 0; v < logo.Verts && err < 0.05; v++)
            {
                float z = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(logo.PosOff + v * 12 + 8));
                float u = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(o + v * 12));
                float uv = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(o + v * 12 + 4));
                err = Math.Max(err, Math.Abs(u - (0.5 + z / FrameLength)));
                if (uv is < -0.01f or > 1.01f) err = 1;
            }
            if (err < 0.02 && err < uMaxErr) { uMaxErr = err; best = o; }
        }
        if (best < 0) throw new InvalidDataException("No UV stream of the centre logo section validates against u = 0.5 + z / 62.56.");
        return best;
    }

    static void Write(byte[] b, int at, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 8), z);
    }

    static void WriteUv(byte[] b, int at, double u, double v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), (float)u);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), (float)v);
    }

    static void WriteBox(byte[] b, int at, (float X0, float Y0, float Z0, float X1, float Y1, float Z1) box)
    {
        Write(b, at + BMinAt, box.X0, box.Y0, box.Z0);
        Write(b, at + BMaxAt, box.X1, box.Y1, box.Z1);
    }
}
