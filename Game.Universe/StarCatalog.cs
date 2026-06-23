using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// One block of the tiled star lattice: a fixed-size cube of pre-generated stars with stable indices.
/// Space is partitioned into a regular lattice of these cubes (see <see cref="StarCatalogPager"/>);
/// block <c>(bx,by,bz)</c> is centred at <c>blockCoord × side</c> and seeded from its coordinate, so
/// the whole infinite field is deterministic and any block can be (re)generated on demand without
/// storing it. A star's global id packs its block and its local index (<see cref="StarId"/>): within a
/// block the index is stable forever, and the home block (0,0,0) keeps ids 0..Count-1.
///
/// Positions are fixed, so a uniform spatial grid built once answers "stars near a point" cheaply
/// (<see cref="Collect"/>). The block size derives from the galaxy's neighbourhood density so a block
/// holds a realistic number of stars a few light-years apart.
/// </summary>
public sealed class StarCatalog
{
    /// <summary>Edge length of every lattice block, in light-years. With the default neighbourhood
    /// density (~0.006 stars/ly³) a block then holds a couple hundred thousand stars.</summary>
    public const double BlockSideLy = 350.0;

    private readonly Star[] _stars;

    // Uniform spatial grid over the cube, stored CSR-style: _cellItems is star indices grouped by
    // cell; _cellStart[c].._cellStart[c+1] is cell c's slice.
    private readonly int _gridDim;
    private readonly double _cellMeters;
    private readonly double _halfMeters;
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;

    /// <summary>This block's lattice coordinate (integer cube index per axis).</summary>
    public Vector3D<long> BlockCoord { get; }
    /// <summary>The block's centre (the cube spans ±<see cref="SideMeters"/>/2 about it).</summary>
    public UniversePosition Origin { get; }
    public double SideMeters { get; }
    public IReadOnlyList<Star> Stars => _stars;
    public int Count => _stars.Length;

    /// <summary>Generate the block at <paramref name="blockCoord"/> of the lattice, populated from the
    /// density of the <paramref name="galaxy"/> that overlaps it. Stars are sampled across the block
    /// and kept with probability proportional to the galaxy's density at their position, so the disk,
    /// arms and edge fall-off all emerge and a block beyond the galaxy's reach ends up empty.</summary>
    public StarCatalog(Galaxy galaxy, Vector3D<long> blockCoord)
    {
        BlockCoord = blockCoord;
        SideMeters = BlockSideLy * MathUtil.LightYear;
        _halfMeters = SideMeters * 0.5;

        double side = SideMeters;
        Origin = UniversePosition.FromMeters(blockCoord.X * side, blockCoord.Y * side, blockCoord.Z * side);

        _stars = Generate(galaxy, blockCoord, Origin, _halfMeters);

        // Grid cells ~6 ly across: a handful of stars each, so a ~25 ly query touches a small
        // neighbourhood of cells.
        _cellMeters = 6.0 * MathUtil.LightYear;
        _gridDim = Math.Max(1, (int)Math.Ceiling(SideMeters / _cellMeters));
        (_cellStart, _cellItems) = BuildGrid(_stars, Origin, _gridDim, _cellMeters, _halfMeters);
    }

    /// <summary>An empty block: intergalactic space, where no galaxy overlaps. Carries no stars and a
    /// trivial 1-cell grid, so it's cheap to keep resident as a "checked, nothing here" marker.</summary>
    public StarCatalog(Vector3D<long> blockCoord)
    {
        BlockCoord = blockCoord;
        SideMeters = BlockSideLy * MathUtil.LightYear;
        _halfMeters = SideMeters * 0.5;
        double side = SideMeters;
        Origin = UniversePosition.FromMeters(blockCoord.X * side, blockCoord.Y * side, blockCoord.Z * side);

        _stars = Array.Empty<Star>();
        _gridDim = 1;
        _cellMeters = SideMeters;
        _cellStart = new int[] { 0, 0 };
        _cellItems = Array.Empty<int>();
    }

