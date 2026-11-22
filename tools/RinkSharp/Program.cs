using System.Diagnostics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using StbImageSharp;

namespace RinkSharp;

/// <summary>
/// Sharp-focused full-rink texture builder.
///
///   rinksharp &lt;layout.png&gt; &lt;out.dds&gt; [--size 4096] [--template] [--quality fast|balanced|best]
///        [--verify &lt;folder&gt;] [--no-fixups]
///
/// Turns a top-down rink layout (TheFaceoff.net style, ~5000x2236) into a square BC7 sRGB DDS with a full
/// mip chain, ready to be wrapped as GNF for the vertex-stretched full-rink model. Filtering and mip
/// averaging run in linear light so lines stay saturated at every distance.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: rinksharp <layout.png> <out.dds> [--size N] [--template] " +
                                    "[--quality fast|balanced|best] [--verify <folder>] [--no-fixups]");
            return 2;
        }

        var input = args[0];
        var output = args[1];
        var size = 4096;
        var mode = FitMode.Layout;
        var quality = CompressionQuality.Balanced;
        var verifyFolder = (string?)null;
        var fixups = true;
        var grade = new Grade();

        for (var i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--size": size = int.Parse(args[++i]); break;
                case "--template": mode = FitMode.Template; break;
                case "--quality": quality = Enum.Parse<CompressionQuality>(args[++i], ignoreCase: true); break;
                case "--verify": verifyFolder = args[++i]; break;
                case "--no-fixups": fixups = false; break;
                case "--darken": grade = grade with { Exposure = double.Parse(args[++i]) }; break;
                case "--contrast": grade = grade with { Contrast = double.Parse(args[++i]) }; break;
                case "--saturation": grade = grade with { Saturation = double.Parse(args[++i]) }; break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 2;
            }

        if (size is < 64 or > 8192 || (size & (size - 1)) != 0)
        {
            Console.Error.WriteLine("--size must be a power of two between 64 and 8192.");
            return 2;
        }

        var clock = Stopwatch.StartNew();

        var bytes = File.ReadAllBytes(input);
        var loaded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        var source = new RgbaImage(loaded.Width, loaded.Height, loaded.Data);
        Console.WriteLine($"source: {source.Width}x{source.Height} ({mode})");

        RinkMeasure? measure = null;
        if (mode == FitMode.Layout)
        {
            measure = RinkFrame.Measure(source);
            Console.WriteLine($"measure: {measure.How}");
            if (!measure.Found)
                Console.WriteLine("WARNING: no board outline detected; falling back to picture width = rink length. " +
                                  "For a known-good picture use --template.");
        }

        var fitted = RinkFrame.Fit(source, mode, size, measure);
        if (!grade.IsIdentity)
        {
            Grading.Apply(fitted, grade);
            Console.WriteLine($"grade: darken x{grade.Exposure:F2}, contrast x{grade.Contrast:F2}, saturation x{grade.Saturation:F2}");
        }
        if (mode == FitMode.Layout && fixups)
        {
            RinkFrame.HideDrawnNets(fitted);
            RinkFrame.ShrinkCreases(fitted);
            Console.WriteLine("fixups: drawn nets painted out, creases shrunk to meet the goal posts");
        }

        Console.WriteLine("mips: building in linear light...");
        var chain = MipChain.Build(fitted);

        Console.WriteLine($"encode: BC7 ({quality})...");
        var mips = chain.Select(m => Bc7.Encode(m, quality)).ToList();
        Dds.Write(output, size, size, mips);
        Console.WriteLine($"wrote {output} ({new FileInfo(output).Length / 1024 / 1024} MB, {mips.Count} mips) in {clock.Elapsed.TotalSeconds:F1}s");

        if (verifyFolder != null)
        {
            Directory.CreateDirectory(verifyFolder);
            var report = Verifier.Run(output, chain, Path.Combine(verifyFolder, Path.GetFileNameWithoutExtension(output) + "_heat.png"));
            Console.WriteLine();
            Console.WriteLine($"{"mip",4} {"size",10} {"PSNR dB",8} {"mean",6} {"max",4} {"sat loss",9}");
            foreach (var m in report.Mips)
                Console.WriteLine($"{m.Level,4} {m.Width + "x" + m.Height,10} {m.Psnr,8:F1} {m.MeanError,6:F2} {m.MaxError,4} {m.MarkingSaturationLoss,9:F2}");
            Console.WriteLine($"heatmap: {report.HeatmapPath}");
            Console.WriteLine(report.Pass ? "VERIFY PASS" : "VERIFY FAIL (inspect the heatmap)");
            return report.Pass ? 0 : 1;
        }
        return 0;
    }
}
