using Engine.Core;
using Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// A toggleable reference grid lying in the galactic plane (world XZ, y = 0), centred on the
/// galaxy origin. It's drawn as faint lines that fade out with distance so the plane reads as an
/// effectively infinite sheet rather than a hard-edged square. The grid spans tens of thousands of
/// light-years — far beyond the main camera's far plane — so it builds its own wide projection each
/// frame and draws as a depth-independent overlay (no depth read or write).
/// </summary>
public sealed class GalacticGridRenderer : IDisposable
{
    // Grid footprint, in light-years.
    private const double HalfExtentLy = 50_000.0;
    private const double StepLy = 1_000.0;
    private const double FadeStartLy = 5_000.0;

    private const string Vert = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aColor;
uniform mat4 uMVP;
uniform mat4 uModel;
out vec3 vColor;
out vec3 vWorld;
void main() {
    vColor = aColor;
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;   // camera-relative world position
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

    private const string Frag = @"#version 410 core
in vec3 vColor;
in vec3 vWorld;
uniform float uAlpha;
uniform float uFadeStart;
uniform float uFadeEnd;
out vec4 FragColor;
void main() {
    float dist = length(vWorld);
    float fade = 1.0 - smoothstep(uFadeStart, uFadeEnd, dist);
    if (fade <= 0.0) discard;
    FragColor = vec4(vColor, uAlpha * fade);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Mesh _grid;

    public GalacticGridRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vert, Frag);
        float half = (float)(HalfExtentLy * MathUtil.LightYear);
        float step = (float)(StepLy * MathUtil.LightYear);
        _grid = Primitives.BuildGrid(gl, half, step, (0.25f, 0.55f, 0.8f));
    }

    public void Render(Camera camera)
    {
        Vector3D<float> originRel = UniversePosition.Origin.ToCameraRelative(camera.Position);
        Matrix4X4<float> model = Matrix4X4.CreateTranslation(originRel);
        Matrix4X4<float> viewProj = camera.ViewMatrix * Projection(camera);

        // Faint additive-style overlay: no depth interaction, so it never occludes (or is occluded
        // by) scene geometry and never z-fights across its enormous extent.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        _shader.Use();
        _shader.SetMatrix("uModel", model);
        _shader.SetMatrix("uMVP", model * viewProj);
        _shader.SetFloat("uAlpha", 0.5f);
        _shader.SetFloat("uFadeStart", (float)(FadeStartLy * MathUtil.LightYear));
        _shader.SetFloat("uFadeEnd", (float)(HalfExtentLy * MathUtil.LightYear));
        _grid.Draw();

        // Restore the frame defaults the rest of the pipeline expects.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    private Matrix4X4<float> Projection(Camera camera)
    {
        double d = camera.Position.DistanceTo(UniversePosition.Origin);
        double extent = HalfExtentLy * MathUtil.LightYear;
        // Depth precision is irrelevant (we don't depth-test); only the clip planes matter, so keep
        // the near plane small and stretch the far plane past the whole grid.
        float near = (float)Math.Max(1.0e8, d * 0.001);
        float far = (float)((d + extent) * 2.0);
        return MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _grid.Dispose();
    }
}
