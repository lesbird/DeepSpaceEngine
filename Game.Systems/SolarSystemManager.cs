using Engine.Core;
using Game.Universe;

namespace Game.Systems;

/// <summary>
/// Owns the lifecycle of the one solar system that can be active at a time. Spawns the
/// nearest star's system when the camera crosses <see cref="SpawnLightYears"/>, and
/// despawns it only after the camera retreats past <see cref="DespawnLightYears"/> — the
/// gap between the two is hysteresis that prevents spawn/despawn thrash at the boundary.
/// </summary>
public sealed class SolarSystemManager
{
    public double SpawnLightYears = 0.01;
    public double DespawnLightYears = 0.012;

    /// <summary>
    /// Simulation seconds elapsed per real second. Orbital motion you see is time-lapse, so
    /// the apparent orbital velocity is the real velocity × TimeScale. Kept modest by default
    /// so inner planets/moons don't appear to move faster than light; adjustable at runtime.
    /// </summary>
    public double TimeScale = 1000.0;

    private SolarSystem? _active;
    private double _simTime;

    public bool HasActive => _active != null;
    public SolarSystem? Active => _active;
    public ulong? ActiveStarId => _active?.Sun.Id;
    public double SimTime => _simTime;

    /// <summary>Distance from the camera to the active star, in light-years (∞ if none).</summary>
    public double ActiveStarDistanceLy { get; private set; } = double.PositiveInfinity;

    public void Update(double dt, in UniversePosition camera, INearestStar field)
    {
        _simTime += dt * TimeScale;

        // Despawn if we've drifted out of range of the currently active star.
        if (_active != null)
        {
            ActiveStarDistanceLy = camera.DistanceTo(_active.Sun.Position) / MathUtil.LightYear;
            if (ActiveStarDistanceLy > DespawnLightYears)
            {
                _active = null;
                ActiveStarDistanceLy = double.PositiveInfinity;
            }
        }

        // Spawn the nearest system once we're close enough and nothing is active.
        if (_active == null && field.HasNearest &&
            field.NearestDistanceMeters / MathUtil.LightYear < SpawnLightYears)
        {
            _active = SystemGenerator.Generate(field.Nearest);
            ActiveStarDistanceLy = field.NearestDistanceMeters / MathUtil.LightYear;
        }

        _active?.UpdatePositions(_simTime);
    }
}
