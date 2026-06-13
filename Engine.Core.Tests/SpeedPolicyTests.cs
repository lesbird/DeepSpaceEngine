using Engine.Core;
using Game.Systems;
using Xunit;

namespace Engine.Core.Tests;

public class SpeedPolicyTests
{
    [Fact]
    public void OpenGalaxy_IsUnlimited()
    {
        Assert.True(double.IsPositiveInfinity(SpeedPolicy.MaxSpeed(inSystem: false, nearestSurfaceMeters: 0)));
        // Distance is irrelevant when not in a system.
        Assert.True(double.IsPositiveInfinity(SpeedPolicy.MaxSpeed(false, 1.0e15)));
    }

    [Fact]
    public void FarFromPlanets_CapsAtTheSystemCeiling()
    {
        // Beyond the proportional band (distance × rate ≥ ceiling) → clamped to the system ceiling.
        double beyond = 2.0 * SpeedPolicy.SystemMaxSpeed / SpeedPolicy.ApproachRate;
        double cap = SpeedPolicy.MaxSpeed(inSystem: true, nearestSurfaceMeters: beyond);
        Assert.Equal(SpeedPolicy.SystemMaxSpeed, cap);
        Assert.Equal(1_000_000.0 * MathUtil.SpeedOfLight, cap); // ~1,000,000 c
    }

    [Fact]
    public void InsideAtmosphere_FloorsAtThePlanetFloor()
    {
        // At (and below) the surface the proportional value is tiny, so the floor takes over.
        Assert.Equal(SpeedPolicy.PlanetMaxSpeed, SpeedPolicy.MaxSpeed(true, 0));
        Assert.Equal(1.0e6, SpeedPolicy.MaxSpeed(true, 0)); // 1,000 km/s
        Assert.Equal(SpeedPolicy.PlanetMaxSpeed, SpeedPolicy.MaxSpeed(true, -500)); // negative clamped to 0
    }

    [Fact]
    public void ApproachBand_IsProportionalAndMonotonic()
    {
        // Between the floor and ceiling the cap rises with distance, so you decelerate as you close in.
        double near = SpeedPolicy.MaxSpeed(true, 5.0e9);
        double far = SpeedPolicy.MaxSpeed(true, 5.0e10);
        Assert.True(near < far);
        Assert.True(near > SpeedPolicy.PlanetMaxSpeed && near < SpeedPolicy.SystemMaxSpeed);

        double d = 1.0e10;
        Assert.Equal(SpeedPolicy.ApproachRate * d, SpeedPolicy.MaxSpeed(true, d), 3);
    }
}
