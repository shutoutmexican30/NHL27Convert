using System.Text;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class UniformPhysicalPlacementTests
{
    static Rgba Image(byte color, bool visible = true)
    {
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color;
            pixels[i + 3] = visible ? (byte)255 : (byte)0;
        }
        return new Rgba(8, 8, pixels);
    }

    static NewUniformTexture Texture(string[] source, Dictionary<int, Rgba> art) => new()
    {
        SourceSlot = "content/character/skater/league/nhl/test/adidas/home/branding_jersey_adidas_test_home_color_array",
        RequiredRoles = [.. source],
        Artwork = new TextureReplacement("", "", art.ToDictionary(kv => kv.Key, kv => "test.png")),
    };

    static Dictionary<int, Rgba> Art(int count, int transparent = -1) => Enumerable.Range(0, count)
        .ToDictionary(layer => layer, layer => Image((byte)(30 + layer * 20), layer != transparent));

    [Fact]
    public void Anaheim_shoulders_shield_brand_and_sponsor_use_their_physical_slots()
    {
        string[] source = ["crest", "shoulder", "shoulder", "shield", "brand", "sponsor"];
        string[] donor = ["crest", "shield", "brand", "epw_tag", "shoulder", "shoulder", "sponsor"];
        var art = Art(6);
        var stock = Enumerable.Range(0, 7).Select(_ => Image(0, false)).ToArray();
        var placement = UniformBrandingLayers.Plan(Texture(source, art), art, stock, 7, donor);
        Assert.Equal(4, placement.Destination(1));
        Assert.Equal(5, placement.Destination(2));
        Assert.Equal(1, placement.Destination(3));
        Assert.Equal(2, placement.Destination(4));
        Assert.Equal(6, placement.Destination(5));
        Assert.Empty(placement.BlankSources);
    }

    [Fact]
    public void Boston_has_seven_positions_and_fanatics_no_longer_occupies_the_crest()
    {
        string[] source = ["crest", "shoulder", "shoulder", "sponsor", "brand", "shield"];
        string[] donor = ["crest", "shield", "brand", "epw_tag", "shoulder", "shoulder", "sponsor"];
        var art = Art(6);
        var stock = Enumerable.Range(0, 7).Select(_ => Image(0, false)).ToArray();
        var placement = UniformBrandingLayers.Plan(Texture(source, art), art, stock, 7, donor);
        Assert.Equal(0, placement.Destination(0));
        Assert.Equal(2, placement.Destination(4));
        Assert.Equal(6, placement.Destination(3));
        var placed = UniformBrandingLayers.Place(art, stock, placement, "color");
        Assert.Same(art[0], placed[0]);
        Assert.Same(art[4], placed[2]);
        Assert.False(UniformBrandingLayers.Visible(placed[3]));
        Assert.Equal(7, placed.Count);
    }

    [Fact]
    public void Duplicate_shoulders_keep_their_source_occurrence_order()
    {
        var placement = UniformBrandingLayers.PlanRoles(["shoulder", "shoulder"],
            ["blank", "shoulder", "blank", "shoulder"]);
        Assert.Equal(1, placement.Destination(0));
        Assert.Equal(3, placement.Destination(1));
    }

    [Fact]
    public void Transparent_away_sponsor_does_not_require_a_hotspot_or_replace_a_shoulder()
    {
        string[] source = ["crest", "shoulder", "shoulder", "shield", "brand", "sponsor"];
        string[] donor = ["crest", "shield", "brand", "epw_tag", "shoulder", "shoulder"];
        var art = Art(6, transparent: 5);
        var stock = Enumerable.Range(0, 6).Select(_ => Image(0, false)).ToArray();
        var placement = UniformBrandingLayers.Plan(Texture(source, art), art, stock, 6, donor);
        Assert.Contains(5, placement.BlankSources);
        Assert.Equal(4, placement.Destination(1));
        Assert.Equal(5, placement.Destination(2));
        var placed = UniformBrandingLayers.Place(art, stock, placement, "color");
        Assert.Same(art[1], placed[4]);
        Assert.Same(art[2], placed[5]);
        Assert.DoesNotContain(art[5], placed.Values);
    }

    [Fact]
    public void Visible_sponsor_without_its_physical_hotspot_is_refused()
    {
        var art = Art(2);
        var texture = Texture(["crest", "sponsor"], art);
        var ex = Assert.Throws<InvalidDataException>(() => UniformBrandingLayers.Plan(texture, art,
            [Image(0, false), Image(0, false)], 2, ["crest", "blank"]));
        Assert.Contains("sponsor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transparent_required_crest_is_refused()
    {
        var art = Art(2, transparent: 0);
        var texture = Texture(["crest", "shield"], art);
        var ex = Assert.Throws<InvalidDataException>(() => UniformBrandingLayers.Plan(texture, art,
            [Image(0, false), Image(0, false)], 2, ["crest", "shield"]));
        Assert.Contains("required crest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_source_cannot_overwrite_another_placed_logo()
    {
        var crest = Image(200);
        var transparent = Image(0, false);
        var placement = new UniformLayerPlacement(new Dictionary<int, int> { [0] = 1 }, new HashSet<int>())
        {
            BlankSources = new HashSet<int> { 1 },
        };
        var placed = UniformBrandingLayers.Place(new Dictionary<int, Rgba> { [0] = crest, [1] = transparent },
            [Image(0, false), Image(0, false)], placement, "color");
        Assert.Same(crest, placed[1]);
        Assert.False(UniformBrandingLayers.Visible(placed[0]));
    }

    [Fact]
    public void Utah_crest_and_shield_exchange_array_positions_without_dropping_either()
    {
        var art = Art(2);
        var placement = UniformBrandingLayers.Plan(Texture(["crest", "shield"], art), art,
            [Image(0, false), Image(0, false)], 2, ["shield", "crest"]);
        Assert.Equal(1, placement.Destination(0));
        Assert.Equal(0, placement.Destination(1));
        var placed = UniformBrandingLayers.Place(art, [Image(0, false), Image(0, false)], placement, "color");
        Assert.Same(art[0], placed[1]);
        Assert.Same(art[1], placed[0]);
    }

    [Fact]
    public void Sparse_pant_ids_map_to_slices_in_id_order()
    {
        byte[] xml = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><hotspots><garment>"
            + "<hotspot enable=\"1.0\" id=\"8\" type=\"brand\" name=\"logo_team\" r=\"0\" sx=\".1\" sy=\".1\" tx=\".2\" ty=\".2\"/>"
            + "<hotspot enable=\"1.0\" id=\"10\" type=\"brand\" name=\"back_nhl\" r=\"0\" sx=\".1\" sy=\".1\" tx=\".2\" ty=\".2\"/>"
            + "</garment></hotspots>");
        // Stock Boston five pants: ids 8 and 10 over a 2-slice array. The game reads slices in id order, so a gap is not
        // a missing slice; growing the array to 3 put the shield where the game never looks, and Boston crashed.
        Assert.Equal(["crest", "shield"], UniformHotspots.Roles(xml));
    }

    [Fact]
    public void Curated_sparse_layout_follows_id_order_and_duplicate_ids_are_rejected()
    {
        var layout = new UniformHotspotLayout
        {
            SourceSlot = "stamp_pant_test",
            Branding = [Slot(8, "logo_team", "crest"), Slot(10, "back_nhl", "shield")],
        };
        Assert.Equal(["crest", "shield"], UniformHotspots.Roles(layout));
        Assert.Equal(1, UniformHotspots.SliceOf(layout, layout.Branding[1]));
        layout.Branding.Add(Slot(10, "copy", "sponsor"));
        Assert.Throws<InvalidDataException>(() => UniformHotspots.Roles(layout));
    }

    [Fact]
    public void Hotspot_replacement_preserves_identity_fonts_coordinates_and_non_xml_bytes()
    {
        byte[] source = StampFixture();
        int xmlAt = source.AsSpan().IndexOf("<?xml"u8);
        int xmlEnd = source.AsSpan().IndexOf("</hotspots>"u8) + "</hotspots>".Length;
        var layout = new UniformHotspotLayout
        {
            SourceSlot = "content/test/stamp_jersey_test",
            Branding =
            [
                Slot(8, "mainlogo", "crest", "180", ".1905", ".1905", ".5000", ".2870"),
                Slot(9, "collar_nhl", "shield", "180", ".0286", ".0286", ".5000", ".4230"),
                Slot(10, "back_adidas", "brand", "0", ".0357", ".0357", ".5000", ".5625"),
            ],
        };
        byte[] result = UniformHotspots.Replace(source, layout);
        Assert.Equal(source.Length, result.Length);
        Assert.Equal(EbxRiff.Parse(source).FileGuid, EbxRiff.Parse(result).FileGuid);
        Assert.Equal(EbxRiff.Parse(source).Name, EbxRiff.Parse(result).Name);
        Assert.True(UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(result, layout.SourceSlot), layout));
        string xml = Encoding.ASCII.GetString(result, xmlAt, xmlEnd - xmlAt);
        Assert.Contains("type=\"font\"", xml, StringComparison.Ordinal);
        Assert.Contains("name=\"number\"", xml, StringComparison.Ordinal);
        Assert.Contains("tx=\".7711\"", xml, StringComparison.Ordinal);
        for (int i = 0; i < source.Length; i++)
            if (i < xmlAt || i >= xmlEnd) Assert.Equal(source[i], result[i]);
    }

    [Fact]
    public void Growing_hotspot_xml_preserves_ebx_identity_and_all_reviewed_positions()
    {
        byte[] source = StampFixture(padding: 0);
        var layout = new UniformHotspotLayout
        {
            SourceSlot = "content/test/stamp_jersey_test",
            Branding = Enumerable.Range(0, 7).Select(i => Slot(8 + i,
                i == 0 ? "mainlogo" : i == 1 ? "collar_nhl" : i == 2 ? "back_adidas" : i == 3 ? "adidas primegreen"
                    : i == 4 ? "right_shoulder" : i == 5 ? "left_shoulder" : "sponsor",
                i == 0 ? "crest" : i == 1 ? "shield" : i == 2 ? "brand" : i == 3 ? "epw_tag"
                    : i < 6 ? "shoulder" : "sponsor")).ToList(),
        };
        byte[] result = UniformHotspots.Replace(source, layout);
        Assert.True(result.Length > source.Length);
        Assert.Equal(EbxRiff.Parse(source).FileGuid, EbxRiff.Parse(result).FileGuid);
        Assert.Equal(EbxRiff.Parse(source).ImportedFileGuids, EbxRiff.Parse(result).ImportedFileGuids);
        Assert.True(UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(result, layout.SourceSlot), layout));
    }

    [Fact]
    public void Legacy_duplicate_rotation_and_untyped_brand_nodes_are_safely_normalized()
    {
        byte[] source = StampFixture(legacy: true);
        var layout = new UniformHotspotLayout
        {
            SourceSlot = "content/test/stamp_jersey_test",
            Branding = [Slot(8, "mainlogo", "crest")],
        };
        byte[] result = UniformHotspots.Replace(source, layout);
        var slots = UniformHotspots.Read(result);
        Assert.Single(slots);
        Assert.Equal("crest", slots[0].Role);
        string xml = Encoding.ASCII.GetString(result);
        Assert.Contains("name=\"number\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("r=\"5\" r=", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_hotspot_ids_are_rejected_across_all_types()
    {
        byte[] source = StampFixture(duplicateFontId: true);
        Assert.Throws<InvalidDataException>(() => UniformHotspots.Read(source));
    }

    [Fact]
    public void Unknown_source_or_misassigned_reviewed_hotspot_roles_are_rejected()
    {
        byte[] unknown = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><hotspots><garment>"
            + "<hotspot enable=\"1.0\" id=\"8\" type=\"brand\" name=\"mystery\" r=\"0\" sx=\".1\" sy=\".1\" tx=\".2\" ty=\".2\"/>"
            + "</garment></hotspots>");
        Assert.Throws<InvalidDataException>(() => UniformHotspots.Roles(unknown));
        var wrong = new UniformHotspotLayout { SourceSlot = "stamp", Branding = [Slot(8, "mainlogo", "sponsor")] };
        Assert.Throws<InvalidDataException>(() => UniformHotspots.Roles(wrong));
    }

    [Fact]
    public void Donor_stamp_takes_the_teams_number_spots_and_keeps_its_logo_spots()
    {
        var donor = StampFixture();
        var team = StampFixture(numberTx: ".398200");
        var output = UniformHotspots.CopyFontPlacement(donor, team);
        string xml = Encoding.ASCII.GetString(output);
        Assert.Contains("tx=\".398200\"", xml);
        Assert.DoesNotContain("tx=\".7711\"", xml);
        Assert.True(UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(output, "content/test/stamp_jersey_test"),
            UniformHotspots.Capture(donor, "content/test/stamp_jersey_test")));
        Assert.Equal(EbxRiff.Parse(donor).FileGuid, EbxRiff.Parse(output).FileGuid);
    }

    [Fact]
    public void Same_number_spots_leave_the_stamp_untouched()
    {
        var donor = StampFixture();
        Assert.Equal(donor, UniformHotspots.CopyFontPlacement(donor, StampFixture()));
    }

    static UniformHotspotPlacement Slot(int id, string name, string role, string r = "0", string sx = ".1",
        string sy = ".1", string tx = ".2", string ty = ".2") => new()
    {
        Id = id, Name = name, Role = role, Rotation = r, ScaleX = sx, ScaleY = sy, TranslateX = tx, TranslateY = ty,
    };

    static byte[] StampFixture(bool duplicateFontId = false, int padding = 1600, bool legacy = false, string numberTx = ".7711")
    {
        const string name = "content/test/stamp_jersey_test";
        string xml = "<?xml version=\"1.0\"?><hotspots><garment>"
            + "<hotspot enable=\"1.0\" id=\"1\" type=\"font\" name=\"number\" r=\"0\" sx=\".2\" sy=\".2\" tx=\"" + numberTx + "\" ty=\".4\"/>"
            + (legacy
                ? "<hotspot enable=\"1.0\" id=\"8\" name=\"mainlogo\" r=\"5\" r=\"180\" sx=\".19\" sy=\".19\" tx=\".5\" ty=\".28\"/>"
                : "<hotspot enable=\"1.0\" id=\"8\" type=\"brand\" name=\"mainlogo\" r=\"180\" sx=\".19\" sy=\".19\" tx=\".5\" ty=\".28\"/>")
            + (duplicateFontId ? "<hotspot enable=\"1.0\" id=\"1\" type=\"brand\" name=\"collar_nhl\" r=\"0\" sx=\".1\" sy=\".1\" tx=\".2\" ty=\".2\"/>" : "")
            + new string(' ', padding) + "</garment></hotspots>";
        byte[] payload = Encoding.ASCII.GetBytes(name + "\0" + xml);
        const int dataStart = 32;
        int ebxdSize = dataStart - 20 + payload.Length;
        int efix = (20 + ebxdSize + 1) & ~1;
        var efixBody = new List<byte>();
        efixBody.AddRange(Guid.NewGuid().ToByteArray());
        void Count(uint value) => efixBody.AddRange(BitConverter.GetBytes(value));
        Count(0); Count(0); Count(0); Count(0); Count(0); Count(0); Count(1);
        efixBody.AddRange(Guid.NewGuid().ToByteArray());
        efixBody.AddRange(Guid.NewGuid().ToByteArray());
        Count(0); Count(0); Count(0); Count(0); Count(0);
        int ebxx = efix + 8 + efixBody.Count;
        var output = new byte[ebxx + 16];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(output, 0);
        BitConverter.TryWriteBytes(output.AsSpan(4), (uint)(output.Length - 8));
        Encoding.ASCII.GetBytes("EBX").CopyTo(output, 8);
        Encoding.ASCII.GetBytes("EBXD").CopyTo(output, 12);
        BitConverter.TryWriteBytes(output.AsSpan(16), (uint)ebxdSize);
        for (int i = 20; i < dataStart; i++) output[i] = 0xA5;
        payload.CopyTo(output, dataStart);
        Encoding.ASCII.GetBytes("EFIX").CopyTo(output, efix);
        BitConverter.TryWriteBytes(output.AsSpan(efix + 4), (uint)efixBody.Count);
        efixBody.CopyTo(output, efix + 8);
        Encoding.ASCII.GetBytes("EBXX").CopyTo(output, ebxx);
        BitConverter.TryWriteBytes(output.AsSpan(ebxx + 4), 0u);
        return output;
    }
}
