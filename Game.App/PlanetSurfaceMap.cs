using Game.Universe;
using Silk.NET.OpenGL;

namespace Game.App;

/// <summary>
/// A body's equirectangular <b>albedo + object-space normal</b> surface map, baked ON THE GPU straight from
/// the same terrain field + biome/regolith shader the quadtree tiles use (see
/// <see cref="TerrainTileGenerator.BakeEquirect"/>). The distant <see cref="SystemRenderer"/> sphere samples
/// it by direction→lat/long, so a body's far view IS the surface you land on — not a divergent CPU
/// approximation that snaps colour when the quadtree takes over. The bake is a few-ms fullscreen pass on the
/// render thread (the old CPU bake was seconds and pool-starved, so it never arrived — see the
/// surface-map-bake-cost note); the map is ready the frame it's requested-and-baked.
/// </summary>
public sealed class PlanetSurfaceMap : IDisposable
{
    public const int MapWidth = 2048;
    public const int MapHeight = 1024;

    private readonly GL _gl;
    private uint _albedoTex;
    private uint _normalTex;

    /// <summary>True once the map has been baked and is ready to sample.</summary>
    public bool Ready { get; private set; }
    /// <summary>The body the currently-baked map belongs to.</summary>
    public ulong BodyId { get; private set; } = ulong.MaxValue;
    public uint AlbedoTex => _albedoTex;
    public uint NormalTex => _normalTex;

    public PlanetSurfaceMap(GL gl) => _gl = gl;

    /// <summary>Render-thread: (re)bake this map for <paramref name="terrain"/> via the GPU generator, then
    /// mip + filter the two textures. Fast enough to run synchronously.</summary>
    public void Bake(TerrainTileGenerator gen, PlanetTerrain terrain, ulong bodyId)
    {
        Alloc(ref _albedoTex);
        Alloc(ref _normalTex);
        gen.BakeEquirect(_albedoTex, _normalTex, MapWidth, MapHeight, terrain.GpuParams(), terrain,
            Math.Max(0f, TerrainTuning.CraterAlbedo), Math.Max(0f, TerrainTuning.MariaStrength));
        Finish(_albedoTex);
        Finish(_normalTex);
        BodyId = bodyId;
        Ready = true;
    }

    /// <summary>Allocate (once) an empty RGB8 target the equirect pass renders into.</summary>
    private unsafe void Alloc(ref uint tex)
    {
        if (tex != 0) return;
        tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb8, MapWidth, MapHeight, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, null);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>After the bake: mipmap + set the sampling filters/wrap (longitude wraps ±180°, latitude
    /// clamps at the poles) — matching what the sphere/terrain shaders expect.</summary>
    private void Finish(uint tex)
    {
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (_albedoTex != 0) _gl.DeleteTexture(_albedoTex);
        if (_normalTex != 0) _gl.DeleteTexture(_normalTex);
    }
}

/// <summary>
/// A small LRU set of <see cref="PlanetSurfaceMap"/>s — one per body — so several nearby bodies can each show
/// their exact baked surface in the SAME frame (a planet and its moon together), not just the single nearest.
/// Owns the GPU <see cref="TerrainTileGenerator"/> that bakes them. Each body's map bakes once (a few-ms GPU
/// pass, at most one per frame to avoid a spike) and is reused; the least-recently-requested map is evicted
/// when the set is full. Cheap to call every frame: <see cref="Request"/> for a resident body only touches it.
/// </summary>
public sealed class PlanetSurfaceMapCache : IDisposable
{
    private readonly GL _gl;
    private readonly int _capacity;
    private readonly TerrainTileGenerator _gen;
    private readonly Dictionary<ulong, PlanetSurfaceMap> _maps = new();
    private readonly Dictionary<ulong, PlanetTerrain> _pending = new(); // bodyId → terrain awaiting its GPU bake
    private readonly List<ulong> _lru = new();                          // least-recently-requested first

    public PlanetSurfaceMapCache(GL gl, int capacity)
    {
        _gl = gl;
        _capacity = Math.Max(1, capacity);
        _gen = new TerrainTileGenerator(gl);
    }

    /// <summary>Ensure a map exists (and is queued to bake) for <paramref name="body"/>, marking it
    /// most-recently-used. Builds the <see cref="PlanetTerrain"/> once, on first request; later calls just
    /// touch the LRU. Evicts the least-recently-requested map when the set is over capacity.</summary>
    public void Request(CelestialBody body)
    {
        ulong id = body.Seed;
        if (_maps.ContainsKey(id)) { Touch(id); return; }
        while (_maps.Count >= _capacity) EvictOldest();
        _maps[id] = new PlanetSurfaceMap(_gl);
        _lru.Add(id);
        _pending[id] = new PlanetTerrain(body); // baked on the render thread in Update
    }

    /// <summary>Render-thread: bake at most ONE pending map this frame (a few-ms GPU pass), so several
    /// bodies entering range at once don't stack their bakes into a single-frame hitch.</summary>
    public void Update()
    {
        if (_pending.Count == 0) return;
        ulong id = 0;
        PlanetTerrain? terr = null;
        foreach (KeyValuePair<ulong, PlanetTerrain> kv in _pending) { id = kv.Key; terr = kv.Value; break; }
        _pending.Remove(id);
        if (_maps.TryGetValue(id, out PlanetSurfaceMap? m)) m.Bake(_gen, terr!, id);
    }

    /// <summary>The ready map for <paramref name="bodyId"/>, or null if none is resident or it hasn't baked
    /// yet (the caller then draws the procedural sphere / procedural terrain relief).</summary>
    public PlanetSurfaceMap? Get(ulong bodyId)
        => _maps.TryGetValue(bodyId, out PlanetSurfaceMap? m) && m.Ready ? m : null;

    private void Touch(ulong id)
    {
        _lru.Remove(id); // O(capacity); capacity is a handful
        _lru.Add(id);
    }

    private void EvictOldest()
    {
        if (_lru.Count == 0) return;
        ulong id = _lru[0];
        _lru.RemoveAt(0);
        _pending.Remove(id);
        if (_maps.Remove(id, out PlanetSurfaceMap? m)) m.Dispose();
    }

    public void Dispose()
    {
        foreach (PlanetSurfaceMap m in _maps.Values) m.Dispose();
        _maps.Clear();
        _lru.Clear();
        _pending.Clear();
        _gen.Dispose();
    }
}
