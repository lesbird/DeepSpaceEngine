using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>One soft volumetric "puff" of a galaxy's luminous body — a large nebula-style fBm cloud.
/// A galaxy is rendered up close as an overlapping cluster of these (a bright warm central bulge plus
/// cooler disk puffs laid along the spiral), so it reads as billowing glowing gas you fly through
/// rather than a flat disk or a field of discrete points.</summary>
public readonly struct GalaxyGlowPuff
{
    public readonly Vector3D<float> Offset;   // from the galaxy centre, metres
    public readonly float RadiusMeters;
    public readonly Vector3D<float> Color;
    public readonly float Brightness;
    public readonly float Seed;
    public readonly float Core;               // 0 = wispy disk cloud, →1 = smooth concentrated bulge

    public GalaxyGlowPuff(Vector3D<float> offset, float radiusMeters, Vector3D<float> color,
        float brightness, float seed, float core)
    {
        Offset = offset; RadiusMeters = radiusMeters; Color = color;
        Brightness = brightness; Seed = seed; Core = core;
    }
}

/// <summary>
/// Builds the volumetric-glow representation of a galaxy: the same soft fBm-cloud technique used for
/// emission <see cref="NebulaField">nebulae</see>, scaled up to paint the whole galaxy body. The puffs
/// follow the galaxy's density model (a spheroidal bulge + an exponential, arm-biased disk), so the glow
/// concentrates in the core and traces the arms. Deterministic from the galaxy seed; a few dozen puffs.
/// </summary>
public static class GalaxyGlow
{
    public static GalaxyGlowPuff[] ForGalaxy(Galaxy g)
    {
        GalaxyModel m = GalaxyModel.For(g);
        Basis(g.DiskNormal, out Vector3D<double> u, out Vector3D<double> v, out Vector3D<double> n);

        double ly = MathUtil.LightYear;
        double rMax = g.RadiusLy * ly;
        double scaleLen = Math.Max(m.ScaleLength, 1.0) * ly;
        double truncR = 1.0 - Math.Exp(-rMax / scaleLen);
        bool spiral = m.ArmCount > 0 && m.ArmStrength > 0;
        double armPeak = 1.0 + m.ArmStrength;

        var rng = new DeterministicRng(Hashing.Combine(g.Seed, 0x6107UL)); // "GLOW"

        int diskCount = g.Type switch
        {
            GalaxyType.Elliptical => 10,   // smooth spheroid, fewer disk wisps
            GalaxyType.Dwarf => 8,
            _ => 18,
        };
        var puffs = new GalaxyGlowPuff[1 + diskCount];

        // Warm central bulge: a single big concentrated puff at the centre.
        Vector3D<float> warm = Blend(g.Color, new Vector3D<float>(1.0f, 0.86f, 0.62f), 0.6f);
        puffs[0] = new GalaxyGlowPuff(
            offset: new Vector3D<float>(0, 0, 0),
            radiusMeters: (float)(0.40 * rMax),
            color: warm,
            brightness: 1.5f,
            seed: rng.NextFloat() * 40f,
            core: 0.7f);

        // Cooler disk puffs along the exponential, arm-biased disk; bigger and brighter toward the core.
        for (int i = 0; i < diskCount; i++)
        {
            double rr = -scaleLen * Math.Log(1.0 - rng.NextDouble() * truncR);
            double phi = SampleAngle(ref rng, m, rr / ly, spiral, armPeak);
            double hh = (rng.NextDouble() - 0.5) * 0.08 * rMax;
            Vector3D<double> off = u * (rr * Math.Cos(phi)) + v * (rr * Math.Sin(phi)) + n * hh;

            double frac = Math.Clamp(rr / rMax, 0.0, 1.0);
            double radius = (0.34 - 0.18 * frac) * rMax;     // ~0.34R near centre → ~0.16R at the rim
            float jitter = 0.75f + 0.5f * rng.NextFloat();
            Vector3D<float> col = Blend(g.Color, warm, 0.15f * (float)(1.0 - frac)); // a touch warmer inward

            puffs[1 + i] = new GalaxyGlowPuff(
                offset: new Vector3D<float>((float)off.X, (float)off.Y, (float)off.Z),
                radiusMeters: (float)radius,
                color: col,
                brightness: (float)(0.55 + 0.55 * (1.0 - frac)) * jitter,
                seed: rng.NextFloat() * 40f,
                core: 0.0f);
        }
        return puffs;
    }

    private static Vector3D<float> Blend(Vector3D<float> a, Vector3D<float> b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>Sample a disk angle, biased toward the spiral-arm crests (uniform if armless).</summary>
    private static double SampleAngle(ref DeterministicRng rng, GalaxyModel m, double rLy, bool spiral, double armPeak)
    {
        if (!spiral) return rng.Range(0, 2 * Math.PI);
        for (int t = 0; t < 8; t++)
        {
            double phi = rng.Range(0, 2 * Math.PI);
            double arm = 1.0 + m.ArmStrength * Math.Cos(m.ArmCount * phi - m.ArmWind * Math.Log(rLy + 1.0));
            if (rng.NextDouble() * armPeak <= arm) return phi;
        }
        return rng.Range(0, 2 * Math.PI);
    }

    private static void Basis(Vector3D<float> normal, out Vector3D<double> u, out Vector3D<double> v, out Vector3D<double> n)
    {
        var nn = new Vector3D<double>(normal.X, normal.Y, normal.Z);
        nn = nn.LengthSquared < 1e-12 ? new Vector3D<double>(0, 1, 0) : Vector3D.Normalize(nn);
        Vector3D<double> a = Math.Abs(nn.Y) < 0.99 ? new Vector3D<double>(0, 1, 0) : new Vector3D<double>(1, 0, 0);
        u = Vector3D.Normalize(Vector3D.Cross(a, nn));
        v = Vector3D.Cross(nn, u);
        n = nn;
    }
}
