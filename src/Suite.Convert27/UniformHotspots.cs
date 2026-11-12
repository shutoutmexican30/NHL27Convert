using System.Globalization;
using System.Text;
using System.Xml;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Reads and repairs the engine-facing XML hotspot definitions embedded in stamp_jersey/stamp_pant EBX assets.
/// Hotspot ids select branding-array slices: id 8 selects slice 0, id 9 slice 1, and so on.
/// Layout updates are length-preserving, so all existing EBX pointers, names, imports and font placements stay intact.
/// </summary>
public static class UniformHotspots
{
    public const int FirstBrandingId = 8;
    public sealed record Slot(int Id, string Name, string Role);

    public static IReadOnlyList<Slot> Read(ReadOnlySpan<byte> ebx)
    {
        var document = Document(ebx, out _, out _);
        return BrandNodes(document).Select(node =>
        {
            int id = Integer(node, "id");
            if (id < FirstBrandingId) throw new InvalidDataException($"Branding hotspot id {id} overlaps the font ids.");
            string name = node.GetAttribute("name");
            return new Slot(id, name, Role(name));
        }).OrderBy(slot => slot.Id).ToArray();
    }

    /// <summary>Returns physical slot roles in array order. Unknown, garment-specific names must be explicitly mapped.</summary>
    /// <remarks>
    /// The game maps brand hotspots to array slices in id order, skipping gaps: stock Boston five pants have ids 8 and 10
    /// over a 2-slice array, Nashville three and Pittsburgh five have five ids up to 13 over 5 slices. Treating id 10 as
    /// slice 2 grew Boston's new pants to 3 slices with its shield art in a slice the game never reads (Boston crashed).
    /// </remarks>
    public static IReadOnlyList<string> Roles(ReadOnlySpan<byte> ebx, string sourceSlot = "")
    {
        var slots = Capture(ebx, sourceSlot).Branding.OrderBy(s => s.Id).ToList();
        var roles = new string[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            string role = slot.Role.Length > 0 ? slot.Role : PositionRole(slot);
            if (role.Length == 0) throw new InvalidDataException(
                $"Hotspot id {slot.Id} has unknown physical meaning '{slot.Name}'; an explicit role review is required.");
            roles[i] = role;
        }
        return roles;
    }

    /// <summary>The array slice a hotspot draws from: its position among the layout's brand hotspots in id order.</summary>
    public static int SliceOf(UniformHotspotLayout layout, UniformHotspotPlacement slot) =>
        layout.Branding.OrderBy(s => s.Id).ToList().FindIndex(s => s.Id == slot.Id);

    /// <summary>
    /// The role of a hotspot whose name says nothing (Toronto and Seattle number theirs), from where it sits on the jersey
    /// texture: tags at the hem, shoulders turned sideways, and the front logos by size and height. Empty when unclear.
    /// </summary>
    public static string PositionRole(UniformHotspotPlacement slot) => PositionRole(slot, false);

    /// <summary>
    /// Pants (<paramref name="pants"/>) have their own map: the team logo sits at the top of the leg (translate Y below
    /// 0.15) and the NHL shield, upside down, at about (0.37, 0.49). 35 stock pants name that shield 'branding0'.
    /// </summary>
    public static string PositionRole(UniformHotspotPlacement slot, bool pants)
    {
        if (!double.TryParse(slot.Rotation, System.Globalization.CultureInfo.InvariantCulture, out double r)
            || !double.TryParse(slot.ScaleX, System.Globalization.CultureInfo.InvariantCulture, out double sx)
            || !double.TryParse(slot.TranslateX, System.Globalization.CultureInfo.InvariantCulture, out double tx)
            || !double.TryParse(slot.TranslateY, System.Globalization.CultureInfo.InvariantCulture, out double ty)) return "";
        r = ((r % 360) + 360) % 360;
        if (pants)
        {
            if (ty < 0.15) return "crest";
            if (Math.Abs(r - 180) < 20 && ty > 0.4 && ty < 0.6 && sx <= 0.06) return "shield";
            return "";
        }
        bool sideways = Math.Abs(r - 90) < 20 || Math.Abs(r - 270) < 20;
        bool upsideDown = Math.Abs(r - 180) < 20;
        if (ty > 0.9) return "epw_tag";
        if (sideways && Math.Abs(tx - 0.5) > 0.06) return "shoulder";
        if (sx >= 0.12) return "crest";
        if (upsideDown && Math.Abs(tx - 0.5) > 0.03 && ty > 0.3 && ty < 0.5) return "sponsor";
        if (upsideDown && Math.Abs(tx - 0.5) <= 0.03 && ty > 0.38 && ty < 0.48) return "shield";
        if (!upsideDown && !sideways && Math.Abs(tx - 0.5) <= 0.03 && ty > 0.5 && ty < 0.65) return "brand";
        return "";
    }

