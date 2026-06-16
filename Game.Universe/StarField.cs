using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// The procedural star field: an infinite, deterministic catalog of stars addressed by
/// a coarse spatial cell grid. Stars are generated on demand from a cell's hash and
/// cached; cells far from the camera are evicted. Nothing is ever persisted.
///
/// Cells are aligned to an integer number of sectors (AU), so a star's
/// <see cref="UniversePosition"/> is built from exact integer sector indices plus a
/// sub-AU double — full precision anywhere in the universe, never routed through a
/// giant absolute double.
/// </summary>
public sealed class StarField : INearestStar
{
    /// <summary>Cell edge length in sectors (AU). 262144 AU ≈ 4.146 light-years.</summary>
    public const long CellSizeSectors = 262_144;

    public static readonly double CellSizeMeters = CellSizeSectors * UniversePosition.SectorSize;
    public static readonly double CellSizeLy = CellSizeMeters / MathUtil.LightYear;
    private static readonly double CellVolumeLy3 = CellSizeLy * CellSizeLy * CellSizeLy;

    private const int MaxStarsPerCell = 4096;
    // Must comfortably exceed the working set of one render bubble (the keep region is
    // (2·(radiusCells+2)+1)³ cells) or cells inside the bubble get evicted and regenerated every
    // frame. 80k holds up to radiusCells ≈ 16 ((2·18+1)³ = 50653) with headroom.
    private const int MaxCachedCells = 80_000;

    private readonly GalaxyModel _galaxy;
    private readonly Dictionary<Vector3D<long>, Star[]> _cache = new();
    private readonly List<Vector3D<long>> _evictScratch = new();

    /// <summary>Stars within the current render bubble (rebuilt each Update).</summary>
    public readonly List<Star> Visible = new();

    public bool HasNearest { get; private set; }
    public Star Nearest { get; private set; }
    public double NearestDistanceMeters { get; private set; }

    public GalaxyModel Galaxy => _galaxy;

    public StarField(GalaxyModel galaxy) => _galaxy = galaxy;

    /// <summary>Which cell a position falls in.</summary>
    public static Vector3D<long> CellOf(in UniversePosition pos) => new(
        MathUtil.FloorDiv(pos.Sector.X, CellSizeSectors),
        MathUtil.FloorDiv(pos.Sector.Y, CellSizeSectors),
        MathUtil.FloorDiv(pos.Sector.Z, CellSizeSectors));

    /// <summary>Generate (or fetch cached) stars for a cell. Deterministic.</summary>
    public Star[] GetOrGenerate(Vector3D<long> cell)
    {
        if (_cache.TryGetValue(cell, out var cached))
            return cached;

        var stars = GenerateCell(cell);
        _cache[cell] = stars;
        return stars;
    }

