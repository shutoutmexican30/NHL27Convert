namespace Suite.Core;

/// <summary>One source texture and the independent resource name it will have in a newly added uniform.</summary>
public sealed class NewUniformTexture
{
    public string SourceSlot { get; set; } = "";
    public string TargetSlot { get; set; } = "";
    public TextureReplacement Artwork { get; set; } = new("", "");
    /// <summary>Complete ordered semantic roles of the converted source artwork; null for non-array resources.</summary>
    public List<string>? RequiredRoles { get; set; }
    /// <summary>Physical NHL 24 donor-slot roles. This is separate from RequiredRoles because array indexes can move.</summary>
    public List<string>? DonorRoles { get; set; }
    /// <summary>Output layers the user pinned to a physical slot in the matcher; they win over role placement.</summary>
    public Dictionary<int, int>? PinnedSlots { get; set; }
}

/// <summary>One engine-facing branding hotspot in a uniform's stamp_jersey or stamp_pant definition.</summary>
public sealed class UniformHotspotPlacement
{
    public int Id { get; set; }
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rotation { get; set; } = "0.0000";
    public string ScaleX { get; set; } = "0.0800";
    public string ScaleY { get; set; } = "0.0800";
    public string TranslateX { get; set; } = "0.5000";
    public string TranslateY { get; set; } = "0.5000";
    public bool Enabled { get; set; } = true;
}

/// <summary>A complete replacement for one stamp asset's brand hotspots. Font and captain placements stay unchanged.</summary>
public sealed class UniformHotspotLayout
{
    public string SourceSlot { get; set; } = "";
    public List<UniformHotspotPlacement> Branding { get; set; } = [];
}

/// <summary>
/// A new selectable home or away uniform. Its textures are source artwork plus explicit target identities, never ordinary
/// replacements. TargetVariant zero asks the planner to allocate the highest spare variant in the reserved 2–25 range.
/// </summary>
public sealed class NewUniformAddition
{
    public const string ModKind = "new-uniforms";

    public int TeamId { get; set; }
    public string Team { get; set; } = "";
    public string SourceKit { get; set; } = "";
    public string SourcePrefix { get; set; } = "";
    public string? SourceFolder27 { get; set; }
    public string TargetPrefix { get; set; } = "";
    public int TargetVariant { get; set; }
    public int Style { get; set; } = 2;
    public bool IsLight { get; set; }
    /// <summary>
    /// A name for this kit that is unique within its team (for example "alt-twelve" for an NHL 27 alternate). Per-kit
    /// settings (number colours, temp art files, batch roles) are keyed by <see cref="Kit"/>, so an alternate built on the
    /// team's home or away structure never picks up the Fanatics home/away kit's own settings. Empty = SourceKit.
    /// </summary>
    public string? KitLabel { get; set; }
    public string Kit => string.IsNullOrWhiteSpace(KitLabel) ? SourceKit : KitLabel;
    /// <summary>Jersey picker label: 2 = the shared "Alternate" text (no language entry needed); null or 6 = a per-kit
    /// VINTAGE_ key named <see cref="JerseyName"/> (or the install's jersey name).</summary>
    public int? LocString { get; set; }
    public string? JerseyName { get; set; }
    /// <summary>Use the kit's own NHL 27 number/letter sheets (fitted into NHL 24's glyph layout and verified per glyph)
    /// instead of the team's NHL 24 sheet. NHL 27 alternates often have their own numbers (Detroit's alternate 2).</summary>
    public bool Nhl27Numbers { get; set; }
    public List<NewUniformTexture> Textures { get; set; } = [];
    /// <summary>Optional physical-layout repairs. These change placement metadata, never the team's fabric or artwork.</summary>
    public List<UniformHotspotLayout> HotspotLayouts { get; set; } = [];
    /// <summary>Stamp EBX → the stamp whose font hotspots (number, name, captain positions) it takes (donor clones).</summary>
    public Dictionary<string, string> FontPlacement { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stamp EBX → NHL 27 hotspot XML whose sponsor spot it takes, written in place (donor clones).</summary>
    public Dictionary<string, string> SponsorPlacement { get; set; } = new(StringComparer.Ordinal);
}
