using System;
using Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Screen-space ambient occlusion. The forward renderer keeps no G-buffer, so this pass reconstructs
/// view-space position and a normal from the scene <b>depth texture</b> alone, samples a hemisphere of
/// points around each pixel, and counts how many fall behind nearby geometry — darkening creases,
/// crater floors, rock bases and the contact where one body meets another. Runs at half resolution
/// with a per-pixel rotated kernel, then a small box blur removes the rotation noise. The result is a
/// single-channel occlusion texture the bloom composite multiplies the scene by.
///
/// Reconstruction uses the SAME near/far the dominant geometry's projection wrote, passed in per frame
/// (as the atmosphere pass does), so the linearised depth is correct whether the system or the terrain
/// projection owns the buffer. Sky pixels (depth at the far plane) short-circuit to fully lit.
/// </summary>
public sealed class SsaoRenderer : IDisposable
{
    // OFF by default: at planetary scale the scene depth spans metres-to-thousands-of-km, and that huge
    // near/far range quantises the depth buffer enough that the per-pixel normal reconstructed from depth
    // derivatives dissolves into banding/noise on distant terrain. Robust planetary SSAO needs a linear-
    // depth prepass and distance-gated application; until then this stays an opt-in HUD experiment.
    public bool Enabled = false;
    /// <summary>Sample radius in view-space metres — the scale of crease the AO reacts to.</summary>
    public float Radius = 2.0f;
    /// <summary>Depth bias (metres) to avoid self-occlusion on flat surfaces.</summary>
    public float Bias = 0.05f;
    /// <summary>Contrast of the occlusion (higher = darker, tighter creases).</summary>
    public float Power = 1.6f;
    /// <summary>How strongly the AO darkens the scene in the composite (0 = off).</summary>
    public float Strength = 0.6f;

    private const int KernelSize = 16;

    private const string FullscreenVert = @"#version 410 core
out vec2 vUV;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vUV = p * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string AoFrag = @"#version 410 core
in vec2 vUV;
uniform sampler2D uDepth;
uniform mat4  uProj;         // same projection (near/far) that wrote the depth
uniform float uNear, uFar;
uniform float uTanHalf, uAspect;
uniform float uRadius, uBias, uPower;
uniform vec3  uKernel[16];
out vec4 FragColor;

float linstep(float d) {                             // window depth [0,1] -> positive view distance
    float z = d * 2.0 - 1.0;
    return (2.0 * uNear * uFar) / (uFar + uNear - z * (uFar - uNear));
}
vec3 viewPos(vec2 uv) {
    float lin = linstep(texture(uDepth, uv).r);
    vec2 ndc = uv * 2.0 - 1.0;
    return vec3(ndc.x * uTanHalf * uAspect, ndc.y * uTanHalf, -1.0) * lin;  // z = -lin
}
float hash(vec2 p) { return fract(sin(dot(p, vec2(41.13, 289.7))) * 43758.5453); }

void main() {
    float d0 = texture(uDepth, vUV).r;
    if (d0 >= 0.99999) { FragColor = vec4(1.0); return; }   // sky / far plane — never occluded

    vec3 P = viewPos(vUV);
    vec3 N = normalize(cross(dFdx(P), dFdy(P)));
    if (N.z < 0.0) N = -N;                                   // face the camera (+Z in view space)

    // Per-pixel rotation of the kernel about the normal, so 16 samples cover more directions.
    float rnd = hash(gl_FragCoord.xy) * 6.2831853;
    vec3 randv = vec3(cos(rnd), sin(rnd), 0.0);
    vec3 T = normalize(randv - N * dot(randv, N));
    vec3 B = cross(N, T);
    mat3 TBN = mat3(T, B, N);

    float occ = 0.0;
    for (int i = 0; i < 16; i++) {
        vec3 sp = P + (TBN * uKernel[i]) * uRadius;          // view-space sample in the hemisphere
        vec4 clip = uProj * vec4(sp, 1.0);
        if (clip.w <= 0.0) continue;
        vec2 suv = (clip.xy / clip.w) * 0.5 + 0.5;
        if (suv.x < 0.0 || suv.x > 1.0 || suv.y < 0.0 || suv.y > 1.0) continue;
        float sceneZ = -linstep(texture(uDepth, suv).r);     // geometry view-Z at that screen point
        // Occluded when real geometry sits in front of the sample; range check ignores far background.
        float range = smoothstep(0.0, 1.0, uRadius / max(abs(P.z - sceneZ), 1e-4));
        occ += (sceneZ >= sp.z + uBias ? 1.0 : 0.0) * range;
    }
    float ao = pow(clamp(1.0 - occ / 16.0, 0.0, 1.0), uPower);
    FragColor = vec4(ao, ao, ao, 1.0);
}";

