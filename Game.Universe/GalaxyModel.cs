using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Describes the stellar density of <b>one galaxy</b> as a function of position. The disk lies in the
/// plane perpendicular to <see cref="DiskNormal"/> (the Milky Way's normal is +Y, so its disk is the
/// world XZ plane); density falls off exponentially with galactic radius and disk height, with a mild
/// spiral-arm modulation, and is clamped to zero beyond <see cref="RadiusLy"/>.
///
/// Two ways to query it: <see cref="DensityAtLocal"/> takes a galaxy-relative offset (full precision —
/// use this when generating a galaxy's stars), while the legacy <see cref="DensityAt"/> takes an
/// absolute offset from the universe origin (the global Milky-Way-at-origin model still used by
/// <see cref="StarField"/>). Build a model for a specific galaxy with <see cref="For"/>.
/// </summary>
public sealed class GalaxyModel
{
    public ulong WorldSeed { get; }

    /// <summary>Galaxy centre (the universe origin for the legacy/global model).</summary>
    public UniversePosition Center;
    /// <summary>Unit normal of the disk plane (+Y → disk in XZ, matching the Milky Way).</summary>
    public Vector3D<double> DiskNormal = new(0, 1, 0);

    // Tunables (light-years; stars per ly^3).
    public double BaseDensity = 0.006;   // stars per ly^3 at the galaxy centre
    public double ScaleLength = 3500.0;  // radial exponential scale
    public double ScaleHeight = 320.0;   // vertical exponential scale
    public int ArmCount = 2;
    public double ArmStrength = 0.4;
    public double ArmWind = 0.25;
    /// <summary>Hard radial cutoff (ly): beyond this the density is zero. +Infinity for the global model.</summary>
    public double RadiusLy = double.PositiveInfinity;

    public GalaxyModel(ulong worldSeed)
    {
        WorldSeed = worldSeed;
        Center = UniversePosition.Origin;
    }

    /// <summary>Peak density (galaxy centre, on an arm crest) — the acceptance-sampling reference.</summary>
    public double PeakDensity => BaseDensity * (1.0 + Math.Max(0.0, ArmStrength));

    /// <summary>
    /// Build a density model for a specific galaxy: centre + orientation from the descriptor, shape
    /// scaled by type and radius. The Spiral branch with a 50,000 ly radius reproduces the tuned
    /// Milky-Way values (ScaleLength 3500, ScaleHeight 320), so the home galaxy looks unchanged.
    /// </summary>
    public static GalaxyModel For(Galaxy g)
    {
        var n = new Vector3D<double>(g.DiskNormal.X, g.DiskNormal.Y, g.DiskNormal.Z);
        n = n.LengthSquared < 1e-12 ? new Vector3D<double>(0, 1, 0) : Vector3D.Normalize(n);

        var m = new GalaxyModel(g.Seed)
        {
            Center = g.Center,
            DiskNormal = n,
            RadiusLy = g.RadiusLy,
        };

        switch (g.Type)
        {
            case GalaxyType.Spiral:
                m.BaseDensity = 0.006;
                m.ScaleLength = g.RadiusLy * 0.07;    // 50,000 ly → 3,500 (Milky-Way tuned)
                m.ScaleHeight = g.RadiusLy * 0.0064;  // 50,000 ly → 320
                m.ArmCount = 2; m.ArmStrength = 0.4; m.ArmWind = 0.25;
                break;
            case GalaxyType.Elliptical:
                m.BaseDensity = 0.010;
                m.ScaleLength = g.RadiusLy * 0.25;    // puffy, near-spherical, no arms
                m.ScaleHeight = g.RadiusLy * 0.18;
                m.ArmCount = 0; m.ArmStrength = 0.0;
                break;
            case GalaxyType.Irregular:
                m.BaseDensity = 0.005;
                m.ScaleLength = g.RadiusLy * 0.35;
                m.ScaleHeight = g.RadiusLy * 0.12;
                m.ArmCount = 1; m.ArmStrength = 0.25; m.ArmWind = 0.6;
                break;
            default: // Dwarf
                m.BaseDensity = 0.004;
                m.ScaleLength = g.RadiusLy * 0.4;
                m.ScaleHeight = g.RadiusLy * 0.3;
                m.ArmCount = 0; m.ArmStrength = 0.0;
                break;
        }
        return m;
    }

    /// <summary>Stars per cubic light-year at a galaxy-relative offset (metres; full precision).</summary>
    public double DensityAtLocal(Vector3D<double> offsetMeters)
    {
        double h = Vector3D.Dot(offsetMeters, DiskNormal);          // signed height along the disk normal
        Vector3D<double> inPlane = offsetMeters - DiskNormal * h;   // projection into the disk plane
        double r = inPlane.Length / MathUtil.LightYear;
        if (r > RadiusLy) return 0.0;
        double yLy = Math.Abs(h) / MathUtil.LightYear;

        double disk = Math.Exp(-r / ScaleLength) * Math.Exp(-yLy / ScaleHeight);

        double arm = 1.0;
        if (ArmCount > 0 && ArmStrength > 0.0)
        {
            DiskBasis(out Vector3D<double> u, out Vector3D<double> v);
            double theta = Math.Atan2(Vector3D.Dot(inPlane, u), Vector3D.Dot(inPlane, v));
            arm = 1.0 + ArmStrength * Math.Cos(ArmCount * theta - ArmWind * Math.Log(r + 1.0));
        }
        return BaseDensity * disk * Math.Max(arm, 0.1);
    }

    /// <summary>Stars per cubic light-year at an absolute offset from the universe origin (metres).
    /// For the global model (centre = origin) this matches the original disk/arm formula exactly.</summary>
    public double DensityAt(Vector3D<double> absoluteMeters)
        => DensityAtLocal(absoluteMeters - Center.DeltaMeters(UniversePosition.Origin));

    /// <summary>A stable orthonormal basis (u, v) spanning the disk plane. For the +Y normal this yields
    /// u = +Z, v = +X, so the arm angle reduces to the original atan2(z, x) parameterisation.</summary>
    private void DiskBasis(out Vector3D<double> u, out Vector3D<double> v)
    {
        Vector3D<double> a = Math.Abs(DiskNormal.Y) < 0.99
            ? new Vector3D<double>(0, 1, 0)
            : new Vector3D<double>(1, 0, 0);
        u = Vector3D.Normalize(Vector3D.Cross(a, DiskNormal));
        v = Vector3D.Cross(DiskNormal, u);
    }
}
