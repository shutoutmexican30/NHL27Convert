using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Core.FullRink;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

var res = index.ResByName["content/worlds/rink/ice/components/rink_ice_nhl_mesh"].First();
var stored = BundleIndex.ReadBytes(res.Data);
byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
var sections = FullRinkMesh.Parse(header);
var logo = sections.First(s => s.Name == "ice_centerlogo_mat");
Console.WriteLine($"logo patch: verts={logo.Verts} bbox X [{logo.MinX:F2}..{logo.MaxX:F2}]  Z [{logo.MinZ:F2}..{logo.MaxZ:F2}]  (stock = ~+-6.08)");

var s = CenterIceSlots.Read(game, index, 0);
ImageIO.SavePng(ImageIO.ResizeSmooth(s.Image, 256, 256), @"C:\dev\NHL27Convert\tools\MeshProbe\out\slot0_after_rollback.png");
Console.WriteLine("slot0_after_rollback.png saved");
