using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Convert27;

namespace Suite.App.Pages;

/// <summary>
/// Full-game asset browser: one search box over every EBX and RES resource in the NHL 24 bundles. Meshes open
/// as a rendered top view (one color per section, triangle fill, vertex dots when the index buffer is not
/// recoverable) and export/import as OBJ vertex positions; textures decode to images (BC7 / BC1 / BC5) and import
/// as PNG through the same writer every other page uses; a "From NHL 27" button pulls the same-named texture out
/// of an NHL 27 (PS5) build and converts it into the NHL 24 slot. RIFF EBX shows name/guid/imports; anything else
/// gets a size + hex summary.
/// </summary>
public sealed class AssetsPage : Grid
{
    const int MaxResults = 500;

    sealed class Entry(string name, char kind)
    {
        public string Name { get; } = name;
        public string Folder { get; } = name.LastIndexOf('/') is int i && i > 0 ? name[..i] : "";
        public char Kind { get; } = kind;            // E ebx, M mesh, T texture/RES, D data RES
        public string KindLabel => Kind switch { 'M' => "mesh", 'T' => "texture", 'E' => "ebx", _ => "data" };
        public override string ToString() => Name;
    }

    sealed class SectionRow(string name, int verts, int prims, Color color)
    {
        public string Label => $"{name}   —   {verts} verts · {prims} tris";
        public Color Color { get; } = color;
    }

