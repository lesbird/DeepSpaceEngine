using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class GalaxyGlowTests
{
    private const ulong Seed = 0xA11CE5EEDUL;

    private static Galaxy MilkyWay() =>
        new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(0, 0, 0)).Galaxies[0];

    [Fact]
    public void ForGalaxy_IsDeterministic()
    {
        Galaxy g = MilkyWay();
        GalaxyGlowPuff[] a = GalaxyGlow.ForGalaxy(g);
        GalaxyGlowPuff[] b = GalaxyGlow.ForGalaxy(g);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Offset, b[i].Offset);
            Assert.Equal(a[i].RadiusMeters, b[i].RadiusMeters);
            Assert.Equal(a[i].Brightness, b[i].Brightness);
        }
    }

    [Fact]
    public void FirstPuff_IsTheConcentratedCentralBulge()
    {
        GalaxyGlowPuff[] p = GalaxyGlow.ForGalaxy(MilkyWay());
        Assert.True(p.Length > 1);
        // The bulge sits at the centre and is the brightest, most concentrated puff.
        Assert.True(p[0].Offset.Length < 1.0f, "bulge should be at the galaxy centre");
        Assert.True(p[0].Core > 0.5f, "bulge should be concentrated (high Core)");
    }

    [Fact]
    public void Puffs_SitWithinAFewGalaxyRadii()
    {
        Galaxy g = MilkyWay();
        foreach (GalaxyGlowPuff p in GalaxyGlow.ForGalaxy(g))
        {
            // Length in double — the offsets are ~1e20 m, whose squares overflow float.
            double x = p.Offset.X, y = p.Offset.Y, z = p.Offset.Z;
            double len = Math.Sqrt(x * x + y * y + z * z);
            Assert.InRange(len, 0.0, g.RadiusMeters * 1.5);
            Assert.True(p.RadiusMeters > 0f);
        }
    }

    [Fact]
    public void DifferentGalaxies_ProduceDifferentGlows()
    {
        GalaxyGlowPuff[] a = GalaxyGlow.ForGalaxy(MilkyWay());
        Galaxy other = new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(1, 0, 0)).Galaxies[0];
        GalaxyGlowPuff[] b = GalaxyGlow.ForGalaxy(other);
        Assert.True(a.Length != b.Length || a[1].Offset != b[1].Offset);
    }
}
