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

    // Atmosphere (a volumetric single-scattering shell; see AtmosphereRenderer). The optical fields
    // below are DERIVED from the scanned composition + temperature + gravity + surface pressure by
    // AtmosphereModel.Derive, so the look reflects the chemistry rather than a per-type constant.
    public bool HasAtmosphere;
    public float SurfacePressureBar;  // surface pressure (bar) — drives optical depth + scanner readout
    public Vector3D<float> AtmosphereColor; // Rayleigh absorption tint (≈white clear gas; cyan for methane)
    public float AtmosphereHeight;    // shell thickness as a fraction of radius (from the scale height)
    public float AtmosphereDensity;   // Rayleigh optical-depth scale (pressure × gas refractivity)
    public Vector3D<float> MieColor;  // aerosol/haze colour (dust ochre, sulphuric yellow, …)
    public float MieDensity;          // aerosol optical-depth scale (haze gases + suspended surface dust)

    // Scan data — deterministic per-body detail surfaced by the in-game scanner (see
    // SystemGenerator.ApplyScanData). Compositions are sorted most-abundant first and sum to ~1.
    public float SurfaceTempK;                                              // representative surface temperature
    public bool Habitable;                                                  // liquid water + breathable air + temperate
    public Constituent[] AtmosphereComposition = Array.Empty<Constituent>(); // empty when airless
    public Constituent[] SurfaceComposition = Array.Empty<Constituent>();    // empty for gas/ice giants

    // Axial rotation (drives the day/night cycle). Seeded per body. The geometry/camera stay in the body's
    // co-rotating frame, so rotation is expressed by sweeping the apparent SUN DIRECTION (see ApparentSunDir)
    // rather than spinning the mesh — the terminator moves over the surface as sim time advances.
    public double RotationPeriodSeconds;          // 0 = no spin (always-day/night by orbit only)
    public Vector3D<double> SpinAxis = new(0, 1, 0); // unit, body/world frame (tilt = axial obliquity)

    /// <summary>The sun direction as seen from this body's co-rotating surface at <paramref name="simTime"/>:
    /// the true planet→sun direction rotated backward by the spin angle about <see cref="SpinAxis"/>, so a
    /// fixed surface point sees the sun rise, cross the sky and set. Returns the input unchanged when the body
    /// has no spin. Used by the terrain, rover and atmosphere so the ground and sky share one day/night.</summary>
    public Vector3D<float> ApparentSunDir(Vector3D<float> trueSunDir, double simTime)
    {
        if (RotationPeriodSeconds <= 0.0) return trueSunDir;
        double theta = -2.0 * Math.PI * simTime / RotationPeriodSeconds; // surface observer sees the sun move backward
        double c = Math.Cos(theta), s = Math.Sin(theta);
        var v = new Vector3D<double>(trueSunDir.X, trueSunDir.Y, trueSunDir.Z);
        var k = Vector3D.Normalize(SpinAxis);
        // Rodrigues rotation of v about k by theta.
        Vector3D<double> r = v * c + Vector3D.Cross(k, v) * s + k * (Vector3D.Dot(k, v) * (1.0 - c));
        r = Vector3D.Normalize(r);
        return new Vector3D<float>((float)r.X, (float)r.Y, (float)r.Z);
    }

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
