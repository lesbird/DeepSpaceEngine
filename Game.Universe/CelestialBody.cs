using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>One labelled fraction (0..1) of a composition breakdown — e.g. ("Nitrogen", 0.78)
/// of an atmosphere or ("Basalt", 0.4) of a surface. Surfaced via the in-game scanner.</summary>
public readonly record struct Constituent(string Name, float Fraction);

/// <summary>
/// Shared state for any body on a (circular, slightly inclined) Keplerian orbit — a
/// <see cref="Planet"/> around its star or a <see cref="Moon"/> around its parent planet.
/// Orbital elements are immutable and seeded; <see cref="CurrentPosition"/> is recomputed each
/// frame from simulation time. Holding the surface-relevant data here (type, mass, atmosphere)
/// is what lets the terrain, atmosphere and rover systems treat planets and moons identically.
/// </summary>
public abstract class CelestialBody
{
    public ulong Seed;
    public PlanetType Type;
    public string Designation = "";

    // Orbital elements (SI). For a planet these are around the star; for a moon, around the planet.
    public double SemiMajorAxis;   // metres
    public double Inclination;     // radians
    public double AscendingNode;   // radians
    public double Phase;           // radians at t = 0
    public double MeanMotion;      // radians / second

    // Physical / visual.
    public double RadiusMeters;
    public double MassKg;

    /// <summary>Seeded base tint, used both as the distant-sphere fallback colour and as the lowland
    /// ground tint the terrain colouring builds on (see <see cref="PlanetTerrain"/>).</summary>
    public Vector3D<float> Color;

    /// <summary>Mean albedo the body actually shows (the average of the terrain/baked-map surface
    /// colour). The distant sphere uses this so the far view matches the surface — a cold regolith
    /// moon reads white from orbit, not the raw brown <see cref="Color"/> tint. Set by the generator
    /// for surfaced worlds; equals <see cref="Color"/> for gas/ice giants.</summary>
    public Vector3D<float> SurfaceAlbedo;

    // Atmosphere (a volumetric single-scattering shell; see AtmosphereRenderer).
    public bool HasAtmosphere;
    public Vector3D<float> AtmosphereColor;
    public float AtmosphereHeight;   // shell thickness as a fraction of radius
    public float AtmosphereDensity;  // optical-depth multiplier

    // Scan data — deterministic per-body detail surfaced by the in-game scanner (see
    // SystemGenerator.ApplyScanData). Compositions are sorted most-abundant first and sum to ~1.
    public float SurfaceTempK;                                              // representative surface temperature
    public bool Habitable;                                                  // liquid water + breathable air + temperate
    public Constituent[] AtmosphereComposition = Array.Empty<Constituent>(); // empty when airless
    public Constituent[] SurfaceComposition = Array.Empty<Constituent>();    // empty for gas/ice giants

    /// <summary>World position for the current simulation time (updated by SolarSystem).</summary>
    public UniversePosition CurrentPosition;

    /// <summary>True for solid worlds the camera can land on (everything but the gas/ice giants).</summary>
    public bool HasSurface => Type is not (PlanetType.GasGiant or PlanetType.IceGiant);

    /// <summary>True for worlds with surface liquid water (the ocean worlds).</summary>
    public bool HasLiquidWater => Type == PlanetType.Ocean;

    /// <summary>Surface gravity (m/s²) from mass and radius; 0 for bodies with no solid surface.</summary>
    public double SurfaceGravity => MassKg > 0 && RadiusMeters > 0
        ? MathUtil.GravitationalConstant * MassKg / (RadiusMeters * RadiusMeters)
        : 0;

    /// <summary>Orbital period in seconds.</summary>
    public double PeriodSeconds => MeanMotion != 0 ? 2.0 * Math.PI / MeanMotion : 0;

    /// <summary>Offset from the primary (metres) at simulation time t.</summary>
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
