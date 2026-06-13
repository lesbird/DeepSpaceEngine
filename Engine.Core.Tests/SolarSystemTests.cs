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
                Assert.Equal(a.Planets[i].Moons[j].Designation, b.Planets[i].Moons[j].Designation);
                Assert.Equal(a.Planets[i].Moons[j].SemiMajorAxis, b.Planets[i].Moons[j].SemiMajorAxis);
            }
        }
    }

    [Fact]
    public void Manager_SpawnsHysteresisDespawns()
    {
        var field = new StarField(new GalaxyModel(777));
        field.Update(UniversePosition.Origin, 8);
        Star n = field.Nearest;

        var mgr = new SolarSystemManager(); // spawn 0.5 ly, despawn 0.6 ly

        // Approach to 0.3 ly → spawns.
        UniversePosition near = n.Position.Translated(new Vector3D<double>(0.3 * Ly, 0, 0));
        field.Update(near, 8);
        mgr.Update(0.0, near, field);
        Assert.True(mgr.HasActive);
        ulong activeId = mgr.ActiveStarId!.Value;
        UniversePosition activePos = mgr.Active!.Sun.Position;

        // Retreat to 0.55 ly (between thresholds) → STAYS active (hysteresis), same star.
        UniversePosition mid = activePos.Translated(new Vector3D<double>(0.55 * Ly, 0, 0));
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
