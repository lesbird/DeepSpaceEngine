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
            planet.SurfaceTempK = (float)EquilibriumTempK(star.Luminosity, aAu);
            ApplyScanData(planet);
            AtmosphereModel.Derive(planet); // composition + T + g + pressure → atmosphere look
            AssignSurfaceAlbedo(planet);
            // Moons share the parent's distance from the star, so they inherit its temperature.
            planet.Moons = GenerateMoons(ref rng, planet, star.Designation, i + 1, planet.SurfaceTempK);
            planets[i] = planet;
        }

        // A main belt near the snow line (independent RNG, so it never perturbs the stream above).
        AsteroidBelt? belt = AsteroidBelt.Generate(star, snowLineAu, starMassKg);

        return new SolarSystem(star, planets, belt);
    }

    private static Moon[] GenerateMoons(ref DeterministicRng rng, Planet planet, string starDesignation, int planetIndex, double tempK)
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

        // Moons of a giant form cold and icy out past the snow line; moons of an inner world
        // are mostly bare rock. Both get procedural terrain (they're never gas/ice giants).
        bool outer = planet.Type is PlanetType.GasGiant or PlanetType.IceGiant;

        var moons = new Moon[count];
        double a = planet.RadiusMeters * rng.Range(2.5, 5.0);
        for (int j = 0; j < count; j++)
        {
            if (j > 0) a *= rng.Range(1.5, 2.2);
            double n = Math.Sqrt(MathUtil.GravitationalConstant * planet.MassKg / (a * a * a));

            double radius = planet.RadiusMeters * rng.Range(0.08, 0.28);
            PlanetType type = ChooseMoonType(ref rng, outer, tempK, radius);
            double mass = DensityFor(type) * (4.0 / 3.0 * Math.PI * radius * radius * radius);

            // An ocean moon out by a cold giant is kept liquid by tidal heating (think Europa),
            // so warm it into the habitable band rather than leaving it frozen at orbit temperature.
            double moonTemp = type == PlanetType.Ocean && tempK is < 255 or > 320 ? 288.0 : tempK;

            var moon = new Moon
            {
                Seed = Hashing.Combine(planet.Seed, (ulong)(j + 1)),
                Type = type,
                Designation = $"{starDesignation}-{planetIndex}-{j + 1}",
                SemiMajorAxis = a,
                Inclination = rng.Range(-0.08, 0.08),
                AscendingNode = rng.Range(0, 2 * Math.PI),
                Phase = rng.Range(0, 2 * Math.PI),
                MeanMotion = n,
                RadiusMeters = radius,
                MassKg = mass,
                Color = MoonColor(type),
                SurfaceTempK = (float)moonTemp,
            };
            AssignMoonAtmosphere(ref rng, moon);
            ApplyScanData(moon);
            AtmosphereModel.Derive(moon); // composition + T + g + pressure → atmosphere look
            AssignSurfaceAlbedo(moon);
            moons[j] = moon;
        }
        return moons;
    }

    /// <summary>Moons are rock/ice worlds (never gas/ice giants): icy around the cold giants, mostly
    /// bare rock around the inner planets, with the occasional tidally-heated lava world (Io) or
    /// dusty desert. A sizable moon in the temperate zone can rarely be a habitable ocean world.</summary>
    private static PlanetType ChooseMoonType(ref DeterministicRng rng, bool outer, double tempK, double radius)
    {
        // Habitable ocean moons: a sizable moon that's either in the star's temperate zone or warmed
        // by tidal heating around a giant. Rare, but every system has a chance at one.
        bool temperate = tempK is >= 255 and <= 320;
        bool oceanEligible = radius > 0.12 * MathUtil.EarthRadiusM && (temperate || outer);
        if (oceanEligible && rng.NextDouble() < 0.15)
            return PlanetType.Ocean;

        double u = rng.NextDouble();
        return outer
            ? (u < 0.55 ? PlanetType.Ice : u < 0.90 ? PlanetType.Rocky : PlanetType.Lava)
            : (u < 0.60 ? PlanetType.Rocky : u < 0.82 ? PlanetType.Desert : u < 0.94 ? PlanetType.Ice : PlanetType.Lava);
    }

    /// <summary>A muted, greyed-down version of the type's planet colour — moons read as dusty
    /// and washed-out rather than as vivid full-size worlds.</summary>
    private static Vector3D<float> MoonColor(PlanetType type)
    {
        Vector3D<float> c = ColorFor(type);
        var grey = new Vector3D<float>(0.58f, 0.58f, 0.56f);
        return c * 0.6f + grey * 0.4f;
    }

    /// <summary>Record the mean albedo the body's surface actually shows, so the distant sphere matches
    /// the terrain/baked map instead of popping colour when they load. Surfaceless gas/ice giants keep
    /// their flat seeded tint. Builds a throwaway <see cref="PlanetTerrain"/> (cheap, pure RNG reads).</summary>
    private static void AssignSurfaceAlbedo(CelestialBody b)
        => b.SurfaceAlbedo = b.HasSurface ? new PlanetTerrain(b).AverageAlbedo() : b.Color;

    /// <summary>Most moons are airless. Only the larger ones have a chance at a thin atmosphere
    /// (think Titan), at a lower pressure than a planet's. The optical look is derived from the
    /// scanned composition later by <see cref="AtmosphereModel.Derive"/>.</summary>
    private static void AssignMoonAtmosphere(ref DeterministicRng rng, Moon m)
    {
        // Habitable ocean moons always carry a breathable atmosphere.
        if (m.Type == PlanetType.Ocean)
        {
            m.HasAtmosphere = true;
            m.SurfacePressureBar = (float)rng.Range(0.7, 1.2);
            return;
        }

        double earthFrac = m.RadiusMeters / MathUtil.EarthRadiusM;
        double chance = earthFrac < 0.25 ? 0.0 : Math.Min(0.5, (earthFrac - 0.25) * 1.2);
        if (rng.NextDouble() >= chance) { m.HasAtmosphere = false; return; }

        m.HasAtmosphere = true;
        m.SurfacePressureBar = (float)rng.Range(0.05, 0.6); // thin, Titan-like
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

        // Fill the annulus with orbiting asteroid particles (chunky up close, smooth band from afar).
        p.RingRocks = PlanetRing.Generate(p);
    }

    /// <summary>Decide whether a planet has an atmosphere and its surface pressure (bar). The optical
    /// look (colour/thickness/haze) is derived later from the scanned composition by
    /// <see cref="AtmosphereModel.Derive"/> — this only sets the physical drivers.</summary>
    private static void AssignAtmosphere(ref DeterministicRng rng, Planet p)
    {
        // Bigger terrestrial worlds hold onto more atmosphere.
        float size = (float)Math.Clamp(Math.Sqrt(p.RadiusMeters / MathUtil.EarthRadiusM), 0.6, 1.6);
        switch (p.Type)
        {
            case PlanetType.Ocean:
                p.HasAtmosphere = true; p.SurfacePressureBar = (float)rng.Range(0.8, 1.4) * size; break;
            case PlanetType.Desert: // Mars-thin through Venus-thick — the variety is deliberate
                p.HasAtmosphere = true; p.SurfacePressureBar = (float)rng.Range(0.3, 3.0) * size; break;
            case PlanetType.Rocky:
                p.HasAtmosphere = rng.NextDouble() < 0.6; p.SurfacePressureBar = (float)rng.Range(0.2, 1.2) * size; break;
            case PlanetType.Ice:
                p.HasAtmosphere = rng.NextDouble() < 0.5; p.SurfacePressureBar = (float)rng.Range(0.05, 0.5) * size; break;
            case PlanetType.Lava:
                p.HasAtmosphere = rng.NextDouble() < 0.6; p.SurfacePressureBar = (float)rng.Range(1.0, 6.0) * size; break;
            case PlanetType.GasGiant:
                p.HasAtmosphere = true; p.SurfacePressureBar = (float)rng.Range(60.0, 160.0); break;
            case PlanetType.IceGiant:
                p.HasAtmosphere = true; p.SurfacePressureBar = (float)rng.Range(30.0, 80.0); break;
        }
    }

    /// <summary>Black-body equilibrium temperature (K) at <paramref name="aAu"/> AU from a star of
    /// the given luminosity (≈ 278 K at 1 AU around a Sun-like star), used for the scanner readout
    /// and to decide which worlds sit in the temperate, potentially-habitable band.</summary>
    private static double EquilibriumTempK(double luminosity, double aAu)
        => 278.3 * Math.Pow(Math.Max(luminosity, 1e-4), 0.25) / Math.Sqrt(Math.Max(aAu, 1e-3));

    /// <summary>
    /// Fill in the scanner detail (atmosphere/surface composition + habitability) from an
    /// independent, body-seeded RNG, so it never disturbs the main generation stream. Habitability
    /// is an ocean world with breathable air sitting in the liquid-water temperature band.
    /// </summary>
    private static void ApplyScanData(CelestialBody body)
    {
        var r = new DeterministicRng(Hashing.Combine(body.Seed, 0x5CA17EDu));
        body.AtmosphereComposition = body.HasAtmosphere ? Composition(AtmosphereBasis(body.Type), ref r) : Array.Empty<Constituent>();
        body.SurfaceComposition = body.HasSurface ? Composition(SurfaceBasis(body.Type), ref r) : Array.Empty<Constituent>();
        body.Habitable = body.Type == PlanetType.Ocean && body.HasAtmosphere
                         && body.SurfaceTempK is >= 255f and <= 320f;

        // Axial rotation / day-night is DISABLED for now (the sun-direction-sweep approach didn't read right).
        // RotationPeriodSeconds = 0 makes CelestialBody.ApparentSunDir return the true sun, so the terrain,
        // rover and atmosphere all use the un-rotated sun (no day/night sweep). To re-enable, seed a period:
        //   double hours = body.HasSurface ? r.Range(8.0, 48.0) : r.Range(6.0, 16.0);
        //   body.RotationPeriodSeconds = hours * 3600.0;
        //   double tilt = r.Range(0.0, 0.6) + (r.NextDouble() < 0.1 ? r.Range(0.0, 1.4) : 0.0);
        //   double az = r.Range(0.0, 2.0 * Math.PI);
        //   body.SpinAxis = Vector3D.Normalize(new Vector3D<double>(
        //       Math.Sin(tilt) * Math.Cos(az), Math.Cos(tilt), Math.Sin(tilt) * Math.Sin(az)));
        body.RotationPeriodSeconds = 0.0;
    }

    /// <summary>Jitter the per-type base weights, renormalise to sum 1, and sort most-abundant first.</summary>
    private static Constituent[] Composition((string name, double weight)[] basis, ref DeterministicRng r)
    {
        var items = new (string name, double w)[basis.Length];
        double sum = 0;
        for (int i = 0; i < basis.Length; i++)
        {
            double w = basis[i].weight * r.Range(0.7, 1.3);
            items[i] = (basis[i].name, w);
            sum += w;
        }
        Array.Sort(items, (x, y) => y.w.CompareTo(x.w));
        var result = new Constituent[items.Length];
        for (int i = 0; i < items.Length; i++)
            result[i] = new Constituent(items[i].name, (float)(items[i].w / sum));
        return result;
    }

    private static (string, double)[] AtmosphereBasis(PlanetType t) => t switch
    {
        PlanetType.Ocean => new[] { ("Nitrogen", 0.77), ("Oxygen", 0.205), ("Argon", 0.009), ("Water vapour", 0.012), ("Carbon dioxide", 0.004) },
        PlanetType.Desert => new[] { ("Carbon dioxide", 0.95), ("Nitrogen", 0.027), ("Argon", 0.016), ("Oxygen", 0.002) },
        PlanetType.Rocky => new[] { ("Carbon dioxide", 0.6), ("Nitrogen", 0.3), ("Argon", 0.06), ("Oxygen", 0.04) },
        PlanetType.Ice => new[] { ("Nitrogen", 0.90), ("Methane", 0.06), ("Hydrogen", 0.02), ("Argon", 0.02) },
        PlanetType.Lava => new[] { ("Carbon dioxide", 0.55), ("Sulphur dioxide", 0.35), ("Nitrogen", 0.08), ("Water vapour", 0.02) },
        PlanetType.GasGiant => new[] { ("Hydrogen", 0.90), ("Helium", 0.098), ("Methane", 0.002) },
        PlanetType.IceGiant => new[] { ("Hydrogen", 0.80), ("Helium", 0.15), ("Methane", 0.05) },
        _ => new[] { ("Nitrogen", 1.0) },
    };

    private static (string, double)[] SurfaceBasis(PlanetType t) => t switch
    {
        PlanetType.Ocean => new[] { ("Liquid water", 0.65), ("Silicate rock", 0.2), ("Sand", 0.1), ("Clay", 0.05) },
        PlanetType.Desert => new[] { ("Silica sand", 0.5), ("Iron oxide", 0.25), ("Basalt", 0.2), ("Gypsum", 0.05) },
        PlanetType.Rocky => new[] { ("Basalt", 0.4), ("Granite", 0.3), ("Iron", 0.2), ("Regolith", 0.1) },
        PlanetType.Ice => new[] { ("Water ice", 0.6), ("Carbon-dioxide ice", 0.2), ("Ammonia ice", 0.1), ("Silicate rock", 0.1) },
        PlanetType.Lava => new[] { ("Basalt", 0.45), ("Sulphur", 0.25), ("Obsidian", 0.2), ("Iron", 0.1) },
        _ => new[] { ("Rock", 1.0) },
    };

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
