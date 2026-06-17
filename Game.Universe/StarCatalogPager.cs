using System.Collections.Concurrent;
using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Streams the star lattice: keeps the <see cref="StarCatalog"/> blocks near the camera resident and
/// lets distant ones go, so the catalog is effectively unbounded (billions of stars) while only a
/// small working set is ever in memory. Blocks are generated off the main thread and integrated as
/// they finish; the camera's own block is generated synchronously so the field is never empty.
///
/// It is the single <see cref="INearestStar"/> the rest of the game talks to: each frame
/// <see cref="Update"/> rebuilds the nearby-star set and the nearest star across all loaded blocks.
/// A block's stars keep stable global ids (<see cref="StarId"/>), so <see cref="TryGetStar"/> can
/// resolve any id back to its block — loading it on demand — for the "find #N" jump.
/// </summary>
public sealed class StarCatalogPager : INearestStar
{
    // Load every block within this Chebyshev (cube) radius of the camera's block; evict once a block
    // sits farther than the evict radius. evict > load gives a one-ring hysteresis band so a camera
    // hovering on a boundary doesn't thrash a block in and out.
    private const int LoadRadiusBlocks = 1;   // 3×3×3 resident around the camera
    private const int EvictRadiusBlocks = 2;

    private readonly GalaxyModel _galaxy;
    private readonly double _sideMeters;

    private readonly Dictionary<Vector3D<long>, StarCatalog> _loaded = new();
    private readonly HashSet<Vector3D<long>> _pending = new();
    private readonly ConcurrentQueue<StarCatalog> _ready = new();
    private readonly List<Vector3D<long>> _evictScratch = new();

    /// <summary>Stars within the track radius of the camera (rebuilt each <see cref="Update"/>).</summary>
    public readonly List<Star> Visible = new();

    public bool HasNearest { get; private set; }
    public Star Nearest { get; private set; }
    public double NearestDistanceMeters { get; private set; }

    /// <summary>The currently resident blocks (the renderer mirrors these into per-block GPU buffers).</summary>
    public IReadOnlyCollection<StarCatalog> LoadedBlocks => _loaded.Values;
    public int LoadedBlockCount => _loaded.Count;
    public int LoadedStarCount { get; private set; }

    /// <summary>Edge length of one lattice block (metres) — also the spacing of block centres.</summary>
    public double BlockSideMeters => _sideMeters;

    public StarCatalogPager(GalaxyModel galaxy)
    {
        _galaxy = galaxy;
        _sideMeters = StarCatalog.BlockSideLy * MathUtil.LightYear;
    }

    /// <summary>The lattice block a world position falls in (blocks are centred on lattice points).</summary>
    public Vector3D<long> BlockCoordOf(in UniversePosition pos)
    {
        Vector3D<double> m = pos.DeltaMeters(UniversePosition.Origin);
        return new Vector3D<long>(
            (long)Math.Round(m.X / _sideMeters),
            (long)Math.Round(m.Y / _sideMeters),
            (long)Math.Round(m.Z / _sideMeters));
    }

    /// <summary>
    /// Page blocks around the camera (load near, evict far), then rebuild the nearby-star set and the
    /// nearest star within <paramref name="trackRadiusMeters"/>. Cheap to call every frame.
    /// </summary>
    public void Update(in UniversePosition camera, double trackRadiusMeters)
    {
        // Integrate any blocks that finished baking since last frame.
        while (_ready.TryDequeue(out StarCatalog block))
        {
            _pending.Remove(block.BlockCoord);
            _loaded[block.BlockCoord] = block;
        }

        Vector3D<long> camBlock = BlockCoordOf(camera);

        // Never let the field go empty: the camera's own block is generated synchronously if a fast
        // jump outran the async prefetch. Neighbours stream in below.
        if (!_loaded.ContainsKey(camBlock))
        {
            _pending.Remove(camBlock);
            _loaded[camBlock] = new StarCatalog(_galaxy, camBlock);
        }

        // Request any not-yet-resident block within the load radius (off-thread generation).
        for (long dx = -LoadRadiusBlocks; dx <= LoadRadiusBlocks; dx++)
        for (long dy = -LoadRadiusBlocks; dy <= LoadRadiusBlocks; dy++)
        for (long dz = -LoadRadiusBlocks; dz <= LoadRadiusBlocks; dz++)
        {
            var coord = new Vector3D<long>(camBlock.X + dx, camBlock.Y + dy, camBlock.Z + dz);
            if (_loaded.ContainsKey(coord) || _pending.Contains(coord)) continue;
            _pending.Add(coord);
            Vector3D<long> c = coord;
            Task.Run(() => _ready.Enqueue(new StarCatalog(_galaxy, c)));
        }

        // Evict blocks that have drifted past the evict radius (Chebyshev distance in blocks).
        _evictScratch.Clear();
        foreach (Vector3D<long> coord in _loaded.Keys)
        {
            long cheb = Math.Max(Math.Abs(coord.X - camBlock.X),
                        Math.Max(Math.Abs(coord.Y - camBlock.Y), Math.Abs(coord.Z - camBlock.Z)));
            if (cheb > EvictRadiusBlocks) _evictScratch.Add(coord);
        }
        foreach (Vector3D<long> coord in _evictScratch) _loaded.Remove(coord);

        // Rebuild the nearby set + nearest across all resident blocks.
        Visible.Clear();
        Star nearest = default;
        bool hasNearest = false;
        double bestSq = double.MaxValue;
        int starTotal = 0;
        foreach (StarCatalog block in _loaded.Values)
        {
            starTotal += block.Count;
            block.Collect(camera, trackRadiusMeters, Visible, ref bestSq, ref nearest, ref hasNearest);
        }
        Nearest = nearest;
        HasNearest = hasNearest;
        LoadedStarCount = starTotal;
        NearestDistanceMeters = hasNearest ? Math.Sqrt(bestSq) : double.MaxValue;
    }

    /// <summary>Resolve a global star id to its star, loading (and keeping) its block if needed. Used
    /// by the catalog search to jump to any star in the universe, not just resident ones.</summary>
    public bool TryGetStar(ulong id, out Star star)
    {
        StarId.Unpack(id, out Vector3D<long> block, out int local);
        StarCatalog cat = EnsureLoaded(block);
        return cat.TryGetLocal(local, out star);
    }

    /// <summary>Get the block at <paramref name="coord"/>, generating it synchronously if not resident.</summary>
    public StarCatalog EnsureLoaded(Vector3D<long> coord)
    {
        if (_loaded.TryGetValue(coord, out StarCatalog? cat)) return cat;
        cat = new StarCatalog(_galaxy, coord);
        _pending.Remove(coord);
        _loaded[coord] = cat;
        return cat;
    }
}
