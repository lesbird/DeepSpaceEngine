using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

public enum PlanetType { Lava, Rocky, Desert, Ocean, Ice, IceGiant, GasGiant }

/// <summary>
/// A planet on a (circular, slightly inclined) orbit around its star. Orbital elements
/// are immutable and seeded; <see cref="CurrentPosition"/> is recomputed each frame from
/// simulation time. Terrain comes in M3 — for now it renders as a lit sphere.
/// </summary>
public sealed class Planet
{
    public ulong Seed;
    public PlanetType Type;
    public string Designation = ""; // "<star>-<planet>"

    // Orbital elements (SI).
    public double SemiMajorAxis;   // metres
    public double Inclination;     // radians
    public double AscendingNode;   // radians
    public double Phase;           // radians at t = 0
    public double MeanMotion;      // radians / second

    // Physical / visual.
    public double RadiusMeters;
    public double MassKg;
    public double AxialTilt;
    public Vector3D<float> Color;
    public Moon[] Moons = Array.Empty<Moon>();

    // Rings — a flat annulus in the planet's (tilted) equatorial plane. Only some giants
    // have them; when <see cref="HasRings"/> is false the radii are zero. Radii are absolute
    // metres from the planet centre; the plane is tilted by <see cref="RingTilt"/> about the
    // axis at <see cref="RingTiltAzimuth"/>. <see cref="RingSeed"/> seeds the procedural banding.
    public bool HasRings;
    public double RingInnerRadius;       // metres
    public double RingOuterRadius;       // metres
    public Vector3D<float> RingColor;
    public float RingOpacity;            // base alpha of the densest bands (0..1)
    public float RingTilt;               // radians — inclination of the ring plane
    public float RingTiltAzimuth;        // radians — node the tilt is taken about
    public ulong RingSeed;

    // Atmosphere (rendered as a fresnel-glow shell).
    public bool HasAtmosphere;
    public Vector3D<float> AtmosphereColor;
    public float AtmosphereHeight;   // shell thickness as a fraction of radius
    public float AtmosphereDensity;  // glow intensity

    /// <summary>World position for the current simulation time (updated by SolarSystem).</summary>
    public UniversePosition CurrentPosition;

    /// <summary>Orbital period in seconds.</summary>
    public double PeriodSeconds => MeanMotion != 0 ? 2.0 * Math.PI / MeanMotion : 0;

    /// <summary>Offset from the star (metres) at simulation time t.</summary>
    public Vector3D<double> OrbitalOffset(double t)
    {
        double theta = Phase + MeanMotion * t;
        double x = SemiMajorAxis * Math.Cos(theta);
        double z = SemiMajorAxis * Math.Sin(theta);

        // Tilt the orbital plane by inclination (about X), then rotate by the node (about Y).
        double y = z * Math.Sin(Inclination);
        double zi = z * Math.Cos(Inclination);
        double cosN = Math.Cos(AscendingNode), sinN = Math.Sin(AscendingNode);
        double xr = x * cosN - zi * sinN;
        double zr = x * sinN + zi * cosN;
        return new Vector3D<double>(xr, y, zr);
    }
}
