using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders an active solar system: an emissive sun, lit planet spheres, and faint orbit
/// rings. Sphere normals are derived from position in the fragment shader, sidestepping
/// the matrix-convention pitfalls of transforming normals. The depth range is fit to the
/// bodies actually on screen each frame so spheres stay z-fight-free across the system's
/// enormous scale span (a proper logarithmic depth buffer arrives with planet terrain).
/// </summary>
public sealed class SystemRenderer : IDisposable
{
    private const string PlanetVertex = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal; // unused; normal derived from position
uniform mat4 uMVP;
uniform mat4 uModel;
out vec3 vWorld;
void main() {
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

    private const string PlanetFragment = @"#version 410 core
in vec3 vWorld;
uniform vec3 uPlanetCenter;
uniform vec3 uSunCenter;
uniform vec3 uColor;
uniform float uEmissive;
out vec4 FragColor;
void main() {
    vec3 N = normalize(vWorld - uPlanetCenter);
    vec3 L = normalize(uSunCenter - vWorld);
    float diff = max(dot(N, L), 0.0);
    vec3 lit = uColor * (0.04 + diff);
    FragColor = vec4(mix(lit, uColor, uEmissive), 1.0);
}";

    // --- Planetary rings: a flat annulus whose radius is reconstructed from t in the shader ---
    private const string RingVertex = @"#version 410 core
layout(location = 0) in vec3 aDir;   // unit direction in the ring plane (x,_,z)
layout(location = 1) in vec3 aParam; // .x = t (0 inner .. 1 outer)
uniform mat4 uViewProj;
uniform mat4 uModel;   // ring-plane -> camera-relative world (tilt + translate, no scale)
uniform float uInnerR;
uniform float uOuterR;
out vec3 vWorld;
out float vT;
void main() {
    float radius = mix(uInnerR, uOuterR, aParam.x);
    vec4 world = uModel * vec4(aDir.x * radius, 0.0, aDir.z * radius, 1.0);
    vWorld = world.xyz;
    vT = aParam.x;
    gl_Position = uViewProj * world;
}";

    private const string RingFragment = @"#version 410 core
in vec3 vWorld;
in float vT;
uniform vec3 uPlanetCenter;   // camera-relative
uniform float uPlanetRadius;
uniform vec3 uSunCenter;      // camera-relative
uniform vec3 uColor;
uniform float uOpacity;
uniform float uSeed;
out vec4 FragColor;

float hash(float n) { return fract(sin(n) * 43758.5453); }

void main() {
    // Procedural radial banding: a few octaves of sine in t, seeded for per-planet variety.
    float bands = 0.0, f = 16.0;
    for (int i = 0; i < 4; i++) {
        float phase = hash(uSeed * 0.001 + float(i) * 7.13) * 6.2831853;
        bands += sin(vT * f + phase);
        f *= 1.9;
    }
    float density = clamp(0.62 + bands * 0.16, 0.0, 1.0);
    // A couple of sharp dark gaps (Cassini-like).
    float gap = smoothstep(0.02, 0.06, abs(fract(vT * 4.0 + hash(uSeed * 0.0007)) - 0.5));

    // Planet shadow on the ring: does the ray from this point toward the sun hit the planet?
    vec3 toSun = uSunCenter - vWorld;
    vec3 sd = normalize(toSun);
    vec3 oc = uPlanetCenter - vWorld;
    float tca = dot(oc, sd);
    float shadow = 1.0;
    if (tca > 0.0 && dot(oc, oc) - tca * tca < uPlanetRadius * uPlanetRadius)
        shadow = 0.22;

    vec3 col = uColor * (0.35 + 0.65 * shadow);
    float edge = smoothstep(0.0, 0.05, vT) * (1.0 - smoothstep(0.95, 1.0, vT));
    float alpha = uOpacity * density * gap * edge;
    FragColor = vec4(col, alpha);
}";

    private const string OrbitVertex = @"#version 410 core
layout(location = 0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }";

    private const string OrbitFragment = @"#version 410 core
uniform vec4 uColor;
out vec4 FragColor;
void main() { FragColor = uColor; }";

    private readonly GL _gl;
    private readonly Shader _planetShader;
    private readonly Shader _orbitShader;
    private readonly Shader _ringShader;
    private readonly Mesh _sphere;
    private readonly Mesh _orbit;
    private readonly Mesh _ring;

    public SystemRenderer(GL gl)
    {
        _gl = gl;
        _planetShader = new Shader(gl, PlanetVertex, PlanetFragment);
        _orbitShader = new Shader(gl, OrbitVertex, OrbitFragment);
        _ringShader = new Shader(gl, RingVertex, RingFragment);
        _sphere = Primitives.BuildSphere(gl, stacks: 24, slices: 48);
        _orbit = Primitives.BuildCircleLine(gl, segments: 128);
        _ring = Primitives.BuildRingAnnulus(gl, segments: 256);
    }

    public void Render(Camera camera, SolarSystem system, Planet? terrainPlanet = null)
    {
        UniversePosition cam = camera.Position;
        Vector3D<float> sunRel = system.Sun.Position.ToCameraRelative(cam);

        Matrix4X4<float> proj = FitProjection(camera, system);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);

        // --- Sun (emissive) ---
        _planetShader.Use();
        _planetShader.SetVector3("uSunCenter", sunRel);
        DrawBody(viewProj, sunRel, system.Sun.RadiusMeters, system.Sun.Color, sunRel, emissive: 1f);

        // --- Planets + moons (lit) ---
        foreach (Planet p in system.Planets)
        {
            // The terrain renderer draws this planet as a detailed cube-sphere instead.
            if (!ReferenceEquals(p, terrainPlanet))
            {
                Vector3D<float> rel = p.CurrentPosition.ToCameraRelative(cam);
                DrawBody(viewProj, rel, p.RadiusMeters, p.Color, sunRel, emissive: 0f);
            }

            foreach (Moon mn in p.Moons)
            {
                Vector3D<float> mrel = mn.CurrentPosition.ToCameraRelative(cam);
                DrawBody(viewProj, mrel, mn.RadiusMeters, mn.Color, sunRel, emissive: 0f);
            }
        }

        // Atmospheres are drawn separately by AtmosphereRenderer (a volumetric fullscreen pass),
        // after terrain, so the sky composites correctly both from space and on the surface.

        // --- Planetary rings (lit, banded, blended) — drawn before the faint orbit lines.
        // Depth test stays on so the planet occludes the far half of the ring; depth writes off
        // so the translucent annulus never hides bodies behind it.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _ringShader.Use();
        _ringShader.SetMatrix("uViewProj", viewProj);
        _ringShader.SetVector3("uSunCenter", sunRel);
        foreach (Planet p in system.Planets)
        {
            if (!p.HasRings) continue;
            Vector3D<float> rel = p.CurrentPosition.ToCameraRelative(cam);
            Matrix4X4<float> model =
                Matrix4X4.CreateRotationX(p.RingTilt) *
                Matrix4X4.CreateRotationY(p.RingTiltAzimuth) *
                Matrix4X4.CreateTranslation(rel);
            _ringShader.SetMatrix("uModel", model);
            _ringShader.SetFloat("uInnerR", (float)p.RingInnerRadius);
            _ringShader.SetFloat("uOuterR", (float)p.RingOuterRadius);
            _ringShader.SetVector3("uPlanetCenter", rel);
            _ringShader.SetFloat("uPlanetRadius", (float)p.RadiusMeters);
            _ringShader.SetVector3("uColor", p.RingColor);
            _ringShader.SetFloat("uOpacity", p.RingOpacity);
            _ringShader.SetFloat("uSeed", (float)(p.RingSeed & 0xFFFFFFu));
            _ring.Draw();
        }

        // --- Orbit rings (faint, blended) ---
        _orbitShader.Use();
        _orbitShader.SetVector4("uColor", new Vector4D<float>(0.35f, 0.45f, 0.6f, 0.35f));
        foreach (Planet p in system.Planets)
        {
            Matrix4X4<float> model =
                Matrix4X4.CreateScale((float)p.SemiMajorAxis) *
                Matrix4X4.CreateRotationX((float)p.Inclination) *
                Matrix4X4.CreateRotationY((float)p.AscendingNode) *
                Matrix4X4.CreateTranslation(sunRel);
            _orbitShader.SetMatrix("uMVP", model * viewProj);
            _orbit.Draw();
        }
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
    }

