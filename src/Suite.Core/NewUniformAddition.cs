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
    public List<NewUniformTexture> Textures { get; set; } = [];
    /// <summary>Optional physical-layout repairs. These change placement metadata, never the team's fabric or artwork.</summary>
    public List<UniformHotspotLayout> HotspotLayouts { get; set; } = [];
}
