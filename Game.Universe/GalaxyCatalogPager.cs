using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Streams the universe's galaxy lattice: keeps the <see cref="GalaxyCatalog"/> blocks near the camera
/// resident and lets distant ones go — the universe-level analogue of <see cref="StarCatalogPager"/>.
/// Galaxies are sparse and the blocks are enormous (tens of millions of light-years), so generation is
/// cheap and done synchronously; a linear scan over the few resident galaxies answers "nearest" and
/// "which galaxy am I inside".
///
/// It is the single <see cref="INearestGalaxy"/> the rest of the game talks to: each frame
/// <see cref="Update"/> rebuilds the nearest galaxy and the containing galaxy (if any).
/// </summary>
public sealed class GalaxyCatalogPager : INearestGalaxy
{
    // Load every block within this Chebyshev (cube) radius of the camera's block; evict past the evict
    // radius. evict > load gives a one-ring hysteresis band so a camera on a boundary doesn't thrash.
    // The nearest a block can pop in/out is (LoadRadius-1) blocks ≈ 60 Mly — beyond where GalaxyRenderer
    // fades the far point sprites fully out (~56 Mly), so galaxies never snap on/off at a block boundary.
    // Radius 4 keeps a deep field of distant galaxies resident (visible as fuzzy dots out to ~56 Mly).
    // Galaxies are cheap structs (only the nearest few get clouds/impostors), so the 9×9×9 ring is cheap.
    private const int LoadRadiusBlocks = 4;   // 9×9×9 resident around the camera — a deep galaxy field
    private const int EvictRadiusBlocks = 5;

    private readonly GalaxyField _field;
    private readonly double _sideMeters;
    private readonly Dictionary<Vector3D<long>, GalaxyCatalog> _loaded = new();
    private readonly List<Vector3D<long>> _evictScratch = new();

    public bool HasNearest { get; private set; }
    public Galaxy Nearest { get; private set; }
    public double NearestDistanceMeters { get; private set; }
    public bool IsInside { get; private set; }
    public Galaxy Containing { get; private set; }

    /// <summary>The currently resident blocks (a renderer mirrors these into draw lists in a later phase).</summary>
    public IReadOnlyCollection<GalaxyCatalog> LoadedBlocks => _loaded.Values;
    public int LoadedBlockCount => _loaded.Count;
    public int LoadedGalaxyCount { get; private set; }

    /// <summary>Edge length of one lattice block (metres) — also the spacing of block coordinates.</summary>
    public double BlockSideMeters => _sideMeters;

    public GalaxyCatalogPager(GalaxyField field)
    {
        _field = field;
        _sideMeters = GalaxyCatalog.BlockSideLy * MathUtil.LightYear;
    }

    /// <summary>The lattice block a world position falls in (blocks span [coord×side, coord×side+side)).</summary>
    public Vector3D<long> BlockCoordOf(in UniversePosition pos)
    {
        Vector3D<double> m = pos.DeltaMeters(UniversePosition.Origin);
        return new Vector3D<long>(
            (long)Math.Floor(m.X / _sideMeters),
            (long)Math.Floor(m.Y / _sideMeters),
            (long)Math.Floor(m.Z / _sideMeters));
    }

    /// <summary>
    /// Find the resident galaxy whose volume overlaps a sphere of <paramref name="boundingRadiusMeters"/>
    /// around <paramref name="point"/> — i.e. which galaxy a star-lattice block belongs to. Returns the
    /// nearest such galaxy (overlaps are rare at true scale). False ⇒ intergalactic space (no stars).
    /// Call on the main thread; the result <see cref="Galaxy"/> is a value type, safe to hand to a
    /// background generation task.
    /// </summary>
    public bool TryGetGalaxyForBlock(in UniversePosition point, double boundingRadiusMeters, out Galaxy galaxy)
    {
        bool found = false;
        double bestSq = double.MaxValue;
        Galaxy best = default;
        foreach (GalaxyCatalog block in _loaded.Values)
            foreach (Galaxy g in block.Galaxies)
            {
                double reach = g.RadiusMeters + boundingRadiusMeters;
                double dSq = g.Center.DistanceSquaredTo(point);
                if (dSq <= reach * reach && dSq < bestSq) { bestSq = dSq; best = g; found = true; }
            }
        galaxy = best;
        return found;
    }

