using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class GalaxyCloudTests
{
    private const ulong Seed = 0xA11CE5EEDUL;

    private static Galaxy MilkyWay() =>
        new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(0, 0, 0)).Galaxies[0];

    [Fact]
    public void Generate_ProducesRequestedPointCount()
    {
        float[] data = GalaxyCloud.Generate(MilkyWay(), 5000);
        Assert.Equal(5000 * GalaxyCloud.FloatsPerPoint, data.Length);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        Galaxy g = MilkyWay();
        float[] a = GalaxyCloud.Generate(g, 3000);
        float[] b = GalaxyCloud.Generate(g, 3000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void AllPoints_LieWithinTheGalaxyRadius()
    {
        Galaxy g = MilkyWay();
        double rMax = g.RadiusMeters;
        float[] data = GalaxyCloud.Generate(g, 20000);

        double maxDist = 0;
        for (int i = 0; i < data.Length; i += GalaxyCloud.FloatsPerPoint)
        {
            double x = data[i], y = data[i + 1], z = data[i + 2];
            maxDist = Math.Max(maxDist, Math.Sqrt(x * x + y * y + z * z));
        }
        // In-plane radius is capped at the galaxy radius; disk height adds a small amount.
        Assert.True(maxDist <= rMax * 1.5, $"a cloud point escaped the galaxy: {maxDist:e2} > {rMax:e2}");
    }

    [Fact]
    public void DifferentGalaxies_ProduceDifferentClouds()
    {
        Galaxy mw = MilkyWay();
        // A random galaxy from a non-home block.
        Galaxy other = new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(1, 0, 0)).Galaxies[0];
        float[] a = GalaxyCloud.Generate(mw, 2000);
        float[] b = GalaxyCloud.Generate(other, 2000);
        Assert.NotEqual(a, b);
    }
}
