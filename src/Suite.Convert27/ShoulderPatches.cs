using System.Globalization;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// The user's Shoulder patches choice for one jersey (None / Right only / Left only / Both, from the player's point of
/// view), saved in the slot map. Install runs it in two steps: <see cref="FitArt"/> makes the jersey's art carry one
/// shoulder layer per chosen side before missing slots are added, and <see cref="PlaceSides"/> then puts those layers on
/// the chosen sides, adding the missing side (a mirror of the other) and switching off a shoulder that should be bare.
/// Stock hotspot names agree with position on every stock jersey: right_shoulder sits left of centre on the texture
/// (translate X below 0.5, turned 90 degrees) and left_shoulder right of centre (turned 270 degrees).
/// </summary>
public static class ShoulderPatches
{
    public const string None = "none", Right = "right", Left = "left", Both = "both";
    public static readonly string[] Choices = [None, Right, Left, Both];

    public static string Label(string? choice) => choice switch
    {
        None => "None",
        Right => "Right shoulder only (player's)",
        Left => "Left shoulder only (player's)",
        Both => "Both shoulders",
        _ => "As the NHL 27 jersey",
    };

    static int Wanted(string choice) => choice switch { None => 0, Both => 2, _ => 1 };
    static bool WantsSide(string choice, bool left) => choice == Both || choice == (left ? Left : Right);

    /// <summary>True for the player's left shoulder (by name, else by where it sits on the texture).</summary>
    public static bool IsLeft(UniformHotspotPlacement slot)
    {
        string n = slot.Name.ToLowerInvariant();
        if (n.StartsWith("left", StringComparison.Ordinal) || n.StartsWith("l_", StringComparison.Ordinal)) return true;
        if (n.StartsWith("right", StringComparison.Ordinal) || n.StartsWith("r_", StringComparison.Ordinal)) return false;
        return double.TryParse(slot.TranslateX, CultureInfo.InvariantCulture, out double tx) && tx > 0.5;
    }

    /// <summary>The saved choice for a uniform's jersey, or null to follow the NHL 27 art.</summary>
    public static string? Choice(SlotMapStore store, NewUniformAddition addition)
    {
        if (string.IsNullOrWhiteSpace(addition.SourceFolder27)) return null;
        string folder27 = addition.SourceFolder27.Trim().TrimEnd('/'), source = addition.SourcePrefix.Trim().TrimEnd('/');
        var entry = store.Entries.Where(e => e.Folder27.TrimEnd('/') == folder27 && e.Garment == "branding_jersey")
            .OrderByDescending(e => e.Folder24.TrimEnd('/') == source).FirstOrDefault();
        return entry?.Shoulders is { } c && Choices.Contains(c) ? c : null;
    }

