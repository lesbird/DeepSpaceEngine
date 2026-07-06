using System;
using System.Collections.Generic;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>One surface-object scatter layer (rocks / trees / pickups …). Pure config — the
/// <see cref="ScatterRenderer"/> places it. Each spawner rolls its placement independently (via
/// <see cref="Seed"/>), and is gated on a per-world basis by an environment-trait mask plus a
/// deterministic presence dice-roll (<see cref="SpawnChance"/>).</summary>
public sealed class Spawner
{
    public string Name = "Spawner";
    public bool Enabled = true;
    public int MeshId = 0;                          // index into ScatterRenderer's mesh registry
    public float Density = 0.4f;                     // 0..1 keep-fraction of candidate sites
    public float MinSize = 4f;                       // per-object size rolled uniformly in [min,max] (m)
    public float MaxSize = 10f;
    public int Orient = 0;                           // 0 = world (radial) up, 1 = surface-normal, 2 = random
    public uint Seed = 1;                            // decorrelates placement AND the presence roll
    public EnvTrait Require = EnvTrait.Surface;      // body must have ALL these traits
    public EnvTrait Forbid = EnvTrait.None;          // body must have NONE of these traits
    public float SpawnChance = 1f;                   // per-world probability this spawner appears at all
    public float MinAltitude = -NoAltLimit;          // metres of relief above base radius — skip sites below (keeps out of oceans)
    public float MaxAltitude = NoAltLimit;           // metres of relief above base radius — skip sites above (keeps off peaks)

    /// <summary>Sentinel magnitude meaning "don't clamp this altitude bound" (1e9 m dwarfs any real relief).</summary>
    public const float NoAltLimit = 1e9f;

    /// <summary>Runtime: passes both gates on the current body (recomputed when the body changes).</summary>
    public bool ActiveHere;
}

/// <summary>
/// Up-close surface scatter, placed the only way that stays exactly on the GPU-drawn terrain: by
/// <b>instancing over the terrain's own vertices</b>. The terrain renderer hands us the near drawn leaves
/// (their base-mesh VBO + tile origin + camera-relative centre + morph); for each leaf we draw one object
/// per terrain vertex, and the vertex shader samples the SAME height tile the terrain mesh used
/// (<c>texelFetch(uHeight, tileOrigin + texel)</c>). So an object's base is the displaced surface point
/// itself — no analytic height mirror, no float divergence, no floating.
///
/// Supports any number of <see cref="Spawner"/> layers, each with its own geometry (mesh registry),
/// density/size/orientation, and per-world gating (environment traits + a deterministic presence roll).
/// Spawners are decorrelated by a per-spawner hash salt so two layers don't land on the same vertices.
///
/// Geometry is debug solids (cube / cone / rock / tetra), lit flat — swap meshes for real art later.
/// </summary>
public sealed class ScatterRenderer : IDisposable
{
    public bool Enabled = true;

    /// <summary>The scatter layers, edited live (HUD add/remove). Each draws independently.</summary>
    public readonly List<Spawner> Spawners = new();

    /// <summary>Zero-separated mesh names for an ImGui combo; index matches <see cref="Spawner.MeshId"/>.</summary>
    public const string MeshCombo = "Cube\0Tree (broadleaf)\0Rock\0Pickup\0Tree (conifer)\0";

    /// <summary>Objects scattered this frame (0 = none) — surfaced on the HUD.</summary>
    public int Count => _count;

    private const ulong NoBody = ulong.MaxValue;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly ScatterMesh[] _meshes;
    private readonly List<uint> _ownedBuffers = new();   // mesh VBO/EBOs to free on dispose
    private int _count;
    private ulong _bodyId = NoBody;                       // body the activation gates were last evaluated for

    private readonly struct ScatterMesh
    {
        public readonly uint Vao;
        public readonly int IndexCount;
        public ScatterMesh(uint vao, int indexCount) { Vao = vao; IndexCount = indexCount; }
    }

