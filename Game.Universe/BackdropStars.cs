using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A fixed, deterministic catalog of <b>very distant</b> background stars used as a galactic
/// backdrop. Unlike the streamed <see cref="StarField"/>, these have no position — only a
/// <b>direction</b> on the celestial sphere — so they never parallax as the camera flies and
/// read as the unreachable far field beyond the ~tens-of-ly render bubble.
///
/// Directions are concentrated toward the galactic plane (the world XZ plane, matching
/// <see cref="GalaxyModel"/>: disk in XZ, Y vertical) so the dome reads as a galaxy band rather
/// than a uniform sky, with a mild two-arm longitude modulation and brightness that fades away
/// from the plane. Colours come from <see cref="Blackbody"/>, slightly desaturated toward white
/// to read as unresolved distant light.
///
/// Pure and GL-free so it is unit-testable; <see cref="GalaxyBackdrop"/> uploads
/// <see cref="Interleaved"/> straight into a static vertex buffer.
/// </summary>
public sealed class BackdropStars
{
    /// <summary>Floats per star in <see cref="Interleaved"/>: direction(3) + colour(3) + size cue(1).</summary>
    public const int FloatsPerStar = 7;

    /// <summary>Number of background stars.</summary>
    public int Count { get; }

    /// <summary>Interleaved vertex data, <see cref="Count"/> × <see cref="FloatsPerStar"/> floats.</summary>
    public float[] Interleaved { get; }

    public BackdropStars(ulong worldSeed, int count = 9000)
    {
        Count = count;
        Interleaved = new float[count * FloatsPerStar];
        var rng = new DeterministicRng(Hashing.Combine(worldSeed, 0xBACCD12EUL));

        for (int i = 0; i < count; i++)
        {
            // Latitude concentrated near the plane: y = sin(galactic latitude). Cubing a uniform
            // [-1,1] piles samples up near 0, so most stars hug the band, a few stray to the poles.
            double u = rng.Range(-1.0, 1.0);
            double y = u * u * u;
            double cosB = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            double phi = rng.Range(0.0, 2.0 * Math.PI);
            var dir = new Vector3D<float>(
                (float)(cosB * Math.Cos(phi)),
                (float)y,
                (float)(cosB * Math.Sin(phi)));

            // Brightness: a steep power law (few bright, many faint), boosted toward the plane and
            // gently modulated into two spiral-arm longitudes so the band isn't flat.
            double mag = Math.Pow(rng.NextDouble(), 6.0);
            double planeBoost = 0.45 + 0.55 * (1.0 - Math.Abs(y));
            double arm = 0.78 + 0.22 * Math.Cos(2.0 * phi);
            float size = (float)((0.55 + 2.6 * mag) * planeBoost * arm);

            // Temperature distribution: mostly cool/Sun-like, a minority hot blue-white.
            double tu = rng.NextDouble();
            float temp = tu < 0.70f ? (float)rng.Range(3000, 5200)
                       : tu < 0.93f ? (float)rng.Range(5200, 7500)
                                    : (float)rng.Range(7500, 18000);
            Vector3D<float> color = Desaturate(Blackbody.ColorOf(temp), 0.25f);

            int o = i * FloatsPerStar;
            Interleaved[o + 0] = dir.X;
            Interleaved[o + 1] = dir.Y;
            Interleaved[o + 2] = dir.Z;
            Interleaved[o + 3] = color.X;
            Interleaved[o + 4] = color.Y;
            Interleaved[o + 5] = color.Z;
            Interleaved[o + 6] = size;
        }
    }

    /// <summary>Unit direction of star <paramref name="i"/> (for tests).</summary>
    public Vector3D<float> Direction(int i) =>
        new(Interleaved[i * FloatsPerStar], Interleaved[i * FloatsPerStar + 1], Interleaved[i * FloatsPerStar + 2]);

    /// <summary>Mix a colour toward white by <paramref name="t"/> so unresolved light looks paler.</summary>
    private static Vector3D<float> Desaturate(Vector3D<float> c, float t) => new(
        c.X + (1f - c.X) * t, c.Y + (1f - c.Y) * t, c.Z + (1f - c.Z) * t);
}
