using System;
using Suite.Core;
using Suite.Core.Frostbite;

// synthetic round-trip: random image -> encode -> decode -> error stats
var rnd = new Random(42);
foreach (int dim in new[] { 64, 100, 256 })
{
    int n = dim * dim;
    var px = new byte[n * 4];
    for (int i = 0; i < n; i++)
    {
        // smooth-ish gradients decode more accurately than white noise; use per-block gradient
        int bx = i % dim / 8, by = i / dim / 8;
        px[i * 4] = (byte)(bx * 30 + rnd.Next(20));
        px[i * 4 + 1] = (byte)(by * 30 + rnd.Next(20));
        px[i * 4 + 2] = (byte)((bx + by) * 15 + rnd.Next(20));
        px[i * 4 + 3] = (byte)(128 + bx * 10 + rnd.Next(10));
    }
    var img = new Rgba(dim, dim, px);
    var enc = Bc3.EncodeTiled(img);
    var dec = Bc3.DecodeTiled(enc, dim, dim);
    long err = 0, errA = 0;
    for (int i = 0; i < n; i++)
        for (int c = 0; c < 3; c++) err += Math.Abs(dec.Pixels[i * 4 + c] - px[i * 4 + c]);
    for (int i = 0; i < n; i++) errA += Math.Abs(dec.Pixels[i * 4 + 3] - px[i * 4 + 3]);
    Console.WriteLine($"{dim}x{dim}: mean rgb err {err / (double)(n * 3):F1}/255, alpha err {errA / (double)n:F1}/255");
}
