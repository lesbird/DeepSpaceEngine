using Engine.Core;
using Game.Universe;
using Xunit;

namespace Engine.Core.Tests;

public class RingTests
{
    /// <summary>Generate systems across many stars and yield every planet that carries rings.</summary>
    private static IEnumerable<Planet> RingedPlanets(int starCount = 400)
    {
        var field = new StarField(new GalaxyModel(31337));
        field.Update(UniversePosition.Origin, radiusCells: 12);
        int seen = 0;
        foreach (Star s in field.Visible)
        {
            if (seen++ >= starCount) break;
            foreach (Planet p in SystemGenerator.Generate(s).Planets)
                if (p.HasRings) yield return p;
        }
    }

    [Fact]
    public void Rings_HaveValidGeometry()
    {
        int count = 0;
        foreach (Planet p in RingedPlanets())
        {
            count++;
            Assert.True(p.RingInnerRadius > p.RadiusMeters, "inner ring clears the surface");
            Assert.True(p.RingOuterRadius > p.RingInnerRadius, "outer ring is beyond the inner");
            Assert.InRange(p.RingOpacity, 0.0f, 1.0f);
            Assert.InRange(p.RingColor.X, 0f, 1f);
            Assert.InRange(p.RingColor.Y, 0f, 1f);
            Assert.InRange(p.RingColor.Z, 0f, 1f);
        }
        Assert.True(count > 0, "expected at least one ringed planet in the sample");
    }

    [Fact]
    public void OnlyGiants_HaveRings()
    {
        foreach (Planet p in RingedPlanets())
            Assert.True(p.Type is PlanetType.GasGiant or PlanetType.IceGiant);
    }

    [Fact]
    public void Rings_AreDeterministic()
    {
        var field = new StarField(new GalaxyModel(31337));
        field.Update(UniversePosition.Origin, radiusCells: 8);
        Star s = field.Nearest;

        SolarSystem a = SystemGenerator.Generate(s);
        SolarSystem b = SystemGenerator.Generate(s);

        for (int i = 0; i < a.Planets.Length; i++)
        {
            Planet pa = a.Planets[i], pb = b.Planets[i];
            Assert.Equal(pa.HasRings, pb.HasRings);
            Assert.Equal(pa.RingInnerRadius, pb.RingInnerRadius);
            Assert.Equal(pa.RingOuterRadius, pb.RingOuterRadius);
            Assert.Equal(pa.RingTilt, pb.RingTilt);
            Assert.Equal(pa.RingTiltAzimuth, pb.RingTiltAzimuth);
            Assert.Equal(pa.RingSeed, pb.RingSeed);
        }
    }
}
