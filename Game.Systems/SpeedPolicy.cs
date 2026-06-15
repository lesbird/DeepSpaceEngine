using Engine.Core;

namespace Game.Systems;

/// <summary>
/// The free-fly <b>proximity speed limiter</b>. There is no system-entry "cruise cap" any more: in
/// open space you fly at whatever the wheel commands (up to interstellar velocities). Instead the
/// limit is purely a function of how close you are to a body — a star, planet or moon. Far from
/// everything there is no limit at all; as you near a body your top speed is smoothly reduced, so
/// you decelerate into an approach instead of blowing past at warp.
///
/// <para>Each body limits speed only within an <see cref="EngageDistance"/> of its surface (scaled by
/// its radius, so big stars slow you from much farther out than a small moon). Inside that zone the
/// cap is proportional to the distance to the surface (<see cref="ApproachRate"/> × distance), which
/// makes the descent <i>self-converging</i> — the cap shrinks as you close in, so each second covers a
/// fraction of the remaining distance rather than overshooting. The proportional value is divided by
/// <c>(1 − d/engage)</c> so it rises to +∞ exactly at the zone edge: the limit therefore appears
/// continuously (no speed cliff) as you cross into the zone. A small floor keeps you from freezing at
/// the surface. The overall limit is the minimum of every nearby body's cap, so the nearest/closest
/// body wins and planets/moons tighten the limit further as you arrive.</para>
///
/// Pure and dependency-light so it is unit-testable without GL or input.
/// </summary>
public static class SpeedPolicy
{
    /// <summary>Cap grows this many m/s per metre of distance to the surface, deep inside a zone.
    /// Its reciprocal is the approach time-constant (≈1/3 s here), so a straight-in descent covers
    /// most of the remaining distance every fraction of a second — fast, but always decelerating.</summary>
    public const double ApproachRate = 3.0;

    /// <summary>Lowest the cap ever falls to (m/s), so you can still creep right at a surface.</summary>
    public const double MinApproachSpeed = 1000.0;

    /// <summary>A star's slowdown zone reaches this many stellar radii from its surface. Large, so the
    /// limit engages out at roughly the scale of the planetary system (~hundredths of a light-year for
    /// a Sun-like star) — yet still far smaller than interstellar spacing, so cruising between stars is
    /// unaffected.</summary>
    public const double StarEngageRadii = 250_000.0;

    /// <summary>A planet's or moon's slowdown zone reaches this many of its radii from its surface.</summary>
    public const double BodyEngageRadii = 4_000.0;

    /// <summary>How far from a body's surface (metres) its speed limit reaches.</summary>
    public static double EngageDistance(double radiusMeters, bool isStar)
        => radiusMeters * (isStar ? StarEngageRadii : BodyEngageRadii);

    /// <summary>
    /// One body's contribution to the speed cap (m/s). <paramref name="distanceToSurfaceMeters"/> is
    /// the camera's centre distance minus the body radius (negatives treated as zero).
    /// <see cref="double.PositiveInfinity"/> — i.e. no limit — once you are beyond
    /// <paramref name="engageDistanceMeters"/> of the surface.
    /// </summary>
    public static double Cap(double distanceToSurfaceMeters, double engageDistanceMeters)
    {
        if (engageDistanceMeters <= 0.0) return double.PositiveInfinity;
        if (distanceToSurfaceMeters >= engageDistanceMeters) return double.PositiveInfinity;

        double d = Math.Max(0.0, distanceToSurfaceMeters);
        // Proportional cap, divided by the remaining fraction of the zone so it → +∞ at the edge.
        double cap = ApproachRate * d / (1.0 - d / engageDistanceMeters);
        return Math.Max(cap, MinApproachSpeed);
    }
}
