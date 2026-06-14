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
out vec3 vColor;
void main() {
    vColor = aColor;
    gl_Position = uViewProj * vec4(aRelPos, 1.0);
    float dist = max(length(aRelPos), 1.0);
    gl_PointSize = clamp(uPixelScale * aSize / dist, uMinSize, uMaxSize);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vColor;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float a = exp(-r2 * 2.5);
    FragColor = vec4(vColor, 1.0) * a;
}";

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

    public unsafe void Render(Camera camera, IReadOnlyList<Star> visible, ulong? excludeId = null)
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
    }
}
