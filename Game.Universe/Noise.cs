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

    private static double SmoothDeriv(double t) => 6.0 * t * (1.0 - t); // d/dt of Smooth

    /// <summary>Value noise plus its analytic gradient (∂/∂x, ∂/∂y, ∂/∂z). Value in [-1, 1].
    /// Used by <see cref="ErodedFbm"/> to suppress detail on slopes (a cheap erosion model).</summary>
    public (double value, Vector3D<double> grad) ValueD(double x, double y, double z)
    {
        long xi = (long)Math.Floor(x), yi = (long)Math.Floor(y), zi = (long)Math.Floor(z);
        double xf = x - xi, yf = y - yi, zf = z - zi;
        double u = Smooth(xf), v = Smooth(yf), w = Smooth(zf);
        double du = SmoothDeriv(xf), dv = SmoothDeriv(yf), dw = SmoothDeriv(zf);

        double c000 = Lattice(xi, yi, zi), c100 = Lattice(xi + 1, yi, zi);
        double c010 = Lattice(xi, yi + 1, zi), c110 = Lattice(xi + 1, yi + 1, zi);
        double c001 = Lattice(xi, yi, zi + 1), c101 = Lattice(xi + 1, yi, zi + 1);
        double c011 = Lattice(xi, yi + 1, zi + 1), c111 = Lattice(xi + 1, yi + 1, zi + 1);

        double x00 = Lerp(c000, c100, u), x10 = Lerp(c010, c110, u);
        double x01 = Lerp(c001, c101, u), x11 = Lerp(c011, c111, u);
        double y0 = Lerp(x00, x10, v), y1 = Lerp(x01, x11, v);
        double val = Lerp(y0, y1, w);

        double dfu = Lerp(Lerp(c100 - c000, c110 - c010, v), Lerp(c101 - c001, c111 - c011, v), w);
        double dfv = Lerp(x10 - x00, x11 - x01, w);
        double dfw = y1 - y0;
        return (val, new Vector3D<double>(dfu * du, dfv * dv, dfw * dw));
    }

    /// <summary>
    /// Erosive fBm: ordinary fBm, but each octave is damped by the squared magnitude of the
    /// accumulated gradient so far — so fine detail is suppressed on already-steep slopes. The
    /// result reads as eroded terrain: smooth, carved valley floors with detail concentrated on
    /// flatter shoulders and ridgelines. Fractional octave count fades the top octave in smoothly
    /// (LOD-continuous, like <see cref="Fbm(Vector3D{double}, double, double, double, double)"/>),
    /// and the output stays normalised to [-1, 1] (each octave's damping ≤ 1), so callers' amplitude
    /// bounds are preserved.
    /// </summary>
    public double ErodedFbm(Vector3D<double> p, double octaves, double frequency, double lacunarity, double gain)
    {
        if (octaves <= 0) return 0;
        const double k = 1.4; // erosion strength: higher = flatter valleys, sharper ridges
        int full = (int)Math.Floor(octaves);
        double frac = octaves - full;

        double sum = 0, amp = 1, f = frequency, norm = 0;
        Vector3D<double> gradSum = default;
        for (int i = 0; i < full; i++)
        {
            (double n, Vector3D<double> g) = ValueD(p.X * f, p.Y * f, p.Z * f);
            gradSum += g;
            double damp = 1.0 / (1.0 + k * Vector3D.Dot(gradSum, gradSum));
            sum += amp * n * damp;
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }
        if (frac > 0.0)
        {
            (double n, Vector3D<double> g) = ValueD(p.X * f, p.Y * f, p.Z * f);
            gradSum += g * frac;
            double damp = 1.0 / (1.0 + k * Vector3D.Dot(gradSum, gradSum));
            sum += amp * frac * n * damp;
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

    /// <summary>
    /// One jittered cellular feature: the feature point inside lattice cell (cx,cy,cz) plus a stable
    /// [0,1) random for that cell. Used to scatter impact craters — the feature point is the crater
    /// centre and the random drives its size/existence. Callers iterate the 3×3×3 neighbourhood and
    /// accumulate each crater's smooth footprint, so the field stays continuous across cell borders
    /// (a plain nearest-feature lookup jumps there when the winning crater's radius changes).
    /// </summary>
    public (double fx, double fy, double fz, double rand) CellFeature(long cx, long cy, long cz, ulong salt)
    {
        ulong h = Hashing.Combine(_seed, salt);
        h = Hashing.Combine(h, unchecked((ulong)cx));
        h = Hashing.Combine(h, unchecked((ulong)cy));
        h = Hashing.Combine(h, unchecked((ulong)cz));
        double fx = cx + Hashing.ToUnitDouble(h);
        double fy = cy + Hashing.ToUnitDouble(Hashing.Combine(h, 0x9E3779B9u));
        double fz = cz + Hashing.ToUnitDouble(Hashing.Combine(h, 0x85EBCA6Bu));
        double rand = Hashing.ToUnitDouble(Hashing.Combine(h, 0xC2B2AE35u));
        return (fx, fy, fz, rand);
    }
}