    private const string BlurFrag = @"#version 410 core
in vec2 vUV;
uniform sampler2D uAo;
uniform vec2 uTexel;
out vec4 FragColor;
void main() {
    // 4x4 box blur to wash out the per-pixel kernel-rotation noise.
    float sum = 0.0;
    for (int y = -2; y < 2; y++)
        for (int x = -2; x < 2; x++)
            sum += texture(uAo, vUV + vec2(x, y) * uTexel).r;
    float ao = sum / 16.0;
    FragColor = vec4(ao, ao, ao, 1.0);
}";

    private readonly GL _gl;
    private readonly Shader _ao;
    private readonly Shader _blur;
    private readonly uint _vao;
    private readonly ColorTarget _raw;    // half-res raw AO
    private readonly ColorTarget _blurred;
    private readonly float[] _kernel = new float[KernelSize * 3];

    public SsaoRenderer(GL gl)
    {
        _gl = gl;
        _ao = new Shader(gl, FullscreenVert, AoFrag);
        _blur = new Shader(gl, FullscreenVert, BlurFrag);
        _vao = gl.GenVertexArray();
        _raw = new ColorTarget(gl);
        _blurred = new ColorTarget(gl);

        // A fixed hemisphere kernel (z+), clustered toward the origin so nearby geometry dominates.
        var rng = new Random(1234);
        for (int i = 0; i < KernelSize; i++)
        {
            var s = new Vector3D<float>(
                (float)(rng.NextDouble() * 2.0 - 1.0),
                (float)(rng.NextDouble() * 2.0 - 1.0),
                (float)rng.NextDouble());
            s = Vector3D.Normalize(s) * (float)rng.NextDouble();
            float t = i / (float)KernelSize;
            s *= 0.1f + 0.9f * t * t;                     // more samples close to the point
            _kernel[i * 3 + 0] = s.X;
            _kernel[i * 3 + 1] = s.Y;
            _kernel[i * 3 + 2] = s.Z;
        }
    }

    /// <summary>The most recently computed (blurred) AO texture — the composite always binds this; when
    /// SSAO is disabled the composite passes strength 0 so its stale contents are ignored.</summary>
    public uint AoTexture => _blurred.ColorTexture;

    public void Resize(int width, int height)
    {
        int hw = Math.Max(1, width / 2), hh = Math.Max(1, height / 2);
        _raw.Resize(hw, hh);
        _blurred.Resize(hw, hh);
    }

    /// <summary>
    /// Compute AO from the scene <paramref name="depthTex"/> using the projection that wrote it
    /// (<paramref name="near"/>/<paramref name="far"/> + the camera's fov/aspect). Returns the blurred
    /// AO texture. Leaves GL state (blend/depth) as it found it enough for the following composite.
    /// </summary>
    public uint Render(uint depthTex, Camera camera, float near, float far)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        // AO pass -> _raw (half res).
        _raw.Bind();
        _ao.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, depthTex);
        _ao.SetInt("uDepth", 0);
        _ao.SetMatrix("uProj", MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far));
        _ao.SetFloat("uNear", near);
        _ao.SetFloat("uFar", far);
        _ao.SetFloat("uTanHalf", MathF.Tan(camera.FovRadians * 0.5f));
        _ao.SetFloat("uAspect", camera.AspectRatio);
        _ao.SetFloat("uRadius", Radius);
        _ao.SetFloat("uBias", Bias);
        _ao.SetFloat("uPower", Power);
        for (int i = 0; i < KernelSize; i++)
            _ao.SetVector3($"uKernel[{i}]",
                new Vector3D<float>(_kernel[i * 3], _kernel[i * 3 + 1], _kernel[i * 3 + 2]));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Blur pass -> _blurred (half res).
        _blurred.Bind();
        _blur.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _raw.ColorTexture);
        _blur.SetInt("uAo", 0);
        _blur.SetVector2("uTexel", new Vector2D<float>(1f / _raw.Width, 1f / _raw.Height));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        return _blurred.ColorTexture;
    }

    public void Dispose()
    {
        _ao.Dispose();
        _blur.Dispose();
        _gl.DeleteVertexArray(_vao);
        _raw.Dispose();
        _blurred.Dispose();
    }
}
