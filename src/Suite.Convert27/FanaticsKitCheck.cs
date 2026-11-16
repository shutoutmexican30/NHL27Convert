using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Reads an installed Fanatics kit back from the game files and checks the owner's rules, so every kit the tool adds is
/// verified the same way (2026-11-12): no Adidas shoulder-yoke fabric zone (no dimples), the back-hem tag present, the
/// crest slot empty (the crest is painted), hems and sock stripes within NHL 24's limits, number/letter sheets equal to
/// the team's own NHL 24 sheets (Arizona → Utah remapped), and NHL 24 stock shine (coefficient) maps only.
/// </summary>
public static class FanaticsKitCheck
{
    const string Donor = "content/character/skater/league/nhl/anaheim/adidas/home/";

    public static List<string> Check(string game, BundleIndex index, string targetPrefix, string team, string ownKit)
    {
        Rgba? Read(string name)
        {
            if (!index.ResByName.ContainsKey(name)) return null;
            try { var s = TextureSlots.Read(game, index, name); return Ps4Codec.DecodeSlices(s.Res, s.Stock)[0]; }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return null; }
        }
        List<Rgba> ReadAll(string name)
        {
            if (!index.ResByName.ContainsKey(name)) return [];
            try { var s = TextureSlots.Read(game, index, name); return Ps4Codec.DecodeSlices(s.Res, s.Stock).ToList(); }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return []; }
        }
        string f = targetPrefix.TrimEnd('/') + "/";
        string id = KitRegistration.Identity(f);
        string own = $"content/character/skater/league/nhl/{team}/adidas/{ownKit}/";
        string ownId = $"{team}_{ownKit}";
        var bad = new List<string>();

        var color = Read($"{f}jersey_adidas_{id}_base_color");
        if (color is null) bad.Add("no jersey base colour");
        else
        {
            var counts = new int[256];
            for (int i = 3; i < color.Pixels.Length; i += 4) counts[color.Pixels[i]]++;
            int n = color.Width * color.Height;
            if (counts[KitDonor.AdidasYoke] > n * 0.003) bad.Add($"Adidas shoulder-yoke zone (dimples) on {(double)counts[KitDonor.AdidasYoke] / n:P1}");
            if (counts.Count(c => c > n * 0.003) < 2) bad.Add("no fabric zones (whole jersey one fabric)");
            var reference = Read($"{own}jersey_adidas_{ownId}_base_color") ?? Read($"{own}jersey_adidas_{ownId}_color");
            var hem = HemFit.Plan(color, reference);
            if (hem[0] is not null || hem[1] is not null) bad.Add("hem stripes run past NHL 24's hem");
        }

        string jerseyArray = $"{f}branding_jersey_adidas_{id}_color_array";
        var arr = ReadAll(jerseyArray);
        var roles = SponsorSlots.PhysicalRoles(index, jerseyArray)?.ToList();
        if (arr.Count == 0 || roles is null) bad.Add("no jersey logo array");
        else
        {
            int tag = roles.IndexOf(SlotRoles.EpwRole);
            if (tag < 0 || tag >= arr.Count || Opaque(arr[tag]) < 0.05) bad.Add("back-hem tag missing");
            // The captain's letter must not sit on the sponsor patch (Carolina home: the A covered Nucor).
            int sponsorSlot = roles.IndexOf("sponsor");
            string stamp = $"{f}stamp_jersey_adidas_{id}";
            if (sponsorSlot >= 0 && sponsorSlot < arr.Count && Opaque(arr[sponsorSlot]) > 0.01 && index.EbxByName.TryGetValue(stamp, out var stampEntries)
                && UniformHotspots.FontSpot(UniformPhysicalLayouts.ReadStamp(index, stamp, stampEntries[0].Bundle), "captain") is { } captain
                && SponsorSlots.WithRoles(SponsorSlots.Copies(index, stamp)[0]).Branding.FirstOrDefault(s => s.Role == "sponsor") is { } sponsorSpot)
            {
                double sx = double.Parse(sponsorSpot.TranslateX, System.Globalization.CultureInfo.InvariantCulture);
                double sy = double.Parse(sponsorSpot.TranslateY, System.Globalization.CultureInfo.InvariantCulture);
                double ss = double.Parse(sponsorSpot.ScaleX, System.Globalization.CultureInfo.InvariantCulture);
                if (Math.Abs(captain.X - sx) < (captain.W + ss) / 2 && Math.Abs(captain.Y - sy) < (captain.H + ss) / 2)
                    bad.Add("captain's letter sits on the sponsor patch");
            }
            int crest = roles.IndexOf("crest");
            if (crest >= 0 && crest < arr.Count && Opaque(arr[crest]) > 0.01) bad.Add("crest slot has art (the crest is painted; it would show twice)");
        }

        var sock = Read($"{f}sock_adidas_{id}_color") ?? Read($"{f}sock_adidas_{id}_base_color");
        var sock24 = Read($"{own}sock_adidas_{ownId}_color") ?? Read($"{own}sock_adidas_{ownId}_base_color");
        if (sock is not null && SockFit.Plan(sock, sock24) is { } sp) bad.Add($"sock stripes end at {sp.From:P1}, past {sp.To:P1}");

        var font = Read($"{f}font_adidas_{id}_color");
        var font24 = Read($"{own}font_adidas_{ownId}_color");
        if (font is null) bad.Add("no number/letter sheet");
        else if (team != "arizona" && !FontRecolor.WantsRemap($"{team} {ownKit}") && font24 is not null && InkDiffers(font, font24)) bad.Add("number/letter sheet is not the team's NHL 24 sheet");

        foreach (string garment in new[] { "jersey", "pant", "sock" })
        foreach (string variant in new[] { "base_", "" })
        {
            var mine = Read($"{f}{garment}_adidas_{id}_{variant}coeff");
            if (mine is null) continue;
            var stock = Read($"{Donor}{garment}_adidas_anaheim_home_{variant}coeff");
            if (stock is not null && MeanDiff(mine, stock) > 6) bad.Add($"{garment} {variant}coeff is not NHL 24 stock");
        }
        return bad;
    }

    static double Opaque(Rgba img)
    {
        int n = 0;
        for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] > 24) n++;
        return (double)n / (img.Width * img.Height);
    }

    static double MeanDiff(Rgba a, Rgba b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.MaxValue;
        long d = 0;
        for (int i = 0; i < a.Pixels.Length; i++) d += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return (double)d / a.Pixels.Length;
    }

    /// <summary>Glyph shapes differ (compression changes hidden colour under transparent pixels, not the ink).</summary>
    static bool InkDiffers(Rgba a, Rgba b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return true;
        int n = 0;
        for (int i = 3; i < a.Pixels.Length; i += 4) if ((a.Pixels[i] > 64) != (b.Pixels[i] > 64)) n++; // faint edges (alpha ~20) flip with re-encoding
        return n > a.Width * a.Height * 0.002;
    }
}
