using Silk.NET.Maths;

namespace Game.Universe;

public enum PlanetType { Lava, Rocky, Desert, Ocean, Ice, IceGiant, GasGiant }

/// <summary>
/// A planet on a (circular, slightly inclined) orbit around its star. Shared orbital, physical
/// and atmosphere state lives on <see cref="CelestialBody"/>; this adds the planet-only extras —
/// satellites, axial tilt, and an optional ring system.
/// </summary>
public sealed class Planet : CelestialBody
{
    public double AxialTilt;
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

    /// <summary>Asteroid particles filling the ring annulus (null when the planet has no rings).
    /// Rendered chunky/up-close by the hybrid-LOD asteroid renderer, over the smooth annulus band.</summary>
    public PlanetRing? RingRocks;
}