    /// <summary>
    /// Resolve a galaxy by its global id (unpack block + local), generating its lattice block on demand
    /// if not resident. A galaxy's <see cref="Galaxy.Center"/> is deterministic and fixed, so a course can
    /// target it from anywhere — even before its block has streamed in. False only if the local index is
    /// out of range for that block.
    /// </summary>
    public bool TryGetGalaxy(ulong id, out Galaxy galaxy)
    {
        GalaxyId.Unpack(id, out Vector3D<long> block, out int local);
        if (!_loaded.TryGetValue(block, out GalaxyCatalog? cat))
            cat = new GalaxyCatalog(_field, block); // transient; not added to the resident set
        if (local >= 0 && local < cat.Count) { galaxy = cat.Galaxies[local]; return true; }
        galaxy = default;
        return false;
    }

    /// <summary>
    /// Find the resident galaxy the camera is <i>aiming at</i> — the one whose sprite sits under the
    /// centre-screen reticle. A galaxy qualifies when the angle between <paramref name="forward"/> and
    /// the direction to it is within the galaxy's own angular radius (so anywhere on a big near disk
    /// counts) plus a small fixed cone <paramref name="toleranceRadians"/> (so a tiny distant point
    /// sprite is still selectable by centring it). Among qualifiers the most centred one wins. False ⇒
    /// the reticle isn't on any galaxy. Independent of <see cref="Nearest"/>, which is by distance.
    /// </summary>
    public bool TryGetAimedGalaxy(in UniversePosition camera, Vector3D<float> forward,
        out Galaxy galaxy, out double distanceMeters, double toleranceRadians = 0.025)
    {
        // Forward arrives unit-length from the camera; normalise defensively.
        double fLen = Math.Sqrt((double)forward.X * forward.X + (double)forward.Y * forward.Y + (double)forward.Z * forward.Z);
        if (fLen < 1e-9) { galaxy = default; distanceMeters = 0; return false; }
        double fx = forward.X / fLen, fy = forward.Y / fLen, fz = forward.Z / fLen;

        bool found = false;
        double bestCos = -2.0;
        Galaxy best = default;
        double bestDist = 0;
        foreach (GalaxyCatalog block in _loaded.Values)
            foreach (Galaxy g in block.Galaxies)
            {
                Vector3D<double> rel = g.Center.DeltaMeters(camera);
                double dist = rel.Length;
                if (dist < 1.0) continue; // sitting on the centre — no meaningful direction
                double cos = (rel.X * fx + rel.Y * fy + rel.Z * fz) / dist;
                if (cos <= 0) continue;   // behind the camera
                double angle = Math.Acos(Math.Clamp(cos, -1.0, 1.0));
                double angularRadius = Math.Atan2(g.RadiusMeters, dist);
                if (angle > angularRadius + toleranceRadians) continue; // reticle isn't on this sprite
                if (cos > bestCos) { bestCos = cos; best = g; bestDist = dist; found = true; }
            }
        galaxy = best;
        distanceMeters = bestDist;
        return found;
    }

    /// <summary>
    /// Page galaxy blocks around the camera (load near, evict far), then rebuild the nearest galaxy and
    /// the containing galaxy. Cheap to call every frame.
    /// </summary>
    public void Update(in UniversePosition camera)
    {
        Vector3D<long> camBlock = BlockCoordOf(camera);

        // Load any not-yet-resident block within the load radius (synchronous — galaxies are sparse).
        for (long dx = -LoadRadiusBlocks; dx <= LoadRadiusBlocks; dx++)
        for (long dy = -LoadRadiusBlocks; dy <= LoadRadiusBlocks; dy++)
        for (long dz = -LoadRadiusBlocks; dz <= LoadRadiusBlocks; dz++)
        {
            var coord = new Vector3D<long>(camBlock.X + dx, camBlock.Y + dy, camBlock.Z + dz);
            if (!_loaded.ContainsKey(coord))
                _loaded[coord] = new GalaxyCatalog(_field, coord);
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

        // Rebuild nearest + containing across all resident galaxies.
        Galaxy nearest = default, containing = default;
        bool hasNearest = false, inside = false;
        double bestSq = double.MaxValue;
        int total = 0;
        foreach (GalaxyCatalog block in _loaded.Values)
        {
            total += block.Count;
            foreach (Galaxy g in block.Galaxies)
            {
                double dSq = g.Center.DistanceSquaredTo(camera);
                if (dSq < bestSq) { bestSq = dSq; nearest = g; hasNearest = true; }
                // Inside this galaxy's volume? Keep the nearest enclosing one (overlaps are rare).
                if (dSq <= g.RadiusMeters * g.RadiusMeters &&
                    (!inside || dSq < containing.Center.DistanceSquaredTo(camera)))
                {
                    inside = true;
                    containing = g;
                }
            }
        }

        Nearest = nearest;
        HasNearest = hasNearest;
        NearestDistanceMeters = hasNearest ? Math.Sqrt(bestSq) : double.MaxValue;
        IsInside = inside;
        Containing = containing;
        LoadedGalaxyCount = total;
    }
}
