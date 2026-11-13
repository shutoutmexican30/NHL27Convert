using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// NHL 27's finished jersey/pants sheets do not paint every logo: Seattle's Muckleshoot and Toronto's Milk chest patches,
/// Vegas' sponsor and LA's "LA" pants logo exist only as separate logo layers. The painted donor route would lose them, so
/// a sponsor (jersey) or team logo (pants) that the finished sheet does not show is put in the donor's slot of the same
/// role, at the donor's spot. The NHL shield never goes into a team-logo slot (roles must match exactly).
/// </summary>
public static class UnpaintedLayers
{
    /// <summary>A donor logo slot: its role and where it sits on the sheet (0..1, from the stamp hotspot).</summary>
    public sealed record Spot(string Role, double X, double Y, double Size);

    // The chest-sponsor band on the jersey sheet, both sides (see SponsorSide).
    // Ends above the crest: Anaheim's Ducks crest starts at 64.8% and was read as a painted sponsor.
    const double SponsorTop = 0.55, SponsorBottom = 0.635;

    /// <summary>Pixels where the finished sheet has paint the plain sheet lacks.</summary>
    static bool Painted(Rgba finished, Rgba plain, int x, int y)
    {
        int at = (y * finished.Width + x) * 4;
        return Math.Abs(finished.Pixels[at] - plain.Pixels[at]) + Math.Abs(finished.Pixels[at + 1] - plain.Pixels[at + 1])
            + Math.Abs(finished.Pixels[at + 2] - plain.Pixels[at + 2]) > 90;
    }

    /// <summary>True when the finished jersey already shows a chest sponsor on either side.</summary>
    public static bool HasChestSponsor(Rgba finished, Rgba plain)
    {
        int w = finished.Width, h = finished.Height, count = 0;
        for (int y = (int)(h * SponsorTop); y < (int)(h * SponsorBottom); y++)
        for (int x = (int)(w * 0.30); x < (int)(w * 0.70); x++)
            if ((x < w * 0.47 || x > w * 0.53) && Painted(finished, plain, x, y)) count++;
        return count >= w * h / 20000;
    }

    /// <summary>True when the finished pants show paint outside the shield's spot (a team logo is painted on).</summary>
    public static bool HasPantsLogo(Rgba finished, Rgba plain, IEnumerable<Spot> shields)
    {
        int w = finished.Width, h = finished.Height, count = 0;
        var boxes = shields.Select(s => (X1: (s.X - s.Size) * w, X2: (s.X + s.Size) * w, Y1: (s.Y - s.Size) * h, Y2: (s.Y + s.Size) * h)).ToList();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (Painted(finished, plain, x, y) && !boxes.Any(b => x >= b.X1 && x <= b.X2 && y >= b.Y1 && y <= b.Y2)) count++;
        return count >= w * h / 20000;
    }

    /// <summary>
    /// True when the sheet already shows a logo at a donor spot: within half the spot's size around its centre, at least
    /// 3% of the pixels differ strongly from the area's most common colour. Edmonton's NHL 27 pants paint the Oilers logo
    /// at the hip (also in the plain sheet, so comparing with the plain sheet saw nothing) and keep a CCM wordmark as a
    /// separate layer; placing that layer put CCM over the Oilers logo.
    /// </summary>
    public static bool LogoAt(Rgba sheet, Spot spot)
    {
        int w = sheet.Width, h = sheet.Height;
        int x1 = Math.Max(0, (int)((spot.X - spot.Size / 2) * w)), x2 = Math.Min(w - 1, (int)((spot.X + spot.Size / 2) * w));
        int y1 = Math.Max(0, (int)((spot.Y - spot.Size / 2) * h)), y2 = Math.Min(h - 1, (int)((spot.Y + spot.Size / 2) * h));
        if (x2 <= x1 || y2 <= y1) return false;
        var counts = new Dictionary<int, int>();
        for (int y = y1; y <= y2; y++)
        for (int x = x1; x <= x2; x++)
        {
            int at = (y * w + x) * 4, key = (sheet.Pixels[at] >> 4) << 8 | (sheet.Pixels[at + 1] >> 4) << 4 | sheet.Pixels[at + 2] >> 4;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        int common = counts.MaxBy(kv => kv.Value).Key;
        int cr = (common >> 8 & 15) * 16 + 8, cg = (common >> 4 & 15) * 16 + 8, cb = (common & 15) * 16 + 8, differ = 0, total = 0;
        for (int y = y1; y <= y2; y++)
        for (int x = x1; x <= x2; x++)
        {
            int at = (y * w + x) * 4; total++;
            if (Math.Abs(sheet.Pixels[at] - cr) + Math.Abs(sheet.Pixels[at + 1] - cg) + Math.Abs(sheet.Pixels[at + 2] - cb) > 90) differ++;
        }
        return differ >= total * 0.03;
    }

    /// <summary>
    /// Donor slot → NHL 27 layer for logos the finished sheet lacks: jersey sponsors and pants team logos, roles matched
    /// exactly, each NHL 27 layer used once.
    /// </summary>
    public static Dictionary<int, int> Place(bool pants, IReadOnlyList<Spot> donor, IReadOnlyList<string> layerRoles,
        ICollection<int> layersWithArt, Rgba finished, Rgba plain)
    {
        var placed = new Dictionary<int, int>();
        string role = pants ? "crest" : "sponsor";
        bool painted = pants
            ? donor.Where(s => s.Role == role).Any(s => LogoAt(finished, s)) || HasPantsLogo(finished, plain, donor.Where(s => s.Role == "shield"))
            : HasChestSponsor(finished, plain);
        if (painted) return placed;
        var free = layerRoles.Select((r, i) => (r, i)).Where(x => x.r == role && layersWithArt.Contains(x.i)).Select(x => x.i).ToList();
        for (int slot = 0; slot < donor.Count && free.Count > 0; slot++)
        {
            if (donor[slot].Role != role) continue;
            placed[slot] = free[0];
            free.RemoveAt(0);
        }
        return placed;
    }
}