    private static Star[] Generate(Galaxy galaxy, Vector3D<long> block, in UniversePosition origin, double half)
    {
        GalaxyModel model = GalaxyModel.For(galaxy);

        // Sample the galaxy's density ONCE, at the block centre, and place that many stars uniformly.
        // A block (350 ly) is tiny next to a galaxy (tens of kly), so the density barely varies across
        // it — and the galaxy's macro structure (disk fall-off, arms, edge) still emerges because each
        // block's centre density differs. Sampling per-block instead of per-candidate avoids a
        // trig-heavy density evaluation on every star, so a block generates fast enough to stream in
        // without a hitch (the per-candidate version made blocks arrive late, in visible chunks).
        double density = model.DensityAtLocal(origin.DeltaMeters(galaxy.Center));
        int count = (int)Math.Round(density * BlockSideLy * BlockSideLy * BlockSideLy);
        count = Math.Clamp(count, 0, StarId.MaxLocal);
        // NB: don't early-return on count == 0 — halo blocks have no galaxy-density stars but may still
        // host a globular cluster, injected below.

        // Seed from the block coordinate AND the galaxy seed, so each galaxy's lattice is distinct.
        ulong seed = Hashing.HashCell(block.X, block.Y, block.Z, galaxy.Seed);
        var rng = new DeterministicRng(Hashing.Combine(seed, 0xCA7A106UL));

        var stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            // Uniform placement in the cube. At neighbourhood density this is a Poisson process, so
            // stars naturally land light-years apart and effectively never overlap.
            double x = rng.Range(-half, half);
            double y = rng.Range(-half, half);
            double z = rng.Range(-half, half);
            UniversePosition pos = origin.Translated(new Vector3D<double>(x, y, z));

            StarField.SampleStarType(ref rng, out float temp, out float lum, out float mass,
                out double radius, out SpectralClass cls);

            // Global id packs (block, local index): unique and deterministic, so downstream system
            // and belt seeds (which run it through Mix64) still produce well-varied systems, and the
            // "find #N" box can unpack it back to a block + index.
            stars[i] = new Star(StarId.Pack(block, i), pos, temp, lum, mass, radius, cls);
        }

