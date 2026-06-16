using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws each planet and moon of the active system as a bright additive dot so you can tell an
/// object is there from across the system, long before its lit sphere resolves to more than a
/// pixel. The dot's on-screen size is distance-scaled but clamped, so it stays a small marker at
/// any range — never a visibly large blob. As you approach, the body's real projected disk grows;
/// once the disk reaches the dot's scale the glow fades out (computed per-vertex from the disk's
/// projected pixel size), handing the view over to the sphere the <see cref="SystemRenderer"/> draws.
/// </summary>
public sealed class PlanetGlowRenderer : IDisposable
{
    private const int FloatsPerBody = 7; // relPos(3) + color(3) + radius(1)

    private const string VertexSource = @"#version 410 core
layout(location = 0) in vec3 aRelPos;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aRadius;   // body radius in metres
uniform mat4 uViewProj;
uniform float uPixelScale;
uniform float uMinSize;
uniform float uMaxSize;
uniform float uViewportH;
uniform float uTanHalfFov;
uniform float uFadeStartPx;   // disk diameter (px) at which the glow starts to fade
uniform float uFadeEndPx;     // disk diameter (px) at which the glow is gone
out vec3 vColor;
out float vFade;
void main() {
    // Brighten toward white so even dark bodies read as a clear marker.
    vColor = mix(aColor, vec3(1.0), 0.35);
    gl_Position = uViewProj * vec4(aRelPos, 1.0);

    float dist = max(length(aRelPos), 1.0);
    gl_PointSize = clamp(uPixelScale * aRadius / dist, uMinSize, uMaxSize);

    // Approximate projected disk diameter in pixels; fade the glow as the real sphere overtakes it.
    float diskPx = (aRadius / dist) / uTanHalfFov * uViewportH;
    vFade = 1.0 - smoothstep(uFadeStartPx, uFadeEndPx, diskPx);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vColor;
in float vFade;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    if (vFade <= 0.0) discard;
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float a = exp(-r2 * 2.5);
    FragColor = vec4(vColor * uBrightness, 1.0) * a * vFade;
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private float[] _data = new float[FloatsPerBody * 64];

    /// <summary>Larger = dots grow faster as you approach (still clamped to the size range).</summary>
    public float PixelScale = 4_000f;
    /// <summary>Smallest on-screen dot, so distant bodies stay visible.</summary>
    public float MinPixelSize = 4f;
    /// <summary>Largest on-screen dot, so a glow never becomes a big blob.</summary>
    public float MaxPixelSize = 16f;
    public float Brightness = 1.6f;

    public int LastDrawn { get; private set; }

    public unsafe PlanetGlowRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        uint stride = FloatsPerBody * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draw a glow for every body in <paramref name="system"/> except <paramref name="skip"/> (the
    /// body the terrain renderer is drawing up close — its glow has faded to nothing anyway).
    /// Call into the scene buffer after the system spheres; additive and depth-independent.
    /// </summary>
    public unsafe void Render(Camera camera, SolarSystem system, float viewportHeight, CelestialBody? skip = null)
    {
        UniversePosition cam = camera.Position;
        int count = 0;
        foreach (CelestialBody b in system.AllBodies())
        {
            if (ReferenceEquals(b, skip)) continue;
            EnsureCapacity(count + 1);
            Vector3D<float> rel = b.CurrentPosition.ToCameraRelative(cam);
            int o = count * FloatsPerBody;
            count++;
            _data[o + 0] = rel.X;
            _data[o + 1] = rel.Y;
            _data[o + 2] = rel.Z;
            // Mean surface albedo (not the raw seeded tint) so a body's colour stays consistent from a
            // far glow dot, through the lit sphere, to the close terrain — no pop as it resolves.
            _data[o + 3] = b.SurfaceAlbedo.X;
            _data[o + 4] = b.SurfaceAlbedo.Y;
            _data[o + 5] = b.SurfaceAlbedo.Z;
            _data[o + 6] = (float)b.RadiusMeters;
        }

        LastDrawn = count;
        if (count == 0) return;

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
        _shader.SetFloat("uViewportH", viewportHeight);
        _shader.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
        _shader.SetFloat("uFadeStartPx", MaxPixelSize * 0.6f);
        _shader.SetFloat("uFadeEndPx", MaxPixelSize * 2.75f);
        _shader.SetFloat("uBrightness", Brightness);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = new ReadOnlySpan<float>(_data, 0, count * FloatsPerBody);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, span, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)count);
        _gl.BindVertexArray(0);

        // Restore the frame defaults the rest of the pipeline expects.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    private void EnsureCapacity(int bodyCount)
    {
        int needed = bodyCount * FloatsPerBody;
        if (_data.Length < needed)
            Array.Resize(ref _data, Math.Max(needed, _data.Length * 2));
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
