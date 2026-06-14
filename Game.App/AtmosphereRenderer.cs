using System.Collections.Generic;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders real volumetric atmospheres: for each atmospheric planet, a fullscreen pass
/// ray-marches single-scattering (Rayleigh + Mie) through the atmosphere shell. Unlike the
/// old fresnel-glow shell this works <b>both from space and from the surface</b> — fly down
/// and the sky goes blue overhead, hazy at the horizon, and reddens toward the terminator.
///
/// Everything is computed in camera-relative space (and planet-radius units inside the shader,
/// for float precision): the view ray is built from the camera basis + FOV and the planet centre
/// comes from <see cref="UniversePosition.ToCameraRelative"/>, so this is immune to the
/// floating-origin precision problem.
///
/// It is <b>depth-aware</b>: the scene is rendered to an offscreen buffer first, and this pass
/// samples that depth to clamp each ray's march to the <i>real</i> per-pixel geometry distance
/// (terrain relief, bodies). That gives correct aerial-perspective haze on distant terrain and a
/// clean limb — no smooth-sphere horizon cutting across the surface. Compositing is premultiplied
/// via glBlendFunc(ONE, SRC_ALPHA): result = inscatter·(1-trans) + scene·trans, so a bright sky
/// can never swamp the surface it shows through.
/// </summary>
public sealed class AtmosphereRenderer : IDisposable
{
    // Wavelength-dependent Rayleigh ratio (Earth's 680/550/440 nm, normalised to blue=1).
    // Keeping the real ratio is what makes sunsets redden; the per-planet colour tints it.
    private static readonly Vector3D<float> EarthRayleighRatio = new(0.175f, 0.409f, 1.0f);

    // Live-tunable via the HUD sliders (see Program.DrawTuning). Defaults chosen to look good
    // AND keep the surface clearly visible — the vertical optical depth is roughly Earth-like
    // (~0.4 in blue) so you can see terrain from low altitude instead of a pea-soup haze.
    public float RayleighStrength = 0.45f; // optical depth across one Rayleigh scale height
    public float MieStrength = 0.16f;
    public float MieG = 0.76f;            // Mie forward-scattering anisotropy (sun halo)
    public float SunIntensity = 20f;
    public float Exposure = 1.2f;
    public float HeightScale = 1.0f;      // multiplies each planet's atmosphere shell thickness
    public bool Enabled = true;           // master on/off — when false, render the bare surface
    public bool DebugTransmittance = false; // show view-ray transmittance as greyscale

