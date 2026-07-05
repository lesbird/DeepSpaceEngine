using System;
using System.Collections.Generic;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Procedural cities on inhabited worlds. From orbit the terrain shader paints warm night-side glow
/// where <c>region × sparkle</c> (a low-freq populated-region field × a finer settlement field) is high
/// on temperate coastal lowland; up close this renderer plants ACTUAL BUILDINGS on those same cells, so
/// the lights you saw from space resolve into a lit city as you descend.
///
/// Placement reuses the <see cref="ScatterRenderer"/> trick — instancing over the terrain's own drawn
/// vertices and sampling the SAME height tile the mesh used — so every building sits exactly on the drawn
/// surface (no CPU height mirror, no float divergence). The city field evaluated in the vertex shader is
/// the IDENTICAL expression the terrain fragment shader uses for its orbital glow (same fbm, same
/// <c>uCityFreq = ContinentFreq × 14</c>, same lowland/latitude gates), which is what keeps the ground
/// city registered under the orbital lights.
///
/// Buildings are box prisms laid out on a street grid (roads carved by a metric texel modulo), taller and
/// denser toward each settlement's core (downtown ∝ city²), lit as concrete/glass by day and glowing warm
/// windows by night (emissive → the bloom pass haloes them). Geometry is debug solids; swap for art later.
/// </summary>
public sealed class CityRenderer : IDisposable
{
    public bool Enabled = true;

    /// <summary>Buildings drawn this frame (approx; culled sites collapse offscreen) — surfaced on the HUD.</summary>
    public int Count => _count;

    // Only place buildings on leaves fine enough that a block reads. High-relief worlds keep the LOD coarse
    // even near the surface (the quadtree measures distance to the base sphere, which sits far below a tall
    // surface), so this can't be too strict or cities never reach drawable resolution. Footprint scales with
    // spacing, so at coarse LOD you get a few big blocks that refine into proper towers as you descend.
    public const float MaxSpacingMeters = 140f;
    private const float BlockMeters = 64f;   // city block pitch (buildings + one bounding road)
    private const float RoadMeters = 12f;    // street width carved out of each block
    private const float FootprintFill = 0.86f; // building footprint as a fraction of its cell (near-touching)
    private const float MinHeight = 6f;      // suburban low-rise (m)
    private const float MaxHeight = 110f;    // downtown tower (m) — reached only in the densest cores

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly int _indexCount;
    private readonly List<uint> _owned = new();
    private int _count;