    static IEnumerable<(NewUniformTexture Color, List<NewUniformTexture> Siblings)> Jerseys(NewUniformAddition addition)
    {
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("branding_jersey_", StringComparison.Ordinal)).ToList())
        {
            string stem = color.SourceSlot[..^"_color_array".Length];
            yield return (color, addition.Textures.Where(t => t.SourceSlot.StartsWith(stem + "_", StringComparison.Ordinal)
                && t.SourceSlot.EndsWith("_array", StringComparison.Ordinal)).ToList());
        }
    }

    /// <summary>Step 1: one shoulder art layer per chosen side (extra ones dropped, a single one repeated for Both).</summary>
    public static int FitArt(IEnumerable<NewUniformAddition> additions, SlotMapStore store, Action<string>? say = null)
    {
        int changed = 0;
        foreach (var addition in additions)
        {
            if (Choice(store, addition) is not { } choice) continue;
            foreach (var (color, siblings) in Jerseys(addition))
            {
                if (color.RequiredRoles is not { } roles || color.Artwork.Layers is not { } layers) continue;
                var shoulders = roles.Select((r, i) => (r, i)).Where(x => x.r == "shoulder" && layers.ContainsKey(x.i)).Select(x => x.i).ToList();
                int want = Wanted(choice);
                if (shoulders.Count > want)
                {
                    var drop = shoulders.Skip(want).ToList();
                    foreach (var t in siblings) DuplicateLogoLayers.Remove(t, drop);
                    changed++;
                }
                else if (shoulders.Count < want)
                {
                    if (shoulders.Count == 0)
                    {
                        say?.Invoke($"{addition.Team} {addition.SourceKit}: {Label(choice)} chosen, but the NHL 27 jersey has no shoulder logo to use");
                        continue;
                    }
                    foreach (var t in siblings)
                    {
                        if (t.Artwork.Layers is not { } tl || !tl.TryGetValue(shoulders[0], out var picture)) continue;
                        int next = Math.Max(tl.Keys.DefaultIfEmpty(-1).Max(), (t.RequiredRoles?.Count ?? 0) - 1) + 1;
                        var copy = new Dictionary<int, string>(tl) { [next] = picture };
                        t.Artwork = t.Artwork with { Layers = copy };
                        if (t.RequiredRoles is { } tr)
                        {
                            while (tr.Count < next) tr.Add("blank");
                            tr.Add("shoulder");
                        }
                    }
                    changed++;
                }
            }
        }
        return changed;
    }

    /// <summary>Step 2, after missing slots are added: the chosen sides get the patch, the others are switched off.</summary>
    public static int PlaceSides(IEnumerable<NewUniformAddition> additions, SlotMapStore store, BundleIndex index, Action<string>? say = null)
    {
        int changed = 0;
        foreach (var addition in additions)
        {
            if (Choice(store, addition) is not { } choice) continue;
            foreach (var (color, siblings) in Jerseys(addition))
            {
                string stamp = UniformPhysicalLayouts.StampForArray(color.SourceSlot);
                var existing = addition.HotspotLayouts.FirstOrDefault(l => l.SourceSlot == stamp);
                UniformHotspotLayout layout;
                if (existing is not null) layout = SponsorSlots.WithRoles(existing);
                else if (index.EbxByName.ContainsKey(stamp)) layout = SponsorSlots.WithRoles(SponsorSlots.Copies(index, stamp)[0]);
                else continue;
                var result = Place(layout, choice, color, siblings, () => SponsorSlots.StockPosition(index, "shoulder"));
                if (existing is not null) addition.HotspotLayouts.Remove(existing);
                addition.HotspotLayouts.Add(result);
                say?.Invoke($"{addition.Team} {addition.SourceKit}: shoulder patches set to {Label(choice)}");
                changed++;
            }
        }
        return changed;
    }

    /// <summary>The layout with patches on exactly the chosen sides, and the art's shoulder layers pinned to them.</summary>
    public static UniformHotspotLayout Place(UniformHotspotLayout stock, string choice, NewUniformTexture color,
        IEnumerable<NewUniformTexture> siblings, Func<UniformHotspotPlacement?> stockShoulder)
    {
        var layout = SponsorSlots.WithRoles(stock);
        var add = new List<(string, UniformHotspotPlacement)>();
        foreach (bool left in new[] { false, true })
        {
            if (!WantsSide(choice, left) || layout.Branding.Any(s => s.Role == "shoulder" && IsLeft(s) == left)) continue;
            var other = layout.Branding.FirstOrDefault(s => s.Role == "shoulder" && IsLeft(s) != left) ?? stockShoulder()
                ?? throw new InvalidDataException("No stock jersey has a shoulder position to copy.");
            add.Add(("shoulder", IsLeft(other) == left ? other : SponsorSlots.Mirror(other)));
        }
        if (add.Count > 0) layout = SponsorSlots.WithAdded(layout, add);
        var targets = new List<int>();
        foreach (var slot in layout.Branding.Where(s => s.Role == "shoulder").OrderBy(s => IsLeft(s)).ThenBy(s => s.Id))
        {
            bool keep = WantsSide(choice, IsLeft(slot)) && !targets.Any(t => IsLeft(layout.Branding.OrderBy(b => b.Id).ElementAt(t)) == IsLeft(slot));
            slot.Enabled = keep;
            if (keep) targets.Add(UniformHotspots.SliceOf(layout, slot));
        }
        if (color.RequiredRoles is { } roles && color.Artwork.Layers is { } layers)
        {
            var art = roles.Select((r, i) => (r, i)).Where(x => x.r == "shoulder" && layers.ContainsKey(x.i)).Select(x => x.i).ToList();
            foreach (var t in siblings)
            {
                t.PinnedSlots ??= [];
                foreach (var (layer, slot) in art.Zip(targets)) t.PinnedSlots[layer] = slot;
            }
        }
        return layout;
    }
}
