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
    const double SponsorTop = 0.55, SponsorBottom = 0.655;

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
    /// Donor slot → NHL 27 layer for logos the finished sheet lacks: jersey sponsors and pants team logos, roles matched
    /// exactly, each NHL 27 layer used once.
    /// </summary>
    public static Dictionary<int, int> Place(bool pants, IReadOnlyList<Spot> donor, IReadOnlyList<string> layerRoles,
        ICollection<int> layersWithArt, Rgba finished, Rgba plain)
    {
        var placed = new Dictionary<int, int>();
        string role = pants ? "crest" : "sponsor";
        bool painted = pants ? HasPantsLogo(finished, plain, donor.Where(s => s.Role == "shield")) : HasChestSponsor(finished, plain);
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