    // One building per terrain vertex (most cull). The vertex shader reads that vertex's drawn height from
    // the tile, evaluates the shared city field to gate/scale it, and grows a box up the surface normal.
    // Per-instance attributes (basePos/dir/texel) are re-pointed at each leaf's own base VBO every draw.
    private const string Vertex = @"#version 410 core
layout(location = 0) in vec3 aCorner;   // unit box corner, y in [-0.5,0.5] (base at -0.5)
layout(location = 1) in vec3 aBasePos;  // per-instance: terrain vertex, patch-centre-relative base sphere pos
layout(location = 2) in vec3 aDir;      // per-instance: outward unit direction (planet-local)
layout(location = 3) in vec2 aTexel;    // per-instance: this vertex's guard-offset texel in the height tile
uniform mat4 uViewProj;
uniform mat4 uModel;         // CreateTranslation(patch centre, camera-relative)
uniform float uMorph;        // fine<->coarse height blend, matches the leaf's terrain draw
uniform vec2 uTileOrigin;    // this leaf's tile origin (texels) in the atlas
uniform sampler2D uHeight;   // terrain height atlas (RG = fine/coarse metres)
uniform float uGridN;
uniform float uVertexSpacing;// metres between adjacent texels (block/footprint metric + slope basis)
uniform int uFace;
uniform vec4 uRect;          // (u0,v0,u1,v1) of this patch on the cube face
uniform float uCityFreq;     // = ContinentFreq * 14 (identical to the terrain orbital-glow field)
uniform float uAmplitude;    // relief amplitude (m) — normalises elevation for the coastal-lowland gate
uniform float uHasLife;      // 1 = inhabited world (gate)
uniform float uDensity;      // 0..1 city extent (lowers the field threshold → cities spread wider)
uniform float uBlockCells;   // texels per block (metric, from spacing)
uniform float uRoadCells;    // texels of road per block
uniform float uMinH, uMaxH;  // building height range (m)
out vec3 vWorld;
out vec3 vLocal;             // mesh-local corner (for the window pattern)
flat out float vKeep;
flat out float vSeed;
flat out float vHeight;      // building height (m)
flat out float vWidth;       // building footprint width (m)
flat out vec3 vUp;           // planet-local up (night-side test in the fragment)

float hash13(vec3 p) {
    p = mod(p, 4096.0);
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}
float vnoise3(vec3 p) {
    vec3 c = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(c),               n100 = hash13(c + vec3(1,0,0));
    float n010 = hash13(c + vec3(0,1,0)), n110 = hash13(c + vec3(1,1,0));
    float n001 = hash13(c + vec3(0,0,1)), n101 = hash13(c + vec3(1,0,1));
    float n011 = hash13(c + vec3(0,1,1)), n111 = hash13(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm3(vec3 p, float freq) {
    float s = 0.0, a = 1.0, f = freq, n = 0.0;
    for (int i = 0; i < 4; i++) { s += a * (vnoise3(p * f) * 2.0 - 1.0); n += a; a *= 0.5; f *= 2.0; }
    return s / n;
}
float hash21(vec2 p) { p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
float Hs(ivec2 t) { vec2 hc = texelFetch(uHeight, t, 0).rg; return mix(hc.x, hc.y, uMorph); }
vec3 facePoint(int f, float u, float v) {
    float a = u * 2.0 - 1.0, b = v * 2.0 - 1.0; vec3 p;
    if (f == 0)      p = vec3( 1.0,  b, -a);
    else if (f == 1) p = vec3(-1.0,  b,  a);
    else if (f == 2) p = vec3(  a, 1.0, -b);
    else if (f == 3) p = vec3(  a,-1.0,  b);
    else if (f == 4) p = vec3(  a,  b, 1.0);
    else             p = vec3( -a,  b,-1.0);
    return normalize(p);
}

void main() {
    ivec2 o = ivec2(int(uTileOrigin.x), int(uTileOrigin.y)) + ivec2(int(aTexel.x), int(aTexel.y));
    float h = Hs(o);
    vec3 up = aDir; vUp = up;

    // Shared city field — IDENTICAL to the terrain orbital glow so buildings land under the lights.
    float region  = smoothstep(0.5, 0.85, 0.5 + 0.5 * fbm3(up, uCityFreq * 0.25));
    float sparkle = smoothstep(0.55, 0.95, 0.5 + 0.5 * fbm3(up + vec3(11.0, 4.0, 7.0), uCityFreq * 1.6));
    float elevN = h / max(1.0, uAmplitude);
    float lowOk = smoothstep(-0.02, 0.05, elevN) * (1.0 - smoothstep(0.15, 0.40, elevN)); // coastal lowland
    float tempOk = 1.0 - smoothstep(0.6, 0.95, abs(up.y));                                 // not polar
    float city = region * sparkle * lowOk * tempOk * step(0.5, uHasLife);
    vSeed = city;

    // Street grid: carve roads out of a metric block lattice in texel space (uBlockCells sized from the
    // vertex spacing so blocks stay ~constant in metres across LODs). Keep a building on every non-road
    // cell inside a city — density lowers the field threshold so settlements grow/shrink smoothly.
    vec2 g = aTexel - vec2(1.0);
    vec2 inCell = mod(g, uBlockCells);
    float road = (inCell.x < uRoadCells || inCell.y < uRoadCells) ? 0.0 : 1.0;
    float keep = step(mix(0.42, 0.12, clamp(uDensity, 0.0, 1.0)), city) * road;

    float seed = hash21(g + uTileOrigin * 1.7);
    float downtown = city * city;                                   // height concentrates in the core
    float hgt = mix(uMinH, uMaxH, clamp(downtown * (0.35 + 0.65 * seed), 0.0, 1.0));
    float width = uVertexSpacing * 0.86;                            // fills its cell → contiguous block mass
    vSeed = seed; vHeight = hgt; vWidth = width; vKeep = keep;

    if (keep < 0.5) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; } // offscreen → culled

    vec3 surf = aBasePos + aDir * h;
    vec3 base = (uModel * vec4(surf, 1.0)).xyz;

    // Align building axes to the surface tangents so rows run with the streets (not random yaw).
    float u = mix(uRect.x, uRect.z, g.x / uGridN);
    float v = mix(uRect.y, uRect.w, g.y / uGridN);
    vec3 tU = normalize(facePoint(uFace, u + 0.0005, v) - facePoint(uFace, u - 0.0005, v));
    vec3 right = normalize(tU - dot(tU, up) * up);
    vec3 fwd = normalize(cross(up, right));

    vec3 local = right * (aCorner.x * width) + up * ((aCorner.y + 0.5) * hgt) + fwd * (aCorner.z * width);
    vec3 world = base + local;
    vWorld = world;
    vLocal = aCorner;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

    private const string Fragment = @"#version 410 core
in vec3 vWorld;
in vec3 vLocal;
flat in float vKeep;
flat in float vSeed;
flat in float vHeight;
flat in float vWidth;
flat in vec3 vUp;
uniform vec3 uSunDir;
uniform float uCityGlow;   // night window brightness (0 = dark structures, still visible by day)
out vec4 FragColor;
float h11(float x) { return fract(sin(x * 127.1) * 43758.5453); }
float win(float f, float c, float s) { return fract(sin(f * 57.3 + c * 131.7 + s * 311.1) * 43758.5453); }
void main() {
    if (vKeep < 0.5) discard;
    vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));   // flat per-face normal
    float diff = max(dot(n, normalize(uSunDir)), 0.0);
    float night = 1.0 - smoothstep(-0.12, 0.10, dot(normalize(vUp), normalize(uSunDir)));

    vec3 a = abs(vLocal);
    bool roof = (a.y >= a.x && a.y >= a.z);
    vec3 baseCol = mix(vec3(0.30, 0.31, 0.35), vec3(0.52, 0.49, 0.45), h11(vSeed * 3.1)); // concrete↔sandstone
    vec3 col = baseCol;
    float emit = 0.0;

    if (!roof) {
        float floors = max(1.0, floor(vHeight / 3.5));
        float cols   = max(1.0, floor(vWidth / 4.0));
        float fy = vLocal.y + 0.5;                              // 0..1 up the facade
        float hc = (a.x >= a.z) ? (vLocal.z + 0.5) : (vLocal.x + 0.5); // across the facing wall
        float fi = floor(fy * floors), ci = floor(hc * cols);
        vec2 wl = fract(vec2(hc * cols, fy * floors));          // position within a window cell
        float frame = step(0.12, wl.x) * step(wl.x, 0.88) * step(0.15, wl.y) * step(wl.y, 0.86);
        float lit = step(0.35, win(fi, ci, vSeed));             // ~65% of windows lit
        col = mix(col, vec3(0.07, 0.08, 0.11), frame * 0.6);    // recessed glazing (dark by day)
        emit = uCityGlow * night * frame * lit;
    } else {
        col = baseCol * 0.55;                                   // dark rooftop
        if (vHeight > 70.0 && a.x < 0.14 && a.z < 0.14 && vLocal.y > 0.4)
            emit = 2.5 * night;                                 // aircraft-warning beacon on tall towers
    }

    vec3 warm = vec3(1.0, 0.85, 0.55);                          // sodium/incandescent window glow
    vec3 shaded = col * (0.16 + 0.9 * diff);
    FragColor = vec4(shaded + warm * emit, 1.0);
}";

    public CityRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);
        (_vao, _indexCount) = BuildBox();
    }

    /// <summary>Draw the city over the near drawn leaves the terrain reported this frame. Gated to
    /// inhabited worlds (HasLife); each building samples its leaf's height tile for the exact drawn base.</summary>
    public unsafe void Render(Camera camera, CelestialBody? target, PlanetTerrainRenderer terrainRenderer,
                              Vector3D<float> sunDir, float near, float far)
    {
        _count = 0;
        PlanetTerrain? terrain = terrainRenderer.ActiveTerrain;
        IReadOnlyList<PlanetTerrainRenderer.GrassLeaf> leaves = terrainRenderer.GrassLeaves;
        uint heightTex = terrainRenderer.HeightTexture;
        if (!Enabled || target == null || terrain == null || heightTex == 0 || leaves.Count == 0) return;

        PlanetTerrain.GpuTerrainParams gp = terrain.GpuParams();
        if (gp.HasLife < 0.5f) return;   // cities only on inhabited worlds

        int perLeaf = terrainRenderer.GrassVertsPerLeaf;
        int stride = terrainRenderer.GrassVertexStrideFloats * sizeof(float);
        Matrix4X4<float> viewProj = camera.ViewMatrix * MatrixHelper.PerspectiveGL(
            camera.FovRadians, camera.AspectRatio, near, far);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetVector3("uSunDir", Vector3D.Normalize(sunDir));
        _shader.SetFloat("uGridN", terrainRenderer.GrassGridN);
        _shader.SetFloat("uCityFreq", (float)(gp.ContinentFreq * 14.0)); // matches the orbital-glow field
        _shader.SetFloat("uAmplitude", (float)Math.Max(1.0, gp.Amplitude));
        _shader.SetFloat("uHasLife", gp.HasLife);
        _shader.SetFloat("uDensity", Math.Clamp(TerrainTuning.CityDensity, 0f, 1f));
        _shader.SetFloat("uMinH", MinHeight);
        _shader.SetFloat("uMaxH", MaxHeight);
        _shader.SetFloat("uCityGlow", Math.Max(0f, TerrainTuning.CityGlow));
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, heightTex);
        _shader.SetInt("uHeight", 0);

        _gl.BindVertexArray(_vao);
        foreach (PlanetTerrainRenderer.GrassLeaf leaf in leaves)
        {
            if (leaf.BaseVbo == 0) continue;
            float spacing = leaf.VertexSpacing;
            if (spacing <= 0f || spacing > MaxSpacingMeters) continue; // too coarse for a legible block

            float blockCells = MathF.Max(3f, MathF.Round(BlockMeters / spacing));
            float roadCells = Math.Clamp(MathF.Round(RoadMeters / spacing), 1f, blockCells - 2f);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, leaf.BaseVbo);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

            _shader.SetMatrix("uModel", Matrix4X4.CreateTranslation(leaf.Rel));
            _shader.SetFloat("uMorph", leaf.Morph);
            _shader.SetVector2("uTileOrigin", leaf.TileOrigin);
            _shader.SetInt("uFace", leaf.Face);
            _shader.SetVector4("uRect", leaf.Rect);
            _shader.SetFloat("uVertexSpacing", spacing);
            _shader.SetFloat("uBlockCells", blockCells);
            _shader.SetFloat("uRoadCells", roadCells);

            _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)_indexCount,
                DrawElementsType.UnsignedInt, null, (uint)perLeaf);
            _count += perLeaf; // upper bound (most instances cull in the vertex shader)
        }
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    // Unit box, y in [-0.5,0.5] (base at -0.5) — same convention as the scatter meshes.
    private unsafe (uint vao, int indexCount) BuildBox()
    {
        float[] p = {
            -0.5f, -0.5f, -0.5f,  0.5f, -0.5f, -0.5f,  0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
            -0.5f, -0.5f,  0.5f,  0.5f, -0.5f,  0.5f,  0.5f, 0.5f,  0.5f, -0.5f, 0.5f,  0.5f,
        };
        uint[] idx = {
            0,1,2, 0,2,3,   4,6,5, 4,7,6,   0,4,5, 0,5,1,
            3,2,6, 3,6,7,   1,5,6, 1,6,2,   0,3,7, 0,7,4,
        };
        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);
        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, p, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);
        // Instance attributes 1..3 re-pointed at each leaf's base VBO in Render; divisors fixed here.
        for (uint a = 1; a <= 3; a++) { _gl.EnableVertexAttribArray(a); _gl.VertexAttribDivisor(a, 1); }
        _gl.BindVertexArray(0);
        _owned.Add(vbo); _owned.Add(ebo);
        return (vao, idx.Length);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        foreach (uint b in _owned) _gl.DeleteBuffer(b);
        _shader.Dispose();
    }
}