    // One object per terrain vertex; the vertex shader looks up that vertex's drawn height from the tile and
    // plants the object there. Per-instance attributes (basePos/dir/texel) are RE-POINTED at each leaf's base
    // VBO every draw — that buffer is the terrain's own mesh, already resident on the GPU. uHashSalt
    // decorrelates spawners: every spawn/size/yaw roll folds it in, so each layer lands on a different subset.
    private const string Vertex = @"#version 410 core
layout(location = 0) in vec3 aCorner;   // mesh-local corner, y in [-0.5,0.5] (base at -0.5)
layout(location = 1) in vec3 aBasePos;  // per-instance: terrain vertex, patch-centre-relative base sphere pos
layout(location = 2) in vec3 aDir;      // per-instance: outward unit direction
layout(location = 3) in vec2 aTexel;    // per-instance: this vertex's texel in the height tile (guard-offset)
layout(location = 4) in vec3 aColor;    // mesh-local per-vertex colour (canopy green / trunk brown / rock grey)
uniform mat4 uViewProj;
uniform mat4 uModel;        // CreateTranslation(patch centre, camera-relative)
uniform float uMorph;       // fine<->coarse height blend, matches the leaf's terrain draw
uniform vec2 uTileOrigin;   // this leaf's tile origin (texels) in the atlas
uniform sampler2D uHeight;  // the terrain height atlas (RG = fine/coarse metres)
uniform float uMinSize;     // per-object size is rolled between these (m)
uniform float uMaxSize;
uniform float uThin;        // keep a site if its spawn hash < uThin
uniform float uMinAlt;      // skip sites whose drawn height (m above base radius) is below this
uniform float uMaxAlt;      // skip sites whose drawn height (m above base radius) is above this
uniform int uOrient;        // 0 = world (radial) up, 1 = surface-normal up, 2 = random up
uniform int uFace;          // for the surface-normal slope basis
uniform vec4 uRect;         // (u0,v0,u1,v1) of this patch on the face
uniform float uGridN;
uniform float uVertexSpacing;
uniform float uHashSalt;    // per-spawner decorrelation offset
uniform float uTime;        // seconds, for wind sway
uniform float uSway;        // 1 for foliage (canopy drifts), 0 for rock/other
uniform float uFadeStart;   // camera distance (m) where density starts thinning
uniform float uFadeEnd;     // camera distance (m) where the last objects are gone
out vec3 vWorld;
out float vKeep;
out vec3 vCol;      // per-vertex base colour
out float vUp01;    // 0 at the object's base, 1 at its top — drives base-darkening AO
out float vShade;   // per-instance brightness jitter
out float vHue;     // per-instance warm/cool jitter (-1..1)
float hash(vec2 p, float seed){ return fract(sin(dot(p, vec2(41.3, 289.1)) + seed + uHashSalt) * 43758.5453); }
float Hs(ivec2 t){ vec2 hc = texelFetch(uHeight, t, 0).rg; return mix(hc.x, hc.y, uMorph); }
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
    float h = Hs(o);                              // EXACT drawn height at this vertex
    vec3 surf = aBasePos + aDir * h;              // patch-relative surface point (identical to terrain mesh)
    vec3 base = (uModel * vec4(surf, 1.0)).xyz;   // camera-relative surface point

    float keep = step(hash(aTexel, 0.0), uThin);  // spawn/skip roll
    keep *= step(uMinAlt, h) * step(h, uMaxAlt);  // altitude band: drop oceans (below min) / peaks (above max)
    // Distance LOD: stochastically drop instances as they near the scatter cutoff, so the field THINS toward
    // the horizon and is empty before the terrain leaf disappears — no hard wall, no pop as leaves swap out.
    float distFade = 1.0 - smoothstep(uFadeStart, uFadeEnd, length(base));
    keep *= step(hash(aTexel, 71.9), distFade);
    float sizeT = hash(aTexel, 7.31);             // independent size roll → variety
    vKeep = keep;
    float s = mix(uMinSize, uMaxSize, sizeT) * keep; // random size in [min,max]; culled sites collapse to 0

