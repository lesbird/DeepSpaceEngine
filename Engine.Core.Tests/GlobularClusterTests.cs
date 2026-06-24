using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class GlobularClusterTests
{
    private const ulong Seed = 0xA11CE5EEDUL;

    private static Galaxy MilkyWay() =>
        new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(0, 0, 0)).Galaxies[0];

    [Fact]
    public void ForGalaxy_IsDeterministic()
    {
        Galaxy g = MilkyWay();
        GlobularCluster[] a = GlobularClusters.ForGalaxy(g);
        GlobularCluster[] b = GlobularClusters.ForGalaxy(g);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Id, b[i].Id);
            Assert.Equal(a[i].Center.Sector, b[i].Center.Sector);
            Assert.Equal(a[i].RadiusMeters, b[i].RadiusMeters);
        }
    }

    [Fact]
    public void Clusters_SitInTheHalo_WithinAFewGalaxyRadii()
    {
        Galaxy g = MilkyWay();
        foreach (GlobularCluster c in GlobularClusters.ForGalaxy(g))
        {
            double distFromCentre = c.Center.DistanceTo(g.Center);
            Assert.InRange(distFromCentre, 0.0, g.RadiusMeters * 1.6);
            Assert.InRange(c.RadiusLy, 15.0, 110.0); // ~40–200 ly diameter
        }
    }

    [Fact]
    public void DifferentGalaxies_HaveDifferentClusters()
    {
        Galaxy mw = MilkyWay();
        Galaxy other = new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(1, 0, 0)).Galaxies[0];
        GlobularCluster[] a = GlobularClusters.ForGalaxy(mw);
        GlobularCluster[] b = GlobularClusters.ForGalaxy(other);
        Assert.True(a.Length != b.Length || a[0].Center.Sector != b[0].Center.Sector);
    }

    [Fact]
    public void DecorativeCloud_SharesStarPositionsWithTheInjectedStars()
    {
        // The decorative cloud point i must sit exactly where the injected catalog star i is generated
        // (both via StarRng + StarOffset, position drawn first), so the LOD hand-off is seamless.
        GlobularCluster c = GlobularClusters.ForGalaxy(MilkyWay())[0];
        float[] cloud = GlobularClusters.Stars(c);
        for (int i = 0; i < 50; i++)
        {
            DeterministicRng rng = GlobularClusters.StarRng(c, i);
            Vector3D<double> off = GlobularClusters.StarOffset(ref rng, c.RadiusMeters);
            int o = i * GlobularClusters.FloatsPerStar;
            Assert.Equal((float)off.X, cloud[o + 0]);
            Assert.Equal((float)off.Y, cloud[o + 1]);
            Assert.Equal((float)off.Z, cloud[o + 2]);
        }
    }

    [Fact]
    public void CoreStars_StaySpacedFartherThanTheSpawnRange()
    {
        // The dense core must not pack stars closer than the solar-system spawn range, or you can't fly
        // to individual systems. Check the innermost stars are all mutually separated by > the spawn range.
        const double au = MathUtil.AstronomicalUnit;
        const double spawnRange = 500.0 * au;           // SolarSystemManager.SpawnAu

        GlobularCluster c = GlobularClusters.ForGalaxy(MilkyWay())[0];

        // Gather the 250 stars nearest the centre — the densest region, where pile-ups would occur.
        var inner = new List<Vector3D<double>>();
        for (int i = 0; i < c.StarCount; i++)
        {
            DeterministicRng rng = GlobularClusters.StarRng(c, i);
            inner.Add(GlobularClusters.StarOffset(ref rng, c.RadiusMeters));
        }
        inner.Sort((a, b) => a.LengthSquared.CompareTo(b.LengthSquared));
        int n = Math.Min(250, inner.Count);

        double minSep = double.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                minSep = Math.Min(minSep, (inner[i] - inner[j]).Length);

        Assert.True(minSep > spawnRange,
            $"core stars only {minSep / au:0} AU apart — closer than the {spawnRange / au:0} AU spawn range");
    }

    [Fact]
    public void Stars_FillTheClusterRadius_Deterministically()
    {
        GlobularCluster c = GlobularClusters.ForGalaxy(MilkyWay())[0];
        float[] a = GlobularClusters.Stars(c);
        float[] b = GlobularClusters.Stars(c);
        Assert.Equal(a, b);
        Assert.Equal(c.StarCount * GlobularClusters.FloatsPerStar, a.Length);

        double maxR = 0;
        for (int i = 0; i < a.Length; i += GlobularClusters.FloatsPerStar)
            maxR = Math.Max(maxR, Math.Sqrt(a[i] * (double)a[i] + a[i + 1] * (double)a[i + 1] + a[i + 2] * (double)a[i + 2]));
        Assert.True(maxR <= c.RadiusMeters * 1.01, "a cluster star escaped its radius");
    }
}
