using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders a planet's procedural terrain into a <see cref="TerrainTileCache"/> layer on the GPU — the
/// generation half of the SpaceEngine-style tile path. A fullscreen fragment pass evaluates the noise
/// stack per texel and writes <c>RG = (fineHeight, coarseHeight)</c> (metres above the base radius), the
/// two band-limits the terrain shader blends for geomorphing.
///
/// <para><b>Precision.</b> The surface direction is computed <i>exactly</i> in the shader
/// (<c>facePoint</c>), so adjacent tiles agree on their shared edge (no seams), and the noise is sampled
/// at <c>dir·frequency</c> directly in <c>float</c>. To keep that precise, the CPU clamps each layer's
/// octave count so its top frequency stays under <see cref="FloatSafeFreq"/> cells — beyond that float
/// loses the fractional cell and the lattice would shimmer. Geometry therefore resolves to ~metre-scale
/// features; finer roughness comes from the per-pixel detail shader (the same one the CPU path uses).
/// This is the heightmap-plus-detail-texture split SpaceEngine uses.</para>
///
/// <para><b>Scope (first cut).</b> Continents (fBm) + ridged mountains on the highland mask + detail
/// (fBm). Domain warp, erosion, craters, micro-relief and strata are not ported yet — the silhouette is
/// right; full parity with <see cref="PlanetTerrain.HeightAt"/> follows. The GPU uses its own GLSL hash,
/// so the look has the same character as the CPU terrain rather than matching it bit-for-bit.</para>
/// </summary>
public sealed class TerrainTileGenerator : IDisposable
{
    /// <summary>Highest noise frequency (cells over the unit sphere) that stays precise in float: beyond
    /// ~2^23 / 10 the fractional cell position is lost. Octaves past this are clamped off (and covered by
    /// the per-pixel detail shader instead).</summary>
    public const double FloatSafeFreq = 700_000.0;

    private const string VertexSource = @"#version 410 core
out vec2 vUV;
void main() {
    // Attributeless fullscreen triangle: ids 0,1,2 → (0,0),(2,0),(0,2) in UV, covering the viewport.
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vUV;
uniform int uFace;
uniform vec4 uRect;        // (u0, v0, u1, v1) of this tile on the cube face
uniform vec3 uSeed;        // per-planet hash offset, so worlds differ
uniform vec3 uFreq;        // (continent, mountain, detail) base frequencies
uniform vec3 uWeight;      // (continent, mountain, detail) layer weights
uniform vec3 uGain;        // (continent, mountain, detail) per-octave gains
uniform float uScale;      // metres of relief (height = scale * shape)
uniform vec3 uOctFine;     // (continent, mountain, detail) octave counts at the fine band-limit
uniform vec3 uOctCoarse;   // ... at the parent (coarse) band-limit
uniform float uTexelN;     // texels per tile edge — snap each texel to its mesh-vertex (u,v) so seams match
layout(location = 0) out vec4 oHeight;

const int MaxOct = 32;

vec3 facePoint(int f, float u, float v) {
    float a = u * 2.0 - 1.0, b = v * 2.0 - 1.0;
    vec3 p;
    if (f == 0)      p = vec3( 1.0,  b,  -a);
    else if (f == 1) p = vec3(-1.0,  b,   a);
    else if (f == 2) p = vec3(  a, 1.0,  -b);
    else if (f == 3) p = vec3(  a,-1.0,   b);
    else if (f == 4) p = vec3(  a,  b,  1.0);
    else             p = vec3( -a,  b, -1.0);
    return normalize(p);
}

// Small-input value hash; cells are wrapped to a large period so the float math stays precise and the
// per-planet uSeed offset varies the field between worlds.
float hash(vec3 c) {
    c = mod(c, 8192.0) + uSeed;
    c = fract(c * 0.1031);
    c += dot(c, c.yzx + 33.33);
    return fract((c.x + c.y) * c.z);
}

float vnoise(vec3 p) {
    vec3 c = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash(c),               n100 = hash(c + vec3(1,0,0));
    float n010 = hash(c + vec3(0,1,0)), n110 = hash(c + vec3(1,1,0));
    float n001 = hash(c + vec3(0,0,1)), n101 = hash(c + vec3(1,0,1));
    float n011 = hash(c + vec3(0,1,1)), n111 = hash(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z) * 2.0 - 1.0; // [-1, 1]
}

// Fractional-octave fBm in ~[-1,1] (the top octave fades in by its fraction, matching the CPU path).
float fbm(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, f = freq, norm = 0.0;
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        sum += amp * vnoise(dir * f); norm += amp; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { sum += amp * frac * vnoise(dir * f); norm += amp * frac; }
    return norm > 0.0 ? sum / norm : 0.0;
}

// Fractional-octave ridged multifractal in [0,1] (creases at zero crossings, detail riding ridges).
float ridged(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 0.5, f = freq, prev = 1.0, norm = 0.0;
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        float n = 1.0 - abs(vnoise(dir * f)); n *= n; n *= prev;
        sum += n * amp; norm += amp; prev = n; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { float n = 1.0 - abs(vnoise(dir * f)); n *= n; n *= prev; sum += n * amp * frac; norm += amp * frac; }
    return norm > 0.0 ? clamp(sum / norm, 0.0, 1.0) : 0.0;
}

float shape(vec3 dir, vec3 oct) {
    float cont = fbm(dir, uFreq.x, oct.x, uGain.x);     // broad continents / basins
    float mask = smoothstep(-0.2, 0.4, cont);           // highlands carry the mountains
    float mtn  = ridged(dir, uFreq.y, oct.y, uGain.y);
    float det  = fbm(dir, uFreq.z, oct.z, uGain.z);      // high-frequency roughness
    return uWeight.x * cont + uWeight.y * mtn * mask + uWeight.z * det;
}

void main() {
    // Snap to the mesh vertex grid, with a 1-texel guard ring: texel t holds the height at grid fraction
    // (t-1)/GridN (GridN = uTexelN-3), so interior texel 1 = vertex 0 (u0) and texel uTexelN-2 = the last
    // vertex (u1); texels 0 and uTexelN-1 are the guard just outside the patch (for edge-vertex normals).
    // A shared patch edge samples the identical direction from both sides → no height seam.
    float gridN = uTexelN - 3.0;
    float gi = min(floor(vUV.x * uTexelN), uTexelN - 1.0);
    float gj = min(floor(vUV.y * uTexelN), uTexelN - 1.0);
    float u = mix(uRect.x, uRect.z, (gi - 1.0) / gridN);
    float v = mix(uRect.y, uRect.w, (gj - 1.0) / gridN);
    vec3 dir = facePoint(uFace, u, v);
    float hFine   = uScale * shape(dir, uOctFine);
    float hCoarse = uScale * shape(dir, uOctCoarse);
    oHeight = vec4(hFine, hCoarse, 0.0, 1.0);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _emptyVao; // attributeless fullscreen-triangle draws need a bound VAO in core

    public TerrainTileGenerator(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>
    /// Generate the tile for a quadtree node into <paramref name="layer"/> of <paramref name="cache"/>.
    /// <paramref name="face"/> + (<paramref name="u0"/>…<paramref name="v1"/>) locate the node on the cube;
    /// the spacings are the node's fine and parent-coarse vertex spacings (metres), which clamp the octave
    /// counts to what each band-limit resolves (and the float-safe ceiling).
    /// </summary>
    public void Generate(TerrainTileCache cache, int layer, int face, double u0, double v0, double u1, double v1,
        in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain, double spacingFine, double spacingCoarse)
    {
        var octFine = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingFine, p.MaxContinentOctaves),
            (float)OctClamp(terrain, p.MountainFreq, spacingFine, p.MaxMountainOctaves),
            (float)OctClamp(terrain, p.DetailFreq, spacingFine, p.MaxDetailOctaves));
        var octCoarse = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingCoarse, p.MaxContinentOctaves),
            (float)OctClamp(terrain, p.MountainFreq, spacingCoarse, p.MaxMountainOctaves),
            (float)OctClamp(terrain, p.DetailFreq, spacingCoarse, p.MaxDetailOctaves));

