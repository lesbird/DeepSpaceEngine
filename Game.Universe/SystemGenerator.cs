using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Turns a <see cref="Star"/> into a full <see cref="SolarSystem"/>, deterministically.
/// Planets are spaced by a randomized Titius–Bode-like progression, typed by their
/// position relative to the star's snow line, and given real Keplerian orbital periods
/// from the star's mass.
/// </summary>
public static class SystemGenerator
{
    public static SolarSystem Generate(Star star)
    {
        var rng = new DeterministicRng(Hashing.Combine(star.Id, 0x50A1u));

        double au = MathUtil.AstronomicalUnit;
        double starMassKg = star.MassSolar * MathUtil.SolarMassKg;

        // Snow line scales with the square root of luminosity (~2.7 AU for the Sun).
        double snowLineAu = 2.7 * Math.Sqrt(Math.Max(star.Luminosity, 0.01));

        int count = rng.NextDouble() < 0.12 ? 0 : Math.Clamp(rng.Poisson(3.2) + 1, 1, 9);
        var planets = new Planet[count];

        double aAu = rng.Range(0.25, 0.6);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) aAu *= rng.Range(1.4, 1.9);

            double a = aAu * au;
            double n = Math.Sqrt(MathUtil.GravitationalConstant * starMassKg / (a * a * a)); // rad/s

            PlanetType type = ChooseType(ref rng, aAu, snowLineAu);
            double radius = RadiusFor(ref rng, type);
            double mass = DensityFor(type) * (4.0 / 3.0 * Math.PI * radius * radius * radius);

