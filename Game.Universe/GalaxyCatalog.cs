using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// One block of the universe's galaxy lattice: a fixed cube of procedurally placed galaxies with
/// stable ids — the universe-level analogue of <see cref="StarCatalog"/>. Space is partitioned into a
/// regular lattice of these huge cubes; block <c>(bx,by,bz)</c> spans <c>[blockCoord×side,
/// blockCoord×side + side]</c> and is seeded from its coordinate, so the whole universe is
/// deterministic and any block can be regenerated on demand without being stored.
///
/// The block containing the universe origin always hosts the <b>Milky Way</b> at galaxy index 0,
/// positioned at the origin, so the player starts inside it (matching the home view's
/// galactic-centre black hole at the origin).
/// </summary>
public sealed class GalaxyCatalog
{
    /// <summary>Edge length of every lattice block, in light-years. 20 million ly — at the field
    /// density a block then holds a handful of galaxies, each millions of light-years apart.</summary>
    public const double BlockSideLy = 2.0e7;

    private readonly Galaxy[] _galaxies;

    /// <summary>This block's lattice coordinate (integer cube index per axis).</summary>
    public Vector3D<long> BlockCoord { get; }
    /// <summary>The block's minimum corner (the cube spans [Origin, Origin + SideMeters] per axis).</summary>
    public UniversePosition Origin { get; }
    public double SideMeters { get; }
    public IReadOnlyList<Galaxy> Galaxies => _galaxies;
    public int Count => _galaxies.Length;

    /// <summary>Generate the block at <paramref name="blockCoord"/> of the universe lattice.</summary>
    public GalaxyCatalog(GalaxyField field, Vector3D<long> blockCoord)
    {
        BlockCoord = blockCoord;
        SideMeters = BlockSideLy * MathUtil.LightYear;
        double side = SideMeters;
        Origin = UniversePosition.FromMeters(blockCoord.X * side, blockCoord.Y * side, blockCoord.Z * side);
        _galaxies = Generate(field, blockCoord, Origin, side);
    }

    private static bool IsHomeBlock(Vector3D<long> block) =>
        block.X == 0 && block.Y == 0 && block.Z == 0;

    private static Galaxy[] Generate(GalaxyField field, Vector3D<long> block, in UniversePosition origin, double side)
    {
        // Seed from the block coordinate so each block is independent yet reproducible.
        ulong seed = Hashing.HashCell(block.X, block.Y, block.Z, field.WorldSeed);
        var rng = new DeterministicRng(Hashing.Combine(seed, 0x6A1A899UL)); // "GALAXY"

        // Expected count from the field density at the block centre (Poisson), capped to the id layout.
        Vector3D<double> center = origin.DeltaMeters(UniversePosition.Origin) + new Vector3D<double>(side * 0.5);
        double volumeLy3 = BlockSideLy * BlockSideLy * BlockSideLy;
        double lambda = field.DensityAt(center) * volumeLy3;

        bool home = IsHomeBlock(block);
        int extra = Math.Min(rng.Poisson(lambda), GalaxyId.MaxLocal - 1);
        int count = extra + (home ? 1 : 0);

        var galaxies = new Galaxy[count];
        int idx = 0;
        if (home)
        {
            galaxies[0] = MakeMilkyWay(field.WorldSeed);
            idx = 1;
        }
        for (; idx < count; idx++)
        {
            UniversePosition pos = origin.Translated(new Vector3D<double>(
                rng.Range(0, side), rng.Range(0, side), rng.Range(0, side)));
            galaxies[idx] = SampleGalaxy(ref rng, GalaxyId.Pack(block, idx), pos);
        }
        return galaxies;
    }

    /// <summary>The home galaxy: a spiral at the universe origin, disk in the world XZ plane (matching
    /// <see cref="GalaxyModel"/>), seeded from the world seed so its internal stars tie to the existing
    /// catalog. It is block (0,0,0) local 0 — a fixed, stable id.</summary>
    private static Galaxy MakeMilkyWay(ulong worldSeed) => new(
        id: GalaxyId.Pack(default, 0),
        center: UniversePosition.Origin,
        type: GalaxyType.Spiral,
        seed: worldSeed,
        radiusMeters: 5.0e4 * MathUtil.LightYear,        // ~50,000 ly disk radius
        diskNormal: new Vector3D<float>(0f, 1f, 0f),     // disk lies in XZ; Y is the thin axis
        starCount: 2.0e11,                                // ~200 billion stars
        color: new Vector3D<float>(0.86f, 0.88f, 1.0f),  // slightly blue-white
        name: "Milky Way");

    /// <summary>Sample a random galaxy's morphology, size, orientation and colour. Phase-0 placeholder
    /// distributions — broadly plausible, refined alongside the renderer in later phases.</summary>
    private static Galaxy SampleGalaxy(ref DeterministicRng rng, ulong id, in UniversePosition pos)
    {
        double u = rng.NextDouble();
        GalaxyType type;
        double radiusLy;
        Vector3D<float> color;
        if (u < 0.55) // spiral
        {
            type = GalaxyType.Spiral;
            radiusLy = rng.Range(3.0e4, 7.0e4);
            color = new Vector3D<float>(0.80f, 0.86f, 1.0f);
        }
        else if (u < 0.80) // elliptical
        {
            type = GalaxyType.Elliptical;
            radiusLy = rng.Range(3.0e4, 1.2e5);
            color = new Vector3D<float>(1.0f, 0.92f, 0.78f);
        }
        else if (u < 0.95) // irregular
        {
            type = GalaxyType.Irregular;
            radiusLy = rng.Range(1.0e4, 3.0e4);
            color = new Vector3D<float>(0.78f, 0.84f, 1.0f);
        }
        else // dwarf
        {
            type = GalaxyType.Dwarf;
            radiusLy = rng.Range(3.0e3, 1.0e4);
            color = new Vector3D<float>(1.0f, 0.86f, 0.74f);
        }

        // Star count scales roughly with volume; coarse, just for budgeting/UI.
        double rRel = radiusLy / 5.0e4;
        double starCount = 2.0e11 * rRel * rRel * rRel;

        return new Galaxy(id, pos, type, Hashing.Combine(id, 0x5EEDUL),
            radiusLy * MathUtil.LightYear, RandomUnit(ref rng), starCount, color, Naming.GalaxyName(id));
    }

    /// <summary>A deterministic unit vector, uniform on the sphere.</summary>
    private static Vector3D<float> RandomUnit(ref DeterministicRng rng)
    {
        double z = rng.Range(-1.0, 1.0);
        double phi = rng.Range(0.0, 2.0 * Math.PI);
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
        return new Vector3D<float>((float)(r * Math.Cos(phi)), (float)z, (float)(r * Math.Sin(phi)));
    }
}
