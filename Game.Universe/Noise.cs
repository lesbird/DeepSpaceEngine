using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Deterministic 3D value noise with fractional Brownian motion (fBm). Lattice values are
/// hashed from integer coordinates, so the field is reproducible everywhere with no storage.
/// </summary>
public sealed class Noise
{
    private readonly ulong _seed;

    public Noise(ulong seed) => _seed = seed;

    private double Lattice(long x, long y, long z)
    {
        ulong h = _seed;
        h = Hashing.Combine(h, unchecked((ulong)x));
        h = Hashing.Combine(h, unchecked((ulong)y));
        h = Hashing.Combine(h, unchecked((ulong)z));
        return Hashing.ToUnitDouble(h) * 2.0 - 1.0; // [-1, 1]
    }

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Value noise in [-1, 1] at a continuous 3D point.</summary>
    public double Value(double x, double y, double z)
    {
        long xi = (long)Math.Floor(x), yi = (long)Math.Floor(y), zi = (long)Math.Floor(z);
        double xf = x - xi, yf = y - yi, zf = z - zi;
        double u = Smooth(xf), v = Smooth(yf), w = Smooth(zf);

        double c000 = Lattice(xi, yi, zi), c100 = Lattice(xi + 1, yi, zi);
        double c010 = Lattice(xi, yi + 1, zi), c110 = Lattice(xi + 1, yi + 1, zi);
        double c001 = Lattice(xi, yi, zi + 1), c101 = Lattice(xi + 1, yi, zi + 1);
        double c011 = Lattice(xi, yi + 1, zi + 1), c111 = Lattice(xi + 1, yi + 1, zi + 1);

        double x00 = Lerp(c000, c100, u), x10 = Lerp(c010, c110, u);
        double x01 = Lerp(c001, c101, u), x11 = Lerp(c011, c111, u);
        double y0 = Lerp(x00, x10, v), y1 = Lerp(x01, x11, v);
        return Lerp(y0, y1, w);
    }

    /// <summary>Sum of octaves; returns roughly [-1, 1].</summary>
    public double Fbm(Vector3D<double> p, int octaves, double frequency, double lacunarity, double gain)
        => Fbm(p, (double)octaves, frequency, lacunarity, gain);

    /// <summary>
    /// fBm with a <b>fractional</b> octave count. The whole octaves sum as usual; the leftover
    /// fraction fades the next (highest) octave in by its weight, so the field is a <i>continuous</i>
    /// function of <paramref name="octaves"/> — adding detail as the count rises never snaps the
    /// surface. This is what makes the terrain LOD detail appear smoothly instead of popping when a
    /// patch subdivides. Returns roughly [-1, 1].
    /// </summary>
    public double Fbm(Vector3D<double> p, double octaves, double frequency, double lacunarity, double gain)
    {
        if (octaves <= 0) return 0;
        int full = (int)Math.Floor(octaves);
        double frac = octaves - full;

        double sum = 0, amp = 1, f = frequency, norm = 0;
        for (int i = 0; i < full; i++)
        {
            sum += amp * Value(p.X * f, p.Y * f, p.Z * f);
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }
        if (frac > 0.0)
        {
            sum += amp * frac * Value(p.X * f, p.Y * f, p.Z * f); // last octave faded by its fraction
            norm += amp * frac;
        }
        return norm > 0 ? sum / norm : 0;
    }

    /// <summary>
    /// Ridged multifractal noise in [0, 1]. Folding the field at its zero crossings
    /// (1 - |value|) turns smooth fBm valleys into sharp creases; squaring sharpens them and
    /// the running <c>prev</c> weight concentrates fine detail onto existing ridges, giving
    /// mountain ranges and canyons rather than rolling fBm hills.
    /// </summary>
    public double Ridged(Vector3D<double> p, int octaves, double frequency, double lacunarity, double gain)
        => Ridged(p, (double)octaves, frequency, lacunarity, gain);

    /// <summary>
    /// Ridged multifractal with a <b>fractional</b> octave count — the highest octave fades in by
    /// its fraction (see <see cref="Fbm(Vector3D{double}, double, double, double, double)"/>), so the
    /// mountain detail grows continuously with LOD instead of snapping. Returns [0, 1].
    /// </summary>
    public double Ridged(Vector3D<double> p, double octaves, double frequency, double lacunarity, double gain)
    {
        if (octaves <= 0) return 0;
        int full = (int)Math.Floor(octaves);
        double frac = octaves - full;

        double sum = 0, amp = 0.5, f = frequency, prev = 1.0, norm = 0;
        for (int i = 0; i < full; i++)
        {
            double n = 1.0 - Math.Abs(Value(p.X * f, p.Y * f, p.Z * f)); // [0, 1], crease at zero
            n *= n;          // sharpen the ridge
            n *= prev;       // multifractal: detail rides on top of coarser ridges
            sum += n * amp;
            norm += amp;
            prev = n;
            amp *= gain;
            f *= lacunarity;
        }
        if (frac > 0.0)
        {
            double n = 1.0 - Math.Abs(Value(p.X * f, p.Y * f, p.Z * f));
            n *= n;
            n *= prev;
            sum += n * amp * frac;
            norm += amp * frac;
        }
        return norm > 0 ? Math.Clamp(sum / norm, 0.0, 1.0) : 0;
    }
}