        _shader.Use();
        _shader.SetInt("uFace", face);
        _shader.SetVector4("uRect", new Vector4D<float>((float)u0, (float)v0, (float)u1, (float)v1));
        _shader.SetVector3("uSeed", SeedOffset(p.Seed));
        _shader.SetVector3("uFreq", new Vector3D<float>((float)p.ContinentFreq, (float)p.MountainFreq, (float)p.DetailFreq));
        _shader.SetVector3("uWeight", new Vector3D<float>((float)p.ContinentWeight, (float)p.MountainWeight, (float)p.DetailWeight));
        _shader.SetVector3("uGain", new Vector3D<float>((float)p.ContinentGain, (float)p.MountainGain, (float)p.DetailGain));
        _shader.SetFloat("uScale", (float)p.Scale);
        _shader.SetVector3("uOctFine", octFine);
        _shader.SetVector3("uOctCoarse", octCoarse);
        _shader.SetFloat("uTexelN", cache.TileSize);

        // Render the noise into the tile, then restore exactly the framebuffer + viewport that were bound
        // (the scene FBO mid-render): generation can run inside the terrain pass, so it must leave no trace.
        Span<int> prevFbo = stackalloc int[1];
        Span<int> prevVp = stackalloc int[4];
        _gl.GetInteger(GetPName.DrawFramebufferBinding, prevFbo);
        _gl.GetInteger(GetPName.Viewport, prevVp);
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        if (depth) _gl.Disable(EnableCap.DepthTest);

        cache.BeginRender(layer); // bind the atlas FBO + clip to this tile's sub-rect
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        cache.EndRender();        // disable scissor

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        _gl.Viewport(prevVp[0], prevVp[1], (uint)prevVp[2], (uint)prevVp[3]);
        if (depth) _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Octave count for a band-limit, clamped to both the LOD budget and the float-safe ceiling.</summary>
    private static double OctClamp(PlanetTerrain terrain, double baseFreq, double spacing, int max)
    {
        double lod = terrain.OctavesForSpacing(baseFreq, spacing, max);
        double safe = Math.Floor(Math.Log2(FloatSafeFreq / Math.Max(1.0, baseFreq))) + 1.0;
        return Math.Max(0.0, Math.Min(lod, safe));
    }

    /// <summary>Three fractional offsets in [0,1) from the body seed, shifting the hash so worlds differ.</summary>
    private static Vector3D<float> SeedOffset(ulong seed)
        => new(((seed) & 1023) / 1024f, ((seed >> 10) & 1023) / 1024f, ((seed >> 20) & 1023) / 1024f);

    public void Dispose()
    {
        _gl.DeleteVertexArray(_emptyVao);
        _shader.Dispose();
    }
}