    public static string Role(string name) => name switch
    {
        "mainlogo" or "front_logo" or "main" or "main_logo" => "crest",
        "collar_nhl" or "back_nhl" or "nhl" or "nhl_collar" => "shield",
        "team_logo" or "logo_team" => "crest",
        "back_adidas" or "adidas" or "adidas_back" => "brand",
        "right_shoulder" or "left_shoulder" or "r_shoulder" or "l_shoulder" => "shoulder",
        "sponsor" => "sponsor",
        "adidas primegreen" or "bottomlogo" or "primegreen" or "adidas_tag" => "epw_tag",
        _ => NormalizedRole(name),
    };

    /// <summary>Spelling variants seen in stock stamps (maintlogo, collornhl, backaddiaslogo, leftshoulderlogo, ...).</summary>
    static string NormalizedRole(string name)
    {
        string n = new string(name.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (n.Length == 0) return "";
        if (n.Contains("shoulder")) return "shoulder";
        if (n.Contains("sponsor")) return "sponsor";
        if (n.Contains("ccm")) return "brand";
        if (n.Contains("nhl") && n.Contains("shield")) return "shield";
        if (n.Contains("primegreen") || n.Contains("primagreen") || n.Contains("endplastic") || n == "bottom" || n.EndsWith("tag")) return "epw_tag";
        if (n.Contains("nhl") && (n.Contains("collar") || n.Contains("collor") || n.Contains("back") || n == "nhl")) return "shield";
        if ((n.Contains("adidas") || n.Contains("addias")) && (n.Contains("back") || n == "adidas" || n.EndsWith("logo"))) return "brand";
        if (n.StartsWith("main") || n.Contains("teamlogo") || n.Contains("frontlogo") || n.StartsWith("logo")) return "crest";
        return "";
    }

    /// <summary>Captures all physical coordinates, not just names. Unknown names still need a reviewed override.</summary>
    public static UniformHotspotLayout Capture(ReadOnlySpan<byte> ebx, string sourceSlot)
    {
        var document = Document(ebx, out _, out _);
        bool pants = IsPants(document, sourceSlot);
        return new UniformHotspotLayout
        {
            SourceSlot = sourceSlot,
            Branding = BrandNodes(document).Select(node => new UniformHotspotPlacement
            {
                Id = Integer(node, "id"),
                Name = node.GetAttribute("name"),
                Role = Role(node.GetAttribute("name")),
                Rotation = node.GetAttribute("r"),
                ScaleX = node.GetAttribute("sx"),
                ScaleY = node.GetAttribute("sy"),
                TranslateX = node.GetAttribute("tx"),
                TranslateY = node.GetAttribute("ty"),
                Enabled = node.GetAttribute("enable") == "1.0",
            }).Select(slot => { if (slot.Role.Length == 0) slot.Role = PositionRole(slot, pants); return slot; })
            .OrderBy(slot => slot.Id).ToList(),
        };
    }

    /// <summary>Returns reviewed roles in contiguous physical array order. A sparse override cannot hide an unused logo.</summary>
    public static IReadOnlyList<string> Roles(UniformHotspotLayout layout)
    {
        // Ids may skip (stock Boston pants: 8 and 10); slices follow id order either way.
        var ordered = layout.Branding.OrderBy(slot => slot.Id).ToArray();
        foreach (var slot in ordered) Validate(slot);
        if (ordered.Select(s => s.Id).Distinct().Count() != ordered.Length)
            throw new InvalidDataException($"{layout.SourceSlot}: two branding hotspots share an id.");
        return ordered.Select(slot => slot.Role).ToArray();
    }

    /// <summary>Replaces only brand hotspots. Font/name/number/captain hotspots retain every attribute.</summary>
    public static byte[] Replace(ReadOnlySpan<byte> source, UniformHotspotLayout layout)
    {
        _ = Roles(layout);
        var document = Document(source, out int xmlAt, out int xmlLength);
        var preserved = OtherHotspots(document);
        foreach (var node in BrandNodes(document)) node.ParentNode!.RemoveChild(node);
        var garment = document.DocumentElement!.ChildNodes.Cast<XmlNode>()
            .SingleOrDefault(n => n.NodeType == XmlNodeType.Element) ?? throw new InvalidDataException("The stamp has no garment element.");
        foreach (var slot in layout.Branding.OrderBy(s => s.Id))
        {
            var node = document.CreateElement("hotspot");
            node.SetAttribute("enable", slot.Enabled ? "1.0" : "0.0");
            node.SetAttribute("id", slot.Id.ToString(CultureInfo.InvariantCulture));
            node.SetAttribute("type", "brand");
            node.SetAttribute("name", slot.Name);
            node.SetAttribute("r", slot.Rotation);
            node.SetAttribute("sx", slot.ScaleX);
            node.SetAttribute("sy", slot.ScaleY);
            node.SetAttribute("tx", slot.TranslateX);
            node.SetAttribute("ty", slot.TranslateY);
            garment.AppendChild(node);
        }
        if (!preserved.SequenceEqual(OtherHotspots(document)))
            throw new InvalidDataException("A non-branding hotspot changed during XML repair.");
        var output = Write(source, document, xmlAt, xmlLength);
        // Compare every physical attribute after reparsing, not just the number or semantic role of the hotspots.
        if (!UniformPhysicalLayouts.SameLayout(Capture(output, layout.SourceSlot), layout))
            throw new InvalidDataException("Repaired hotspot ids, names, roles or coordinates did not read back.");
        return output;
    }

    /// <summary>
    /// The stamp with its font hotspots (name, back and sleeve numbers, captain letter, patch) placed like
    /// <paramref name="team"/>'s: a donor clone otherwise keeps the donor team's sleeve-number spot, which on Florida's
    /// painted kits (numbers at 40%/60%, shoulder patches at 29.5%/70.5%) put Anaheim's numbers (27%/73%) over the
    /// patches. Brand hotspots are left exactly as they are. Returns the source unchanged when nothing differs.
    /// </summary>
    public static byte[] CopyFontPlacement(ReadOnlySpan<byte> source, ReadOnlySpan<byte> team)
    {
        var document = Document(source, out int xmlAt, out int xmlLength);
        var from = Document(team, out _, out _);
        string[] brands = BrandNodes(document).Select(n => n.OuterXml).ToArray();
        var teamFonts = from.GetElementsByTagName("hotspot").Cast<XmlElement>().Where(n => n.GetAttribute("type") == "font")
            .GroupBy(n => n.GetAttribute("name")).ToDictionary(g => g.Key, g => g.First());
        bool changed = false;
        foreach (var node in document.GetElementsByTagName("hotspot").Cast<XmlElement>().Where(n => n.GetAttribute("type") == "font"))
        {
            if (!teamFonts.TryGetValue(node.GetAttribute("name"), out var match)) continue;
            foreach (XmlAttribute attr in match.Attributes)
            {
                if (attr.Name is "id" or "type" or "name") continue;
                if (node.GetAttribute(attr.Name) == attr.Value) continue;
                if (attr.Value.Any(c => c < 0x20 || c > 0x7E || c is '"' or '<' or '>' or '&'))
                    throw new InvalidDataException("A font hotspot contains unsafe XML metadata.");
                node.SetAttribute(attr.Name, attr.Value);
                changed = true;
            }
        }
        if (!changed) return source.ToArray();
        if (!brands.SequenceEqual(BrandNodes(document).Select(n => n.OuterXml)))
            throw new InvalidDataException("A branding hotspot changed while placing the fonts.");
        var output = Write(source, document, xmlAt, xmlLength);
        var check = Document(output, out _, out _);
        if (!OtherHotspots(check).SequenceEqual(OtherHotspots(document)))
            throw new InvalidDataException("Font hotspots did not read back.");
        return output;
    }

    static byte[] Write(ReadOnlySpan<byte> source, XmlDocument document, int xmlAt, int xmlLength)
    {
        string xml = document.OuterXml;
        if (xml.Any(c => c > 0x7F)) throw new InvalidDataException("Hotspot metadata must remain printable ASCII.");
        byte[] encoded = Encoding.ASCII.GetBytes(xml);
        byte[] output;
        var parsed = EbxRiff.Parse(source);
        int count = BitConverter.ToInt32(source.Slice(xmlAt - 4, 4));
        if (encoded.Length > xmlLength && xmlAt < source.IndexOf(Encoding.ASCII.GetBytes(parsed.Name + "\0")) && count >= xmlLength && count - xmlLength <= 8)
        {
            // Stock stamps keep this XML in a byte array before the asset name: grow the array, keeping its trailing bytes.
            var content = new byte[encoded.Length + (count - xmlLength)];
            encoded.CopyTo(content, 0);
            source.Slice(xmlAt + xmlLength, count - xmlLength).CopyTo(content.AsSpan(encoded.Length));
            output = parsed.ReplaceArray(xmlAt, content);
        }
        else if (encoded.Length > xmlLength)
        {
            // Compact legacy stamps may need a second physical logo where none existed. Grow only the XML region
            // and repair every EBX pointer/offset that crosses it; never borrow another team's whole stamp.
            output = EbxRiff.Parse(source).ReplaceEmbedded(xmlAt, xmlLength, encoded);
            int oldEbxdEnd = 20 + BitConverter.ToInt32(source.Slice(16, 4));
            int newEbxdEnd = 20 + BitConverter.ToInt32(output.AsSpan(16, 4));
            // RIFF/EBXD sizes at offsets 4 and 16 intentionally change; every other byte before the XML is fixed.
            if (!output.AsSpan(20, xmlAt - 20).SequenceEqual(source.Slice(20, xmlAt - 20))
                || !output.AsSpan(xmlAt + encoded.Length, newEbxdEnd - xmlAt - encoded.Length)
                    .SequenceEqual(source.Slice(xmlAt + xmlLength, oldEbxdEnd - xmlAt - xmlLength)))
                throw new InvalidDataException("Growing hotspot XML changed bytes outside its EBXD string region.");
        }
        else
        {
            output = source.ToArray();
            encoded.CopyTo(output, xmlAt);
            output.AsSpan(xmlAt + encoded.Length, xmlLength - encoded.Length).Fill((byte)' ');
            for (int i = 0; i < output.Length; i++)
                if ((i < xmlAt || i >= xmlAt + xmlLength) && output[i] != source[i])
                    throw new InvalidDataException("A byte outside the hotspot XML changed.");
        }
        var identity = EbxRiff.Parse(output);
        var original = EbxRiff.Parse(source);
        if (identity.Name != original.Name || identity.FileGuid != original.FileGuid
            || !identity.ImportedFileGuids.SequenceEqual(original.ImportedFileGuids))
            throw new InvalidDataException("Hotspot repair changed the EBX name, GUID or imports.");
        return output;
    }

    static string[] OtherHotspots(XmlDocument document) => document.GetElementsByTagName("hotspot")
        .Cast<XmlElement>().Where(n => n.GetAttribute("type") != "brand")
        .Select(n => string.Join(";", n.Attributes.Cast<XmlAttribute>().Select(a => a.Name + "=" + a.Value))).ToArray();

    static void Validate(UniformHotspotPlacement slot)
    {
        if (slot.Id < FirstBrandingId) throw new InvalidDataException($"Branding id {slot.Id} must be at least {FirstBrandingId}.");
        if (slot.Role.Length == 0 || slot.Name.Length == 0 || !SlotRoles.Roles.Contains(slot.Role, StringComparer.Ordinal))
            throw new InvalidDataException($"Hotspot id {slot.Id} needs a reviewed role and name.");
        if (Role(slot.Name) is { Length: > 0 } inferred && inferred != slot.Role)
            throw new InvalidDataException($"Hotspot '{slot.Name}' cannot be assigned role '{slot.Role}'; its physical role is '{inferred}'.");
        foreach (string text in new[] { slot.Name, slot.Rotation, slot.ScaleX, slot.ScaleY, slot.TranslateX, slot.TranslateY })
            if (text.Length == 0 || text.Any(c => c < 0x20 || c > 0x7E || c is '"' or '<' or '>' or '&'))
                throw new InvalidDataException($"Hotspot id {slot.Id} contains unsafe XML metadata.");
        foreach (string number in new[] { slot.Rotation, slot.ScaleX, slot.ScaleY, slot.TranslateX, slot.TranslateY })
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new InvalidDataException($"Hotspot id {slot.Id} has an invalid coordinate or rotation.");
    }

    /// <summary>True for a pants stamp: by its asset name, or by its garment element when the name is not given.</summary>
    public static bool IsPants(string sourceSlot) => sourceSlot.Contains("stamp_pant_", StringComparison.Ordinal);

    static bool IsPants(XmlDocument document, string sourceSlot) => IsPants(sourceSlot)
        || document.DocumentElement?.ChildNodes.Cast<XmlNode>().FirstOrDefault(n => n.NodeType == XmlNodeType.Element) is { } garment
            && garment.Name.Contains("pant", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<XmlElement> BrandNodes(XmlDocument document) => document.GetElementsByTagName("hotspot")
        .Cast<XmlElement>().Where(n => n.GetAttribute("type") == "brand").ToArray();

    static XmlDocument Document(ReadOnlySpan<byte> ebx, out int xmlAt, out int xmlLength)
    {
        byte[] bytes = ebx.ToArray();
        byte[] declaration = Encoding.ASCII.GetBytes("<?xml");
        byte[] closing = Encoding.ASCII.GetBytes("</hotspots>");
        xmlAt = bytes.AsSpan().IndexOf(declaration);
        int end = bytes.AsSpan().IndexOf(closing);
        if (xmlAt < 0 || end < xmlAt) throw new InvalidDataException("The stamp asset has no readable hotspot XML.");
        xmlLength = end + closing.Length - xmlAt;
        string xml = Encoding.UTF8.GetString(bytes, xmlAt, xmlLength);
        var document = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
        try
        {
            using (var reader = new StringReader(xml)) document.Load(reader);
        }
        catch (XmlException)
        {
            // A small number of legacy custom kits contain a duplicated rotation and omit type="brand" on ids >= 8.
            // Normalize only that verified legacy shape; all names, coordinates, ids and font hotspots are retained.
            xml = NormalizeLegacyXml(xml);
            document = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
            using (var reader = new StringReader(xml)) document.Load(reader);
        }
        if (document.DocumentElement?.Name != "hotspots") throw new InvalidDataException("Unexpected hotspot XML root.");
        var ids = document.GetElementsByTagName("hotspot").Cast<XmlElement>().Select(n => Integer(n, "id")).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("The stamp has duplicate hotspot ids.");
        return document;
    }

    static string NormalizeLegacyXml(string xml)
    {
        const string tagPattern = "<hotspot\\b[^>]*>";
        const string attributePattern = "([A-Za-z_][A-Za-z0-9_.:-]*)\\s*=\\s*\"([^\"]*)\"";
        var tags = System.Text.RegularExpressions.Regex.Matches(xml, tagPattern).Cast<System.Text.RegularExpressions.Match>().ToArray();
        bool duplicatedRotation = false;
        foreach (var tag in tags)
        {
            var names = System.Text.RegularExpressions.Regex.Matches(tag.Value, attributePattern)
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            foreach (var group in names.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                if (group.Key != "r") throw new InvalidDataException($"Legacy hotspot has unsupported duplicate attribute '{group.Key}'.");
                duplicatedRotation = true;
            }
        }
        if (!duplicatedRotation) throw new InvalidDataException("The stamp XML is malformed but is not the reviewed legacy rotation case.");
        return System.Text.RegularExpressions.Regex.Replace(xml, tagPattern, tag =>
        {
            var parsed = System.Text.RegularExpressions.Regex.Matches(tag.Value, attributePattern)
                .Cast<System.Text.RegularExpressions.Match>().Select(m => (Name: m.Groups[1].Value, Value: m.Groups[2].Value)).ToArray();
            var attributes = new List<(string Name, string Value)>();
            var order = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var attribute in parsed)
            {
                if (positions.TryGetValue(attribute.Name, out int at)) attributes[at] = attribute;
                else { positions.Add(attribute.Name, attributes.Count); order.Add(attribute.Name); attributes.Add(attribute); }
            }
            // Duplicate updates retain the first attribute's position and the last reviewed value.
            var values = positions.ToDictionary(kv => kv.Key, kv => attributes[kv.Value].Value, StringComparer.Ordinal);
            if (!values.ContainsKey("type") && values.TryGetValue("id", out string? id)
                && int.TryParse(id, out int numericId) && numericId >= FirstBrandingId)
            {
                order.Add("type");
                values.Add("type", "brand");
            }
            bool selfClosing = tag.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
            return "<hotspot" + string.Concat(order.Select(name => $" {name}=\"{values[name]}\"")) + (selfClosing ? "/>" : ">");
        });
    }

    static int Integer(XmlElement node, string attribute)
    {
        if (!int.TryParse(node.GetAttribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException($"Hotspot has an invalid {attribute}.");
        return value;
    }
}
