using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Raymarched volumetric clouds in a spherical shell around the nearest atmospheric body. The march
/// runs at HALF resolution into its own buffer (the expensive part), then a cheap fullscreen pass
/// upscales and composites it over the scene — keeping it real-time. A single view-ray march samples
/// a procedural density field — low-frequency fBm <b>coverage</b> over the sphere, a <b>height
/// gradient</b> rounding the layer, and higher-frequency <b>billow erosion</b> carving puffs — with
/// Beer's-law extinction toward the sun (a short secondary march), a Henyey–Greenstein phase, a
/// Beer-powder darkening, and a flat ambient sky term.
///
/// Like <see cref="AtmosphereRenderer"/> it works from orbit and from the surface (camera-relative,
/// planet-radius units for precision) and is depth-aware — scene depth clamps the march so terrain in
/// front occludes the clouds. Optical depth is accumulated in METRES (segment length × an extinction
/// coefficient), which is what makes the clouds actually opaque at planetary scale. Drawn before the
/// atmosphere so distant clouds pick up aerial-perspective haze.
/// </summary>
public sealed class CloudRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>True = full raymarched volumetric clouds (#3, expensive); false = cheap analytic
    /// shell layer (#2). Off by default.</summary>
    public bool Volumetric = false;
    /// <summary>Cloud cover (0 = clear sky, 1 = overcast).</summary>
    public float Coverage = 0.5f;
    /// <summary>Opacity / density multiplier (extinction scale for volumetric; layer opacity for the shell).</summary>
    public float Density = 1.0f;
    /// <summary>Wind speed scrolling the cloud field.</summary>
    public float WindSpeed = 0.004f;

    private const string VertexSource = @"#version 410 core
out vec2 vNdc;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string MarchFragment = @"#version 410 core
in vec2 vNdc;
uniform vec3  uForward, uRight, uUp;
uniform float uTanHalfFov, uAspect;
uniform vec3  uPlanet;                 // planet centre, camera-relative, planet-radius units
uniform vec3  uSunDir;
uniform float uRp, uRin, uRout;        // surface / cloud inner / cloud outer radius (rp units)
uniform float uCoverage, uSigma;       // cover threshold control & extinction (per metre, ×density)
uniform vec3  uWind, uSunColor, uAmbient;
uniform float uG;
uniform sampler2D uDepth;              // scene depth (full-res) for occlusion
uniform float uNear, uFar, uRpMeters;
out vec4 FragColor;

const int PRIMARY = 24;
const int LIGHT = 3;
const float PI = 3.14159265359;

bool raySphere(vec3 o, vec3 d, vec3 c, float r, out float t0, out float t1) {
    vec3 oc = o - c;
    float b = dot(oc, d);
    vec3 perp = oc - b * d;
    float disc = r * r - dot(perp, perp);
    if (disc < 0.0) return false;
    float s = sqrt(disc);
    t0 = -b - s; t1 = -b + s;
    return true;
}
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.yzx + 33.33); return fract((p.x + p.y) * p.z); }
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i + vec3(0,0,0)), n100 = hash13(i + vec3(1,0,0));
    float n010 = hash13(i + vec3(0,1,0)), n110 = hash13(i + vec3(1,1,0));
    float n001 = hash13(i + vec3(0,0,1)), n101 = hash13(i + vec3(1,0,1));
    float n011 = hash13(i + vec3(0,1,1)), n111 = hash13(i + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm4(vec3 p) {
    float s = 0.0, a = 0.5, n = 0.0;
    for (int i = 0; i < 4; i++) { s += a * vnoise(p); n += a; p *= 2.02; a *= 0.5; }
    return s / n;
}
float billow3(vec3 p) {
    float s = 0.0, a = 0.5, n = 0.0;
    for (int i = 0; i < 3; i++) { s += a * abs(vnoise(p) * 2.0 - 1.0); n += a; p *= 2.03; a *= 0.5; }
    return s / n;
}
float remap(float v, float lo, float hi, float nlo, float nhi) {
    return nlo + (clamp(v, lo, hi) - lo) / max(hi - lo, 1e-4) * (nhi - nlo);
}

// Cheap density: coverage × height gradient only (no erosion). Used for the secondary light march,
// where fine detail in the shadow term isn't worth its cost.
float densityLow(vec3 lp) {
    float r = length(lp);
    float hf = (r - uRin) / (uRout - uRin);
    if (hf <= 0.0 || hf >= 1.0) return 0.0;
    vec3 dir = lp / r;
    float cov = fbm4(dir * 8.0 + uWind);
    cov = remap(cov, mix(0.68, 0.32, uCoverage), 1.0, 0.0, 1.0);
    if (cov <= 0.0) return 0.0;
    float hg = smoothstep(0.0, 0.18, hf) * (1.0 - smoothstep(0.5, 1.0, hf));
    return cov * hg;
}
// Full density: the cheap field carved by higher-frequency billow erosion (the view march).
float density(vec3 lp) {
    float d = densityLow(lp);
    if (d <= 0.0) return 0.0;
    float ero = billow3(lp * 120.0 + uWind * 3.0);
    return clamp(remap(d, ero * 0.32, 1.0, 0.0, 1.0), 0.0, 1.0);
}
float hgPhase(float c, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
}
// Optical depth from a point toward the sun (for self-shadowing).
float lightTau(vec3 P) {
    float l0, l1; raySphere(P, uSunDir, uPlanet, uRout, l0, l1);
    float far = max(l1, 0.0);
    float p0, p1; if (raySphere(P, uSunDir, uPlanet, uRp, p0, p1) && p0 > 0.0) far = min(far, p0);
    float lseg = far / float(LIGHT), lsegM = lseg * uRpMeters, sum = 0.0;
    for (int j = 0; j < LIGHT; j++)
        sum += densityLow(P + uSunDir * (lseg * (float(j) + 0.5)) - uPlanet) * lsegM;
    return sum * uSigma;
}
// Ordered 4x4 Bayer dither in [0,1) — a stable march-start offset that upscales far cleaner than
// per-pixel random noise (which created the speckle).
float bayer(vec2 p) {
    int M[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
    int i = int(mod(p.x, 4.0)) + int(mod(p.y, 4.0)) * 4;
    return float(M[i]) / 16.0;
}

void main() {
    const vec4 CLEAR = vec4(0.0, 0.0, 0.0, 1.0);
    vec3 ro = vec3(0.0);
    vec3 rd = normalize(uForward + vNdc.x * uTanHalfFov * uAspect * uRight + vNdc.y * uTanHalfFov * uUp);

    float a0, a1;
    if (!raySphere(ro, rd, uPlanet, uRout, a0, a1) || a1 < 0.0) { FragColor = CLEAR; return; }
    float tStart = max(a0, 0.0), tEnd = a1;
    float i0, i1;
    if (raySphere(ro, rd, uPlanet, uRin, i0, i1) && i0 > 0.0) tEnd = min(tEnd, i0);

    float pp0, pp1;
    if (raySphere(ro, rd, uPlanet, uRp, pp0, pp1) && pp0 > 0.0) tEnd = min(tEnd, pp0);
    vec2 uv = vNdc * 0.5 + 0.5;
    float depth = textureLod(uDepth, uv, 0.0).r; // full-res depth sampled at this (half-res) pixel
    if (depth < 1.0) {
        float ndcZ = depth * 2.0 - 1.0;
        float eyeZ = (2.0 * uNear * uFar) / (uFar + uNear - ndcZ * (uFar - uNear));
        float geom = (eyeZ / max(dot(rd, uForward), 1e-3)) / uRpMeters;
        tEnd = min(tEnd, geom);
    }
    if (tEnd <= tStart) { FragColor = CLEAR; return; }

    float seg = (tEnd - tStart) / float(PRIMARY);
    float segM = seg * uRpMeters;
    float t = tStart + seg * bayer(gl_FragCoord.xy);

    float cosT = dot(rd, uSunDir);
    float glory = hgPhase(cosT, 0.72);   // tight forward sun-halo bonus
    float T = 1.0;
    vec3 scatter = vec3(0.0);
    for (int i = 0; i < PRIMARY; i++) {
        if (T < 0.05) break;
        float d = density((ro + rd * t) - uPlanet);
        if (d > 0.0) {
            // Lit-vs-shadow model: bright sunlit colour where the sun reaches the sample, fading to
            // a blue ambient where it's self-shadowed — far brighter and more stable than a raw
            // normalised phase, which goes near-zero (dark) when viewing the lit side from sun-side.
            float lightT = exp(-lightTau(ro + rd * t));
            float powder = 1.0 - exp(-d * 3.0);          // dark cores, brighter edges
            vec3 S = mix(uAmbient, uSunColor, lightT) * mix(0.65, 1.0, powder)
                   + uSunColor * (glory * 0.5 * lightT);  // forward glory only where lit
            float dt = d * segM * uSigma;
            float Ti = exp(-dt);
            scatter += T * S * (1.0 - Ti);
            T *= Ti;
        }
        t += seg;
    }
    FragColor = vec4(scatter, T); // premultiplied; alpha = transmittance
}";

    // Upscale + composite the half-res cloud buffer over the scene (blend ONE, SRC_ALPHA).
    private const string CompositeFragment = @"#version 410 core
in vec2 vNdc;
uniform sampler2D uCloud;
out vec4 FragColor;
void main() { FragColor = texture(uCloud, vNdc * 0.5 + 0.5); }";

    // Option #2: a cheap analytic cloud SHELL. Instead of marching a volume, intersect a single cloud
    // sphere, sample 2D coverage at the hit direction, and shade it like a thin lit layer. Reads great
    // as a cloud band on the globe from orbit (flat overhead from the surface). Full-res, one sample.
    private const string ShellFragment = @"#version 410 core
in vec2 vNdc;
uniform vec3  uForward, uRight, uUp;
uniform float uTanHalfFov, uAspect;
uniform vec3  uPlanet, uSunDir;
uniform float uCloudR;                 // cloud layer radius (rp units)
uniform float uCoverage, uOpacity;
uniform vec3  uWind, uSunColor, uAmbient;
uniform sampler2D uDepth;
uniform float uNear, uFar, uRpMeters;
out vec4 FragColor;
const float PI = 3.14159265359;

bool raySphere(vec3 o, vec3 d, vec3 c, float r, out float t0, out float t1) {
    vec3 oc = o - c; float b = dot(oc, d); vec3 perp = oc - b * d;
    float disc = r * r - dot(perp, perp);
    if (disc < 0.0) return false;
    float s = sqrt(disc); t0 = -b - s; t1 = -b + s; return true;
}
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.yzx + 33.33); return fract((p.x + p.y) * p.z); }
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i + vec3(0,0,0)), n100 = hash13(i + vec3(1,0,0));
    float n010 = hash13(i + vec3(0,1,0)), n110 = hash13(i + vec3(1,1,0));
    float n001 = hash13(i + vec3(0,0,1)), n101 = hash13(i + vec3(1,0,1));
    float n011 = hash13(i + vec3(0,1,1)), n111 = hash13(i + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm4(vec3 p) { float s=0.0,a=0.5,n=0.0; for(int i=0;i<4;i++){s+=a*vnoise(p);n+=a;p*=2.02;a*=0.5;} return s/n; }
float remap(float v, float lo, float hi, float nlo, float nhi) {
    return nlo + (clamp(v, lo, hi) - lo) / max(hi - lo, 1e-4) * (nhi - nlo);
}

void main() {
    const vec4 CLEAR = vec4(0.0, 0.0, 0.0, 1.0);
    vec3 ro = vec3(0.0);
    vec3 rd = normalize(uForward + vNdc.x * uTanHalfFov * uAspect * uRight + vNdc.y * uTanHalfFov * uUp);

    float t0, t1;
    if (!raySphere(ro, rd, uPlanet, uCloudR, t0, t1)) { FragColor = CLEAR; return; }
    bool outside = t0 > 0.0;            // outside the layer (orbit) vs inside it (on the surface)
    float t = outside ? t0 : t1;        // front of the layer, or the ceiling overhead when inside
    if (t <= 0.0) { FragColor = CLEAR; return; }

    // Terrain in front of the layer occludes it (e.g. a peak poking above the cloud deck).
    vec2 uv = vNdc * 0.5 + 0.5;
    float depth = textureLod(uDepth, uv, 0.0).r;
    if (depth < 1.0) {
        float ndcZ = depth * 2.0 - 1.0;
        float eyeZ = (2.0 * uNear * uFar) / (uFar + uNear - ndcZ * (uFar - uNear));
        float geom = (eyeZ / max(dot(rd, uForward), 1e-3)) / uRpMeters;
        if (geom < t) { FragColor = CLEAR; return; }
    }

    vec3 dir = normalize((ro + rd * t) - uPlanet);
    float cov = fbm4(dir * 8.0 + uWind);
    cov = remap(cov, mix(0.68, 0.32, uCoverage), 1.0, 0.0, 1.0);
    if (cov <= 0.0) { FragColor = CLEAR; return; }
    cov *= mix(0.6, 1.0, fbm4(dir * 40.0 + uWind * 2.0)); // higher-freq breakup of the edges
    // From orbit, fade out at the limb so the layer doesn't show a hard ring at the planet's edge.
    // From inside the layer (on the surface) there's no limb — keep full opacity overhead.
    float limb = outside ? smoothstep(0.0, 0.25, dot(dir, -rd)) : 1.0;
    float a = clamp(cov, 0.0, 1.0) * uOpacity * limb;

    float ndl = max(dot(dir, uSunDir), 0.0);
    vec3 col = mix(uAmbient, uSunColor, ndl);
    FragColor = vec4(col * a, 1.0 - a); // premultiplied; alpha = transmittance
}";

    private readonly GL _gl;
    private readonly Shader _march;
    private readonly Shader _composite;
    private readonly Shader _shell;
    private readonly uint _vao;
    private readonly ColorTarget _half;   // half-resolution cloud buffer (volumetric only)
    private int _fullW, _fullH;

    public CloudRenderer(GL gl)
    {
        _gl = gl;
        _march = new Shader(gl, VertexSource, MarchFragment);
        _composite = new Shader(gl, VertexSource, CompositeFragment);
        _shell = new Shader(gl, VertexSource, ShellFragment);
        _vao = gl.GenVertexArray();
        _half = new ColorTarget(gl);
    }

    /// <summary>Match the half-res buffer to the (full) framebuffer size.</summary>
    public void Resize(int fullWidth, int fullHeight)
    {
        _fullW = fullWidth; _fullH = fullHeight;
        _half.Resize(Math.Max(1, fullWidth / 2), Math.Max(1, fullHeight / 2));
    }

    /// <summary>
    /// March clouds at half-res, then composite into <paramref name="post"/>. Call after the scene is
    /// blitted to <paramref name="post"/> and BEFORE the atmosphere; <paramref name="depthTexture"/>/
    /// <paramref name="near"/>/<paramref name="far"/> are the scene depth and its planes (for occlusion).
    /// Leaves <paramref name="post"/> bound (full-res viewport) for the atmosphere pass that follows.
    /// </summary>
    public void Render(Camera camera, SolarSystem system, uint depthTexture, float near, float far,
        float time, CelestialBody? groundBody, double groundAmplitudeMeters, ColorTarget post)
    {
        if (!Enabled || _fullW == 0) return;

        UniversePosition cam = camera.Position;
        CelestialBody? body = PickBody(system, cam, groundBody);
        if (body == null) { return; }

        Vector3D<float> sunRel = system.Sun.Position.ToCameraRelative(cam);
        Vector3D<float> planetRel = body.CurrentPosition.ToCameraRelative(cam);
        Vector3D<float> sunDir = Vector3D.Normalize(sunRel - planetRel);

        double rp = body.RadiusMeters;
        float atmH = Math.Max(0.012f, body.AtmosphereHeight);
        // Cloud deck sits a little above the surface — but never below the tallest terrain, or the
        // mountains poke through the layer (and the half-res depth edge dithers at that intersection).
        float ampFrac = ReferenceEquals(body, groundBody) && groundAmplitudeMeters > 0.0
            ? (float)(groundAmplitudeMeters / rp) : 0f;
        float baseAlt = Math.Max(atmH * 0.06f, ampFrac * 1.15f);
        Vector3D<float> planetScaled = planetRel * (float)(1.0 / rp);
        float w = WindSpeed * time;
        var wind = new Vector3D<float>(w, w * 0.3f, -w * 0.6f);
        var sunColor = new Vector3D<float>(1.5f, 1.45f, 1.35f);  // bright sunlit cloud
        var ambient = new Vector3D<float>(0.40f, 0.44f, 0.54f);  // bluish self-shadow

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);

        if (Volumetric)
        {
            // #3: raymarch the shell into the half-res buffer (writes every pixel; no clear/blend),
            // then upscale-composite over the scene.
            float rin = 1.0f + baseAlt;
            float rout = rin + atmH * 0.20f;
            _half.Bind();
            _gl.Disable(EnableCap.Blend);
            _march.Use();
            _gl.BindTexture(TextureTarget.Texture2D, depthTexture);
            _march.SetInt("uDepth", 0);
            _march.SetFloat("uNear", near);
            _march.SetFloat("uFar", far);
            _march.SetFloat("uRpMeters", (float)rp);
            _march.SetVector3("uForward", camera.Forward);
            _march.SetVector3("uRight", camera.Right);
            _march.SetVector3("uUp", camera.Up);
            _march.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
            _march.SetFloat("uAspect", camera.AspectRatio);
            _march.SetVector3("uPlanet", planetScaled);
            _march.SetVector3("uSunDir", sunDir);
            _march.SetFloat("uRp", 1.0f);
            _march.SetFloat("uRin", rin);
            _march.SetFloat("uRout", rout);
            _march.SetFloat("uCoverage", Math.Clamp(Coverage, 0f, 1f));
            _march.SetFloat("uSigma", 0.006f * Math.Max(0f, Density)); // extinction per metre × density
            _march.SetVector3("uWind", wind);
            _march.SetVector3("uSunColor", sunColor);
            _march.SetVector3("uAmbient", ambient);
            _march.SetFloat("uG", 0.55f);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            post.Bind(); // restores the full-res viewport
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.SrcAlpha);
            _composite.Use();
            _gl.BindTexture(TextureTarget.Texture2D, _half.ColorTexture);
            _composite.SetInt("uCloud", 0);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        else
        {
            // #2: cheap analytic shell layer, drawn full-res straight into the scene buffer.
            post.Bind();
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.SrcAlpha);
            _shell.Use();
            _gl.BindTexture(TextureTarget.Texture2D, depthTexture);
            _shell.SetInt("uDepth", 0);
            _shell.SetFloat("uNear", near);
            _shell.SetFloat("uFar", far);
            _shell.SetFloat("uRpMeters", (float)rp);
            _shell.SetVector3("uForward", camera.Forward);
            _shell.SetVector3("uRight", camera.Right);
            _shell.SetVector3("uUp", camera.Up);
            _shell.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
            _shell.SetFloat("uAspect", camera.AspectRatio);
            _shell.SetVector3("uPlanet", planetScaled);
            _shell.SetVector3("uSunDir", sunDir);
            _shell.SetFloat("uCloudR", 1.0f + baseAlt + atmH * 0.13f); // layer mid-height
            _shell.SetFloat("uCoverage", Math.Clamp(Coverage, 0f, 1f));
            _shell.SetFloat("uOpacity", Math.Clamp(Density, 0f, 1.5f) * 0.92f);
            _shell.SetVector3("uWind", wind);
            _shell.SetVector3("uSunColor", sunColor);
            _shell.SetVector3("uAmbient", ambient);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        _gl.BindVertexArray(0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
    }

    // Clouds only attach to terrestrial worlds. Gas/ice giants set HasAtmosphere (for the atmosphere
    // limb/scattering) but have no solid surface — their banding IS their appearance, so an Earth-style
    // cloud deck layered over it just looks wrong. Gate on HasSurface to exclude them.
    private static CelestialBody? PickBody(SolarSystem system, in UniversePosition cam, CelestialBody? groundBody)
    {
        if (groundBody is { HasAtmosphere: true, HasSurface: true }) return groundBody;
        CelestialBody? best = null;
        double bestSq = double.MaxValue;
        foreach (CelestialBody b in system.AllBodies())
        {
            if (!b.HasAtmosphere || !b.HasSurface) continue;
            double dsq = b.CurrentPosition.DistanceSquaredTo(cam);
            if (dsq < bestSq) { bestSq = dsq; best = b; }
        }
        return best;
    }

    public void Dispose()
    {
        _march.Dispose();
        _composite.Dispose();
        _shell.Dispose();
        _half.Dispose();
        _gl.DeleteVertexArray(_vao);
    }
}