        // Globular clusters in the halo become REAL catalog stars where they overlap this block — so a
        // cluster resolves into thousands of visitable stars, each with its own solar system. (Cheap to
        // skip for the common inner-disk blocks; see CollectClusterStars.)
        List<Star>? extra = CollectClusterStars(galaxy, block, origin, half, count);
        if (extra is not { Count: > 0 }) return stars;
        var all = new Star[count + extra.Count];
        Array.Copy(stars, all, count);
        extra.CopyTo(all, count);
        return all;
    }

    /// <summary>The host galaxy's globular-cluster stars that fall inside this block, as real catalog
    /// stars with continuing local ids (starting at <paramref name="startIndex"/>). Returns null when
    /// the block is well inside the disk (no clusters there) or no cluster overlaps it — the common case,
    /// kept cheap. Each cluster is generated identically in every block it touches; only the in-block
    /// stars are kept, so a star lands in exactly one block with a stable id.</summary>
    private static List<Star>? CollectClusterStars(Galaxy galaxy, Vector3D<long> block,
        in UniversePosition origin, double half, int startIndex)
    {
        // Clusters live at ≳0.3 R with radii ≤ ~100 ly, so a block deep in the disk can't touch one.
        if (origin.DistanceTo(galaxy.Center) < 0.28 * galaxy.RadiusMeters) return null;

        List<Star>? extra = null;
        foreach (GlobularCluster c in GlobularClusters.ForGalaxy(galaxy))
        {
            double r = c.RadiusMeters;
            Vector3D<double> dc = c.Center.DeltaMeters(origin); // cluster centre relative to block centre
            if (Math.Abs(dc.X) > half + r || Math.Abs(dc.Y) > half + r || Math.Abs(dc.Z) > half + r) continue;

            for (int i = 0; i < c.StarCount; i++)
            {
                // Identical per-star generation to the decorative cloud (position drawn first), so the real
                // star lands exactly where the cloud's point i was. Generate the full set regardless of
                // block so a star is reproduced identically wherever it's touched, then keep in-block ones.
                DeterministicRng srng = GlobularClusters.StarRng(c, i);
                Vector3D<double> off = GlobularClusters.StarOffset(ref srng, r);
                UniversePosition pos = c.Center.Translated(off);

                StarField.SampleStarType(ref srng, out float temp, out float lum, out float mass,
                    out double radius, out SpectralClass cls);

                Vector3D<double> rel = pos.DeltaMeters(origin);
                if (Math.Abs(rel.X) > half || Math.Abs(rel.Y) > half || Math.Abs(rel.Z) > half) continue;

                extra ??= new List<Star>(256);
                int localIndex = startIndex + extra.Count;
                if (localIndex >= StarId.MaxLocal) return extra; // respect the id layout's per-block capacity
                extra.Add(new Star(StarId.Pack(block, localIndex), pos, temp, lum, mass, radius, cls));
            }
        }
        return extra;
    }

    private static (int[] start, int[] items) BuildGrid(Star[] stars, in UniversePosition origin,
        int dim, double cellMeters, double half)
    {
        int cellCount = dim * dim * dim;
        var start = new int[cellCount + 1];
        var cellOf = new int[stars.Length];

        for (int i = 0; i < stars.Length; i++)
        {
            Vector3D<double> rel = stars[i].Position.DeltaMeters(origin);
            int ci = CellIndex(rel, dim, cellMeters, half);
            cellOf[i] = ci;
            start[ci + 1]++;
        }
        for (int c = 0; c < cellCount; c++) start[c + 1] += start[c];

        var items = new int[stars.Length];
        var cursor = (int[])start.Clone();
        for (int i = 0; i < stars.Length; i++)
            items[cursor[cellOf[i]]++] = i;

        return (start, items);
    }

    private static int Axis(double meters, int dim, double cellMeters, double half)
        => Math.Clamp((int)Math.Floor((meters + half) / cellMeters), 0, dim - 1);

    private static int CellIndex(Vector3D<double> rel, int dim, double cellMeters, double half)
    {
        int cx = Axis(rel.X, dim, cellMeters, half);
        int cy = Axis(rel.Y, dim, cellMeters, half);
        int cz = Axis(rel.Z, dim, cellMeters, half);
        return (cx * dim + cy) * dim + cz;
    }

    /// <summary>Fetch a star by its local index within this block (the low bits of its global id).</summary>
    public bool TryGetLocal(int localIndex, out Star star)
    {
        if ((uint)localIndex < (uint)_stars.Length) { star = _stars[localIndex]; return true; }
        star = default;
        return false;
    }

    /// <summary>
    /// Append every star within <paramref name="radiusMeters"/> of <paramref name="camera"/> to
    /// <paramref name="visible"/>, and fold this block's nearest such star into the running
    /// (<paramref name="bestSq"/>, <paramref name="nearest"/>, <paramref name="hasNearest"/>) so the
    /// pager can track the nearest across all loaded blocks. Touches only the grid cells the query
    /// sphere overlaps, so it stays cheap however many blocks are resident.
    /// </summary>
    public void Collect(in UniversePosition camera, double radiusMeters, List<Star> visible,
        ref double bestSq, ref Star nearest, ref bool hasNearest)
    {
        // The query sphere may not touch this block at all (it's one of several loaded); the grid
        // clamp below already restricts the scan, and the per-star distance test rejects the rest.
        double radiusSq = radiusMeters * radiusMeters;
        Vector3D<double> camRel = camera.DeltaMeters(Origin);

        int ccx = Axis(camRel.X, _gridDim, _cellMeters, _halfMeters);
        int ccy = Axis(camRel.Y, _gridDim, _cellMeters, _halfMeters);
        int ccz = Axis(camRel.Z, _gridDim, _cellMeters, _halfMeters);
        int reach = (int)Math.Ceiling(radiusMeters / _cellMeters);

        for (int dx = -reach; dx <= reach; dx++)
        {
            int cx = ccx + dx;
            if (cx < 0 || cx >= _gridDim) continue;
            for (int dy = -reach; dy <= reach; dy++)
            {
                int cy = ccy + dy;
                if (cy < 0 || cy >= _gridDim) continue;
                for (int dz = -reach; dz <= reach; dz++)
                {
                    int cz = ccz + dz;
                    if (cz < 0 || cz >= _gridDim) continue;

                    int ci = (cx * _gridDim + cy) * _gridDim + cz;
                    for (int k = _cellStart[ci]; k < _cellStart[ci + 1]; k++)
                    {
                        Star s = _stars[_cellItems[k]];
                        double d2 = s.Position.DistanceSquaredTo(camera);
                        if (d2 > radiusSq) continue;
                        visible.Add(s);
                        if (d2 < bestSq) { bestSq = d2; nearest = s; hasNearest = true; }
                    }
                }
            }
        }
    }
}
