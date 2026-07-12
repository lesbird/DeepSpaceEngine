using Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;
using Vector2 = System.Numerics.Vector2;

namespace Game.App;

/// <summary>
/// The backup high-speed effect (see <see cref="StreakMode.ScreenBlur"/>): a single full-screen radial
/// motion blur centred on the flight's focus-of-expansion — the screen point you're heading toward. Each
/// pixel is smeared along the line back toward that point, so the whole image (stars, galaxies, nebulae,
/// everything) streaks outward as you accelerate. Cheaper and more uniform than the per-point geometry
/// streaks, but softer and it blurs solid geometry too.
/// </summary>
public sealed class VelocityBlurRenderer : IDisposable
{
    private const string FullscreenVert = @"#version 410 core
out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.0, 1.0);
    vUv = p * 0.5 + 0.5;
}";

    private const string BlurFrag = @"#version 410 core
in vec2 vUv;
uniform sampler2D uScene;
uniform vec2 uFoe;        // focus-of-expansion in UV (the point you're flying toward)
uniform float uStrength;  // max fraction of the FoE→pixel vector to sample across
out vec4 FragColor;
const int N = 16;
void main() {
    // Sample from this pixel back toward the focus-of-expansion: each pixel's 'past' positions lie along
    // that line, so accumulating them produces an outward radial streak. Strength (and thus streak length)
    // grows with speed; at rest it's zero and this is a straight copy.
    vec2 dir = (vUv - uFoe) * uStrength;
    vec3 acc = vec3(0.0);
    for (int i = 0; i < N; i++) {
        float t = float(i) / float(N - 1);
        acc += texture(uScene, vUv - dir * t).rgb;
    }
    FragColor = vec4(acc / float(N), 1.0);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;               // empty; the full-screen triangle comes from gl_VertexID
    private readonly ColorTarget _target;

    public VelocityBlurRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, FullscreenVert, BlurFrag);
        _vao = gl.GenVertexArray();
        _target = new ColorTarget(gl);
    }

    public void Resize(int width, int height) => _target.Resize(width, height);

    /// <summary>Blur <paramref name="sceneTex"/> along the flight's radial motion and return the result
    /// texture. Returns <paramref name="sceneTex"/> unchanged when the camera is effectively still.</summary>
    public uint Render(uint sceneTex, Camera camera)
    {
        double speed = camera.WorldVelocity.Length;
        // Ramp the blur in over the same band the per-point streaks use, so switching modes feels similar.
        float ramp = Smoothstep((float)MotionStreak.MinSpeedMps, (float)MotionStreak.MinSpeedMps * 8f, (float)speed);
        float strength = MotionStreak.ScreenBlurStrength * ramp;
        if (strength < 1e-4f) return sceneTex;

        // Focus-of-expansion: the velocity direction in VIEW space projected to the screen. Working in view
        // space (not via the view-proj matrix) sidesteps any row/column convention and handles the point at
        // infinity exactly. If it lands behind the camera (flying backward), mirror it and the streaks
        // converge instead — still correct for a radial smear.
        var velDir = camera.WorldVelocity / Math.Max(speed, 1e-9);
        var vWorld = new Vector3D<float>((float)velDir.X, (float)velDir.Y, (float)velDir.Z);
        Vector3D<float> dv = Vector3D.Transform(vWorld, Quaternion<float>.Inverse(camera.Orientation));
        if (dv.Z > 0f) dv = -dv;                 // FoE behind → mirror to the forward hemisphere
        float th = MathF.Tan(camera.FovRadians * 0.5f);
        float ndcx = dv.X / (-dv.Z * th * camera.AspectRatio);
        float ndcy = dv.Y / (-dv.Z * th);
        var foe = new Vector2(ndcx * 0.5f + 0.5f, ndcy * 0.5f + 0.5f);

        _target.Bind();
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _shader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneTex);
        _shader.SetInt("uScene", 0);
        _shader.SetVector2("uFoe", new Vector2D<float>(foe.X, foe.Y));
        _shader.SetFloat("uStrength", strength);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        return _target.ColorTexture;
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
        _target.Dispose();
    }
}