    private Star[] GenerateCell(Vector3D<long> cell)
    {
        ulong seed = Hashing.HashCell(cell.X, cell.Y, cell.Z, _galaxy.WorldSeed);
        var rng = new DeterministicRng(seed);

        // Expected star count from the galaxy density at the cell centre.
        var center = new Vector3D<double>(
            (cell.X + 0.5) * CellSizeMeters,
            (cell.Y + 0.5) * CellSizeMeters,
            (cell.Z + 0.5) * CellSizeMeters);
        double lambda = _galaxy.DensityAt(center) * CellVolumeLy3;

        int count = Math.Min(rng.Poisson(lambda), MaxStarsPerCell);
        if (count <= 0) return Array.Empty<Star>();

        var stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            var sector = new Vector3D<long>(
                cell.X * CellSizeSectors + rng.RangeLong(CellSizeSectors),
                cell.Y * CellSizeSectors + rng.RangeLong(CellSizeSectors),
                cell.Z * CellSizeSectors + rng.RangeLong(CellSizeSectors));
            var local = new Vector3D<double>(
                rng.NextDouble() * UniversePosition.SectorSize,
                rng.NextDouble() * UniversePosition.SectorSize,
                rng.NextDouble() * UniversePosition.SectorSize);

            ulong id = Hashing.Combine(seed, (ulong)i);
            SampleStarType(ref rng, out float temp, out float lum, out float mass, out double radius, out SpectralClass cls);
            stars[i] = new Star(id, new UniversePosition(sector, local), temp, lum, mass, radius, cls);
        }
        return stars;
    }

    internal static void SampleStarType(ref DeterministicRng rng, out float temp, out float lum,
        out float mass, out double radiusMeters, out SpectralClass cls)
    {
        double u = rng.NextDouble();
        double rSun; // radius in solar radii
        if (u < 0.74) { cls = SpectralClass.M; temp = (float)rng.Range(2400, 3700); lum = (float)rng.Range(0.0008, 0.07); mass = (float)rng.Range(0.10, 0.55); rSun = rng.Range(0.18, 0.6); }
        else if (u < 0.86) { cls = SpectralClass.K; temp = (float)rng.Range(3700, 5200); lum = (float)rng.Range(0.08, 0.6); mass = (float)rng.Range(0.55, 0.85); rSun = rng.Range(0.6, 0.9); }
        else if (u < 0.94) { cls = SpectralClass.G; temp = (float)rng.Range(5200, 6000); lum = (float)rng.Range(0.6, 1.5); mass = (float)rng.Range(0.85, 1.1); rSun = rng.Range(0.9, 1.15); }
        else if (u < 0.975) { cls = SpectralClass.F; temp = (float)rng.Range(6000, 7500); lum = (float)rng.Range(1.5, 5); mass = (float)rng.Range(1.1, 1.6); rSun = rng.Range(1.15, 1.5); }
        else if (u < 0.992) { cls = SpectralClass.A; temp = (float)rng.Range(7500, 10000); lum = (float)rng.Range(5, 25); mass = (float)rng.Range(1.6, 2.5); rSun = rng.Range(1.5, 2.5); }
        else if (u < 0.999) { cls = SpectralClass.B; temp = (float)rng.Range(10000, 20000); lum = (float)rng.Range(25, 150); mass = (float)rng.Range(2.5, 12); rSun = rng.Range(2.5, 6); }
        else { cls = SpectralClass.O; temp = (float)rng.Range(20000, 38000); lum = (float)rng.Range(150, 1000); mass = (float)rng.Range(12, 40); rSun = rng.Range(6, 12); }

        radiusMeters = rSun * MathUtil.SolarRadiusM;
    }

    /// <summary>
    /// Rebuild the visible-star set within <paramref name="radiusCells"/> of the camera,
    /// track the nearest star, and evict distant cached cells.
    /// </summary>
    public void Update(in UniversePosition camera, int radiusCells)
    {
        Visible.Clear();
        HasNearest = false;
        NearestDistanceMeters = double.MaxValue;

        Vector3D<long> camCell = CellOf(camera);
        double bubbleMeters = (radiusCells + 0.5) * CellSizeMeters;
        double bubbleSq = bubbleMeters * bubbleMeters;

        for (long dx = -radiusCells; dx <= radiusCells; dx++)
        for (long dy = -radiusCells; dy <= radiusCells; dy++)
        for (long dz = -radiusCells; dz <= radiusCells; dz++)
        {
            var cell = new Vector3D<long>(camCell.X + dx, camCell.Y + dy, camCell.Z + dz);
            Star[] stars = GetOrGenerate(cell);
            for (int i = 0; i < stars.Length; i++)
            {
                double distSq = stars[i].Position.DistanceSquaredTo(camera);
                if (distSq > bubbleSq) continue;
                Visible.Add(stars[i]);
                if (!HasNearest || distSq < _nearestSq)
                {
                    _nearestSq = distSq;
                    Nearest = stars[i];
                    HasNearest = true;
                }
            }
        }

        NearestDistanceMeters = HasNearest ? Math.Sqrt(_nearestSq) : double.MaxValue;
        EvictDistantCells(camCell, radiusCells);
    }

    private double _nearestSq;

    private void EvictDistantCells(Vector3D<long> camCell, int radiusCells)
    {
        if (_cache.Count <= MaxCachedCells) return;

        long keep = radiusCells + 2;
        _evictScratch.Clear();
        foreach (var key in _cache.Keys)
        {
            long cheby = Math.Max(Math.Abs(key.X - camCell.X),
                         Math.Max(Math.Abs(key.Y - camCell.Y), Math.Abs(key.Z - camCell.Z)));
            if (cheby > keep) _evictScratch.Add(key);
        }
        foreach (var key in _evictScratch) _cache.Remove(key);
    }

    public int CachedCellCount => _cache.Count;
}
