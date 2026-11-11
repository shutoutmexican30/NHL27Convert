using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Plain-word names for logo slots ("Crest", "Right shoulder (player's)", "EPW back-hem tag"...), so the uniform page
/// never shows bare slot numbers. Physical names come from the jersey stamp with the same role and side resolution the
/// install uses (<see cref="SponsorSlots.WithRoles"/> and <see cref="ShoulderPatches.IsLeft"/>).
/// </summary>
public static class SlotNames
{
    public static string Name(string role, bool? left = null, string? detail = null, string? garment = null) => role switch
    {
        "crest" when garment == "branding_pant" => "Pants logo",
        "crest" => "Crest",
        "shoulder" => left switch { true => "Left shoulder (player's)", false => "Right shoulder (player's)", _ => "Shoulder patch" },
        "brand" => detail is { Length: > 0 } d ? $"Brand ({Title(d)})" : "Brand",
        "shield" => "NHL shield",
        SlotRoles.EpwRole => "EPW back-hem tag",
        "sponsor" => detail is { Length: > 0 } s ? $"Sponsor ({Title(s)})" : "Sponsor",
        "blank" or "" => "Empty",
        _ => Title(role),
    };

    public static string Name(LayerLabel label, string? garment = null) => Name(label.Role, null, label.Detail, garment);

    /// <summary>The name of each physical slot of a branding array, from its stamp; null when the stamp cannot be read.</summary>
    public static IReadOnlyList<string>? Physical(BundleIndex index, string arraySlot)
    {
        try
        {
            string stamp = UniformPhysicalLayouts.StampForArray(arraySlot);
            if (!index.EbxByName.ContainsKey(stamp)) return null;
            var copies = SponsorSlots.Copies(index, stamp);
            var layout = SponsorSlots.WithRoles(copies.OrderByDescending(c => c.Branding.Count).First());
            string garment = arraySlot.Contains("branding_pant_", StringComparison.Ordinal) ? "branding_pant" : "branding_jersey";
            var names = new string[layout.Branding.Count];
            foreach (var slot in layout.Branding)
                names[UniformHotspots.SliceOf(layout, slot)] = Name(slot.Role, slot.Role == "shoulder" ? ShoulderPatches.IsLeft(slot) : null,
                    slot.Role == "brand" ? "adidas" : null, garment) + (slot.Enabled ? "" : " (off)");
            return names;
        }
        catch (InvalidDataException) { return null; }
    }

    static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ');
}