    readonly SuiteSettings _settings;
    readonly TextBox _search = new() { Width = 380 };
    readonly ComboBox _kind = new() { Width = 110, Margin = new Thickness(8, 0, 0, 0) };
    readonly ListBox _hits = new() { Margin = new Thickness(0, 8, 0, 0), MaxHeight = 560 };    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)) };
    readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxWidth = 860, MaxHeight = 760 };
    readonly StackPanel _detailInfo = new();
    readonly ComboBox _lod = new() { Width = 90, Margin = new Thickness(0, 0, 10, 0) };
    readonly ListBox _sections = new() { MaxHeight = 200, Width = 400 };
    readonly Grid _meshPanel = new() { Visibility = Visibility.Collapsed };
    readonly Button _export = new() { Content = "Export…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _import = new() { Content = "Import…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _from27 = new() { Content = "From NHL 27", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };

    BundleIndex? _index;
    string? _indexGame;
    List<Entry>? _catalog;
    MeshSet? _mesh;
    BundleIndex.Res? _meshRes;
    Rgba? _texImage;
    byte[]? _rawBytes;
    Entry? _selected;
    bool _busy;

    public AssetsPage(SuiteSettings settings)
    {
        _settings = settings;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1c));

        _kind.Items.Add("Everything"); _kind.Items.Add("Meshes"); _kind.Items.Add("Textures"); _kind.Items.Add("EBX"); _kind.Items.Add("Data");
        _kind.SelectedIndex = 0;

        // folder group headers: "<folder>  (n assets)"
        var headerPanel = new FrameworkElementFactory(typeof(StackPanel));
        headerPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var headerName = new FrameworkElementFactory(typeof(TextBlock));
        headerName.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        headerName.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x9f, 0xb4, 0xcf)));
        headerName.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var headerCount = new FrameworkElementFactory(typeof(TextBlock));
        headerCount.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ItemCount") { StringFormat = "   ({0})" });
        headerCount.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x5f, 0x73, 0x91)));
        headerPanel.AppendChild(headerName);
        headerPanel.AppendChild(headerCount);
        _hits.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = new DataTemplate { VisualTree = headerPanel },
            ContainerStyle = new Style(typeof(GroupItem)) { Setters = { new Setter(MarginProperty, new Thickness(0, 8, 0, 2)) } },
        });

        var panel = new StackPanel { Margin = new Thickness(32, 26, 32, 26) };
        panel.Children.Add(new TextBlock { Text = "Asset browser", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = "Search everything inside the game — every mesh, texture and data file in the bundles. " +
                   "Meshes render as a colored top view and export/import as OBJ; textures decode to images, import as PNG, and can be pulled straight from an NHL 27 build. " +
                   "Writes take a backup first and are read back; the game must be closed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), FontSize = 12,
        });

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
        searchRow.Children.Add(new TextBlock { Text = "Search", Width = 76, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        searchRow.Children.Add(_search);
        searchRow.Children.Add(_kind);
        panel.Children.Add(searchRow);

        var main = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var left = new StackPanel { Width = 430, Margin = new Thickness(0, 0, 18, 0) };
        left.Children.Add(_hits);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        btnRow.Children.Add(_export);
        btnRow.Children.Add(_import);
        btnRow.Children.Add(_from27);
        left.Children.Add(btnRow);
        left.Children.Add(_status);
        main.Children.Add(left);

        var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 800 };
        var right = new StackPanel { Width = 900 };
        right.Children.Add(_detailInfo);

        var meshRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        meshRow.Children.Add(new TextBlock { Text = "LOD", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), Margin = new Thickness(0, 0, 6, 0) });
        meshRow.Children.Add(_lod);
        meshRow.Children.Add(_sections);
        _meshPanel.Children.Add(meshRow);
        right.Children.Add(_meshPanel);
        right.Children.Add(_preview);
        rightScroll.Content = right;
        main.Children.Add(rightScroll);
        panel.Children.Add(main);

        Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _search.TextChanged += async (_, _) => await SearchAsync();
        _kind.SelectionChanged += async (_, _) => await SearchAsync();
        _hits.SelectionChanged += async (_, _) => await ShowSelectionAsync();
        _lod.SelectionChanged += async (_, _) => await RenderMeshAsync();
        _export.Click += (_, _) => Export();
        _import.Click += async (_, _) => await ImportAsync();
        _from27.Click += async (_, _) => await ImportFrom27Async();
        Loaded += async (_, _) => await EnsureCatalogAsync();
    }

    string? Game => !string.IsNullOrWhiteSpace(_settings.ApplyTargetDir) && Directory.Exists(_settings.ApplyTargetDir) ? _settings.ApplyTargetDir : null;

    BundleIndex Index(string game)
    {
        if (_index is not null && _indexGame == game) return _index;
        _indexGame = game;
        return _index = BundleIndex.Build(game);
    }

    async Task EnsureCatalogAsync()
    {
        var game = Game;
        if (game is null) { Say("Set the NHL 24 game folder in Settings first."); return; }
        if (_catalog is not null) { await SearchAsync(); return; }
        try
        {
            Say("Indexing the game's bundles (first open takes a moment)...");
            var catalog = await Task.Run(() =>
            {
                var index = Index(game);
                var list = new List<Entry>(index.EbxByName.Count + index.ResByName.Count);
                foreach (var name in index.EbxByName.Keys) list.Add(new Entry(name, 'E'));
                foreach (var name in index.ResByName.Keys)
                    list.Add(new Entry(name, name.EndsWith("_mesh", StringComparison.Ordinal) ? 'M' : 'T'));
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return list;
            });
            _catalog = catalog;
            Say($"{catalog.Count:n0} assets indexed ({catalog.Count(c => c.Kind == 'E'):n0} EBX · {catalog.Count(c => c.Kind is 'M' or 'T'):n0} resources). Type to search.");
            await SearchAsync();
        }
        catch (Exception ex) { Say("Could not index the game: " + ex.Message); }
    }

    char? WantedKind => _kind.SelectedIndex switch { 1 => 'M', 2 => 'T', 3 => 'E', 4 => 'D', _ => null };

    async Task SearchAsync()
    {
        if (_catalog is null) return;
        var query = _search.Text.Trim();
        var kind = WantedKind;
        var hits = await Task.Run(() =>
        {
            var list = new List<Entry>();
            if (query.Length == 0)
            {
                foreach (var e in _catalog)
                {
                    if (kind is null ? e.Kind == 'M' : e.Kind == kind.Value) list.Add(e);
                    if (list.Count >= MaxResults) break;
                }
            }
            else
            {
                foreach (var e in _catalog)
                {
                    if (kind is not null && e.Kind != kind.Value) continue;
                    if (e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) list.Add(e);
                    if (list.Count >= MaxResults) break;
                }
            }
            return list;
        });
        // group by folder so related assets (a mesh and its textures) sit together
        var cvs = new CollectionViewSource { Source = hits };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Entry.Folder)));
        cvs.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(Entry.Name), System.ComponentModel.ListSortDirection.Ascending));
        _hits.ItemsSource = cvs.View;
        Say(hits.Count >= MaxResults ? $"{MaxResults}+ matches (showing first {MaxResults})" : $"{hits.Count} match(es).");
    }

    async Task ShowSelectionAsync()
    {
        if (_busy || _hits.SelectedItem is not Entry e) return;
        var game = Game;
        if (game is null) return;
        _busy = true;
        _selected = e;
        _mesh = null; _meshRes = null; _texImage = null; _rawBytes = null;
        _meshPanel.Visibility = Visibility.Collapsed;
        _preview.Source = null;
        _detailInfo.Children.Clear();
        _lod.Items.Clear();
        _sections.Items.Clear();
        _export.IsEnabled = _import.IsEnabled = _from27.IsEnabled = false;
        try
        {
            await Task.Run(() => LoadDetail(game, e));
            _export.IsEnabled = true;
            _import.IsEnabled = e.Kind is 'M' or 'T';
            _from27.IsEnabled = e.Kind == 'T';
        }
        catch (Exception ex)
        {
            InfoLine(e.Name, e.KindLabel, "unreadable: " + ex.Message);
        }
        finally { _busy = false; }
    }

    void LoadDetail(string game, Entry e)
    {
        var index = Index(game);
        switch (e.Kind)
        {
            case 'E':
            {
                var ebx = index.EbxByName[e.Name][0];
                var raw = BundleIndex.ReadBytes(ebx.Data);
                _rawBytes = raw;
                try
                {
                    var riff = EbxRiff.Parse(raw);
                    InfoLine(e.Name, "ebx", $"{riff.FileGuid}  ·  {riff.ImportedFileGuids.Count} import(s)");
                    InfoLine("", "", $"bundle: {ebx.Bundle.FirstName ?? Path.GetFileName(ebx.Bundle.Toc)}  ·  {raw.Length:n0} bytes");
                }
                catch
                {
                    InfoLine(e.Name, "ebx (non-RIFF)", $"{raw.Length:n0} bytes");
                    ShowHex(raw);
                }
                break;
            }
            case 'M':
            {
                var res = index.ResByName[e.Name][0];
                var mesh = MeshSet.Load(game, index, e.Name);
                _mesh = mesh; _meshRes = res;
                Dispatcher.BeginInvoke(() =>
                {
                    for (int i = 0; i < mesh.Lods.Count; i++) _lod.Items.Add($"LOD {i}");
                    _lod.SelectedIndex = 0;
                    _meshPanel.Visibility = Visibility.Visible;
                });
                break;
            }
            default:
            {
                var res = index.ResByName[e.Name][0];
                var stored = BundleIndex.ReadBytes(res.Data);
                byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
                // header byte 12 is the BCn format: 0x37 BC1, 0x3D BC3, 0x3F BC5, 0x43 BC7.
                int fmt = header.Length > 48 ? header[12] : 0;
                try
                {
                    switch (fmt)
                    {
                        case 0x43:
                        {
                            var tr = TextureRes.Parse(header);
                            var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid)
                                .FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw
                                ?? throw new InvalidOperationException("no full copy of the texture chunk");
                            var img = Bc7.DecodeTiled(TextureChunk.Parse(full, tr).Mips[0], tr.Width, tr.Height);
                            _texImage = img;
                            InfoLine(e.Name, "texture (BC7)", $"{tr.Width}×{tr.Height} · {tr.MipCount} mips · {stored.Length:n0} bytes");
                            Show(img);
                            break;
                        }
                        case 0x37 or 0x3D or 0x3F:
                        {
                            var tr = TextureRes.Parse(header);
                            long size = tr.PayloadSize * Ps4Codec.Slices(header);
                            var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid)
                                .FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == size).Raw
                                ?? throw new InvalidOperationException("no full copy of the texture chunk");
                            var mip = TextureChunk.Parse(full[..(int)tr.PayloadSize], tr).Mips[0];
                            Rgba img = fmt switch
                            {
                                0x37 => Bc1.DecodeTiled(mip, tr.Width, tr.Height),
                                0x3D => Bc3.DecodeTiled(mip, tr.Width, tr.Height),
                                _ => Bc5.DecodeTiled(mip, tr.Width, tr.Height),
                            };
                            _texImage = img;
                            InfoLine(e.Name, $"texture ({(fmt == 0x37 ? "BC1" : fmt == 0x3D ? "BC3" : "BC5")})", $"{tr.Width}×{tr.Height} · {tr.MipCount} mips · {stored.Length:n0} bytes");
                            Show(img);
                            break;
                        }
                        default:
                            throw new InvalidDataException($"unrecognized texture format byte 0x{fmt:X2}");
                    }
                }
                catch (Exception texEx)
                {
                    _rawBytes = stored;
                    InfoLine(e.Name, "data", $"{stored.Length:n0} bytes — not a recognized texture ({texEx.Message})");
                    ShowHex(header);
                }
                break;
            }
        }
    }

    async Task RenderMeshAsync()
    {
        if (_mesh is null || _meshRes is null || _lod.SelectedIndex < 0) return;
        var game = Game;
        if (game is null) return;
        var mesh = _mesh; var res = _meshRes; var lodIndex = _lod.SelectedIndex;
        try
        {
            var (img, rows) = await Task.Run(() =>
            {
                var index = Index(game);
                return Render(index, mesh, res, lodIndex);
            });
            _preview.Source = MakeBitmap(img);
            _sections.Items.Clear();
            foreach (var r in rows) _sections.Items.Add(r);
            var lod = mesh.Lods[lodIndex];
            InfoLine(mesh.Name, "mesh", $"{lod.Sections.Count} section(s) · {lod.Sections.Sum(s => s.VertexCount):n0} verts · type {mesh.MeshType} · LOD {lodIndex}");
        }
        catch (Exception ex)
        {
            _preview.Source = null;
            InfoLine(mesh.Name, "mesh", "render failed: " + ex.Message);
        }
    }

    /// <summary>Top view (x→right, z→up), one hue per section, filled triangles; vertex dots when no index buffer.</summary>
    static (Rgba, List<SectionRow>) Render(BundleIndex index, MeshSet mesh, BundleIndex.Res res, int lodIndex)
    {
        var lod = mesh.Lods[lodIndex];
        var chunk = MeshSet.LoadLodChunk(index, res, lod);

        var sections = new List<(MeshSet.Section s, float[] pos)>();
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var s in lod.Sections)
        {
            float[] p;
            try { p = MeshSet.Positions(lod, s, chunk); }
            catch { continue; }
            for (int v = 0; v < s.VertexCount; v++)
            {
                float x = p[v * 3], z = p[v * 3 + 2];
                if (!float.IsFinite(x) || !float.IsFinite(z)) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            sections.Add((s, p));
        }
        if (sections.Count == 0) throw new InvalidOperationException("no section decoded");
        if (minX >= maxX) { minX -= 1; maxX += 1; }
        if (minZ >= maxZ) { minZ -= 1; maxZ += 1; }

        const int maxSide = 960, margin = 30;
        float spanX = maxX - minX, spanZ = maxZ - minZ;
        float scale = Math.Min((maxSide - margin * 2) / spanX, (maxSide - margin * 2) / spanZ);
        int w = (int)(spanX * scale) + margin * 2, h = (int)(spanZ * scale) + margin * 2;
        var px = new byte[w * h * 4];
        for (int i = 0; i + 3 < px.Length; i += 4) { px[i] = 0x10; px[i + 1] = 0x14; px[i + 2] = 0x1a; px[i + 3] = 255; }

        int MapX(float x) => margin + (int)((x - minX) * scale);
        int MapZ(float z) => h - margin - (int)((z - minZ) * scale);   // flip: +z up

        var rows = new List<SectionRow>();
        var idx = MeshSet.Indices(lod, chunk);
        for (int si = 0; si < sections.Count; si++)
        {
            var (s, p) = sections[si];
            var color = ColorFromHsi(si * 47 % 360);
            rows.Add(new SectionRow(s.Name.Length == 0 ? "(unnamed)" : s.Name, s.VertexCount, s.Primitives, color));
            byte r = color.R, g = color.G, b = color.B;

            void Dot(int cx, int cy)
            {
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = cx + dx, yy = cy + dy;
                    if ((uint)xx < w && (uint)yy < h)
                    { int o = (yy * w + xx) * 4; px[o] = r; px[o + 1] = g; px[o + 2] = b; }
                }
            }

            bool drew = false;
            if (idx.Length > 0)
            {
                int start = s.StartIndex, count = Math.Min(s.Primitives * 3, idx.Length - start);
                for (int t = start; t + 2 < start + count; t += 3)
                {
                    if (idx[t] >= s.VertexCount || idx[t + 1] >= s.VertexCount || idx[t + 2] >= s.VertexCount) break;
                    Fill(px, w, h, MapX(p[idx[t] * 3]), MapZ(p[idx[t] * 3 + 2]),
                                     MapX(p[idx[t + 1] * 3]), MapZ(p[idx[t + 1] * 3 + 2]),
                                     MapX(p[idx[t + 2] * 3]), MapZ(p[idx[t + 2] * 3 + 2]), r, g, b);
                    drew = true;
                }
            }
            if (!drew)
                for (int v = 0; v < s.VertexCount; v++)
                    if (float.IsFinite(p[v * 3]) && float.IsFinite(p[v * 3 + 2])) Dot(MapX(p[v * 3]), MapZ(p[v * 3 + 2]));
        }

        // scale bar: 5 meters
        int bar = (int)(5 * scale);
        for (int x = margin; x < margin + bar && x < w; x++) { int o = ((h - margin / 2) * w + x) * 4; px[o] = 200; px[o + 1] = 200; px[o + 2] = 200; }
        return (new Rgba(w, h, px), rows);
    }

    static void Fill(byte[] px, int w, int h, int x0, int y0, int x1, int y1, int x2, int y2, byte r, byte g, byte b)
    {
        int minX = Math.Max(0, Math.Min(x0, Math.Min(x1, x2))), maxX = Math.Min(w - 1, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(0, Math.Min(y0, Math.Min(y1, y2))), maxY = Math.Min(h - 1, Math.Max(y0, Math.Max(y1, y2)));
        int d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
        if (d == 0) return;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            int w0 = (y1 - y2) * (x - x2) + (x2 - x1) * (y - y2);
            int w1 = (y2 - y0) * (x - x2) + (x0 - x2) * (y - y2);
            int w2 = d - w0 - w1;
            if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0))
            { int o = (y * w + x) * 4; px[o] = r; px[o + 1] = g; px[o + 2] = b; }
        }
    }

    static Color ColorFromHsi(double hue)
    {
        double hf = hue / 60;
        int i = (int)hf % 6;
        double f = hf - (int)hf;
        byte v = 235, p = 70, q = (byte)(v - (v - p) * f), t = (byte)(p + (v - p) * f);
        return i switch
        {
            0 => Color.FromRgb(v, t, p), 1 => Color.FromRgb(q, v, p), 2 => Color.FromRgb(p, v, t),
            3 => Color.FromRgb(p, q, v), 4 => Color.FromRgb(t, p, v), _ => Color.FromRgb(v, p, q),
        };
    }

    // ---------- export ----------

    void Export()
    {
        if (_selected is not { } e) return;
        var game = Game;
        if (game is null) return;
        try
        {
            if (e.Kind == 'M' && _mesh is not null && _meshRes is not null)
            {
                var lodIndex = Math.Max(0, _lod.SelectedIndex);
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export mesh as OBJ", FileName = Path.GetFileName(e.Name) + ".obj", Filter = "Wavefront OBJ (*.obj)|*.obj" };
                if (dlg.ShowDialog() != true) return;
                var obj = BuildObj(game, _mesh, _meshRes, lodIndex);
                File.WriteAllText(dlg.FileName, obj);
                Say($"Exported OBJ → {dlg.FileName} ({obj.Length:n0} bytes). Vertices are in section order; edit positions only, keep the count.");
            }
            else if (_texImage is not null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export texture as PNG", FileName = Path.GetFileName(e.Name) + ".png", Filter = "PNG (*.png)|*.png" };
                if (dlg.ShowDialog() != true) return;
                ImageIO.SavePng(_texImage, dlg.FileName);
                Say($"Exported PNG → {dlg.FileName}.");
            }
            else if (_rawBytes is not null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export raw bytes", FileName = Path.GetFileName(e.Name) + ".bin", Filter = "Binary (*.bin)|*.bin" };
                if (dlg.ShowDialog() != true) return;
                File.WriteAllBytes(dlg.FileName, _rawBytes);
                Say($"Exported {_rawBytes.Length:n0} bytes → {dlg.FileName}.");
            }
        }
        catch (Exception ex) { Say("Export failed: " + ex.Message); }
    }

    static string BuildObj(string game, MeshSet mesh, BundleIndex.Res res, int lodIndex)
    {
        var index = BundleIndex.Build(game);
        var lod = mesh.Lods[lodIndex];
        var chunk = MeshSet.LoadLodChunk(index, res, lod);
        var sb = new StringBuilder();
        sb.AppendLine("# " + mesh.Name + $" (LOD {lodIndex}) — exported by Center Ice Modding Suite");
        sb.AppendLine("# sections in order; edit vertex positions, do not add/remove vertices");
        var idx = MeshSet.Indices(lod, chunk);
        int offset = 1;
        foreach (var s in lod.Sections)
        {
            float[] p;
            try { p = MeshSet.Positions(lod, s, chunk); }
            catch { continue; }
            sb.AppendLine("g " + (s.Name.Length == 0 ? "section" : s.Name.Replace(' ', '_')));
            for (int v = 0; v < s.VertexCount; v++)
            {
                float x = float.IsFinite(p[v * 3]) ? p[v * 3] : 0;
                float y = float.IsFinite(p[v * 3 + 1]) ? p[v * 3 + 1] : 0;
                float z = float.IsFinite(p[v * 3 + 2]) ? p[v * 3 + 2] : 0;
                sb.AppendLine(FormattableString.Invariant($"v {x:0.######} {y:0.######} {z:0.######}"));
            }
            if (idx.Length > 0)
            {
                int start = s.StartIndex, count = Math.Min(s.Primitives * 3, idx.Length - start);
                for (int t = start; t + 2 < start + count; t += 3)
                {
                    if (idx[t] >= s.VertexCount || idx[t + 1] >= s.VertexCount || idx[t + 2] >= s.VertexCount) break;
                    sb.AppendLine($"f {idx[t] + offset} {idx[t + 1] + offset} {idx[t + 2] + offset}");
                }
            }
            offset += s.VertexCount;
        }
        return sb.ToString();
    }

    // ---------- import ----------

    async Task ImportAsync()
    {
        if (_selected is not { } e) return;
        var game = Game;
        if (game is null) return;
        if (e.Kind == 'M') { await ImportMeshObjAsync(game, e); return; }

        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Replacement texture (PNG, BMP, DDS…)", Filter = "Images (*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp)|*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        var answer = MessageBox.Show(
            $"Replace texture\n{e.Name}\nin\n{game}\n\nwith\n{dlg.FileName}\n\nA backup is taken first and the result is read back; the game must be closed.",
            "Import texture", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            Say("Importing texture...");
            var name = e.Name; var src = dlg.FileName;
            var (ok, message, _) = await Task.Run(() => TextureSlots.Apply(game,
                [new TextureReplacement(name, src)], p => Dispatcher.BeginInvoke(() => Say(p))));
            Say(message);
            if (ok) await ShowSelectionAsync();
        }
        catch (Exception ex) { Say("Import failed: " + ex.Message); }
    }

    async Task ImportMeshObjAsync(string game, Entry e)
    {
        if (_mesh is null || _meshRes is null) return;
        if (_lod.SelectedIndex < 0) { Say("Pick a LOD first."); return; }
        var lodIndex = _lod.SelectedIndex;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "OBJ with new vertex positions (same count as exported)", Filter = "Wavefront OBJ (*.obj)|*.obj" };
        if (dlg.ShowDialog() != true) return;
        var answer = MessageBox.Show(
            $"Write the vertex positions from\n{dlg.FileName}\ninto\n{e.Name} (LOD {lodIndex})\nin\n{game}\n\nA backup is taken first and the result is read back; the game must be closed.",
            "Import mesh positions", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            Say("Importing mesh positions...");
            var name = e.Name; var src = dlg.FileName; var mesh = _mesh; var res = _meshRes;
            var (ok, message) = await Task.Run(() =>
            {
                var verts = ParseObjVertices(src);
                var index = Index(game);
                var m = MeshSet.Load(game, index, name);
                return MeshSetPositionsApply.Apply(game, index, m, res, lodIndex, verts,
                    p => Dispatcher.BeginInvoke(() => Say(p)));
            });
            Say(message);
            if (ok) await RenderMeshAsync();
        }
        catch (Exception ex) { Say("Import failed: " + ex.Message); }
    }

    static List<(float X, float Y, float Z)> ParseObjVertices(string path)
    {
        var list = new List<(float, float, float)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                    list.Add((x, y, z));
            }
        }
        if (list.Count == 0) throw new InvalidDataException("No vertices (v lines) in that OBJ.");
        return list;
    }

    async Task ImportFrom27Async()
    {
        if (_selected is not { } e || e.Kind != 'T') return;
        var game = Game;
        if (game is null) return;
        try
        {
            Say("Loading the NHL 27 asset index...");
            var i27 = await IndexStore.GetAsync(IndexStore.Nhl27, _settings, progress: s => Dispatcher.BeginInvoke(() => Say(s)));
            var entry = i27.Get(e.Name);
            if (entry is null)
            {
                // try a few common sibling paths before giving up
                Say($"'{e.Name}' is not in the NHL 27 index under that exact path.");
                return;
            }
            var answer = MessageBox.Show(
                $"Fetch '{e.Name}' from the NHL 27 build, convert it, and write it into NHL 24?\n\nA backup is taken first and the result is read back; the game must be closed.",
                "Import from NHL 27", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            Say("Decoding the NHL 27 (PS5) texture...");
            var tex = new Nhl27Texture(entry);
            var pngs = await new TextureImages(_settings).Nhl27Async([tex], p => Dispatcher.BeginInvoke(() => Say(p)));
            if (!pngs.TryGetValue(tex.Name, out var png))
            { Say("The NHL 27 decoder did not produce an image for that texture."); return; }
            Say("Converting and writing into NHL 24...");
            var name = e.Name;
            var (ok, message, _) = await Task.Run(() => TextureSlots.Apply(game,
                [new TextureReplacement(name, png)], p => Dispatcher.BeginInvoke(() => Say(p))));
            Say(message);
            if (ok) await ShowSelectionAsync();
        }
        catch (Exception ex) { Say("NHL 27 import failed: " + ex.Message); }
    }

    // ---------- info helpers ----------

    void InfoLine(string title, string kind, string detail)
    {
        void Add()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            if (title.Length > 0) row.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            if (kind.Length > 0) row.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x3d, 0x6b)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 6, 0), Child = new TextBlock { Text = kind, FontSize = 11 } });
            if (detail.Length > 0) row.Children.Add(new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), TextWrapping = TextWrapping.Wrap, MaxWidth = 620 });
            _detailInfo.Children.Add(row);
        }
        if (Dispatcher.CheckAccess()) Add(); else Dispatcher.BeginInvoke(Add);
    }

    void Show(Rgba img)
    {
        var bmp = MakeBitmap(img);
        Dispatcher.BeginInvoke(() => _preview.Source = bmp);
    }

    void ShowHex(byte[] data)
    {
        var sb = new StringBuilder();
        for (int row = 0; row < Math.Min(16, (data.Length + 15) / 16); row++)
        {
            for (int col = 0; col < 16 && row * 16 + col < data.Length; col++)
                sb.Append($"{data[row * 16 + col]:X2} ");
            sb.Append('\n');
        }
        Dispatcher.BeginInvoke(() => _detailInfo.Children.Add(new TextBlock
        {
            Text = sb.ToString(), FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7f, 0x95, 0xb2)), Margin = new Thickness(0, 8, 0, 0),
        }));
    }

    static BitmapSource MakeBitmap(Rgba img)
    {
        var file = Path.Combine(Path.GetTempPath(), "cims-asset-" + Guid.NewGuid().ToString("N") + ".png");
        ImageIO.SavePng(img, file);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(file);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    void Say(string text) => _status.Text = text;
}
