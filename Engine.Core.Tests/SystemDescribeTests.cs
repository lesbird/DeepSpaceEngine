using Engine.Core;
using Game.Universe;
using Xunit;

namespace Engine.Core.Tests;

public class SystemDescribeTests
{
    private static IEnumerable<Star> SampleStars(int count = 300)
    {
        var field = new StarField(new GalaxyModel(31337));
        field.Update(UniversePosition.Origin, radiusCells: 12);
        int seen = 0;
        foreach (Star s in field.Visible)
        {
            if (seen++ >= count) break;
            yield return s;
        }
    }

    /// <summary>The cheap summary must describe exactly the system that would spawn: same planets,
    /// same types/orbits/sizes, same ring flags, same moons. This is the guardrail that keeps the
    /// Describe walk in lockstep with Generate's main RNG stream.</summary>
    [Fact]
    public void Describe_MatchesSpawnedSystem()
    {
        foreach (Star s in SampleStars())
        {
            SolarSystem full = SystemGenerator.Generate(s);
            SystemInfo info = SystemGenerator.Describe(s);

            Assert.Equal(s.Id, info.StarId);
            Assert.Equal(full.Planets.Length, info.PlanetCount);

            int gas = 0, ice = 0, moons = 0, ringed = 0;
            for (int i = 0; i < full.Planets.Length; i++)
            {
                Planet p = full.Planets[i];
                PlanetInfo pi = info.Planets[i];

                Assert.Equal(p.Seed, pi.Id);
                Assert.Equal(i, pi.Index);
                Assert.Equal(p.Designation, pi.Designation);
                Assert.Equal(p.Type, pi.Type);
                Assert.Equal(p.SemiMajorAxis, pi.SemiMajorAxis);
                Assert.Equal(p.RadiusMeters, pi.RadiusMeters);
                Assert.Equal(p.MassKg, pi.MassKg);
                Assert.Equal(p.MeanMotion, pi.MeanMotion);
                Assert.Equal(p.AxialTilt, pi.AxialTilt);
                Assert.Equal(p.HasRings, pi.HasRings);
                Assert.Equal(p.SurfaceTempK, pi.SurfaceTempK);
                Assert.Equal(p.HasAtmosphere, pi.HasAtmosphere);
                Assert.Equal(p.SurfacePressureBar, pi.SurfacePressureBar);
                Assert.Equal(p.Moons.Length, pi.MoonCount);

                for (int j = 0; j < p.Moons.Length; j++)
                {
                    Moon m = p.Moons[j];
                    MoonInfo mi = pi.Moons[j];
                    Assert.Equal(m.Seed, mi.Id);
                    Assert.Equal(m.Type, mi.Type);
                    Assert.Equal(m.SemiMajorAxis, mi.SemiMajorAxis);
                    Assert.Equal(m.RadiusMeters, mi.RadiusMeters);
                    Assert.Equal(m.SurfaceTempK, mi.SurfaceTempK);
                    Assert.Equal(m.HasAtmosphere, mi.HasAtmosphere);
                }

                if (p.Type == PlanetType.GasGiant) gas++;
                else if (p.Type == PlanetType.IceGiant) ice++;
                moons += p.Moons.Length;
                if (p.HasRings) ringed++;
            }

            Assert.Equal(gas, info.GasGiantCount);
            Assert.Equal(ice, info.IceGiantCount);
            Assert.Equal(gas + ice, info.GiantCount);
            Assert.Equal(moons, info.MoonCount);
            Assert.Equal(ringed, info.RingedCount);
        }
    }

    [Fact]
    public void Describe_IsDeterministic()
    {
        Star s = SampleStars(1).First();
        SystemInfo a = SystemGenerator.Describe(s);
        SystemInfo b = SystemGenerator.Describe(s);

        Assert.Equal(a.PlanetCount, b.PlanetCount);
        for (int i = 0; i < a.PlanetCount; i++)
        {
            Assert.Equal(a.Planets[i].Id, b.Planets[i].Id);
            Assert.Equal(a.Planets[i].Type, b.Planets[i].Type);
            Assert.Equal(a.Planets[i].RadiusMeters, b.Planets[i].RadiusMeters);
        }
    }

    [Fact]
    public void TryGetPlanet_ResolvesById()
    {
        foreach (Star s in SampleStars(50))
        {
            SystemInfo info = SystemGenerator.Describe(s);
            if (info.PlanetCount == 0) continue;

            PlanetInfo want = info.Planets[info.PlanetCount / 2];
            Assert.True(SystemGenerator.TryGetPlanet(info, want.Id, out PlanetInfo got));
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.Designation, got.Designation);

            Assert.False(SystemGenerator.TryGetPlanet(info, 0xDEADBEEFDEADBEEFUL, out _));
        }
    }
}
