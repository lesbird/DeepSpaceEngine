using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws a flat list of <see cref="AsteroidInstance"/>s with hybrid level-of-detail: asteroids large
/// enough on screen are instanced, noise-displaced, lit 3D rocks; smaller ones are soft point sprites.
/// In the thin transition band an asteroid is drawn as both, cross-faded by projected size, so rocks
/// dissolve into the sprite cloud (and back) with no pop. Each rock's lumpy shape and per-frame tumble
/// come entirely from instance attributes over a single shared unit-sphere mesh.
///
/// Lighting: with a sun present (an in-system belt) rocks are lit from the star; without one (a
/// deep-space cluster) they fall back to a soft fixed key/fill so their silhouette still reads.
/// </summary>
public sealed class AsteroidRenderer : IDisposable
{
    // ---- 3D rock: shared unit sphere + per-instance transform/shape/shade ----
    private const string RockVertex = @"#version 410 core
layout(location = 0) in vec3 aPos;     // unit sphere
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec3 iRelPos;  // per-instance, camera-relative metres
layout(location = 3) in vec3 iBasisX;  // per-instance orientation (rotated unit axes)
layout(location = 4) in vec3 iBasisY;
layout(location = 5) in vec3 iBasisZ;
layout(location = 6) in vec4 iData;    // x=radius(m), y=shapeSeed, z=fade
layout(location = 7) in vec3 iColor;
uniform mat4 uViewProj;
out vec3 vNormal;
out vec3 vColor;
out vec3 vPosW;
out float vFade;

float hash(vec3 p){ p=fract(p*0.3183099+vec3(0.1,0.2,0.3)); p*=17.0; return fract(p.x*p.y*p.z*(p.x+p.y+p.z)); }
float vnoise(vec3 x){ vec3 i=floor(x),f=fract(x); f=f*f*(3.0-2.0*f);
  return mix(mix(mix(hash(i+vec3(0,0,0)),hash(i+vec3(1,0,0)),f.x),
                 mix(hash(i+vec3(0,1,0)),hash(i+vec3(1,1,0)),f.x),f.y),
             mix(mix(hash(i+vec3(0,0,1)),hash(i+vec3(1,0,1)),f.x),
                 mix(hash(i+vec3(0,1,1)),hash(i+vec3(1,1,1)),f.x),f.y),f.z); }
// Radial scale of the lumpy surface in direction d (a unit vector), seeded per rock.
float lump(vec3 d, float seed){
  vec3 p = d*2.3 + vec3(seed*1.7, seed*0.7, seed*2.3);
  float n = vnoise(p)*0.6 + vnoise(p*2.1)*0.3 + vnoise(p*4.3)*0.15;
  return 0.5 + 1.0*n;   // never collapses to zero
}
void main(){
  float seed = iData.y;
  vec3 d = normalize(aPos);
  vec3 localPos = d * lump(d, seed);

  // Recompute the normal from finite differences of the displaced surface so lighting follows
  // the bumps, not the base sphere.
  vec3 t1 = normalize(abs(d.y) < 0.9 ? cross(d, vec3(0,1,0)) : cross(d, vec3(1,0,0)));
  vec3 t2 = cross(d, t1);
  float e = 0.07;
  vec3 da = normalize(d + t1*e); vec3 va = da * lump(da, seed);
  vec3 db = normalize(d + t2*e); vec3 vb = db * lump(db, seed);
  vec3 nrm = normalize(cross(va - localPos, vb - localPos));
  if (dot(nrm, d) < 0.0) nrm = -nrm;

  mat3 basis = mat3(iBasisX, iBasisY, iBasisZ);
  vec3 worldPos = iRelPos + basis * (localPos * iData.x);
  vNormal = normalize(basis * nrm);
  vColor = iColor;
  vPosW = worldPos;
  vFade = iData.z;
  gl_Position = uViewProj * vec4(worldPos, 1.0);
}";

    private const string RockFragment = @"#version 410 core
in vec3 vNormal;
in vec3 vColor;
in vec3 vPosW;
in float vFade;
uniform vec3 uSunRel;   // camera-relative sun position
uniform int  uHasSun;
out vec4 FragColor;
void main(){
  vec3 N = normalize(vNormal);
  float light;
  if (uHasSun == 1){
    vec3 L = normalize(uSunRel - vPosW);
    light = max(dot(N, L), 0.0);
  } else {
    // No star nearby: a soft key plus a dim fill from the opposite side.
    vec3 K = normalize(vec3(0.4, 0.7, 0.5));
    light = max(dot(N, K), 0.0) * 0.85 + max(dot(N, -K), 0.0) * 0.15;
  }
  vec3 col = vColor * (0.08 + light);
  FragColor = vec4(col, vFade);
}";

