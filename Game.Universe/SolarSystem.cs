using System.Collections.Generic;
using Engine.Core;

namespace Game.Universe;

/// <summary>
/// A spawned solar system: its star plus generated planets. Lightweight to create and
/// discard — it holds no GPU resources, only data. Planet positions are advanced by
/// <see cref="UpdatePositions"/> each frame.
/// </summary>
public sealed class SolarSystem
{
    public readonly Star Sun;
    public readonly Planet[] Planets;

    public double MaxOrbitRadius { get; }
    public double MaxPlanetRadius { get; }

    /// <summary>Fastest body's true orbital speed (m/s); multiply by TimeScale for the apparent speed.</summary>
    public double MaxOrbitalSpeedMps { get; }

    public SolarSystem(Star sun, Planet[] planets)
    {
        Sun = sun;
        Planets = planets;

        double maxOrbit = 0, maxRadius = 0, maxSpeed = 0;
        foreach (Planet p in planets)
        {
            if (p.SemiMajorAxis > maxOrbit) maxOrbit = p.SemiMajorAxis;
            if (p.RadiusMeters > maxRadius) maxRadius = p.RadiusMeters;
            maxSpeed = Math.Max(maxSpeed, p.SemiMajorAxis * p.MeanMotion); // v = a·n
            foreach (Moon m in p.Moons)
                maxSpeed = Math.Max(maxSpeed, p.SemiMajorAxis * p.MeanMotion + m.SemiMajorAxis * m.MeanMotion);
        }
        MaxOrbitRadius = maxOrbit;
        MaxPlanetRadius = maxRadius;
        MaxOrbitalSpeedMps = maxSpeed;
    }

    /// <summary>Every body in the system — each planet followed by its moons — for the surface and
    /// atmosphere passes that treat planets and moons identically.</summary>
    public IEnumerable<CelestialBody> AllBodies()
    {
        foreach (Planet p in Planets)
        {
            yield return p;
            foreach (Moon m in p.Moons) yield return m;
        }
    }

    /// <summary>Advance all planet (and moon) positions to simulation time <paramref name="t"/> (seconds).</summary>
    public void UpdatePositions(double t)
    {
        foreach (Planet p in Planets)
        {
            p.CurrentPosition = Sun.Position.Translated(p.OrbitalOffset(t));
            foreach (Moon m in p.Moons)
                m.CurrentPosition = p.CurrentPosition.Translated(m.OrbitalOffset(t));
        }
    }
}
