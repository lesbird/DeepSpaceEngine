using Game.Systems;
using Xunit;

namespace Engine.Core.Tests;

public class SpeedPolicyTests
{
    private const double Engage = 1.0e12; // a representative zone size for the cap tests

    [Fact]
    public void BeyondTheZone_IsUnlimited()
    {
        // At or past the engage distance the body imposes no limit at all.
        Assert.True(double.IsPositiveInfinity(SpeedPolicy.Cap(Engage, Engage)));
        Assert.True(double.IsPositiveInfinity(SpeedPolicy.Cap(2.0 * Engage, Engage)));
    }

    [Fact]
    public void AtTheSurface_FloorsAtTheMinimum()
    {
        // Right at (or below) the surface the proportional value is ~0, so the floor takes over —
        // you can still creep, never freeze.
        Assert.Equal(SpeedPolicy.MinApproachSpeed, SpeedPolicy.Cap(0.0, Engage));
        Assert.Equal(SpeedPolicy.MinApproachSpeed, SpeedPolicy.Cap(-500.0, Engage)); // negative clamped to 0
    }

    [Fact]
    public void InsideTheZone_RisesWithDistanceAndIsProportionalDeepInside()
    {
        // The cap grows as you back away from the surface, so you decelerate as you close in.
        double near = SpeedPolicy.Cap(0.01 * Engage, Engage);
        double far = SpeedPolicy.Cap(0.10 * Engage, Engage);
        Assert.True(near < far);

        // Deep inside the zone (d ≪ engage) the (1 − d/engage) divisor ≈ 1, so cap ≈ ApproachRate × d.
        double d = 1.0e6; // 1000 km from a surface, with a 1e12 m zone
        double expected = SpeedPolicy.ApproachRate * d;
        double cap = SpeedPolicy.Cap(d, Engage);
        Assert.True(Math.Abs(cap - expected) / expected < 1.0e-3); // within 0.1% of strictly proportional
    }

    [Fact]
    public void NearTheZoneEdge_GrowsTowardInfinity_NoCliff()
    {
        // The divisor → 0 at the edge, so the cap climbs without bound as you approach it — the limit
        // appears continuously rather than snapping on at a hard boundary.
        double mid = SpeedPolicy.Cap(0.5 * Engage, Engage);
        double edge = SpeedPolicy.Cap(0.99 * Engage, Engage);
        Assert.True(edge > mid);
        Assert.True(edge > 50.0 * mid); // grows steeply toward the edge
    }

    [Fact]
    public void EngageDistance_ScalesWithRadius_AndStarsReachFarther()
    {
        double r = 6.0e6;
        Assert.Equal(r * SpeedPolicy.BodyEngageRadii, SpeedPolicy.EngageDistance(r, isStar: false));
        Assert.Equal(r * SpeedPolicy.StarEngageRadii, SpeedPolicy.EngageDistance(r, isStar: true));
        // A star slows you from much farther out than a planet/moon of the same radius.
        Assert.True(SpeedPolicy.EngageDistance(r, true) > SpeedPolicy.EngageDistance(r, false));
    }
}