    // ---- Far sprite: one GL point per asteroid, soft lit dot (not emissive) ----
    private const string SpriteVertex = @"#version 410 core
layout(location = 0) in vec3 aRelPos;
layout(location = 1) in vec3 aColor;
layout(location = 2) in vec2 aData;   // x=radius(m), y=fade
uniform mat4 uViewProj;
uniform float uPixelScale;  // viewportHeight / (2 tan(fov/2))
uniform float uMinSize;
uniform float uMaxSize;
out vec3 vColor;
out float vFade;
void main(){
  gl_Position = uViewProj * vec4(aRelPos, 1.0);
  float dist = max(length(aRelPos), 1.0);
  float pr = uPixelScale * aData.x / dist;        // projected radius, px
  gl_PointSize = clamp(pr * 2.0, uMinSize, uMaxSize);
  vColor = aColor;
  vFade = aData.y;
}";

    private const string SpriteFragment = @"#version 410 core
in vec3 vColor;
in float vFade;
out vec4 FragColor;
void main(){
  vec2 c = gl_PointCoord * 2.0 - 1.0;
  float r2 = dot(c, c);
  float a = smoothstep(1.0, 0.2, r2);
  FragColor = vec4(vColor, a * vFade);
}";

    // Projected-radius (px) cross-fade band between sprite and 3D rock.
    private const float BandLoPx = 1.6f;
    private const float BandHiPx = 4.0f;
    // No pixel-size cull: an AU-scale belt's rocks are each far smaller than a pixel, yet thousands
    // of them overlap into a visible dust band. We cull only by distance (the caller's maxDist), and
    // draw every collected rock as at least a faint minimum-size dot.
    private const float HazeFloorAlpha = 0.22f; // min sprite alpha so distant rocks still form a band
    private const float SpriteMinPx = 2.5f;
    private const float SpriteMaxPx = 16.0f;
    private const int MaxRocks = 6000;    // cap on simultaneously-instanced 3D rocks
    private const int RockFloats = 19;    // relPos3 + basis3x3 + data4 + color3
    private const int SpriteFloats = 8;   // relPos3 + color3 + data2

    private readonly GL _gl;
    private readonly Shader _rockShader;
    private readonly Shader _spriteShader;

    private readonly uint _rockVao;
    private readonly uint _rockBaseVbo;
    private readonly uint _rockInstanceVbo;
    private readonly uint _baseVertexCount;

    private readonly uint _spriteVao;
    private readonly uint _spriteVbo;

    private float[] _rockData = new float[RockFloats * 1024];
    private float[] _spriteData = new float[SpriteFloats * 4096];

    public bool Enabled = true;
    public int LastRocks { get; private set; }
    public int LastSprites { get; private set; }

