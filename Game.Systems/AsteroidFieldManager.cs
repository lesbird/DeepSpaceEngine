using System.Collections.Generic;
using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;

namespace Game.Systems;

/// <summary>
/// Streams deep-space <see cref="AsteroidField"/> clusters around the camera, mirroring
/// <see cref="StarField"/>'s coarse-cell scheme: each spatial cell deterministically rolls for at
/// most one cluster, generated on demand and cached, with distant cells evicted. Clusters are far
/// rarer and far smaller than star cells, so the grid here is fine-grained (~0.5 ly).
/// </summary>
public sealed class AsteroidFieldManager
{
    /// <summary>Cell edge in sectors (AU). 32768 AU ≈ 0.518 light-years.</summary>
    public const long CellSizeSectors = 32_768;
    private static readonly double CellSizeMeters = CellSizeSectors * UniversePosition.SectorSize;

    /// <summary>Chance that any given cell contains a cluster.</summary>
    private const double ClusterProbability = 0.2;
    private const int MaxCachedCells = 8_000;

    private readonly ulong _worldSeed;
    private readonly Dictionary<Vector3D<long>, AsteroidField?> _cache = new();
    private readonly List<Vector3D<long>> _evictScratch = new();

    private Vector3D<long> _lastCamCell;
    private bool _hasLastCell;

    /// <summary>Clusters whose centre is within the current render bubble (rebuilt each Update).</summary>
    public readonly List<AsteroidField> Visible = new();

    public AsteroidFieldManager(ulong worldSeed) => _worldSeed = worldSeed;

    private static Vector3D<long> CellOf(in UniversePosition pos) => new(
        MathUtil.FloorDiv(pos.Sector.X, CellSizeSectors),
        MathUtil.FloorDiv(pos.Sector.Y, CellSizeSectors),
        MathUtil.FloorDiv(pos.Sector.Z, CellSizeSectors));

    private AsteroidField? GetOrGenerate(Vector3D<long> cell)
    {
        if (_cache.TryGetValue(cell, out AsteroidField? cached)) return cached;

        ulong seed = Hashing.HashCell(cell.X, cell.Y, cell.Z, _worldSeed);
        var rng = new DeterministicRng(seed);
        AsteroidField? field = null;
        if (rng.NextDouble() < ClusterProbability)
        {
            var sector = new Vector3D<long>(
                cell.X * CellSizeSectors + rng.RangeLong(CellSizeSectors),
                cell.Y * CellSizeSectors + rng.RangeLong(CellSizeSectors),
                cell.Z * CellSizeSectors + rng.RangeLong(CellSizeSectors));
            var local = new Vector3D<double>(
                rng.NextDouble() * UniversePosition.SectorSize,
                rng.NextDouble() * UniversePosition.SectorSize,
                rng.NextDouble() * UniversePosition.SectorSize);
            field = AsteroidField.Generate(new UniversePosition(sector, local), seed);
        }
        _cache[cell] = field;
        return field;
    }

    /// <summary>Rebuild the visible-cluster set within <paramref name="radiusCells"/> of the camera.</summary>
    public void Update(in UniversePosition camera, int radiusCells)
    {
        Visible.Clear();
        Vector3D<long> camCell = CellOf(camera);

        // Speed gate: if the camera jumped more than the whole visible bubble since last frame, it's
        // travelling far faster than any 0.5-ly cluster could be seen or visited (e.g. intergalactic
        // warp at Mly/s). Generating a cluster costs thousands of rocks each, so 125 fresh cells/frame
        // would burn ~5 ms on the main thread for clusters that whip past invisibly. Skip until the
        // motion-per-frame drops back within reach; Visible stays empty (nothing's resolvable anyway).
        if (_hasLastCell)
        {
            long jump = Math.Max(Math.Abs(camCell.X - _lastCamCell.X),
                        Math.Max(Math.Abs(camCell.Y - _lastCamCell.Y), Math.Abs(camCell.Z - _lastCamCell.Z)));
            if (jump > radiusCells) { _lastCamCell = camCell; _hasLastCell = true; return; }
        }
        _lastCamCell = camCell;
        _hasLastCell = true;

        double bubble = (radiusCells + 1) * CellSizeMeters;

        for (long dx = -radiusCells; dx <= radiusCells; dx++)
        for (long dy = -radiusCells; dy <= radiusCells; dy++)
        for (long dz = -radiusCells; dz <= radiusCells; dz++)
        {
            var cell = new Vector3D<long>(camCell.X + dx, camCell.Y + dy, camCell.Z + dz);
            AsteroidField? field = GetOrGenerate(cell);
            if (field != null && camera.DistanceTo(field.Center) - field.BoundRadius <= bubble)
                Visible.Add(field);
        }

        EvictDistantCells(camCell, radiusCells);
    }

    /// <summary>Append all rocks from visible clusters within <paramref name="maxDist"/> of the camera.</summary>
    public void Collect(double t, in UniversePosition camera, double maxDist, List<AsteroidInstance> output)
    {
        foreach (AsteroidField f in Visible)
            f.Collect(t, camera, maxDist, output);
    }

    public int VisibleClusterCount => Visible.Count;

    /// <summary>
    /// Find the nearest cluster to <paramref name="from"/>, scanning cells out to
    /// <paramref name="searchCells"/> in each direction (used by the navigator's "jump to field").
    /// Returns false if no cell within range rolled a cluster.
    /// </summary>
    public bool TryFindNearest(in UniversePosition from, int searchCells, out AsteroidField nearest)
    {
        nearest = null!;
        double bestSq = double.MaxValue;
        Vector3D<long> camCell = CellOf(from);
        for (long dx = -searchCells; dx <= searchCells; dx++)
        for (long dy = -searchCells; dy <= searchCells; dy++)
        for (long dz = -searchCells; dz <= searchCells; dz++)
        {
            AsteroidField? f = GetOrGenerate(new Vector3D<long>(camCell.X + dx, camCell.Y + dy, camCell.Z + dz));
            if (f == null) continue;
            double dsq = f.Center.DistanceSquaredTo(from);
            if (dsq < bestSq) { bestSq = dsq; nearest = f; }
        }
        return nearest != null;
    }

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
}
