using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders the visible star field as additive point sprites. Each frame the visible
/// stars are re-projected relative to the camera (double → float on the CPU, the
/// floating-origin step) and uploaded to a streaming vertex buffer drawn as GL_POINTS.
/// </summary>
public sealed class StarRenderer : IDisposable
{
    private const int FloatsPerStar = 7; // relPos(3) + color(3) + sizeCue(1)

    private const string VertexSource = @"#version 410 core
layout(location = 0) in vec3 aRelPos;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
uniform mat4 uViewProj;
uniform float uPixelScale;
uniform float uMinSize;
uniform float uMaxSize;
uniform float uFadeStart;   // distance (m) where stars begin fading toward the bubble edge
uniform float uFadeEnd;     // distance (m) of the cull boundary — alpha reaches 0 here
out vec3 vColor;
out float vFade;
void main() {
    vColor = aColor;
    gl_Position = uViewProj * vec4(aRelPos, 1.0);
    float dist = max(length(aRelPos), 1.0);
    gl_PointSize = clamp(uPixelScale * aSize / dist, uMinSize, uMaxSize);
    // Fade out toward the cull boundary so stars stream in/out smoothly instead of popping when the
    // bubble edge sweeps past them (more noticeable the larger the draw radius).
    vFade = 1.0 - smoothstep(uFadeStart, uFadeEnd, dist);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vColor;
in float vFade;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float a = exp(-r2 * 2.5) * vFade;
    FragColor = vec4(vColor, 1.0) * a;
}";

    // --- Static catalog path (draws a whole pre-generated block in one go) ---
    private const int CatFloatsPerStar = 7; // pos(3) + color(3) + luminosity(1)

    private const string CatVertexSource = @"#version 410 core
layout(location = 0) in vec3 aPos;     // metres relative to the catalog origin
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aLum;    // luminosity (Lsun)
uniform mat4 uViewProj;
uniform vec3 uCamRel;                   // camera position relative to the catalog origin (m)
uniform float uBrightScale;
uniform float uGamma;                   // perceptual compression of the huge flux range
uniform float uSizeScale;
uniform float uMinSize;
uniform float uMaxSize;
out vec3 vColor;
out float vBright;
const float LY = 9.4607e15;             // metres per light-year
void main() {
    vColor = aColor;
    vec3 rel = aPos - uCamRel;          // relative-to-camera (single-precision; far dots jitter sub-pixel)
    gl_Position = uViewProj * vec4(rel, 1.0);
    float distLy = max(length(rel) / LY, 1.0e-4);
    // Flux falls as 1/d^2, but raw flux spans an enormous range; a gamma far below 1 compresses it
    // perceptually (like apparent magnitude) so distant stars stay visible as faint points instead
    // of snapping to black just past a light-year.
    float flux = aLum / (distLy * distLy);
    float bright = uBrightScale * pow(flux, uGamma);
    vBright = bright;
    gl_PointSize = clamp(uSizeScale * sqrt(bright), uMinSize, uMaxSize);
}";

