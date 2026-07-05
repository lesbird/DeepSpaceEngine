using System.Collections.Generic;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class SunOcclusionTests
{
    private static (Vector3D<double>, double)[] None => System.Array.Empty<(Vector3D<double>, double)>();

    [Fact]
    public void ClearSky_IsFullyVisible()
    {
        var sun = new Vector3D<double>(0, 0, 1e11);          // straight ahead
        Assert.Equal(1.0, SunOcclusion.Visibility(sun, None));
    }

    [Fact]
    public void BodyDeadOnTheSightline_FullyEclipses()
    {
        var sun = new Vector3D<double>(0, 0, 1e11);
        // A planet halfway to the sun, centred on the ray, radius 1e6 → the ray passes through its core.
        var occ = new List<(Vector3D<double>, double)> { (new Vector3D<double>(0, 0, 5e10), 1e6) };
        Assert.Equal(0.0, SunOcclusion.Visibility(sun, occ), 3);
    }

    [Fact]
    public void BodyOffTheSightline_DoesNotBlock()
    {
        var sun = new Vector3D<double>(0, 0, 1e11);
        // Same body, but shifted well clear of the ray (100x its radius sideways).
        var occ = new List<(Vector3D<double>, double)> { (new Vector3D<double>(1e8, 0, 5e10), 1e6) };
        Assert.Equal(1.0, SunOcclusion.Visibility(sun, occ));
    }

    [Fact]
    public void BodyBehindCamera_OrBeyondSun_NeverBlocks()
    {
        var sun = new Vector3D<double>(0, 0, 1e11);
        var behind = new List<(Vector3D<double>, double)> { (new Vector3D<double>(0, 0, -5e10), 1e9) };
        var beyond = new List<(Vector3D<double>, double)> { (new Vector3D<double>(0, 0, 2e11), 1e9) };
        Assert.Equal(1.0, SunOcclusion.Visibility(sun, behind));
        Assert.Equal(1.0, SunOcclusion.Visibility(sun, beyond));
    }

    [Fact]
    public void Limb_FadesSmoothlyAcrossTheEdge()
    {
        // Sweep a body sideways across the ray at a fixed depth; visibility must move monotonically
        // 0 -> 1 through a soft penumbra (no hard pop), which is what makes the flare set behind a limb.
        var sun = new Vector3D<double>(0, 0, 1e11);
        double depth = 5e10, r = 2e6;
        double prev = -1.0;
        bool sawPartial = false;
        for (int i = 0; i <= 20; i++)
        {
            double offset = r * (0.5 + i * 0.05);            // from inside the disc to well outside
            var occ = new List<(Vector3D<double>, double)> { (new Vector3D<double>(offset, 0, depth), r) };
            double v = SunOcclusion.Visibility(sun, occ);
            Assert.InRange(v, 0.0, 1.0);
            Assert.True(v >= prev - 1e-9, $"visibility must not decrease as the body clears the ray ({v} < {prev})");
            if (v > 0.01 && v < 0.99) sawPartial = true;
            prev = v;
        }
        Assert.True(prev >= 0.99, "fully clear once the body is well off the ray");
        Assert.True(sawPartial, "expected a soft penumbra, not a hard on/off edge");
    }
}
