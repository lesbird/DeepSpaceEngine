using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>Broad morphological class of a galaxy.</summary>
public enum GalaxyType { Spiral, Elliptical, Irregular, Dwarf }

/// <summary>
/// A procedurally generated galaxy — the universe-level analogue of <see cref="Star"/>. Every field
/// is derived deterministically from the galaxy's <see cref="Id"/> (and its lattice block), so a
/// galaxy is never stored, only regenerated. The galaxy owns the <see cref="Seed"/> that will drive
/// its internal star field (wired in a later phase), its disk orientation, and the shape parameters a
/// renderer needs to draw it at any LOD (point → impostor → cloud → resolved).
/// </summary>
public readonly struct Galaxy
{
    public readonly ulong Id;
    public readonly UniversePosition Center;
    public readonly GalaxyType Type;

    /// <summary>Seed for this galaxy's own internal generation (its star lattice, SMBH, …).</summary>
    public readonly ulong Seed;

    /// <summary>Half-extent of the luminous galaxy, in metres (disk/halo radius).</summary>
    public readonly double RadiusMeters;

    /// <summary>Unit normal of the disk plane (for spirals; arbitrary-but-stable for the others).</summary>
    public readonly Vector3D<float> DiskNormal;

    /// <summary>Approximate number of stars — for LOD budgeting and UI.</summary>
    public readonly double StarCount;

    /// <summary>Integrated visual colour (bluer for spirals/irregulars, warmer for ellipticals).</summary>
    public readonly Vector3D<float> Color;

    /// <summary>Display name ("Milky Way" for the home galaxy; a designation otherwise).</summary>
    public readonly string Name;

    public Galaxy(ulong id, UniversePosition center, GalaxyType type, ulong seed,
        double radiusMeters, Vector3D<float> diskNormal, double starCount,
        Vector3D<float> color, string name)
    {
        Id = id;
        Center = center;
        Type = type;
        Seed = seed;
        RadiusMeters = radiusMeters;
        DiskNormal = diskNormal;
        StarCount = starCount;
        Color = color;
        Name = name;
    }

    /// <summary>Disk/halo radius in light-years.</summary>
    public double RadiusLy => RadiusMeters / MathUtil.LightYear;
}
