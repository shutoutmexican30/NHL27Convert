using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using StbImageWriteSharp;

namespace RinkSharp;

public sealed record MipReport(int Level, int Width, int Height, double Psnr, int MaxError, double MeanError, double MarkingSaturationLoss);

public sealed record VerifyReport(IReadOnlyList<MipReport> Mips, string HeatmapPath)
{
    public bool Pass => Mips.Count > 0 && Mips[0].Psnr >= 35 && Mips.All(m => m.MaxError <= 64);
}

/// <summary>
/// Round-trip check: decode the DDS that was just written and compare every mip against the reference chain
/// that was handed to the encoder. Reports PSNR per mip, worst texel, and — because faded markings are the
/// failure mode this tool exists to prevent — how much saturation the red/blue line texels lost. Also writes
/// a heatmap PNG (amplified per-texel error) for eyeballing.
/// </summary>
public static class Verifier
{
    public static VerifyReport Run(string ddsPath, IReadOnlyList<RgbaImage> reference, string heatmapPath)
    {
        var (width, height, blocks) = Dds.Read(ddsPath);
        if (width != reference[0].Width || height != reference[0].Height)
            throw new InvalidDataException("DDS dimensions do not match the reference chain.");

        var decoder = new BcDecoder { Options = { IsParallel = true } };
        var reports = new List<MipReport>();
        var topDiff = new byte[(long)width * height * 3];
        byte[]? decodedTop = null;
        byte[]? decodedLast = null;
        var lastW = 0; var lastH = 0;

        for (var level = 0; level < blocks.Count; level++)
        {
            var refImage = reference[level];
            var decoded = decoder.DecodeRaw(blocks[level], refImage.Width, refImage.Height, CompressionFormat.Bc7);
            if (level == 0)
            {
                decodedTop = new byte[(long)refImage.Width * refImage.Height * 3];
                for (var px = 0; px < decoded.Length; px++)
                {
                    decodedTop[px * 3] = decoded[px].r;
                    decodedTop[px * 3 + 1] = decoded[px].g;
                    decodedTop[px * 3 + 2] = decoded[px].b;
                }
            }
            decodedLast = new byte[(long)refImage.Width * refImage.Height * 3];
            for (var px = 0; px < decoded.Length; px++)
            {
                decodedLast[px * 3] = decoded[px].r;
                decodedLast[px * 3 + 1] = decoded[px].g;
                decodedLast[px * 3 + 2] = decoded[px].b;
            }
            lastW = refImage.Width; lastH = refImage.Height;
            long errSum = 0, errSqSum = 0, n = 0;
            var maxErr = 0;
            double satLossSum = 0; var satCount = 0;

            for (var px = 0; px < decoded.Length; px++)
            {
                var ro = px * 4;
                var dR = Math.Abs(decoded[px].r - refImage.Pixels[ro]);
                var dG = Math.Abs(decoded[px].g - refImage.Pixels[ro + 1]);
                var dB = Math.Abs(decoded[px].b - refImage.Pixels[ro + 2]);
                var err = Math.Max(dR, Math.Max(dG, dB));
                errSum += err;
                errSqSum += (long)err * err;
                if (err > maxErr) maxErr = err;
                n++;

                // Marking check: strongly saturated red or blue in the reference (line art), measure saturation drop.
                var rr = refImage.Pixels[ro]; var rg = refImage.Pixels[ro + 1]; var rb = refImage.Pixels[ro + 2];
                if ((rr - rg > 60 && rr - rb > 40) || (rb - rr > 60 && rb - rg > 40))
                {
                    double Sat(byte a, byte b, byte c) => a - (b + c) / 2.0;
                    satLossSum += Math.Max(0, Sat(rr, rg, rb) - Sat(decoded[px].r, decoded[px].g, decoded[px].b));
                    satCount++;
                }

                if (level == 0)
                {
                    var heat = Math.Min(255, err * 8); // x8 amplification
                    var ho = px * 3;
                    topDiff[ho] = (byte)heat;
                    topDiff[ho + 1] = (byte)(err > 0 ? 40 : 0);
                    topDiff[ho + 2] = (byte)(err > 0 ? 40 : 0);
                }
            }

            var mse = (double)errSqSum / n;
            var psnr = mse == 0 ? 99.0 : 10 * Math.Log10(255.0 * 255.0 / mse);
            reports.Add(new MipReport(level, refImage.Width, refImage.Height, psnr, maxErr,
                (double)errSum / n, satCount > 0 ? satLossSum / satCount : 0));
        }

        var name = Path.GetFileNameWithoutExtension(ddsPath);
        var dir = Path.GetDirectoryName(heatmapPath) ?? ".";
        using (var fs = File.Create(heatmapPath))
        {
            var writer = new ImageWriter();
            writer.WritePng(topDiff, width, height, ColorComponents.RedGreenBlue, fs);
        }
        using (var fs = File.Create(Path.Combine(dir, name + "_dec.png")))
        {
            var writer = new ImageWriter();
            writer.WritePng(decodedTop!, width, height, ColorComponents.RedGreenBlue, fs);
        }
        using (var fs = File.Create(Path.Combine(dir, name + "_decmip" + (blocks.Count - 1) + ".png")))
        {
            var writer = new ImageWriter();
            writer.WritePng(decodedLast!, lastW, lastH, ColorComponents.RedGreenBlue, fs);
        }

        return new VerifyReport(reports, heatmapPath);
    }
}
