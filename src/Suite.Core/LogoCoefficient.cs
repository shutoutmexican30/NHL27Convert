namespace Suite.Core;

/// <summary>Clean material coefficients for a logo array, generated from that logo's own visible alpha.</summary>
public static class LogoCoefficient
{
    /// <summary>Inside (128,54,225,0), outside (126,0,255,0). Base-garment coeff maps are deliberately not handled here.</summary>
    public static Rgba FromColor(Rgba color)
    {
        var pixels = new byte[color.Pixels.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Alpha 16 is the suite's existing visibility cutoff. Coefficient alpha itself is always zero.
            if (color.Pixels[i + 3] > 16)
            {
                pixels[i] = 128; pixels[i + 1] = 54; pixels[i + 2] = 225;
            }
            else
            {
                pixels[i] = 126; pixels[i + 2] = 255;
            }
        }
        return new Rgba(color.Width, color.Height, pixels);
    }
}