    private const string CatFragmentSource = @"#version 410 core
in vec3 vColor;
in float vBright;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float a = exp(-r2 * 2.5) * clamp(vBright, 0.0, 1.0);
    FragColor = vec4(vColor, 1.0) * a;
}";

    private Shader? _catShader;

    // One GPU buffer per resident lattice block, keyed by block coordinate. Mirrors the
    // StarCatalogPager's loaded set (see SyncBlocks); positions are baked relative to each block's
    // own origin so the per-frame draw only subtracts the (small) camera-relative-to-block offset.
    private readonly struct CatBlock
    {
        public readonly uint Vao;
        public readonly uint Vbo;
        public readonly int Count;
        public readonly UniversePosition Origin;
        public CatBlock(uint vao, uint vbo, int count, in UniversePosition origin)
        { Vao = vao; Vbo = vbo; Count = count; Origin = origin; }
    }
    private readonly Dictionary<Vector3D<long>, CatBlock> _catBlocks = new();
    private readonly HashSet<Vector3D<long>> _desiredScratch = new();
    private readonly List<Vector3D<long>> _evictScratch = new();
    private const int CatUploadsPerFrame = 4; // cap block uploads so a burst (e.g. startup) can't stall a frame

    /// <summary>Brightness mapping for the catalog draw (tunable). With CatGamma ≈ 0.25 a Sun-like
    /// star stays visible out to tens of light-years and even M-dwarfs show within ~10 ly.</summary>
    public float CatBrightScale = 3.0f;
    public float CatGamma = 0.25f;
    public float CatSizeScale = 5f;
    public float CatMinSize = 1.5f;
    public float CatMaxSize = 160f;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private float[] _data = new float[FloatsPerStar * 1024];

    /// <summary>Tunable: larger = stars appear bigger.</summary>
    public float PixelScale = 2.2e17f;
    /// <summary>Minimum on-screen size so even distant stars stay visible.</summary>
    public float MinPixelSize = 5f;
    /// <summary>Maximum on-screen size (clamps very near stars until M2 renders a sun sphere).</summary>
    public float MaxPixelSize = 160f;

    public int LastDrawn { get; private set; }

    public unsafe StarRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        uint stride = FloatsPerStar * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera, IReadOnlyList<Star> visible, double bubbleMeters, ulong? excludeId = null)
    {
        EnsureCapacity(visible.Count);
        var cam = camera.Position;
        int count = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            Star s = visible[i];
            if (excludeId is { } ex && s.Id == ex) continue; // active system draws its own sun sphere
            Vector3D<float> rel = s.Position.ToCameraRelative(cam);
            int o = count * FloatsPerStar;
            count++;
            _data[o + 0] = rel.X;
            _data[o + 1] = rel.Y;
            _data[o + 2] = rel.Z;
            _data[o + 3] = s.Color.X;
            _data[o + 4] = s.Color.Y;
            _data[o + 5] = s.Color.Z;
            _data[o + 6] = s.SizeCue;
        }

        LastDrawn = count;
        if (count == 0) return;

        // GL state for additive, depth-independent point sprites.
        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        _shader.Use();
        Matrix4X4<float> viewProj = camera.ViewMatrix * camera.ProjectionMatrix;
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetFloat("uPixelScale", PixelScale);
        _shader.SetFloat("uMinSize", MinPixelSize);
        _shader.SetFloat("uMaxSize", MaxPixelSize);
        // Fade across the outer ~15% of the bubble so the cull boundary is invisible.
        _shader.SetFloat("uFadeStart", (float)(bubbleMeters * 0.85));
        _shader.SetFloat("uFadeEnd", (float)bubbleMeters);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = new ReadOnlySpan<float>(_data, 0, count * FloatsPerStar);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, span, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)count);
        _gl.BindVertexArray(0);

        // Restore defaults the rest of the frame expects.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>Mirror the pager's resident block set into per-block GPU buffers: upload any block we
    /// don't have a buffer for yet (bounded per frame so a burst can't stall), and free buffers for
    /// blocks that have been evicted. Call once per frame before <see cref="RenderCatalog"/>.</summary>
    public unsafe void SyncBlocks(IReadOnlyCollection<StarCatalog> loaded)
    {
        EnsureCatShader();

        // Evict GPU buffers whose block is no longer resident.
        _desiredScratch.Clear();
        foreach (StarCatalog block in loaded) _desiredScratch.Add(block.BlockCoord);
        _evictScratch.Clear();
        foreach (Vector3D<long> coord in _catBlocks.Keys)
            if (!_desiredScratch.Contains(coord)) _evictScratch.Add(coord);
        foreach (Vector3D<long> coord in _evictScratch)
        {
            CatBlock b = _catBlocks[coord];
            _gl.DeleteBuffer(b.Vbo);
            _gl.DeleteVertexArray(b.Vao);
            _catBlocks.Remove(coord);
        }

        // Upload newly-resident blocks (capped per frame).
        int uploads = 0;
        foreach (StarCatalog block in loaded)
        {
            if (uploads >= CatUploadsPerFrame) break;
            if (_catBlocks.ContainsKey(block.BlockCoord)) continue;
            UploadBlock(block);
            uploads++;
        }
    }

    private void EnsureCatShader()
    {
        _catShader ??= new Shader(_gl, CatVertexSource, CatFragmentSource);
    }

    /// <summary>Bake one block's stars (position relative to its own origin + colour + luminosity)
    /// into a fresh static VBO. The per-frame draw subtracts the camera-relative-to-origin offset.</summary>
    private unsafe void UploadBlock(StarCatalog block)
    {
        IReadOnlyList<Star> stars = block.Stars;
        var data = new float[stars.Count * CatFloatsPerStar];
        for (int i = 0; i < stars.Count; i++)
        {
            Star s = stars[i];
            Vector3D<float> p = s.Position.ToCameraRelative(block.Origin); // = position relative to origin
            int o = i * CatFloatsPerStar;
            data[o + 0] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
            data[o + 3] = s.Color.X; data[o + 4] = s.Color.Y; data[o + 5] = s.Color.Z;
            data[o + 6] = s.Luminosity;
        }

        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = CatFloatsPerStar * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(data), BufferUsageARB.StaticDraw);
        _gl.BindVertexArray(0);

        _catBlocks[block.BlockCoord] = new CatBlock(vao, vbo, stars.Count, block.Origin);
    }

    /// <summary>Draw every resident block as apparent-brightness point sprites. The active system's
    /// sun (by global id) is skipped in its own block: up close, single-precision relative-to-camera
    /// puts its dot visibly off from its precisely-rendered sphere, so the system pass owns it.</summary>
    public void RenderCatalog(Camera camera, ulong? excludeId = null)
    {
        if (_catShader == null || _catBlocks.Count == 0) return;

        Vector3D<long> exBlock = default;
        int exLocal = -1;
        if (excludeId is { } id) StarId.Unpack(id, out exBlock, out exLocal);

        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        _catShader.Use();
        Matrix4X4<float> viewProj = camera.ViewMatrix * camera.ProjectionMatrix;
        _catShader.SetMatrix("uViewProj", viewProj);
        _catShader.SetFloat("uBrightScale", CatBrightScale);
        _catShader.SetFloat("uGamma", CatGamma);
        _catShader.SetFloat("uSizeScale", CatSizeScale);
        _catShader.SetFloat("uMinSize", CatMinSize);
        _catShader.SetFloat("uMaxSize", CatMaxSize);

        int drawn = 0;
        foreach ((Vector3D<long> coord, CatBlock b) in _catBlocks)
        {
            Vector3D<float> camRel = camera.Position.ToCameraRelative(b.Origin); // camera relative to this block
            _catShader.SetVector3("uCamRel", camRel);
            _gl.BindVertexArray(b.Vao);

            if (exLocal >= 0 && exLocal < b.Count && coord == exBlock)
            {
                // Draw the two spans around the excluded star (drawn precisely by the system pass).
                if (exLocal > 0) _gl.DrawArrays(PrimitiveType.Points, 0, (uint)exLocal);
                int after = b.Count - exLocal - 1;
                if (after > 0) _gl.DrawArrays(PrimitiveType.Points, exLocal + 1, (uint)after);
                drawn += b.Count - 1;
            }
            else
            {
                _gl.DrawArrays(PrimitiveType.Points, 0, (uint)b.Count);
                drawn += b.Count;
            }
        }
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        LastDrawn = drawn;
    }

    private void EnsureCapacity(int starCount)
    {
        int needed = starCount * FloatsPerStar;
        if (_data.Length < needed)
            Array.Resize(ref _data, Math.Max(needed, _data.Length * 2));
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        if (_catShader != null)
        {
            _catShader.Dispose();
            foreach (CatBlock b in _catBlocks.Values)
            {
                _gl.DeleteBuffer(b.Vbo);
                _gl.DeleteVertexArray(b.Vao);
            }
            _catBlocks.Clear();
        }
    }
}