            var planet = new Planet
            {
                Seed = Hashing.Combine(star.Id, (ulong)(i + 1)),
                Type = type,
                Designation = $"{star.Designation}-{i + 1}",
                SemiMajorAxis = a,
                Inclination = rng.Range(-0.05, 0.05),
                AscendingNode = rng.Range(0, 2 * Math.PI),
                Phase = rng.Range(0, 2 * Math.PI),
                MeanMotion = n,
                RadiusMeters = radius,
                MassKg = mass,
                AxialTilt = rng.Range(0, 0.5),
                Color = ColorFor(type),
                HasRings = (type is PlanetType.GasGiant or PlanetType.IceGiant) && rng.NextDouble() < 0.35,
            };
            if (planet.HasRings) AssignRings(planet);
            AssignAtmosphere(ref rng, planet);
            planet.Moons = GenerateMoons(ref rng, planet, star.Designation, i + 1);
            planets[i] = planet;
        }

        return new SolarSystem(star, planets);
    }

    private static Moon[] GenerateMoons(ref DeterministicRng rng, Planet planet, string starDesignation, int planetIndex)
    {
        double lambda = planet.Type switch
        {
            PlanetType.GasGiant => 2.6,
            PlanetType.IceGiant => 1.6,
            PlanetType.Ocean or PlanetType.Rocky or PlanetType.Desert => 0.5,
            _ => 0.2,
        };
        int count = Math.Min(rng.Poisson(lambda), 6);
        if (count == 0) return Array.Empty<Moon>();

        var moons = new Moon[count];
        double a = planet.RadiusMeters * rng.Range(2.5, 5.0);
        for (int j = 0; j < count; j++)
        {
            if (j > 0) a *= rng.Range(1.5, 2.2);
            double n = Math.Sqrt(MathUtil.GravitationalConstant * planet.MassKg / (a * a * a));
            float gray = (float)rng.Range(0.45, 0.7);

            moons[j] = new Moon
            {
                Seed = Hashing.Combine(planet.Seed, (ulong)(j + 1)),
                Designation = $"{starDesignation}-{planetIndex}-{j + 1}",
                SemiMajorAxis = a,
                Inclination = rng.Range(-0.08, 0.08),
                AscendingNode = rng.Range(0, 2 * Math.PI),
                Phase = rng.Range(0, 2 * Math.PI),
                MeanMotion = n,
                RadiusMeters = planet.RadiusMeters * rng.Range(0.08, 0.28),
                Color = new Vector3D<float>(gray, gray * 0.98f, gray * 0.95f),
            };
        }
        return moons;
    }

    /// <summary>
    /// Fill in the ring annulus from an independent, planet-seeded RNG so the main generation
    /// stream (types, atmospheres, moons) is left byte-identical. The ring plane follows the
    /// planet's axial tilt with a little jitter; ice giants get a cool blue-grey, gas giants a
    /// warm tan.
    /// </summary>
    private static void AssignRings(Planet p)
    {
        var r = new DeterministicRng(Hashing.Combine(p.Seed, 0x21A6u));

        double inner = p.RadiusMeters * r.Range(1.4, 1.9);
        double outer = inner * r.Range(1.5, 2.4);
        p.RingInnerRadius = inner;
        p.RingOuterRadius = outer;

        float shade = (float)r.Range(0.55, 0.9);
        Vector3D<float> tint = p.Type == PlanetType.IceGiant
            ? new Vector3D<float>(0.72f, 0.80f, 0.90f)
            : new Vector3D<float>(0.86f, 0.78f, 0.60f);
        p.RingColor = tint * shade;
        p.RingOpacity = (float)r.Range(0.45, 0.8);
        p.RingTilt = (float)(p.AxialTilt + r.Range(-0.12, 0.12));
        p.RingTiltAzimuth = (float)r.Range(0, 2 * Math.PI);
        p.RingSeed = r.NextULong();
    }

    private static void AssignAtmosphere(ref DeterministicRng rng, Planet p)
    {
        float jitterH = (float)rng.Range(0.9, 1.15);
        float jitterD = (float)rng.Range(0.85, 1.2);

        switch (p.Type)
        {
            case PlanetType.Ocean:
                p.HasAtmosphere = true; p.AtmosphereColor = new Vector3D<float>(0.35f, 0.55f, 1.0f);
                p.AtmosphereHeight = 0.035f; p.AtmosphereDensity = 1.3f; break;
            case PlanetType.Desert:
                p.HasAtmosphere = true; p.AtmosphereColor = new Vector3D<float>(0.85f, 0.65f, 0.45f);
                p.AtmosphereHeight = 0.030f; p.AtmosphereDensity = 1.0f; break;
            case PlanetType.Rocky:
                p.HasAtmosphere = rng.NextDouble() < 0.6; p.AtmosphereColor = new Vector3D<float>(0.55f, 0.62f, 0.78f);
                p.AtmosphereHeight = 0.025f; p.AtmosphereDensity = 0.8f; break;
            case PlanetType.Ice:
                p.HasAtmosphere = rng.NextDouble() < 0.5; p.AtmosphereColor = new Vector3D<float>(0.65f, 0.78f, 0.92f);
                p.AtmosphereHeight = 0.022f; p.AtmosphereDensity = 0.6f; break;
            case PlanetType.Lava:
                p.HasAtmosphere = rng.NextDouble() < 0.6; p.AtmosphereColor = new Vector3D<float>(0.85f, 0.30f, 0.15f);
                p.AtmosphereHeight = 0.022f; p.AtmosphereDensity = 0.9f; break;
            case PlanetType.GasGiant:
                p.HasAtmosphere = true; p.AtmosphereColor = Brighten(p.Color);
                p.AtmosphereHeight = 0.060f; p.AtmosphereDensity = 1.6f; break;
            case PlanetType.IceGiant:
                p.HasAtmosphere = true; p.AtmosphereColor = new Vector3D<float>(0.45f, 0.65f, 0.92f);
                p.AtmosphereHeight = 0.050f; p.AtmosphereDensity = 1.4f; break;
        }
        p.AtmosphereHeight *= jitterH;
        p.AtmosphereDensity *= jitterD;
    }

    private static Vector3D<float> Brighten(Vector3D<float> c) => new(
        Math.Min(1f, c.X * 1.2f + 0.1f), Math.Min(1f, c.Y * 1.2f + 0.1f), Math.Min(1f, c.Z * 1.2f + 0.1f));

    /// <summary>Bulk density (kg/m^3) used to estimate planet mass for moon orbits.</summary>
    private static double DensityFor(PlanetType type) => type switch
    {
        PlanetType.GasGiant => 1300,
        PlanetType.IceGiant => 1600,
        PlanetType.Ice => 1800,
        PlanetType.Ocean => 3500,
        _ => 5000, // rocky/lava/desert
    };

    private static PlanetType ChooseType(ref DeterministicRng rng, double aAu, double snowLineAu)
    {
        if (aAu < snowLineAu)
        {
            if (aAu < 0.15) return PlanetType.Lava;
            double u = rng.NextDouble();
            return u < 0.5 ? PlanetType.Rocky : u < 0.8 ? PlanetType.Desert : PlanetType.Ocean;
        }

        double ratio = aAu / snowLineAu;
        double v = rng.NextDouble();
        if (ratio < 2.5) return v < 0.7 ? PlanetType.GasGiant : PlanetType.IceGiant;
        return v < 0.45 ? PlanetType.IceGiant : v < 0.8 ? PlanetType.Ice : PlanetType.GasGiant;
    }

    private static double RadiusFor(ref DeterministicRng rng, PlanetType type)
    {
        double earth = MathUtil.EarthRadiusM;
        return type switch
        {
            PlanetType.Lava => rng.Range(0.4, 1.4) * earth,
            PlanetType.Rocky => rng.Range(0.3, 1.8) * earth,
            PlanetType.Desert => rng.Range(0.4, 1.6) * earth,
            PlanetType.Ocean => rng.Range(0.6, 1.9) * earth,
            PlanetType.Ice => rng.Range(0.3, 1.2) * earth,
            PlanetType.IceGiant => rng.Range(3.0, 5.0) * earth,
            PlanetType.GasGiant => rng.Range(8.0, 13.0) * earth,
            _ => earth,
        };
    }

    private static Vector3D<float> ColorFor(PlanetType type) => type switch
    {
        PlanetType.Lava => new Vector3D<float>(0.55f, 0.18f, 0.08f),
        PlanetType.Rocky => new Vector3D<float>(0.55f, 0.50f, 0.45f),
        PlanetType.Desert => new Vector3D<float>(0.80f, 0.65f, 0.40f),
        PlanetType.Ocean => new Vector3D<float>(0.20f, 0.42f, 0.70f),
        PlanetType.Ice => new Vector3D<float>(0.82f, 0.88f, 0.95f),
        PlanetType.IceGiant => new Vector3D<float>(0.40f, 0.62f, 0.85f),
        PlanetType.GasGiant => new Vector3D<float>(0.80f, 0.70f, 0.52f),
        _ => new Vector3D<float>(0.6f, 0.6f, 0.6f),
    };
}
