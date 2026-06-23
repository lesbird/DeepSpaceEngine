using Engine.Core;
using Game.Universe;

namespace Game.Systems;

/// <summary>
/// Owns the lifecycle of the one solar system that can be active at a time. Spawns the
/// nearest star's system when the camera crosses <see cref="SpawnAu"/>, and despawns it
/// only after the camera retreats past <see cref="DespawnAu"/> — the gap between the two
/// is hysteresis that prevents spawn/despawn thrash at the boundary.
///
/// Ranges are in AU because that is the natural unit at system scale: a generated system's
/// outermost planet reaches at most ~100 AU, so the ~500 AU spawn shell holds the whole
/// system with margin while every body is still sub-pixel, so nothing visibly pops in.
/// </summary>
public sealed class SolarSystemManager
{
    public double SpawnAu = 500.0;
    public double DespawnAu = 600.0;

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

    /// <summary>Distance from the camera to the active star, in AU (∞ if none).</summary>
    public double ActiveStarDistanceAu { get; private set; } = double.PositiveInfinity;

    public void Update(double dt, in UniversePosition camera, INearestStar field)
    {
        _simTime += dt * TimeScale;

        // Despawn if we've drifted out of range of the currently active star.
        if (_active != null)
        {
            ActiveStarDistanceAu = camera.DistanceTo(_active.Sun.Position) / MathUtil.AstronomicalUnit;
            if (ActiveStarDistanceAu > DespawnAu)
            {
                _active = null;
                ActiveStarDistanceAu = double.PositiveInfinity;
            }
        }

        // Spawn the nearest system once we're close enough and nothing is active.
        if (_active == null && field.HasNearest &&
            field.NearestDistanceMeters / MathUtil.AstronomicalUnit < SpawnAu)
        {
            _active = SystemGenerator.Generate(field.Nearest);
            ActiveStarDistanceAu = field.NearestDistanceMeters / MathUtil.AstronomicalUnit;
        }

        _active?.UpdatePositions(_simTime);
    }
}
