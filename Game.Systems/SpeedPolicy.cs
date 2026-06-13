using Engine.Core;

namespace Game.Systems;

/// <summary>
/// The free-fly <b>auto speed cap</b>. Open galaxy is unlimited; inside a solar system the maximum
/// speed is dropped to something you can actually navigate with, and dropped further the closer you
/// get to a planet, so you decelerate smoothly into an approach instead of blowing past at
/// interstellar velocity.
///
/// In the approach band the cap is proportional to the distance to the nearest planet's surface
/// (<see cref="ApproachRate"/> × distance). That makes the descent <i>self-converging</i>: as you
/// near a planet the cap shrinks, so each step covers a fraction of the remaining distance rather
/// than overshooting. It is clamped between a per-system ceiling (cruise between planets) and an
/// in-atmosphere floor. The wheel-driven throttle percentage (see <c>FreeFlyController</c>) then
/// scales whatever cap this returns, for fine control below the auto value.
///
/// Pure and dependency-light so it is unit-testable without GL or input.
/// </summary>
public static class SpeedPolicy
{
    /// <summary>Cap when inside a system but far from any planet (~1,000,000 c).</summary>
    public const double SystemMaxSpeed = 1000000.0 * MathUtil.SpeedOfLight;

    /// <summary>Floor the auto cap settles to in a planet's atmosphere (1,000 km/s).</summary>
    public const double PlanetMaxSpeed = 1.0e6;

    /// <summary>Cap grows this many m/s per metre of altitude in the proportional approach band.</summary>
    public const double ApproachRate = 1.0;

    /// <summary>
    /// The auto max-speed cap (m/s). <see cref="double.PositiveInfinity"/> in open galaxy
    /// (<paramref name="inSystem"/> false), i.e. unlimited.
    /// </summary>
    /// <param name="nearestSurfaceMeters">Distance from the camera to the nearest planet's
    /// surface (centre distance minus radius); negatives are treated as zero.</param>
    public static double MaxSpeed(bool inSystem, double nearestSurfaceMeters)
    {
        if (!inSystem) return double.PositiveInfinity;
        double d = Math.Max(0.0, nearestSurfaceMeters);
        return Math.Clamp(ApproachRate * d, PlanetMaxSpeed, SystemMaxSpeed);
    }
}
