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
    public const string MeshCombo = "Cube\0Cone (tree)\0Rock\0Tetra (pickup)\0";

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
out vec3 vWorld;
out float vKeep;
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

    // aCorner.y in [-0.5,0.5] → +0.5 puts the base on the surface and grows the object up along 'up'.
    vec3 local = r2 * aCorner.x + up * (aCorner.y + 0.5) + f2 * aCorner.z;
    vec3 world = base + local * s;
    vWorld = world;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

    private const string Fragment = @"#version 410 core
in vec3 vWorld;
in float vKeep;
uniform vec3 uSunDir;
out vec4 FragColor;
void main() {
    if (vKeep < 0.5) discard;
    vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));  // flat per-face normal
    float light = 0.35 + 0.65 * abs(dot(n, normalize(uSunDir)));
    FragColor = vec4(vec3(1.0) * light, 1.0);              // opaque white, lit (debug)
}";

    public ScatterRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);
        _meshes = new[] { BuildCube(), BuildCone(8), BuildOcta(), BuildTetra() };

        // Sensible starter set; replaced wholesale if tuning.json carries a saved list.
        Spawners.Add(new Spawner { Name = "Rocks",   MeshId = 2, Orient = 2, Density = 0.25f, MinSize = 2f, MaxSize = 6f,  Seed = 11, Require = EnvTrait.Surface });
        Spawners.Add(new Spawner { Name = "Trees",   MeshId = 1, Orient = 0, Density = 0.50f, MinSize = 6f, MaxSize = 14f, Seed = 22, Require = EnvTrait.Life });
        Spawners.Add(new Spawner { Name = "Pickups", MeshId = 3, Orient = 1, Density = 0.10f, MinSize = 2f, MaxSize = 3f,  Seed = 33, Require = EnvTrait.Surface, SpawnChance = 0.4f });
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

        foreach (Spawner sp in Spawners)
        {
            if (!sp.Enabled || !sp.ActiveHere) continue;
            ScatterMesh mesh = _meshes[Math.Clamp(sp.MeshId, 0, _meshes.Length - 1)];

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

    private unsafe ScatterMesh Build(float[] pos, uint[] idx)
    {
        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, pos, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);

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
        return Build(p, i);
    }

    private ScatterMesh BuildCone(int seg)
    {
        var p = new List<float> { 0f, 0.5f, 0f,  0f, -0.5f, 0f };   // 0 = apex, 1 = base centre
        for (int k = 0; k < seg; k++)
        {
            float a = (float)(k * 2.0 * Math.PI / seg);
            p.Add(0.5f * MathF.Cos(a)); p.Add(-0.5f); p.Add(0.5f * MathF.Sin(a));
        }
        var i = new List<uint>();
        for (uint k = 0; k < seg; k++)
        {
            uint r0 = 2 + k, r1 = 2 + (k + 1) % (uint)seg;
            i.Add(0); i.Add(r0); i.Add(r1);   // side
            i.Add(1); i.Add(r1); i.Add(r0);   // base cap
        }
        return Build(p.ToArray(), i.ToArray());
    }

    private ScatterMesh BuildOcta()
    {
        float[] p = {
            0f, 0.5f, 0f,  0f, -0.5f, 0f,  0.5f, 0f, 0f,  -0.5f, 0f, 0f,  0f, 0f, 0.5f,  0f, 0f, -0.5f,
        };
        uint[] i = {
            0,2,4, 0,4,3, 0,3,5, 0,5,2,   1,4,2, 1,3,4, 1,5,3, 1,2,5,
        };
        return Build(p, i);
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
        return Build(p, i);
    }

    public void Dispose()
    {
        foreach (ScatterMesh m in _meshes) _gl.DeleteVertexArray(m.Vao);
        foreach (uint b in _ownedBuffers) _gl.DeleteBuffer(b);
        _shader.Dispose();
    }
}
