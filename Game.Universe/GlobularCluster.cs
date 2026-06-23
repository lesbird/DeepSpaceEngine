using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A globular cluster: a small, dense, roughly spherical knot of old stars (~40–200 ly across) orbiting
/// in a galaxy's halo. From far it reads as a single fuzzy star; up close it resolves into its component
/// stars. Generated deterministically from the host galaxy's seed (see <see cref="GlobularClusters"/>).
/// </summary>
public readonly struct GlobularCluster
{
    public readonly ulong Id;
    public readonly UniversePosition Center;
    public readonly double RadiusMeters;
    public readonly int StarCount;            // representative render-star count (not the true population)
    public readonly ulong Seed;
    public readonly Vector3D<float> Color;    // integrated tint (old, metal-poor → warm)

    public GlobularCluster(ulong id, UniversePosition center, double radiusMeters, int starCount,
        ulong seed, Vector3D<float> color)
    {
        Id = id;
        Center = center;
        RadiusMeters = radiusMeters;
        StarCount = starCount;
        Seed = seed;
        Color = color;
    }

    public double RadiusLy => RadiusMeters / MathUtil.LightYear;
}

/// <summary>Deterministic generation of a galaxy's globular clusters and their resolved star clouds.</summary>
public static class GlobularClusters
{
    public const int FloatsPerStar = 7; // offset(3) + colour(3) + size(1)

    /// <summary>The clusters orbiting <paramref name="galaxy"/>: scattered through a spheroidal halo,
    /// biased toward the outer galaxy, with varied sizes. Deterministic from the galaxy seed.</summary>
    public static GlobularCluster[] ForGalaxy(Galaxy galaxy)
    {
        var rng = new DeterministicRng(Hashing.Combine(galaxy.Seed, 0x6106C1A5UL)); // "GLOBCLUS"
        double ly = MathUtil.LightYear;
        double rGal = galaxy.RadiusMeters;

        // Count scales with galaxy size (~200 for a Milky-Way-class), clamped.
        int count = (int)Math.Clamp(200 * (galaxy.RadiusLy / 50000.0), 24, 500);
        var clusters = new GlobularCluster[count];
        for (int i = 0; i < count; i++)
        {
            // Halo placement: isotropic direction (roughly spherical halo), radius biased outward so most
            // sit in the outer galaxy / halo rather than the bright disk.
            double z = rng.Range(-1, 1), ph = rng.Range(0, 2 * Math.PI);
            double rp = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            var dir = new Vector3D<double>(rp * Math.Cos(ph), z, rp * Math.Sin(ph));
            // ~0.3–0.95 R, outward-weighted. Kept inside the galaxy sphere so the star-lattice blocks at a
            // cluster resolve to this galaxy (and thus generate the cluster's injected real stars).
            double frac = 0.3 + 0.65 * Math.Pow(rng.NextDouble(), 0.55);
            UniversePosition center = galaxy.Center.Translated(dir * (frac * rGal));

            double diamLy = rng.Range(40.0, 200.0); // ~100 ly typical
            double radius = diamLy * 0.5 * ly;
            int stars = (int)rng.Range(1500, 5000);

            // Old, metal-poor populations → warm yellow-white, with some variation.
            float warm = rng.NextFloat();
            var color = new Vector3D<float>(1.0f, 0.92f - 0.10f * warm, 0.78f - 0.18f * warm);

            ulong id = Hashing.Combine(galaxy.Seed, (ulong)i);
            clusters[i] = new GlobularCluster(id, center, radius, stars, Hashing.Combine(id, 0x57A35UL), color);
        }
        return clusters;
    }

    /// <summary>Per-star RNG for cluster star <paramref name="index"/>. Both the decorative cloud and the
    /// injected catalog stars (see <c>StarCatalog</c>) seed from this and draw the position FIRST, so star
    /// i lands at the same place in both — the decorative cloud and the real stars are the same stars, and
    /// the LOD hand-off is seamless.</summary>
    public static DeterministicRng StarRng(in GlobularCluster c, int index)
        => new(Hashing.Combine(c.Seed, (ulong)index));

    /// <summary>Offset (m) of one cluster star from the centre — centrally concentrated (R·u^1.7),
    /// isotropic. Drawn from <paramref name="rng"/> first, before any star-type draws.</summary>
    public static Vector3D<double> StarOffset(ref DeterministicRng rng, double radiusMeters)
    {
        double rr = radiusMeters * Math.Pow(rng.NextDouble(), 1.7);
        double z = rng.Range(-1, 1), ph = rng.Range(0, 2 * Math.PI);
        double rp = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
        return new Vector3D<double>(rp * Math.Cos(ph), z, rp * Math.Sin(ph)) * rr;
    }

    /// <summary>Resolved star cloud for one cluster: interleaved offset(3)+colour(3)+size(1) per star.
    /// Each star's position, type and colour match the real catalog star injected for it, so as the
    /// cloud fades the real stars sit exactly where the cloud's were.</summary>
    public static float[] Stars(GlobularCluster c)
    {
        var data = new float[c.StarCount * FloatsPerStar];
        for (int i = 0; i < c.StarCount; i++)
        {
            DeterministicRng rng = StarRng(c, i);
            Vector3D<double> off = StarOffset(ref rng, c.RadiusMeters);
            StarField.SampleStarType(ref rng, out float temp, out float lum, out _, out _, out _);

            Vector3D<float> col = Blackbody.ColorOf(temp);            // matches the real star's colour
            float size = Math.Clamp(1.1f + 1.5f * MathF.Sqrt(MathF.Min(lum, 30f)), 1.2f, 6f);

            int o = i * FloatsPerStar;
            data[o + 0] = (float)off.X; data[o + 1] = (float)off.Y; data[o + 2] = (float)off.Z;
            data[o + 3] = col.X; data[o + 4] = col.Y; data[o + 5] = col.Z;
            data[o + 6] = size;
        }
        return data;
    }
}