    public unsafe AsteroidRenderer(GL gl)
    {
        _gl = gl;
        _rockShader = new Shader(gl, RockVertex, RockFragment);
        _spriteShader = new Shader(gl, SpriteVertex, SpriteFragment);

        // --- Base unit-sphere mesh (position + normal), low-poly; the lumps come from the shader.
        float[] sphere = BuildUnitSphere(stacks: 14, slices: 20);
        _baseVertexCount = (uint)(sphere.Length / 6);

        _rockVao = gl.GenVertexArray();
        gl.BindVertexArray(_rockVao);

        _rockBaseVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _rockBaseVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)sphere, BufferUsageARB.StaticDraw);
        uint baseStride = 6 * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, baseStride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, baseStride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);

        // Per-instance attributes (divisor 1) from a separate streaming buffer.
        _rockInstanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _rockInstanceVbo);
        uint s = RockFloats * sizeof(float);
        SetInstanceAttrib(gl, 2, 3, s, 0);   // iRelPos
        SetInstanceAttrib(gl, 3, 3, s, 3);   // iBasisX
        SetInstanceAttrib(gl, 4, 3, s, 6);   // iBasisY
        SetInstanceAttrib(gl, 5, 3, s, 9);   // iBasisZ
        SetInstanceAttrib(gl, 6, 4, s, 12);  // iData
        SetInstanceAttrib(gl, 7, 3, s, 16);  // iColor
        gl.BindVertexArray(0);

        // --- Sprite VAO: one point per asteroid, attributes per vertex (no instancing).
        _spriteVao = gl.GenVertexArray();
        gl.BindVertexArray(_spriteVao);
        _spriteVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
        uint ss = SpriteFloats * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, ss, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, ss, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, ss, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
    }

    private static unsafe void SetInstanceAttrib(GL gl, uint index, int size, uint stride, int offsetFloats)
    {
        gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)(offsetFloats * sizeof(float)));
        gl.EnableVertexAttribArray(index);
        gl.VertexAttribDivisor(index, 1);
    }

    /// <summary>
    /// Draw <paramref name="instances"/> (already camera-relative) using the supplied view-projection.
    /// <paramref name="sunRel"/> is the camera-relative sun position when <paramref name="hasSun"/> is
    /// true (in-system belt); otherwise rocks use ambient/fill lighting (deep-space cluster).
    /// Depth state is left to the caller; this manages only blend + point-size enables.
    /// </summary>
    public unsafe void Render(Camera camera, IReadOnlyList<AsteroidInstance> instances,
        in Matrix4X4<float> viewProj, Vector3D<float> sunRel, bool hasSun, int viewportHeight)
    {
        LastRocks = 0;
        LastSprites = 0;
        if (instances.Count == 0) return;

        float pixelScale = viewportHeight / (2f * MathF.Tan(camera.FovRadians * 0.5f));

        EnsureCapacity(instances.Count);
        int rockCount = 0, spriteCount = 0;

        for (int i = 0; i < instances.Count; i++)
        {
            AsteroidInstance a = instances[i];
            float dist = a.RelPos.Length;
            if (dist < 1f) dist = 1f;
            float pr = pixelScale * a.Radius / dist; // projected radius in px

            float nearFade = Smoothstep(BandLoPx, BandHiPx, pr); // 1 = full rock, 0 = full sprite

            if (nearFade > 0.004f && rockCount < MaxRocks)
                WriteRock(ref rockCount, a, nearFade);
            else if (nearFade > 0.004f)
                nearFade = 0f; // rock budget spent — fall back to a sprite

            if (nearFade < 0.996f)
            {
                // Sub-pixel rocks are dimmed by their coverage (with a small floor) so a distant belt
                // reads as a faint dust band rather than a wall of identical full-bright dots.
                float coverage = Math.Clamp(pr, HazeFloorAlpha, 1f);
                WriteSprite(ref spriteCount, a, (1f - nearFade) * coverage);
            }
        }

        LastRocks = rockCount;
        LastSprites = spriteCount;
        if (rockCount == 0 && spriteCount == 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // --- Far sprites first: depth-tested (so a near rock can cover them) but no depth writes.
        if (spriteCount > 0)
        {
            _gl.Enable(EnableCap.ProgramPointSize);
            _gl.DepthMask(false);
            _spriteShader.Use();
            _spriteShader.SetMatrix("uViewProj", viewProj);
            _spriteShader.SetFloat("uPixelScale", pixelScale);
            _spriteShader.SetFloat("uMinSize", SpriteMinPx);
            _spriteShader.SetFloat("uMaxSize", SpriteMaxPx);
            _gl.BindVertexArray(_spriteVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(_spriteData, 0, spriteCount * SpriteFloats), BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.Points, 0, (uint)spriteCount);
        }

        // --- Near 3D rocks: solid, depth-tested and depth-writing (they occlude correctly).
        if (rockCount > 0)
        {
            _gl.DepthMask(true);
            _rockShader.Use();
            _rockShader.SetMatrix("uViewProj", viewProj);
            _rockShader.SetVector3("uSunRel", sunRel);
            _rockShader.SetInt("uHasSun", hasSun ? 1 : 0);
            _gl.BindVertexArray(_rockVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _rockInstanceVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(_rockData, 0, rockCount * RockFloats), BufferUsageARB.StreamDraw);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, _baseVertexCount, (uint)rockCount);
        }

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
    }

    private void WriteRock(ref int count, in AsteroidInstance a, float fade)
    {
        // Orientation: tumble the rock about its spin axis. Transform the unit axes by the spin
        // quaternion to get the basis columns the vertex shader multiplies (no matrix-convention pitfalls).
        var q = Quaternion<float>.CreateFromAxisAngle(a.SpinAxis, a.SpinAngle);
        Vector3D<float> bx = Vector3D.Transform(Vector3D<float>.UnitX, q);
        Vector3D<float> by = Vector3D.Transform(Vector3D<float>.UnitY, q);
        Vector3D<float> bz = Vector3D.Transform(Vector3D<float>.UnitZ, q);

        int o = count * RockFloats;
        _rockData[o + 0] = a.RelPos.X; _rockData[o + 1] = a.RelPos.Y; _rockData[o + 2] = a.RelPos.Z;
        _rockData[o + 3] = bx.X; _rockData[o + 4] = bx.Y; _rockData[o + 5] = bx.Z;
        _rockData[o + 6] = by.X; _rockData[o + 7] = by.Y; _rockData[o + 8] = by.Z;
        _rockData[o + 9] = bz.X; _rockData[o + 10] = bz.Y; _rockData[o + 11] = bz.Z;
        _rockData[o + 12] = a.Radius; _rockData[o + 13] = a.ShapeSeed; _rockData[o + 14] = fade; _rockData[o + 15] = 0f;
        _rockData[o + 16] = a.Color.X; _rockData[o + 17] = a.Color.Y; _rockData[o + 18] = a.Color.Z;
        count++;
    }

    private void WriteSprite(ref int count, in AsteroidInstance a, float fade)
    {
        int o = count * SpriteFloats;
        _spriteData[o + 0] = a.RelPos.X; _spriteData[o + 1] = a.RelPos.Y; _spriteData[o + 2] = a.RelPos.Z;
        _spriteData[o + 3] = a.Color.X; _spriteData[o + 4] = a.Color.Y; _spriteData[o + 5] = a.Color.Z;
        _spriteData[o + 6] = a.Radius; _spriteData[o + 7] = fade;
        count++;
    }

    private void EnsureCapacity(int instanceCount)
    {
        if (_rockData.Length < instanceCount * RockFloats)
            Array.Resize(ref _rockData, instanceCount * RockFloats);
        if (_spriteData.Length < instanceCount * SpriteFloats)
            Array.Resize(ref _spriteData, instanceCount * SpriteFloats);
    }

    /// <summary>Fit a perspective projection to the instance distances (for the deep-space pass that
    /// has no system projection of its own). Returns the engine's view*proj product.</summary>
    public static Matrix4X4<float> FitProjection(Camera camera, IReadOnlyList<AsteroidInstance> instances)
    {
        float minD = float.MaxValue, maxD = 0f;
        for (int i = 0; i < instances.Count; i++)
        {
            float d = instances[i].RelPos.Length;
            float r = instances[i].Radius;
            minD = MathF.Min(minD, MathF.Max(1f, d - r));
            maxD = MathF.Max(maxD, d + r);
        }
        if (maxD <= 0f) { minD = 1f; maxD = 1e9f; }

        float near = MathF.Max(1f, minD * 0.5f);
        float far = MathF.Max(maxD * 1.3f, near * 10f);
        if (far / near > 5.0e6f) near = far / 5.0e6f; // keep 24-bit depth usable
        Matrix4X4<float> proj = MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far);
        return camera.ViewMatrix * proj;
    }

    private static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Non-indexed unit UV-sphere as [pos.xyz, normal.xyz] floats (normal == position).</summary>
    private static float[] BuildUnitSphere(int stacks, int slices)
    {
        var v = new List<float>();
        (float x, float y, float z) P(int i, int j)
        {
            float phi = MathF.PI * (i / (float)stacks) - MathF.PI / 2f;
            float theta = 2f * MathF.PI * (j / (float)slices);
            float cp = MathF.Cos(phi);
            return (cp * MathF.Cos(theta), MathF.Sin(phi), cp * MathF.Sin(theta));
        }
        void Vert((float x, float y, float z) p) => v.AddRange(new[] { p.x, p.y, p.z, p.x, p.y, p.z });

        for (int i = 0; i < stacks; i++)
        for (int j = 0; j < slices; j++)
        {
            var a = P(i, j); var b = P(i + 1, j); var c = P(i + 1, j + 1); var d = P(i, j + 1);
            Vert(a); Vert(b); Vert(c);
            Vert(a); Vert(c); Vert(d);
        }
        return v.ToArray();
    }

    public void Dispose()
    {
        _rockShader.Dispose();
        _spriteShader.Dispose();
        _gl.DeleteBuffer(_rockBaseVbo);
        _gl.DeleteBuffer(_rockInstanceVbo);
        _gl.DeleteBuffer(_spriteVbo);
        _gl.DeleteVertexArray(_rockVao);
        _gl.DeleteVertexArray(_spriteVao);
    }
}