    // Object 'up' by orientation mode.
    vec3 up = aDir;                               // 0: radial planet up (stands upright, ignores slope)
    if (uOrient == 1) {                           // 1: surface normal (tilts with the terrain slope)
        float hu1 = Hs(o + ivec2(1,0)), hu0 = Hs(o - ivec2(1,0));
        float hv1 = Hs(o + ivec2(0,1)), hv0 = Hs(o - ivec2(0,1));
        vec2 g = aTexel - vec2(1.0);
        float u = mix(uRect.x, uRect.z, g.x / uGridN);
        float v = mix(uRect.y, uRect.w, g.y / uGridN);
        vec3 tU = normalize(facePoint(uFace, u + 0.0005, v) - facePoint(uFace, u - 0.0005, v));
        vec3 tV = normalize(facePoint(uFace, u, v + 0.0005) - facePoint(uFace, u, v - 0.0005));
        up = normalize(aDir - tU * (hu1 - hu0) / (2.0 * uVertexSpacing)
                            - tV * (hv1 - hv0) / (2.0 * uVertexSpacing));
    } else if (uOrient == 2) {                    // 2: random tumble (biased outward so it isn't buried)
        vec3 r = vec3(hash(aTexel,3.1), hash(aTexel,5.7), hash(aTexel,9.2)) - 0.5;
        up = normalize(aDir + 1.4 * r);
    }

    // Build a basis around 'up' with a per-site random yaw (variety for non-symmetric objects).
    vec3 ref = abs(up.y) < 0.99 ? vec3(0.0,1.0,0.0) : vec3(1.0,0.0,0.0);
    vec3 right = normalize(cross(ref, up));
    vec3 fwd   = cross(up, right);
    float yaw = hash(aTexel, 12.9) * 6.2831853;
    float c = cos(yaw), sn = sin(yaw);
    vec3 r2 = right * c + fwd * sn;
    vec3 f2 = -right * sn + fwd * c;

    // Per-instance non-uniform scale → each object a distinct silhouette from the one shared mesh. Y scales
    // around the BASE (height-above-base) so it stays planted; X/Z around the mesh axis (rock centre / trunk).
    vec3 nscale = vec3(0.75 + 0.50 * hash(aTexel, 31.2),
                       0.80 + 0.55 * hash(aTexel, 41.7),
                       0.75 + 0.50 * hash(aTexel, 53.9));
    float hAbove = (aCorner.y + 0.5) * nscale.y;    // 0 at base -> grows up

    // Wind sway (foliage only): amplitude ~ height² so the trunk base holds and the canopy drifts; a
    // per-instance phase keeps a stand from swaying in lockstep.
    float amp = uSway * hAbove * hAbove * 0.045;
    float ph  = hash(aTexel, 61.3) * 6.2831853;
    vec2 wind = vec2(sin(uTime * 1.3 + ph), sin(uTime * 1.7 + ph * 1.4)) * amp;

    // aCorner base (y=-0.5) sits on the surface; the object grows up 'up', sways in the r2/f2 tangent plane.
    vec3 local = r2 * (aCorner.x * nscale.x) + up * hAbove + f2 * (aCorner.z * nscale.z)
               + r2 * wind.x + f2 * wind.y;
    vec3 world = base + local * s;
    vWorld = world;
    vCol = aColor;
    vUp01 = aCorner.y + 0.5;                     // base (-0.5) -> 0, top (+0.5) -> 1
    vShade = 0.82 + 0.34 * hash(aTexel, 21.7);   // per-instance brightness variety
    vHue = hash(aTexel, 27.3) * 2.0 - 1.0;       // per-instance warm/cool shift
    gl_Position = uViewProj * vec4(world, 1.0);
}";

    private const string Fragment = @"#version 410 core
