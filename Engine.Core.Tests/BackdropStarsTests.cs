using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class BackdropStarsTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalCatalog()
    {
        var a = new BackdropStars(0xABCDEF, count: 2000);
        var b = new BackdropStars(0xABCDEF, count: 2000);

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Interleaved, b.Interleaved); // exact float-for-float determinism
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentCatalog()
    {
        var a = new BackdropStars(1, count: 2000);
        var b = new BackdropStars(2, count: 2000);
        Assert.NotEqual(a.Interleaved, b.Interleaved);
    }

    [Fact]
    public void Directions_AreUnitLength()
    {
        var stars = new BackdropStars(42, count: 3000);
        for (int i = 0; i < stars.Count; i++)
        {
            Vector3D<float> d = stars.Direction(i);
            Assert.Equal(1.0, d.Length, 3); // unit within 1e-3
        }
    }

    [Fact]
    public void Stars_ConcentrateTowardTheGalacticPlane()
    {
        // The plane is world XZ, so |dir.Y| = |sin(galactic latitude)|. The cubic latitude sampling
        // should pile most stars near the band; assert a clear majority sit within a thin slab.
        var stars = new BackdropStars(7, count: 8000);
        int nearPlane = 0;
        for (int i = 0; i < stars.Count; i++)
            if (System.Math.Abs(stars.Direction(i).Y) < 0.25f) nearPlane++;

        Assert.True(nearPlane > stars.Count * 0.5,
            $"expected >50% of stars within |y|<0.25 of the plane, got {nearPlane}/{stars.Count}");
    }
}
