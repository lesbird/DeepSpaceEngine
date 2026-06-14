using System.Collections.Generic;
using Engine.Core;
using Game.Systems;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class SolarSystemTests
{
    private const double Ly = MathUtil.LightYear;

    private static Star SampleStar(ulong seed = 4242)
    {
        var field = new StarField(new GalaxyModel(seed));
        field.Update(UniversePosition.Origin, radiusCells: 8);
        Assert.True(field.HasNearest);
        return field.Nearest;
    }

    [Fact]
    public void SystemGenerator_IsDeterministic()
    {
        Star s = SampleStar();
        SolarSystem a = SystemGenerator.Generate(s);
        SolarSystem b = SystemGenerator.Generate(s);

        Assert.Equal(a.Planets.Length, b.Planets.Length);
        for (int i = 0; i < a.Planets.Length; i++)
        {
            Assert.Equal(a.Planets[i].Type, b.Planets[i].Type);
            Assert.Equal(a.Planets[i].SemiMajorAxis, b.Planets[i].SemiMajorAxis);
            Assert.Equal(a.Planets[i].MeanMotion, b.Planets[i].MeanMotion);
        }
    }

    [Fact]
    public void Planets_AreOrderedOutward()
    {
        SolarSystem sys = SystemGenerator.Generate(SampleStar());
        for (int i = 1; i < sys.Planets.Length; i++)
            Assert.True(sys.Planets[i].SemiMajorAxis > sys.Planets[i - 1].SemiMajorAxis);
    }

    [Fact]
    public void OrbitalOffset_StaysOnCircularOrbit()
    {
        SolarSystem sys = SystemGenerator.Generate(SampleStar());
        if (sys.Planets.Length == 0) return;

        Planet p = sys.Planets[0];
        foreach (double t in new[] { 0.0, 1.0e6, 5.0e7, 2.0e8 })
        {
            Vector3D<double> off = p.OrbitalOffset(t);
            double r = Math.Sqrt(off.X * off.X + off.Y * off.Y + off.Z * off.Z);
            Assert.Equal(p.SemiMajorAxis, r, 0); // radius constant along the orbit
        }
    }

    [Fact]
    public void Designations_FollowStarPlanetMoonHierarchy()
    {
        Star s = SampleStar(2024);
        SolarSystem sys = SystemGenerator.Generate(s);

        for (int i = 0; i < sys.Planets.Length; i++)
        {
            Planet p = sys.Planets[i];
            Assert.Equal($"{s.Designation}-{i + 1}", p.Designation);
            for (int j = 0; j < p.Moons.Length; j++)
                Assert.Equal($"{p.Designation}-{j + 1}", p.Moons[j].Designation);
        }
    }

    [Fact]
    public void Moons_AreDeterministic()
    {
        Star s = SampleStar(2024);
        SolarSystem a = SystemGenerator.Generate(s);
        SolarSystem b = SystemGenerator.Generate(s);

        for (int i = 0; i < a.Planets.Length; i++)
        {
            Assert.Equal(a.Planets[i].Moons.Length, b.Planets[i].Moons.Length);
            for (int j = 0; j < a.Planets[i].Moons.Length; j++)
            {
                Moon ma = a.Planets[i].Moons[j], mb = b.Planets[i].Moons[j];
                Assert.Equal(ma.Designation, mb.Designation);
                Assert.Equal(ma.SemiMajorAxis, mb.SemiMajorAxis);
                Assert.Equal(ma.Type, mb.Type);
                Assert.Equal(ma.MassKg, mb.MassKg);
                Assert.Equal(ma.HasAtmosphere, mb.HasAtmosphere);
            }
        }
    }

    [Fact]
    public void Moons_AreSolidWorldsWithMass()
    {
        // Sweep many systems so we see a representative spread of moons.
        bool sawMoon = false, sawAtmosphere = false, sawHabitableOcean = false;
        var types = new HashSet<PlanetType>();
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            if (!field.HasNearest) continue;

            foreach (Planet p in SystemGenerator.Generate(field.Nearest).Planets)
            foreach (Moon m in p.Moons)
            {
                sawMoon = true;
                types.Add(m.Type);
                // Moons are always landable rock/ice/ocean worlds — never gas/ice giants.
                Assert.True(m.HasSurface);
                Assert.NotEqual(PlanetType.GasGiant, m.Type);
                Assert.NotEqual(PlanetType.IceGiant, m.Type);
                Assert.True(m.MassKg > 0, "moon needs mass for surface gravity");
                if (m.HasAtmosphere)
                {
                    sawAtmosphere = true;
                    Assert.True(m.AtmosphereHeight > 0 && m.AtmosphereDensity > 0);
                }
                if (m.Habitable)
                {
                    sawHabitableOcean = true;
                    // Habitable means a breathable, ocean-blue world with liquid water.
                    Assert.Equal(PlanetType.Ocean, m.Type);
                    Assert.True(m.HasAtmosphere && m.HasLiquidWater);
                }
            }
        }

        Assert.True(sawMoon, "expected at least one moon across the sample");
        Assert.True(types.Count > 1, "expected a variety of moon surface types");
        Assert.True(sawAtmosphere, "expected at least one moon with a thin atmosphere across the sample");
        Assert.True(sawHabitableOcean, "expected at least one habitable ocean moon across the sample");
    }

    [Fact]
    public void ScanData_IsPopulatedAndConsistent()
    {
        var bodies = new List<CelestialBody>();
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            if (field.HasNearest) bodies.AddRange(SystemGenerator.Generate(field.Nearest).AllBodies());
        }
        Assert.NotEmpty(bodies);

        foreach (CelestialBody b in bodies)
        {
            Assert.True(b.SurfaceTempK > 0, "every body gets a temperature");

            // Composition is present exactly when the body has the relevant feature, and sums to ~1.
            Assert.Equal(b.HasAtmosphere, b.AtmosphereComposition.Length > 0);
            Assert.Equal(b.HasSurface, b.SurfaceComposition.Length > 0);
            AssertNormalised(b.AtmosphereComposition);
            AssertNormalised(b.SurfaceComposition);
        }
    }

    private static void AssertNormalised(Constituent[] parts)
    {
        if (parts.Length == 0) return;
        float sum = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.True(parts[i].Fraction > 0);
            if (i > 0) Assert.True(parts[i - 1].Fraction >= parts[i].Fraction, "sorted most-abundant first");
            sum += parts[i].Fraction;
        }
        Assert.True(Math.Abs(sum - 1f) < 1e-3f, $"fractions should sum to 1, got {sum}");
    }

    [Fact]
    public void Manager_SpawnsHysteresisDespawns()
    {
        var field = new StarField(new GalaxyModel(777));
        field.Update(UniversePosition.Origin, 8);
        Star n = field.Nearest;

        var mgr = new SolarSystemManager();
        double spawn = mgr.SpawnLightYears, despawn = mgr.DespawnLightYears;

        // Approach to just inside the spawn radius → spawns.
        UniversePosition near = n.Position.Translated(new Vector3D<double>(spawn * 0.5 * Ly, 0, 0));
        field.Update(near, 8);
        mgr.Update(0.0, near, field);
        Assert.True(mgr.HasActive);
        ulong activeId = mgr.ActiveStarId!.Value;
        UniversePosition activePos = mgr.Active!.Sun.Position;

        // Retreat between the thresholds → STAYS active (hysteresis), same star.
        UniversePosition mid = activePos.Translated(new Vector3D<double>((spawn + despawn) * 0.5 * Ly, 0, 0));
        field.Update(mid, 8);
        mgr.Update(0.0, mid, field);
        Assert.True(mgr.HasActive);
        Assert.Equal(activeId, mgr.ActiveStarId);

        // Retreat well past despawn (5 ly) → despawns (and no other star is that close to respawn).
        UniversePosition far = activePos.Translated(new Vector3D<double>(5.0 * Ly, 0, 0));
        field.Update(far, 8);
        mgr.Update(0.0, far, field);
        Assert.False(mgr.HasActive);
    }
}
