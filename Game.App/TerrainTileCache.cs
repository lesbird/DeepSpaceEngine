using Silk.NET.OpenGL;

namespace Game.App;

/// <summary>
/// A pool of procedurally-generated terrain <b>tiles</b> for the GPU terrain path. Each quadtree node
/// owns one slot of a single large 2D <b>atlas</b> texture (RGBA32F): a fragment pass renders the node's
/// height straight into that slot's sub-rectangle, and the terrain shader samples it (vertex texture
/// fetch for the displacement). Tiles are cached and reused — only newly-visible nodes generate — and
/// evicted least-recently-used when the pool fills.
///
/// <para>A 2D atlas (rather than a texture array) is used deliberately: attaching a 2D-array layer to an
/// FBO, and copying float data into an array layer, are both unreliable on macOS GL 4.1 — they left the
/// tiles blank. Rendering directly into a 2D atlas sub-rect is complete and needs no copy. Each tile is
/// <c>RG = (fineHeight, coarseHeight)</c> in metres above the base radius (the geomorph fine/coarse pair,
/// stored in the R,G channels of the RGBA32F atlas).</para>
/// </summary>
public sealed class TerrainTileCache : IDisposable
{
    private readonly GL _gl;
    private readonly int _tileSize;  // T: texels per tile edge (= terrain vertex grid resolution)
    private readonly int _capacity;  // number of slots (max resident tiles)
    private readonly int _perRow;    // tiles per atlas row
    private readonly uint _atlas;    // 2D RGBA32F atlas, (_perRow·T)²
    private readonly uint _fbo;      // FBO with the atlas attached (complete) — generation renders into it

    private readonly Stack<int> _free = new();
    private readonly Dictionary<long, int> _slotOf = new();
    private readonly Dictionary<long, long> _usedFrame = new();
    private readonly Dictionary<int, long> _keyOfSlot = new();

    public TerrainTileCache(GL gl, int tileSize, int capacity)
    {
        _gl = gl;
        _tileSize = tileSize;
        _capacity = capacity;
        _perRow = (int)Math.Ceiling(Math.Sqrt(capacity));
        int dim = _perRow * tileSize;

        _atlas = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _atlas);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba32f,
                (uint)dim, (uint)dim, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        // Nearest: each vertex reads its own texel 1:1 (no blending across the height grid).
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _atlas, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        for (int i = capacity - 1; i >= 0; i--) _free.Push(i);
    }

    /// <summary>Texels per tile edge.</summary>
    public int TileSize => _tileSize;

    /// <summary>The atlas texture, for binding when the terrain shader samples it.</summary>
    public uint HeightTexture => _atlas;

    /// <summary>Bottom-left texel of a slot's sub-rectangle in the atlas (the terrain shader adds the
    /// in-tile (i,j) to this to fetch a vertex's height).</summary>
    public (int x, int y) TileOrigin(int slot) => (slot % _perRow * _tileSize, slot / _perRow * _tileSize);

    /// <summary>Slot holding <paramref name="key"/>'s tile if resident; touches its LRU stamp. -1 if absent.</summary>
    public int TryGetLayer(long key, long frame)
    {
        if (_slotOf.TryGetValue(key, out int slot)) { _usedFrame[key] = frame; return slot; }
        return -1;
    }

    /// <summary>Reserve a slot for <paramref name="key"/>, evicting the least-recently-used resident tile
    /// if the pool is full (never one touched this frame). Returns -1 when every slot is needed this frame
    /// — the caller defers and draws a coarser stand-in.</summary>
    public int AllocateLayer(long key, long frame)
    {
        if (_free.Count == 0) EvictOldest(frame);
        if (_free.Count == 0) return -1;
        int slot = _free.Pop();
        _slotOf[key] = slot;
        _usedFrame[key] = frame;
        _keyOfSlot[slot] = key;
        return slot;
    }

    /// <summary>Return a node's slot to the pool (called when the node is merged away / the tree swaps).</summary>
    public void Release(long key)
    {
        if (!_slotOf.TryGetValue(key, out int slot)) return;
        _slotOf.Remove(key);
        _usedFrame.Remove(key);
        _keyOfSlot.Remove(slot);
        _free.Push(slot);
    }

    /// <summary>Drop every resident tile (e.g. switching planets); slots return to the free pool.</summary>
    public void Clear()
    {
        _slotOf.Clear();
        _usedFrame.Clear();
        _keyOfSlot.Clear();
        _free.Clear();
        for (int i = _capacity - 1; i >= 0; i--) _free.Push(i);
    }

    /// <summary>Bind the atlas FBO and clip the viewport/scissor to <paramref name="slot"/>'s sub-rect, so
    /// the generation fullscreen pass writes only that tile. The caller restores its framebuffer/viewport
    /// (and should disable scissor) afterwards via <see cref="EndRender"/>.</summary>
    public void BeginRender(int slot)
    {
        (int x, int y) = TileOrigin(slot);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(x, y, (uint)_tileSize, (uint)_tileSize);
        _gl.Scissor(x, y, (uint)_tileSize, (uint)_tileSize);
        _gl.Enable(EnableCap.ScissorTest);
    }

    /// <summary>Turn off the scissor enabled by <see cref="BeginRender"/> (the caller restores the
    /// framebuffer + viewport itself).</summary>
    public void EndRender() => _gl.Disable(EnableCap.ScissorTest);

    private void EvictOldest(long frame)
    {
        long oldestKey = 0, oldestFrame = long.MaxValue;
        bool found = false;
        foreach ((long key, long used) in _usedFrame)
        {
            if (used >= frame) continue;
            if (used < oldestFrame) { oldestFrame = used; oldestKey = key; found = true; }
        }
        if (found) Release(oldestKey);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_atlas);
    }
}
