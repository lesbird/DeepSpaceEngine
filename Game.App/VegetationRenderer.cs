using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Up-close surface vegetation, placed the only way that stays exactly on the GPU-drawn terrain: by
/// <b>instancing over the terrain's own vertices</b>. The terrain renderer hands us the near drawn leaves
/// (their base-mesh VBO + tile origin + camera-relative centre + morph); for each leaf we draw one billboard
/// per terrain vertex, and the vegetation vertex shader samples the SAME height tile the terrain mesh used
/// (<c>texelFetch(uHeight, tileOrigin + texel)</c>). So a tuft's base is the displaced surface point itself —
/// no analytic height mirror, no float divergence, no floating. (The CPU <c>GpuHeightAt</c> mirror diverges
/// from the baked tile by kilometres on high-relief worlds, which is why earlier lattice scatter floated.)
///
/// Currently in WHITE-CUBE debug mode to verify ground contact; switch the geometry/shader to grass blades
/// once the cubes sit on the surface.
/// </summary>
public sealed class VegetationRenderer : IDisposable
{
    public bool Enabled = true;
    public float Density = 1f;        // 0..1 keep-fraction: how many of the candidate sites actually spawn
    public float MinSize = 4f;        // each object's size is rolled uniformly in [MinSize, MaxSize] metres
    public float MaxSize = 10f;       // → size variety (e.g. small shrubs to tall trees)
    public int Orient = 0;            // 0 = world (radial) up, 1 = surface-normal up, 2 = random

    /// <summary>Objects scattered this frame (0 = none) — surfaced on the HUD so it's clear when active.</summary>
    public int Count => _count;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _cubeVbo, _ebo, _vao;
    private int _count;

    // One cube per terrain vertex; the vertex shader looks up that vertex's drawn height from the tile and
    // plants the cube there. Per-instance attributes (basePos/dir/texel) are RE-POINTED at each leaf's base
    // VBO every draw — that buffer is the terrain's own mesh, already resident on the GPU.
    private const string Vertex = @"#version 410 core
layout(location = 0) in vec3 aCorner;   // unit-cube corner (±0.5)
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
uniform int uOrient;        // 0 = world (radial) up, 1 = surface-normal up, 2 = random up
uniform int uFace;          // for the surface-normal slope basis
uniform vec4 uRect;         // (u0,v0,u1,v1) of this patch on the face
uniform float uGridN;
uniform float uVertexSpacing;
out vec3 vWorld;
out float vKeep;
float hash(vec2 p, float seed){ return fract(sin(dot(p, vec2(41.3, 289.1)) + seed) * 43758.5453); }
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

    public unsafe VegetationRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);

        float[] cube = {
            -0.5f, -0.5f, -0.5f,  0.5f, -0.5f, -0.5f,  0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
            -0.5f, -0.5f,  0.5f,  0.5f, -0.5f,  0.5f,  0.5f, 0.5f,  0.5f, -0.5f, 0.5f,  0.5f,
        };
        uint[] idx = {
            0,1,2, 0,2,3,   4,6,5, 4,7,6,   0,4,5, 0,5,1,
            3,2,6, 3,6,7,   1,5,6, 1,6,2,   0,3,7, 0,7,4,
        };
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _cubeVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cubeVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, cube, BufferUsageARB.StaticDraw);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);

        // Instance attributes 1..3 are re-pointed at each leaf's base VBO in Render; divisors are fixed here.
        for (uint a = 1; a <= 3; a++) { gl.EnableVertexAttribArray(a); gl.VertexAttribDivisor(a, 1); }
        gl.BindVertexArray(0);
    }

    /// <summary>Draw grass over every near drawn leaf the terrain reported this frame, instancing one tuft per
    /// terrain vertex with the height read from the leaf's own tile (so tufts sit exactly on the drawn surface).</summary>
    public unsafe void Render(Camera camera, CelestialBody? target, PlanetTerrainRenderer terrainRenderer,
                              Vector3D<float> sunDir, float near, float far, float time)
    {
        _count = 0;
        PlanetTerrain? terrain = terrainRenderer.ActiveTerrain;
        IReadOnlyList<PlanetTerrainRenderer.GrassLeaf> leaves = terrainRenderer.GrassLeaves;
        uint heightTex = terrainRenderer.HeightTexture;
        if (!Enabled || target == null || terrain == null || heightTex == 0 || leaves.Count == 0) return;

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
        float lo = Math.Max(0.1f, Math.Min(MinSize, MaxSize));
        float hi = Math.Max(lo, MaxSize);
        _shader.SetFloat("uMinSize", lo);
        _shader.SetFloat("uMaxSize", hi);
        float thin = Math.Clamp(Density, 0f, 1f);
        _shader.SetFloat("uThin", thin);
        _shader.SetInt("uOrient", Orient);
        _shader.SetFloat("uGridN", terrainRenderer.GrassGridN);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, heightTex);
        _shader.SetInt("uHeight", 0);

        _gl.BindVertexArray(_vao);
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

            _gl.DrawElementsInstanced(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, null, (uint)perLeaf);
            _count += (int)(perLeaf * thin);   // ~kept tufts (culled ones discard in the fragment shader)
        }
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_cubeVbo);
        _gl.DeleteBuffer(_ebo);
        _shader.Dispose();
    }
}