in vec3 vWorld;
in float vKeep;
in vec3 vCol;
in float vUp01;
in float vShade;
in float vHue;
uniform vec3 uSunDir;
uniform vec3 uGroundTint;    // planet's average surface colour — objects lean toward it to sit in the palette
uniform float uGroundBlend;  // how far this layer leans toward the ground tint (rocks a lot, foliage a little)
out vec4 FragColor;
void main() {
    if (vKeep < 0.5) discard;
    vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));   // flat per-face normal
    float ndl = abs(dot(n, normalize(uSunDir)));            // two-sided sun term (faces aren't back-culled)
    float ao = mix(0.55, 1.0, vUp01);                      // darker toward the base -> reads as planted, not floating
    float light = 0.30 + 0.70 * ndl;                       // ambient fill + direct sun
    vec3 col = mix(vCol, uGroundTint, uGroundBlend);       // lean into the local palette so it belongs in the scene
    col *= vShade;                                         // per-instance brightness variety
    col *= vec3(1.0 + 0.06 * vHue, 1.0, 1.0 - 0.06 * vHue); // subtle per-instance warm/cool
    FragColor = vec4(col * light * ao, 1.0);
}";

    public ScatterRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);
        _meshes = new[] { BuildCube(), BuildBroadleaf(), BuildBoulder(), BuildTetra(), BuildConifer() };

        // Sensible starter set; replaced wholesale if tuning.json carries a saved list.
        Spawners.Add(new Spawner { Name = "Rocks",    MeshId = 2, Orient = 2, Density = 0.25f, MinSize = 2f, MaxSize = 6f,  Seed = 11, Require = EnvTrait.Surface });
        Spawners.Add(new Spawner { Name = "Trees",    MeshId = 1, Orient = 0, Density = 0.50f, MinSize = 6f, MaxSize = 14f, Seed = 22, Require = EnvTrait.Life });
        Spawners.Add(new Spawner { Name = "Conifers", MeshId = 4, Orient = 0, Density = 0.35f, MinSize = 7f, MaxSize = 16f, Seed = 44, Require = EnvTrait.Life, SpawnChance = 0.6f });
        Spawners.Add(new Spawner { Name = "Pickups",  MeshId = 3, Orient = 1, Density = 0.10f, MinSize = 2f, MaxSize = 3f,  Seed = 33, Require = EnvTrait.Surface, SpawnChance = 0.4f });
    }

    /// <summary>Force the per-world activation gates to recompute next frame (call after editing a
    /// spawner's traits / seed / spawn-chance, which change the result for the current body).</summary>
    public void InvalidateActivation() => _bodyId = NoBody;

    /// <summary>Draw every active spawner over the near drawn leaves the terrain reported this frame,
    /// instancing one object per terrain vertex with the height read from the leaf's own tile.</summary>
    public unsafe void Render(Camera camera, CelestialBody? target, PlanetTerrainRenderer terrainRenderer,
                              Vector3D<float> sunDir, float near, float far, float time)
    {
        _count = 0;
        PlanetTerrain? terrain = terrainRenderer.ActiveTerrain;
        IReadOnlyList<PlanetTerrainRenderer.GrassLeaf> leaves = terrainRenderer.GrassLeaves;
        uint heightTex = terrainRenderer.HeightTexture;
        if (!Enabled || target == null || terrain == null || heightTex == 0 || leaves.Count == 0) return;

        // Per-world gate (env traits + deterministic presence roll), recomputed only when the body changes.
        if (target.Seed != _bodyId) { _bodyId = target.Seed; EvaluateActivation(target); }

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
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, heightTex);
        _shader.SetInt("uHeight", 0);
        _shader.SetVector3("uGroundTint", target.SurfaceAlbedo); // this world's average surface colour
        _shader.SetFloat("uTime", time);                         // wind-sway clock
        // Thin the field out over the last stretch before the terrain's scatter cutoff so it fades into the
        // distance instead of ending at a wall (kept in sync with TerrainTuning.ScatterRange's leaf gate).
        _shader.SetFloat("uFadeStart", TerrainTuning.ScatterRange * 0.55f);
        _shader.SetFloat("uFadeEnd", TerrainTuning.ScatterRange * 0.90f);

        foreach (Spawner sp in Spawners)
        {
            if (!sp.Enabled || !sp.ActiveHere) continue;
            ScatterMesh mesh = _meshes[Math.Clamp(sp.MeshId, 0, _meshes.Length - 1)];

            // Rocks are made of the ground, so lean them hard into its palette; foliage keeps most of its own
            // green; pickups stay their bright signal colour. (Keyed off the debug mesh ids for now.)
            float groundBlend = sp.MeshId switch { 2 => 0.40f, 0 => 0.30f, 3 => 0.0f, _ => 0.14f };
            _shader.SetFloat("uGroundBlend", groundBlend);
            _shader.SetFloat("uSway", (sp.MeshId == 1 || sp.MeshId == 4) ? 1f : 0f); // foliage sways, rocks don't

            float lo = Math.Max(0.1f, Math.Min(sp.MinSize, sp.MaxSize));
            float hi = Math.Max(lo, sp.MaxSize);
            float thin = Math.Clamp(sp.Density, 0f, 1f);
            _shader.SetFloat("uMinSize", lo);
            _shader.SetFloat("uMaxSize", hi);
            _shader.SetFloat("uThin", thin);
            _shader.SetFloat("uMinAlt", Math.Min(sp.MinAltitude, sp.MaxAltitude));
            _shader.SetFloat("uMaxAlt", Math.Max(sp.MinAltitude, sp.MaxAltitude));
            _shader.SetInt("uOrient", sp.Orient);
            // Golden-ratio scramble of the seed → a well-spread per-spawner hash offset.
            _shader.SetFloat("uHashSalt", (sp.Seed & 0xFFFFu) * 0.6180339887f);

            _gl.BindVertexArray(mesh.Vao);
            foreach (PlanetTerrainRenderer.GrassLeaf leaf in leaves)
            {
                if (leaf.BaseVbo == 0) continue;
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, leaf.BaseVbo);
                _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
                _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
                _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

                _shader.SetMatrix("uModel", Matrix4X4.CreateTranslation(leaf.Rel));
                _shader.SetFloat("uMorph", leaf.Morph);
                _shader.SetVector2("uTileOrigin", leaf.TileOrigin);
                _shader.SetInt("uFace", leaf.Face);
                _shader.SetVector4("uRect", leaf.Rect);
                _shader.SetFloat("uVertexSpacing", leaf.VertexSpacing);

                _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)mesh.IndexCount,
                    DrawElementsType.UnsignedInt, null, (uint)perLeaf);
                _count += (int)(perLeaf * thin); // ~kept objects (culled sites discard in the fragment shader)
            }
        }
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Decide which spawners are active on this world: the environment-trait gate (Require/Forbid)
    /// AND a deterministic per-(world,spawner) presence roll against SpawnChance. Deterministic so a given
    /// world always looks the same (no flicker) and lowering a chance drops worlds out predictably.</summary>
    private void EvaluateActivation(CelestialBody body)
    {
        EnvTrait traits = body.Traits;
        foreach (Spawner sp in Spawners)
        {
            bool env = (traits & sp.Require) == sp.Require && (traits & sp.Forbid) == 0;
            double roll = Hashing.Range(Hashing.Combine(body.Seed, sp.Seed ^ 0x9E3779B1u), 0.0, 1.0);
            sp.ActiveHere = env && roll < Math.Clamp(sp.SpawnChance, 0f, 1f);
        }
    }

    // --- Mesh registry: small unit solids in mesh-local space with y in [-0.5,0.5] (base at -0.5). ---

    /// <summary>Per-vertex colour ramped by height (y in [-0.5,0.5]): base tone at the bottom, top tone at the
    /// crown. Gives canopies a lit crown / shaded underside and rocks a darker footing for free.</summary>
    private static float[] ColorByHeight(float[] pos, Vector3D<float> baseCol, Vector3D<float> topCol)
    {
        int n = pos.Length / 3;
        var col = new float[n * 3];
        for (int v = 0; v < n; v++)
        {
            float t = Math.Clamp(pos[v * 3 + 1] + 0.5f, 0f, 1f);
            col[v * 3 + 0] = baseCol.X + (topCol.X - baseCol.X) * t;
            col[v * 3 + 1] = baseCol.Y + (topCol.Y - baseCol.Y) * t;
            col[v * 3 + 2] = baseCol.Z + (topCol.Z - baseCol.Z) * t;
        }
        return col;
    }

    private unsafe ScatterMesh Build(float[] pos, float[] col, uint[] idx)
    {
        // Interleave position + colour into one static mesh VBO (6 floats/vertex). Locations 0 (pos) and 4
        // (colour) bind here at divisor 0; the per-instance attributes 1..3 are re-pointed at each terrain
        // leaf's VBO in Render, so the VAO keeps pos/colour on this buffer and instance data on the leaf.
        int n = pos.Length / 3;
        var data = new float[n * 6];
        for (int v = 0; v < n; v++)
        {
            data[v * 6 + 0] = pos[v * 3 + 0]; data[v * 6 + 1] = pos[v * 3 + 1]; data[v * 6 + 2] = pos[v * 3 + 2];
            data[v * 6 + 3] = col[v * 3 + 0]; data[v * 6 + 4] = col[v * 3 + 1]; data[v * 6 + 5] = col[v * 3 + 2];
        }

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);

        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);

        // Instance attributes 1..3 are re-pointed at each leaf's base VBO in Render; divisors fixed here.
        for (uint a = 1; a <= 3; a++) { _gl.EnableVertexAttribArray(a); _gl.VertexAttribDivisor(a, 1); }
        _gl.BindVertexArray(0);

        _ownedBuffers.Add(vbo);
        _ownedBuffers.Add(ebo);
        return new ScatterMesh(vao, idx.Length);
    }

    // --- Composite geometry: accumulate positions + per-vertex colours + indices into one buffer. ---

    private sealed class MeshData
    {
        public readonly List<float> Pos = new();
        public readonly List<float> Col = new();
        public readonly List<uint> Idx = new();
        public int Count => Pos.Count / 3;
        public void Vert(Vector3D<float> p, Vector3D<float> c)
        {
            Pos.Add(p.X); Pos.Add(p.Y); Pos.Add(p.Z);
            Col.Add(c.X); Col.Add(c.Y); Col.Add(c.Z);
        }
    }

    // Unit icosphere (subdivided icosahedron) as unit-direction verts + triangle faces. sub=1 -> 42 verts /
    // 80 faces: reads as a rounded solid, cheap, and facets nicely under the fragment's flat (derivative) normal.
    private static (List<Vector3D<float>> V, List<(int, int, int)> F) Icosphere(int sub)
    {
        float t = (1f + MathF.Sqrt(5f)) * 0.5f;
        var v = new List<Vector3D<float>> {
            N(-1, t, 0), N(1, t, 0), N(-1, -t, 0), N(1, -t, 0),
            N(0, -1, t), N(0, 1, t), N(0, -1, -t), N(0, 1, -t),
            N(t, 0, -1), N(t, 0, 1), N(-t, 0, -1), N(-t, 0, 1),
        };
        var f = new List<(int, int, int)> {
            (0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),
            (1,5,9),(5,11,4),(11,10,2),(10,7,6),(7,1,8),
            (3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),
            (4,9,5),(2,4,11),(6,2,10),(8,6,7),(9,8,1),
        };
        for (int s = 0; s < sub; s++)
        {
            var nf = new List<(int, int, int)>();
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                v.Add(Vector3D.Normalize(v[a] + v[b])); mid[key] = v.Count - 1; return v.Count - 1;
            }
            foreach (var (a, b, c) in f)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                nf.Add((a, ab, ca)); nf.Add((b, bc, ab)); nf.Add((c, ca, bc)); nf.Add((ab, bc, ca));
            }
            f = nf;
        }
        return (v, f);

        static Vector3D<float> N(float x, float y, float z) => Vector3D.Normalize(new Vector3D<float>(x, y, z));
    }

    // Deterministic low-frequency lumpiness for a direction (summed sines) — pushes an icosphere off-round
    // into an irregular boulder / canopy blob. Per-instance random orientation hides the shared base shape.
    private static float Lumps(Vector3D<float> d)
        => 0.34f * MathF.Sin(1.7f * d.X + 0.3f)
         + 0.30f * MathF.Sin(2.3f * d.Y + 1.1f)
         + 0.28f * MathF.Sin(2.9f * d.Z + 2.2f)
         + 0.18f * MathF.Sin(4.1f * d.X + 3.4f) * MathF.Sin(3.7f * d.Z + 0.7f);

    private static Vector3D<float> Lerp3(Vector3D<float> a, Vector3D<float> b, float t) => a + (b - a) * t;

    /// <summary>Append a lumpy icosphere blob (boulder or canopy), radius scaled per-axis and displaced by
    /// <see cref="Lumps"/>. Colour ramps base->crown over the blob's own height so undersides shade.</summary>
    private static void AddBlob(MeshData md, Vector3D<float> center, Vector3D<float> radius, float lump,
                                Vector3D<float> colLow, Vector3D<float> colHigh, int sub)
    {
        var (V, F) = Icosphere(sub);
        int b = md.Count;
        foreach (Vector3D<float> dir in V)
        {
            float r = 1f + lump * Lumps(dir);
            md.Vert(center + new Vector3D<float>(dir.X * radius.X * r, dir.Y * radius.Y * r, dir.Z * radius.Z * r),
                    Lerp3(colLow, colHigh, Math.Clamp(dir.Y * 0.5f + 0.5f, 0f, 1f)));
        }
        foreach (var (i0, i1, i2) in F) { md.Idx.Add((uint)(b + i0)); md.Idx.Add((uint)(b + i1)); md.Idx.Add((uint)(b + i2)); }
    }

    /// <summary>Append a tapered cylinder y0(r0) -> y1(r1). Trunks.</summary>
    private static void AddCylinder(MeshData md, float y0, float y1, float r0, float r1, int seg, Vector3D<float> col)
    {
        int b = md.Count;
        for (int k = 0; k < seg; k++)
        {
            float a = (float)(k * 2.0 * Math.PI / seg);
            float cx = MathF.Cos(a), cz = MathF.Sin(a);
            md.Vert(new Vector3D<float>(cx * r0, y0, cz * r0), col);
            md.Vert(new Vector3D<float>(cx * r1, y1, cz * r1), col);
        }
        for (int k = 0; k < seg; k++)
        {
            int lo0 = b + k * 2, hi0 = lo0 + 1, lo1 = b + ((k + 1) % seg) * 2, hi1 = lo1 + 1;
            md.Idx.Add((uint)lo0); md.Idx.Add((uint)lo1); md.Idx.Add((uint)hi1);
            md.Idx.Add((uint)lo0); md.Idx.Add((uint)hi1); md.Idx.Add((uint)hi0);
        }
    }

    /// <summary>Append a cone skirt (apex at yApex, ring at yBase/rBase). Conifer tiers.</summary>
    private static void AddCone(MeshData md, float yBase, float yApex, float rBase, int seg,
                                Vector3D<float> colBase, Vector3D<float> colApex)
    {
        int apex = md.Count;
        md.Vert(new Vector3D<float>(0, yApex, 0), colApex);
        int ring = md.Count;
        for (int k = 0; k < seg; k++)
        {
            float a = (float)(k * 2.0 * Math.PI / seg);
            md.Vert(new Vector3D<float>(MathF.Cos(a) * rBase, yBase, MathF.Sin(a) * rBase), colBase);
        }
        for (int k = 0; k < seg; k++)
        {
            int r0 = ring + k, r1 = ring + (k + 1) % seg;
            md.Idx.Add((uint)apex); md.Idx.Add((uint)r0); md.Idx.Add((uint)r1);
        }
    }

    private ScatterMesh BuildBoulder()
    {
        // Centre the rock ON the placement pivot (y=-0.5), so it sits half-embedded in the ground and the
        // per-instance random tumble (Orient=2) spins it about that centre — it can never lift off the surface.
        var md = new MeshData();
        AddBlob(md, new Vector3D<float>(0f, -0.5f, 0f), new Vector3D<float>(0.5f, 0.44f, 0.5f), 0.34f,
                new Vector3D<float>(0.26f, 0.25f, 0.23f), new Vector3D<float>(0.52f, 0.50f, 0.47f), sub: 1);
        return Build(md.Pos.ToArray(), md.Col.ToArray(), md.Idx.ToArray());
    }

    private ScatterMesh BuildBroadleaf()
    {
        // Bark trunk + a single lumpy green canopy blob.
        var md = new MeshData();
        Vector3D<float> bark = new(0.32f, 0.22f, 0.13f);
        AddCylinder(md, -0.5f, 0.02f, 0.07f, 0.05f, 6, bark);
        AddBlob(md, new Vector3D<float>(0f, 0.22f, 0f), new Vector3D<float>(0.42f, 0.40f, 0.42f), 0.28f,
                new Vector3D<float>(0.13f, 0.28f, 0.10f), new Vector3D<float>(0.34f, 0.58f, 0.22f), sub: 1);
        return Build(md.Pos.ToArray(), md.Col.ToArray(), md.Idx.ToArray());
    }

    private ScatterMesh BuildConifer()
    {
        // Short trunk + three stacked cone tiers.
        var md = new MeshData();
        Vector3D<float> bark = new(0.30f, 0.20f, 0.12f);
        Vector3D<float> lo = new(0.10f, 0.24f, 0.10f), hi = new(0.28f, 0.50f, 0.20f);
        AddCylinder(md, -0.5f, -0.20f, 0.055f, 0.04f, 6, bark);
        AddCone(md, -0.30f, 0.10f, 0.34f, 9, lo, hi);
        AddCone(md,  0.02f, 0.34f, 0.24f, 9, lo, hi);
        AddCone(md,  0.26f, 0.55f, 0.15f, 9, lo, hi);
        return Build(md.Pos.ToArray(), md.Col.ToArray(), md.Idx.ToArray());
    }

    private ScatterMesh BuildCube()
    {
        float[] p = {
            -0.5f, -0.5f, -0.5f,  0.5f, -0.5f, -0.5f,  0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
            -0.5f, -0.5f,  0.5f,  0.5f, -0.5f,  0.5f,  0.5f, 0.5f,  0.5f, -0.5f, 0.5f,  0.5f,
        };
        uint[] i = {
            0,1,2, 0,2,3,   4,6,5, 4,7,6,   0,4,5, 0,5,1,
            3,2,6, 3,6,7,   1,5,6, 1,6,2,   0,3,7, 0,7,4,
        };
        return Build(p, ColorByHeight(p, new(0.34f, 0.32f, 0.29f), new(0.48f, 0.45f, 0.41f)), i); // generic stone/dirt
    }

    private ScatterMesh BuildTetra()
    {
        float[] p = {
            0f, 0.5f, 0f,
            0f, -0.5f, 0.5f,
            -0.433f, -0.5f, -0.25f,
            0.433f, -0.5f, -0.25f,
        };
        uint[] i = { 0,1,2, 0,2,3, 0,3,1, 1,3,2 };
        return Build(p, ColorByHeight(p, new(0.90f, 0.74f, 0.22f), new(0.90f, 0.74f, 0.22f)), i); // bright amber pickup
    }

    public void Dispose()
    {
        foreach (ScatterMesh m in _meshes) _gl.DeleteVertexArray(m.Vao);
        foreach (uint b in _ownedBuffers) _gl.DeleteBuffer(b);
        _shader.Dispose();
    }
}