    private void DrawBody(in Matrix4X4<float> viewProj, Vector3D<float> rel, double radius,
        Vector3D<float> color, Vector3D<float> sunRel, float emissive)
    {
        Matrix4X4<float> model = Matrix4X4.CreateScale((float)radius) * Matrix4X4.CreateTranslation(rel);
        _planetShader.SetMatrix("uModel", model);
        _planetShader.SetMatrix("uMVP", model * viewProj);
        _planetShader.SetVector3("uPlanetCenter", rel);
        _planetShader.SetVector3("uSunCenter", sunRel);
        _planetShader.SetVector3("uColor", color);
        _planetShader.SetFloat("uEmissive", emissive);
        _sphere.Draw();
    }

    /// <summary>Near/far planes from the most recent <see cref="FitProjection"/> (for depth reconstruction).</summary>
    public float LastNear { get; private set; }
    public float LastFar { get; private set; }

    /// <summary>Fit the near/far planes to the bodies on screen so depth precision stays usable.</summary>
    private Matrix4X4<float> FitProjection(Camera camera, SolarSystem system)
    {
        UniversePosition cam = camera.Position;
        double minSurf = double.MaxValue, maxFar = 0;

        void Consider(in UniversePosition pos, double radius)
        {
            double d = cam.DistanceTo(pos);
            minSurf = Math.Min(minSurf, d - radius);
            maxFar = Math.Max(maxFar, d + radius);
        }

        Consider(system.Sun.Position, system.Sun.RadiusMeters);
        foreach (Planet p in system.Planets)
        {
            // Rings extend well past the planet's surface — fit to their outer edge.
            double extent = p.HasRings ? Math.Max(p.RadiusMeters, p.RingOuterRadius) : p.RadiusMeters;
            Consider(p.CurrentPosition, extent);
            foreach (Moon mn in p.Moons) Consider(mn.CurrentPosition, mn.RadiusMeters);
        }

        double near = Math.Max(100.0, minSurf * 0.5);
        double far = Math.Max(maxFar * 1.2, near * 10.0);
        if (far / near > 5.0e6) near = far / 5.0e6; // cap ratio for 24-bit depth

        LastNear = (float)near; LastFar = (float)far;
        return MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, (float)near, (float)far);
    }

    public void Dispose()
    {
        _planetShader.Dispose();
        _orbitShader.Dispose();
        _ringShader.Dispose();
        _sphere.Dispose();
        _orbit.Dispose();
        _ring.Dispose();
    }
}
