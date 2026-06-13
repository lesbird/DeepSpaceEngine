using Silk.NET.Maths;

namespace Engine.Core;

/// <summary>
/// A position anywhere in the universe, stored with sub-millimeter precision at
/// any distance via a two-tier hierarchical scheme:
///
///   absolute_meters = Sector * SectorSize + Local
///
/// <see cref="Sector"/> is an exact 64-bit integer cube index per axis; <see cref="Local"/>
/// is a double offset within that sector cube, kept in the range [0, SectorSize).
///
/// Why this works where a single double fails: a double at galactic magnitudes
/// (~1e21 m) only resolves to hundreds of km. By keeping the *fractional* part of
/// the position inside a small (1 AU) sector, the double's full ~15-16 digits of
/// precision are always spent on a value &lt; 1.5e11 m, giving ~0.03 mm resolution
/// everywhere. The Int64 sector index then provides effectively unlimited range
/// (~1e18 AU, far beyond the observable universe).
///
/// Absolute coordinates are NEVER sent to the GPU. <see cref="ToCameraRelative"/>
/// produces a small, precise float vector relative to the camera (floating origin).
/// </summary>
public struct UniversePosition : IEquatable<UniversePosition>
{
    /// <summary>Edge length of one sector cube, in meters. 1 astronomical unit.</summary>
    public const double SectorSize = 1.495978707e11;
    private const double InvSectorSize = 1.0 / SectorSize;

    /// <summary>Integer sector index per axis. Exact, huge range.</summary>
    public Vector3D<long> Sector;

    /// <summary>Offset within the sector cube in meters; normalized to [0, SectorSize).</summary>
    public Vector3D<double> Local;

    public UniversePosition(Vector3D<long> sector, Vector3D<double> local)
    {
        Sector = sector;
        Local = local;
    }

    /// <summary>Origin of the universe (sector 0, local 0).</summary>
    public static UniversePosition Origin => default;

    /// <summary>Build a position from an absolute meter offset relative to the origin.</summary>
    public static UniversePosition FromMeters(Vector3D<double> meters)
    {
        var p = new UniversePosition(default, meters);
        p.Normalize();
        return p;
    }

    /// <summary>Build a position from an absolute meter offset relative to the origin.</summary>
    public static UniversePosition FromMeters(double x, double y, double z)
        => FromMeters(new Vector3D<double>(x, y, z));

    /// <summary>
    /// Carry any overflow/underflow out of <see cref="Local"/> into <see cref="Sector"/>
    /// so that every Local axis ends up in [0, SectorSize). Call after any arithmetic
    /// that may push Local out of range.
    /// </summary>
    public void Normalize()
    {
        NormalizeAxis(ref Sector.X, ref Local.X);
        NormalizeAxis(ref Sector.Y, ref Local.Y);
        NormalizeAxis(ref Sector.Z, ref Local.Z);
    }

    private static void NormalizeAxis(ref long sector, ref double local)
    {
        // floor(local / SectorSize) is how many whole sectors to carry.
        double carryF = Math.Floor(local * InvSectorSize);
        long carry = (long)carryF;
        sector += carry;
        local -= carryF * SectorSize;

        // Guard against floating-point edge cases landing exactly on a boundary.
        if (local >= SectorSize) { local -= SectorSize; sector += 1; }
        else if (local < 0.0) { local += SectorSize; sector -= 1; }
    }

    /// <summary>
    /// Return this position relative to <paramref name="camera"/> as a float vector
    /// suitable for the GPU. Near the camera the result is tiny and precise; far away
    /// it is large but any float error is far smaller than a pixel.
    /// </summary>
    public Vector3D<float> ToCameraRelative(in UniversePosition camera)
    {
        double dx = (double)(Sector.X - camera.Sector.X) * SectorSize + (Local.X - camera.Local.X);
        double dy = (double)(Sector.Y - camera.Sector.Y) * SectorSize + (Local.Y - camera.Local.Y);
        double dz = (double)(Sector.Z - camera.Sector.Z) * SectorSize + (Local.Z - camera.Local.Z);
        return new Vector3D<float>((float)dx, (float)dy, (float)dz);
    }

    /// <summary>Exact double-precision delta in meters from <paramref name="other"/> to this.</summary>
    public Vector3D<double> DeltaMeters(in UniversePosition other)
    {
        return new Vector3D<double>(
            (double)(Sector.X - other.Sector.X) * SectorSize + (Local.X - other.Local.X),
            (double)(Sector.Y - other.Sector.Y) * SectorSize + (Local.Y - other.Local.Y),
            (double)(Sector.Z - other.Sector.Z) * SectorSize + (Local.Z - other.Local.Z));
    }

    /// <summary>Distance in meters between two positions.</summary>
    public double DistanceTo(in UniversePosition other)
    {
        var d = DeltaMeters(other);
        return Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
    }

    /// <summary>Square of the distance in meters (cheaper; good for comparisons).</summary>
    public double DistanceSquaredTo(in UniversePosition other)
    {
        var d = DeltaMeters(other);
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
    }

    /// <summary>Return a new position translated by a meter delta (auto-normalized).</summary>
    public UniversePosition Translated(Vector3D<double> deltaMeters)
    {
        var p = new UniversePosition(Sector, Local + deltaMeters);
        p.Normalize();
        return p;
    }

    /// <summary>Translate this position in place by a meter delta (auto-normalized).</summary>
    public void Translate(Vector3D<double> deltaMeters)
    {
        Local += deltaMeters;
        Normalize();
    }

    public bool Equals(UniversePosition other) => Sector.Equals(other.Sector) && Local.Equals(other.Local);
    public override bool Equals(object? obj) => obj is UniversePosition o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Sector, Local);
    public override string ToString() => $"Sector{Sector} + {Local} m";
}