    private const string VertexSource = @"#version 410 core
// Single oversized triangle covering the screen; vNdc spans [-1,1] across the viewport.
out vec2 vNdc;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vNdc;
uniform vec3  uForward, uRight, uUp;  // camera basis in render space
uniform float uTanHalfFov, uAspect;
uniform vec3  uPlanet;                // planet centre, camera-relative (m)
uniform vec3  uSunDir;                // normalized, planet -> sun
uniform float uRp, uRa;               // planet & atmosphere-top radius (m)
uniform vec3  uBetaR;                 // Rayleigh scattering coeff per channel (1/m)
uniform float uBetaM;                 // Mie scattering coeff (1/m)
uniform float uHr, uHm;               // Rayleigh / Mie scale heights (m)
uniform float uSunI, uExposure, uMieG, uDebug;
uniform sampler2D uDepth;             // scene depth (offscreen) for clamping the march to geometry
uniform float uNear, uFar;           // projection planes that wrote the depth (for linearisation)
uniform float uRpMeters;             // this planet's radius in metres (to convert into rp units)
out vec4 FragColor;

const int PRIMARY = 16;
const int LIGHT = 8;
const float PI = 3.14159265359;

// Returns near/far ray parameters for the sphere; false on a miss. Uses the perpendicular-
// distance form (disc = r^2 - |perp|^2) which avoids the catastrophic cancellation of
// dot(oc,oc) - r*r — important even though we also work in planet-radius units.
bool raySphere(vec3 o, vec3 d, vec3 c, float r, out float t0, out float t1) {
    vec3 oc = o - c;
    float b = dot(oc, d);
    vec3 perp = oc - b * d;                 // center → closest point on the ray
    float disc = r * r - dot(perp, perp);
    if (disc < 0.0) return false;
    float s = sqrt(disc);
    t0 = -b - s; t1 = -b + s;
    return true;
}

void main() {
    vec3 ro = vec3(0.0); // camera sits at the render-space origin
    vec3 rd = normalize(uForward + vNdc.x * uTanHalfFov * uAspect * uRight + vNdc.y * uTanHalfFov * uUp);

    // Transparent output for a ray with no atmosphere on it: with blend (ONE, SRC_ALPHA) the
    // result is inscatter*1 + background*alpha, so a clear pixel must be rgb=0, ALPHA=1 (keep
    // the background). vec4(0) would have alpha=0 and multiply the background to BLACK — which
    // is exactly what every far planet's miss pixels were doing, erasing the terrain.
    const vec4 CLEAR = vec4(0.0, 0.0, 0.0, 1.0);

    float a0, a1;
    if (!raySphere(ro, rd, uPlanet, uRa, a0, a1) || a1 < 0.0) { FragColor = CLEAR; return; }
    float tStart = max(a0, 0.0);
    float tEnd = a1;

    // Stop the march at the planet body if the ray hits it (the surface occludes the far sky).
    float p0, p1;
    bool planetHit = raySphere(ro, rd, uPlanet, uRp, p0, p1) && p0 > 0.0;
    if (planetHit) tEnd = min(tEnd, p0);

    // Clamp the march to the ACTUAL rendered geometry (terrain relief, bodies) via the scene
    // depth, so haze lands on the real surface — not a smooth sphere. Linearise the depth to an
    // eye-space distance, turn it into a distance along this ray, and convert to rp units.
    float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
    if (depth < 1.0) {
        float ndcZ = depth * 2.0 - 1.0;
        float eyeZ = (2.0 * uNear * uFar) / (uFar + uNear - ndcZ * (uFar - uNear)); // metres, along forward
        float geom = (eyeZ / max(dot(rd, uForward), 1e-3)) / uRpMeters;             // rp units, along the ray
        tEnd = min(tEnd, geom);
    }

    // Debug: visualise the march geometry. blue = sky (no planet hit); green = planet hit and
    // the march was correctly clipped to the surface; red = planet hit but the clip did NOT
    // shorten the march (the bug). Blend is disabled for this pass so colours are literal.
    if (uDebug > 0.5) {
        vec3 dbg = !planetHit ? vec3(0.1, 0.2, 0.6)
                 : (tEnd < a1 - 1e-5 ? vec3(0.0, 0.9, 0.0) : vec3(0.9, 0.0, 0.0));
        FragColor = vec4(dbg, 1.0);
        return;
    }

    if (tEnd <= tStart) { FragColor = CLEAR; return; }

    float seg = (tEnd - tStart) / float(PRIMARY);
    float cosT = dot(rd, uSunDir);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + cosT * cosT);
    float g2 = uMieG * uMieG;
    float phaseM = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + cosT * cosT)) /
                   ((2.0 + g2) * pow(max(1.0 + g2 - 2.0 * uMieG * cosT, 1e-4), 1.5));

    vec3 sumR = vec3(0.0), sumM = vec3(0.0);
    float odR = 0.0, odM = 0.0; // optical depth accumulated along the view ray
    for (int i = 0; i < PRIMARY; i++) {
        vec3 P = ro + rd * (tStart + seg * (float(i) + 0.5));
        float h = length(P - uPlanet) - uRp;
        if (h < 0.0) continue;
        float hr = exp(-h / uHr) * seg;
        float hm = exp(-h / uHm) * seg;
        odR += hr; odM += hm;

        // Light ray from this sample toward the sun. Skip if the planet shadows it (terminator).
        float ps0, ps1;
        if (raySphere(P, uSunDir, uPlanet, uRp, ps0, ps1) && ps1 > 0.0 && ps0 > 0.0) continue;

        float l0, l1;
        raySphere(P, uSunDir, uPlanet, uRa, l0, l1);
        float lseg = l1 / float(LIGHT);
        float lodR = 0.0, lodM = 0.0;
        bool ground = false;
        for (int j = 0; j < LIGHT; j++) {
            float hl = length(P + uSunDir * (lseg * (float(j) + 0.5)) - uPlanet) - uRp;
            if (hl < 0.0) { ground = true; break; }
            lodR += exp(-hl / uHr) * lseg;
            lodM += exp(-hl / uHm) * lseg;
        }
        if (ground) continue;

        // Mie extinction ~ scattering/0.9; bundle the 1.1 factor here.
        vec3 tau = uBetaR * (odR + lodR) + uBetaM * 1.1 * (odM + lodM);
        vec3 atten = exp(-tau);
        sumR += atten * hr;
        sumM += atten * hm;
    }

    vec3 inscatter = uSunI * (sumR * uBetaR * phaseR + sumM * uBetaM * phaseM);
    vec3 viewTau = uBetaR * odR + uBetaM * 1.1 * odM;
    vec3 trans = exp(-viewTau);
    float avgTrans = (trans.r + trans.g + trans.b) / 3.0;

    vec3 col = vec3(1.0) - exp(-inscatter * uExposure); // tonemap HDR inscatter to [0,1]
    // Premultiplied composite: result = inscatter*(1-trans) + background*trans (blend ONE,SRC_ALPHA).
    // Weighting the inscatter by (1-trans) is what keeps it from swamping the surface — a bright
    // sky/Mie glow can only fill the part of the pixel the terrain doesn't already show through.
    FragColor = vec4(col * (1.0 - avgTrans), avgTrans);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private uint _depthTexture;
    private float _depthNear = 1f, _depthFar = 1f;

    public AtmosphereRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray(); // empty VAO; the vertex shader generates positions from gl_VertexID
    }

    /// <summary>
    /// Draw volumetric atmospheres for every atmospheric planet in the system. Call after the
    /// scene (stars/system/terrain) has been rendered to an offscreen buffer and blitted to the
    /// screen; <paramref name="depthTexture"/> is that buffer's depth, and <paramref name="near"/>/
    /// <paramref name="far"/> are the projection planes that wrote it (the dominant geometry's).
    /// </summary>
    public void Render(Camera camera, SolarSystem system, uint depthTexture, float near, float far)
    {
        if (!Enabled || system.Planets.Length == 0) return;
        _depthTexture = depthTexture;
        _depthNear = near;
        _depthFar = far;
        UniversePosition cam = camera.Position;
        Vector3D<float> sunRel = system.Sun.Position.ToCameraRelative(cam);

        // Painter's order: far bodies first, so a nearer body's sky composites on top. Planets and
        // moons are gathered together — a moon with a thin atmosphere haloes just like a planet.
        var atmo = new List<CelestialBody>();
        foreach (CelestialBody b in system.AllBodies())
            if (b.HasAtmosphere) atmo.Add(b);
        if (atmo.Count == 0) return;
        atmo.Sort((x, y) =>
            cam.DistanceTo(y.CurrentPosition).CompareTo(cam.DistanceTo(x.CurrentPosition)));

        // No depth TEST — the pass blends over the (blitted) scene everywhere; instead each ray's
        // march is CLAMPED to the scene depth in the shader, which lands haze on the real surface.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        if (DebugTransmittance)
        {
            _gl.Disable(EnableCap.Blend); // overwrite the scene so debug colours are literal
        }
        else
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.SrcAlpha); // bg·transmittance + inscatter
        }

        _shader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
        _shader.SetInt("uDepth", 0);
        _shader.SetFloat("uNear", _depthNear);
        _shader.SetFloat("uFar", _depthFar);
        _shader.SetVector3("uForward", camera.Forward);
        _shader.SetVector3("uRight", camera.Right);
        _shader.SetVector3("uUp", camera.Up);
        _shader.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
        _shader.SetFloat("uAspect", camera.AspectRatio);
        _shader.SetFloat("uSunI", SunIntensity);
        _shader.SetFloat("uExposure", Exposure);
        _shader.SetFloat("uMieG", Math.Clamp(MieG, -0.95f, 0.95f));
        _shader.SetFloat("uDebug", DebugTransmittance ? 1f : 0f);

        _gl.BindVertexArray(_vao);
        foreach (CelestialBody p in atmo)
            DrawAtmosphere(p, cam, sunRel);
        _gl.BindVertexArray(0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
    }

    private void DrawAtmosphere(CelestialBody p, in UniversePosition cam, Vector3D<float> sunRel)
    {
        // Everything below is in PLANET-RADIUS UNITS (divide metres by rp). At planetary scale
        // (rp ~ 6e6) float32 squared is ~4e13 and loses all sub-km precision, which made the
        // ray-march's planet clip fail and run far past the surface, hazing the whole view.
        // Working in units where the planet radius is 1.0 keeps every quantity near order-1.
        double rp = p.RadiusMeters;
        float height = p.AtmosphereHeight * HeightScale;     // shell thickness as a fraction of rp
        float hr = height * 0.25f; // Rayleigh scale height (in rp units)
        float hm = height * 0.10f; // Mie scale height (thinner, hugs the surface)

        Vector3D<float> planetRel = p.CurrentPosition.ToCameraRelative(cam);
        Vector3D<float> sunDir = Vector3D.Normalize(sunRel - planetRel);
        Vector3D<float> planetScaled = planetRel * (float)(1.0 / rp); // ~1.0 in magnitude

        // Size-independent optical depth: beta = tau / scaleHeight, tinted by the planet's colour.
        float tauR = RayleighStrength * p.AtmosphereDensity;
        Vector3D<float> betaR = new(
            tauR * EarthRayleighRatio.X * p.AtmosphereColor.X / hr,
            tauR * EarthRayleighRatio.Y * p.AtmosphereColor.Y / hr,
            tauR * EarthRayleighRatio.Z * p.AtmosphereColor.Z / hr);
        float betaM = MieStrength * p.AtmosphereDensity / hm;

        _shader.SetVector3("uPlanet", planetScaled);
        _shader.SetVector3("uSunDir", sunDir);
        _shader.SetFloat("uRpMeters", (float)rp);
        _shader.SetFloat("uRp", 1.0f);
        _shader.SetFloat("uRa", 1.0f + height);
        _shader.SetVector3("uBetaR", betaR);
        _shader.SetFloat("uBetaM", betaM);
        _shader.SetFloat("uHr", hr);
        _shader.SetFloat("uHm", hm);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
    }
}
