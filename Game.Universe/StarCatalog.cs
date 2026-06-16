using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A finite, pre-generated block of stars with stable integer indices: star #N is
/// <c>Stars[N]</c>, forever. Unlike the infinite per-cell <see cref="StarField"/>, the
/// whole block is generated once at startup and never evicted, so a human-meaningful
/// catalog number can address a specific star and we can fly straight to it.
///
/// Because positions are fixed, a uniform spatial grid built once answers "stars near the
/// camera" cheaply (no per-frame regeneration). The block is a cube centred on
/// <see cref="Origin"/>, sized from the galaxy's stellar density so neighbours sit a few
/// light-years apart. A later milestone tiles several blocks and pages them by distance;
/// the index then becomes (blockId, localIndex), but within a block it stays stable.
/// </summary>
public sealed class StarCatalog : INearestStar
{
    public const int DefaultCount = 1_000_000;

    private readonly Star[] _stars;

    // Uniform spatial grid over the cube, stored CSR-style: _cellItems is star indices
    // grouped by cell; _cellStart[c].._cellStart[c+1] is cell c's slice.
    private readonly int _gridDim;
    private readonly double _cellMeters;
    private readonly double _halfMeters;
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;

    /// <summary>The block's centre (the cube spans ±<see cref="SideMeters"/>/2 about it).</summary>
    public UniversePosition Origin { get; }
    public double SideMeters { get; }
    public IReadOnlyList<Star> Stars => _stars;
    public int Count => _stars.Length;

    /// <summary>Stars within the track radius of the camera (rebuilt each <see cref="Update"/>).</summary>
    public readonly List<Star> Visible = new();

    public bool HasNearest { get; private set; }
    public Star Nearest { get; private set; }
    public double NearestDistanceMeters { get; private set; }

    public StarCatalog(GalaxyModel galaxy, int count = DefaultCount)
        : this(galaxy, UniversePosition.Origin, count) { }

    public StarCatalog(GalaxyModel galaxy, in UniversePosition origin, int count)
    {
        Origin = origin;

        // Size the cube so `count` stars at the galaxy's neighbourhood density fill it; that
        // yields a realistic mean nearest-neighbour spacing of a few light-years.
        double sideLy = Math.Cbrt(count / Math.Max(galaxy.BaseDensity, 1e-6));
        SideMeters = sideLy * MathUtil.LightYear;
        _halfMeters = SideMeters * 0.5;

        _stars = Generate(galaxy, count, origin, _halfMeters);

        // Grid cells ~6 ly across: a handful of stars each, so a ~25 ly query touches a small
        // neighbourhood of cells.
        _cellMeters = 6.0 * MathUtil.LightYear;
        _gridDim = Math.Max(1, (int)Math.Ceiling(SideMeters / _cellMeters));
        (_cellStart, _cellItems) = BuildGrid(_stars, origin, _gridDim, _cellMeters, _halfMeters);
    }

    private static Star[] Generate(GalaxyModel galaxy, int count, in UniversePosition origin, double half)
    {
        var rng = new DeterministicRng(Hashing.Combine(galaxy.WorldSeed, 0xCA7A106UL));
        var stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            // Uniform placement in the cube. At neighbourhood density this is a Poisson process,
            // so stars naturally land light-years apart and effectively never overlap.
            double x = rng.Range(-half, half);
            double y = rng.Range(-half, half);
            double z = rng.Range(-half, half);
            UniversePosition pos = origin.Translated(new Vector3D<double>(x, y, z));

            StarField.SampleStarType(ref rng, out float temp, out float lum, out float mass,
                out double radius, out SpectralClass cls);

            // Id == catalog index: the number the player types. Downstream system/belt seeds
            // run it through Mix64, so sequential indices still produce well-varied systems.
            stars[i] = new Star((ulong)i, pos, temp, lum, mass, radius, cls);
        }
        return stars;
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

    public bool TryGet(int index, out Star star)
    {
        if ((uint)index < (uint)_stars.Length) { star = _stars[index]; return true; }
        star = default;
        return false;
    }

    /// <summary>
    /// Rebuild the nearby-star set within <paramref name="trackRadiusMeters"/> of the camera
    /// and track the nearest. The full block is always drawn; this only feeds HUD labels,
    /// system spawning, and the proximity speed limit.
    /// </summary>
    public void Update(in UniversePosition camera, double trackRadiusMeters)
    {
        Visible.Clear();
        HasNearest = false;
        double bestSq = double.MaxValue;

        Vector3D<double> camRel = camera.DeltaMeters(Origin);
        int ccx = Axis(camRel.X, _gridDim, _cellMeters, _halfMeters);
        int ccy = Axis(camRel.Y, _gridDim, _cellMeters, _halfMeters);
        int ccz = Axis(camRel.Z, _gridDim, _cellMeters, _halfMeters);
        int reach = (int)Math.Ceiling(trackRadiusMeters / _cellMeters);
        double radiusSq = trackRadiusMeters * trackRadiusMeters;

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
                        Visible.Add(s);
                        if (d2 < bestSq) { bestSq = d2; Nearest = s; HasNearest = true; }
                    }
                }
            }
        }

        NearestDistanceMeters = HasNearest ? Math.Sqrt(bestSq) : double.MaxValue;
    }
}
