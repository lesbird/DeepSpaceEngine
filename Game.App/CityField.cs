using System;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>
/// CPU mirror of the <see cref="CityRenderer"/> vertex shader's city-placement field, used to point a
/// nav reticle at the nearest city when you're on the surface. The noise (<c>hash13</c>/<c>vnoise3</c>/
/// <c>fbm3</c>) and the <c>region × sparkle × tempOk × lowOk</c> gates are ported verbatim from that
/// shader (and from the terrain shader's orbital glow), so "where the reticle points" is exactly "where
/// buildings spawn". Evaluated in <see cref="float"/> to match the GPU's precision.
///
/// The one input this can't get from a direction alone is elevation for the coastal-lowland gate, so the
/// search takes an <c>elevN01</c> delegate (height ÷ amplitude) — supplied by the caller from
/// <see cref="Game.Universe.PlanetTerrain.GpuHeightAt"/>, the CPU mirror of the same generator the tiles use.
/// </summary>
public static class CityField
{
    private static float Frac(float x) => x - MathF.Floor(x);
    private static float Mod(float x, float y) => x - y * MathF.Floor(x / y);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Small-input integer hash (Dave Hoskins), wrapped to 4096 like the shader so it stays precise.
    private static float Hash13(float x, float y, float z)
    {
        x = Mod(x, 4096f); y = Mod(y, 4096f); z = Mod(z, 4096f);
        x = Frac(x * 0.1031f); y = Frac(y * 0.1031f); z = Frac(z * 0.1031f);
        float d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f); // dot(p, p.yzx + 33.33)
        x += d; y += d; z += d;
        return Frac((x + y) * z);
    }

    private static float Vnoise3(float px, float py, float pz)
    {
        float cx = MathF.Floor(px), cy = MathF.Floor(py), cz = MathF.Floor(pz);
        float fx = px - cx, fy = py - cy, fz = pz - cz;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
        float n000 = Hash13(cx, cy, cz),         n100 = Hash13(cx + 1, cy, cz);
        float n010 = Hash13(cx, cy + 1, cz),     n110 = Hash13(cx + 1, cy + 1, cz);
        float n001 = Hash13(cx, cy, cz + 1),     n101 = Hash13(cx + 1, cy, cz + 1);
        float n011 = Hash13(cx, cy + 1, cz + 1), n111 = Hash13(cx + 1, cy + 1, cz + 1);
        float x00 = Lerp(n000, n100, fx), x10 = Lerp(n010, n110, fx);
        float x01 = Lerp(n001, n101, fx), x11 = Lerp(n011, n111, fx);
        return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }

    private static float Fbm3(float px, float py, float pz, float freq)
    {
        float s = 0f, a = 1f, f = freq, n = 0f;
        for (int i = 0; i < 4; i++) { s += a * (Vnoise3(px * f, py * f, pz * f) * 2f - 1f); n += a; a *= 0.5f; f *= 2f; }
        return s / n;
    }

    /// <summary>Direction-only part of the city field: <c>region × sparkle × tempOk</c>. This is an upper
    /// bound on the full field (the elevation gate only reduces it), so it's the cheap first filter.</summary>
    public static float DirValue(Vector3D<double> up, float cityFreq)
    {
        float x = (float)up.X, y = (float)up.Y, z = (float)up.Z;
        float region  = Smooth(0.5f, 0.85f, 0.5f + 0.5f * Fbm3(x, y, z, cityFreq * 0.25f));
        float sparkle = Smooth(0.55f, 0.95f, 0.5f + 0.5f * Fbm3(x + 11f, y + 4f, z + 7f, cityFreq * 1.6f));
        float tempOk  = 1f - Smooth(0.6f, 0.95f, MathF.Abs(y));
        return region * sparkle * tempOk;
    }

    /// <summary>Coastal-lowland gate from normalised elevation (height ÷ amplitude) — the same curve the
    /// shader applies: rises just above the base radius, falls off above ~0.4 of the amplitude.</summary>
    public static float LowOk(double elevN)
    {
        float e = (float)elevN;
        return Smooth(-0.02f, 0.05f, e) * (1f - Smooth(0.15f, 0.40f, e));
    }

    /// <summary>Search outward from <paramref name="camDir"/> (planet-local nadir) for the nearest surface
    /// direction that a building would occupy: <c>DirValue × LowOk &gt; threshold</c>. Rings expand by great-
    /// circle angle so the first ring with a hit is the nearest city; within a ring the strongest cell wins.
    /// The (cheap) direction field filters candidates before the (costlier) <paramref name="elevN01"/> sample.
    /// </summary>
    public static bool TryFindNearest(Vector3D<double> camDir, float cityFreq, float threshold,
        double maxAngle, Func<Vector3D<double>, double> elevN01,
        out Vector3D<double> cityDir, out double angle)
    {
        Vector3D<double> c = Vector3D.Normalize(camDir);
        cityDir = c; angle = 0.0;

        Vector3D<double> refv = Math.Abs(c.Y) < 0.99 ? new Vector3D<double>(0, 1, 0) : new Vector3D<double>(1, 0, 0);
        Vector3D<double> t1 = Vector3D.Normalize(Vector3D.Cross(refv, c));
        Vector3D<double> t2 = Vector3D.Cross(c, t1);

        // Ring/angular step scaled to the settlement size (~1/sparkleFreq), clamped so cost stays bounded.
        double step = Math.Clamp(0.7 / Math.Max(1e-3, cityFreq * 1.6), 0.006, 0.03);
        maxAngle = Math.Max(maxAngle, step * 4.0);

        for (double r = 0.0; r <= maxAngle; r += step)
        {
            int nSamples = r < 1e-6 ? 1 : (int)Math.Clamp(Math.Ceiling(2.0 * Math.PI * r / step), 8, 160);
            double best = threshold;
            Vector3D<double> bestDir = default;
            bool hit = false;
            double cr = Math.Cos(r), sr = Math.Sin(r);
            for (int k = 0; k < nSamples; k++)
            {
                double th = 2.0 * Math.PI * k / nSamples;
                Vector3D<double> d = r < 1e-6 ? c
                    : Vector3D.Normalize(c * cr + (t1 * Math.Cos(th) + t2 * Math.Sin(th)) * sr);
                float dv = DirValue(d, cityFreq);
                if (dv <= threshold) continue;                 // dv upper-bounds the full field
                float city = dv * LowOk(elevN01(d));
                if (city > best) { best = city; bestDir = d; hit = true; }
            }
            if (hit) { cityDir = bestDir; angle = r; return true; }
        }
        return false;
    }
}
