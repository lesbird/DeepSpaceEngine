using System;
using System.Collections.Generic;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// A screen-space <b>lens flare</b> for the active system's star: a bright radiating core (disc +
/// starburst spokes + an anamorphic streak) plus a chain of chromatic ghost discs and halo rings
/// strung along the line from the sun through the screen centre — the artefact a real lens throws when
/// a bright light is in frame. Drawn additively over the final composited image (after bloom), so it
/// reads as a lens/eye artefact rather than emitted light.
///
/// The whole effect is faded by <see cref="SunOcclusion"/> (planets/moons crossing the sightline dim
/// it, so it sets behind a world's limb instead of shining through), by how far off-screen the sun has
/// drifted, and by a front-facing gate so it vanishes the instant the sun passes behind the camera.
/// It is fully procedural — no texture assets — matching the rest of the renderer, and draws one
/// full-screen triangle from <c>gl_VertexID</c> with an empty VAO (same trick as the bloom passes).
/// </summary>
public sealed class LensFlareRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>Overall strength of the flare (0 = off). Live; saved with the other look knobs.</summary>
    public float Intensity = 0.25f;

    private const string VertexSource = @"#version 410 core
out vec2 vUV;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vUV = p * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vUV;
uniform vec2  uSun;        // sun position in uv space (0..1), before aspect correction
uniform float uAspect;     // width / height, so discs stay round
uniform vec3  uColor;      // sun tint, brightened toward white
uniform float uIntensity;  // overall strength
uniform float uVis;        // occlusion x on-screen x front-facing fade [0,1]
out vec4 FragColor;

float disc(vec2 p, float r)  { return 1.0 - smoothstep(r * 0.2, r, length(p)); }
float ring(vec2 p, float r, float w) { return 1.0 - smoothstep(0.0, w, abs(length(p) - r)); }

void main() {
    // Aspect-correct everything so circles are round and distances symmetric.
    vec2  uv     = vec2(vUV.x * uAspect, vUV.y);
    vec2  sun    = vec2(uSun.x * uAspect, uSun.y);
    vec2  center = vec2(0.5 * uAspect, 0.5);
    vec2  toSun  = uv - sun;
    vec2  axis   = center - sun;                 // sun -> screen centre: the ghost line
    float d      = length(toSun);

    vec3 col = vec3(0.0);

    // --- Core: a tight bright disc, a softer surround, radial spokes and an anamorphic streak. ---
    col += uColor * (disc(toSun, 0.012) * 4.0 + disc(toSun, 0.06) * 0.6);
    float ang    = atan(toSun.y, toSun.x);
    float spokes = pow(0.5 + 0.5 * cos(ang * 6.0), 8.0);        // six-point starburst
    col += uColor * spokes * (1.0 - smoothstep(0.0, 0.4, d)) * 0.7;
    float streak = exp(-abs(toSun.y) * 130.0) * exp(-abs(toSun.x) * 2.5);
    col += uColor * streak * 0.6;

    // --- Chromatic halo ring hugging the sun. ---
    col += vec3(0.5, 0.65, 1.0) * ring(toSun, 0.30, 0.10) * 0.10;

    // --- Ghost discs marching along the axis toward (and past) the screen centre. Each a different
    //     spacing / radius / tint so the chain reads as glass elements, not copies. ---
    col += vec3(0.4, 0.7, 1.0) * disc(uv - (sun + axis * 0.30), 0.06) * 0.35;
    col += vec3(1.0, 0.5, 0.3) * disc(uv - (sun + axis * 0.55), 0.03) * 0.30;
    col += vec3(0.3, 1.0, 0.6) * disc(uv - (sun + axis * 0.80), 0.10) * 0.18;
    col += vec3(1.0, 0.8, 0.3) * disc(uv - (sun + axis * 1.15), 0.05) * 0.30;
    col += vec3(0.5, 0.4, 1.0) * disc(uv - (sun + axis * 1.45), 0.16) * 0.12;
    col += vec3(1.0, 0.4, 0.7) * disc(uv - (sun + axis * 1.85), 0.04) * 0.28;
    col += vec3(0.6, 0.7, 1.0) * ring(uv - (sun + axis * 1.00), 0.24, 0.05) * 0.14;

    col *= uVis * uIntensity;
    float lum = max(col.r, max(col.g, col.b));
    FragColor = vec4(col, lum);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;                                  // empty; vertices from gl_VertexID
    private readonly List<(Vector3D<double> center, double radius)> _occluders = new();

    public LensFlareRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    /// <summary>
    /// Draw the flare for <paramref name="system"/>'s sun over the current (default) framebuffer. Call
    /// after the bloom composite; it inherits that pass's full-res viewport. A no-op when disabled, when
    /// the sun is behind the camera / well off-screen, or when fully occluded.
    /// </summary>
    public void Render(Camera camera, SolarSystem system)
    {
        if (!Enabled || Intensity <= 0f) return;

        Vector3D<double> toSun = system.Sun.Position.DeltaMeters(camera.Position);
        double sunDist = toSun.Length;
        if (sunDist < 1.0) return;
        Vector3D<double> dir = toSun / sunDist;

        // Camera basis in double precision (orientation only — positions are camera-relative already).
        Vector3D<double> fwd = ToD(camera.Forward), right = ToD(camera.Right), up = ToD(camera.Up);
        double f = Vector3D.Dot(dir, fwd);
        if (f <= 1e-4) return;                                   // sun behind the camera

        double tanHalf = Math.Tan(camera.FovRadians * 0.5);
        double ndcX = (Vector3D.Dot(dir, right) / f) / (tanHalf * camera.AspectRatio);
        double ndcY = (Vector3D.Dot(dir, up) / f) / tanHalf;

        double edge = Math.Max(Math.Abs(ndcX), Math.Abs(ndcY));
        double onScreen = 1.0 - Smoothstep(1.0, 1.6, edge);     // fade as the sun leaves the frame
        double front = Smoothstep(0.0, 0.12, f);                // fade in as it comes round from behind
        if (onScreen <= 0.0) return;

        // Occlusion by every planet and moon crossing the sightline.
        _occluders.Clear();
        foreach (CelestialBody b in system.AllBodies())
            _occluders.Add((b.CurrentPosition.DeltaMeters(camera.Position), b.RadiusMeters));
        double vis = SunOcclusion.Visibility(toSun, _occluders) * onScreen * front;
        if (vis <= 1e-3) return;

        var sunUv = new Vector2D<float>((float)(ndcX * 0.5 + 0.5), (float)(ndcY * 0.5 + 0.5));
        Vector3D<float> tint = Brighten(system.Sun.Color, 0.3f);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);  // additive
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _shader.Use();
        _shader.SetVector2("uSun", sunUv);
        _shader.SetFloat("uAspect", camera.AspectRatio);
        _shader.SetVector3("uColor", tint);
        _shader.SetFloat("uIntensity", Intensity);
        _shader.SetFloat("uVis", (float)vis);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
    }

    private static Vector3D<double> ToD(Vector3D<float> v) => new(v.X, v.Y, v.Z);

    private static Vector3D<float> Brighten(Vector3D<float> c, float t)
        => c + (new Vector3D<float>(1f, 1f, 1f) - c) * t;

    private static double Smoothstep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
    }
}
