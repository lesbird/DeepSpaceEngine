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

    // Distant planets/moons are drawn as smooth spheres (the quadtree terrain only activates for the
    // one body you're near). This shader gives those spheres procedural surface detail — macro relief,
    // impact craters and maria — so they read as real worlds from afar, all per-pixel and footprint
    // band-limited (no aliasing) with no per-planet quadtree. Same noise/crater model as the terrain's
    // orbital pass, so a body looks consistent as you approach and terrain takes over.
    private const string PlanetFragment = @"#version 410 core
in vec3 vWorld;
uniform vec3 uPlanetCenter;
uniform vec3 uSunCenter;
uniform vec3 uColor;
uniform float uEmissive;
uniform float uReliefAmp;       // relief height (metres) → normal perturbation
uniform float uReliefFreq;      // macro-relief base frequency (cells over the unit sphere)
uniform float uCraterStrength;  // 0 = not a cratered (airless) world
uniform float uCraterFreq;      // largest-crater base frequency
uniform float uMariaStrength;   // dark basaltic-plain provinces (airless)
uniform float uPlanetRadiusM;
uniform vec3  uSeedOffset;      // per-planet noise offset (variety)
// Baked surface map for the focused body: exact-match albedo + planet-local normal. When present it
// replaces the procedural detail, so the distant sphere shows the same craters you land in.
uniform float uHasMap;
uniform sampler2D uAlbedoMap;
uniform sampler2D uNormalMap;
out vec4 FragColor;

const float PI_M = 3.14159265359;
vec2 dirToUv(vec3 d) {
    return vec2(atan(d.z, d.x) / (2.0 * PI_M) + 0.5, asin(clamp(d.y, -1.0, 1.0)) / PI_M + 0.5);
}

float h13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.yzx + 33.33); return fract((p.x + p.y) * p.z); }
float vn(vec3 x) {
    vec3 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    float n000 = h13(i), n100 = h13(i + vec3(1,0,0)), n010 = h13(i + vec3(0,1,0)), n110 = h13(i + vec3(1,1,0));
    float n001 = h13(i + vec3(0,0,1)), n101 = h13(i + vec3(1,0,1)), n011 = h13(i + vec3(0,1,1)), n111 = h13(i + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x), x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm(vec3 p) { float s=0.0,a=0.5,n=0.0; for (int i=0;i<5;i++){s+=a*vn(p);n+=a;p*=2.03;a*=0.5;} return s/n; }
float reliefF(vec3 d, float f, float fp) {
    float s=0.0, fr=f, a=1.0, nm=0.0;
    for (int o=0;o<6;o++){ float aa=1.0-smoothstep(0.4,0.9,fr*fp); s+=aa*a*(vn(d*fr)*2.0-1.0); nm+=a; fr*=2.0; a*=0.6; }
    return s / max(nm, 1e-4);
}
float craterF(vec3 dir, float baseFreq, float fp) {
    float sum=0.0, wsum=0.0, freq=baseFreq, weight=1.0;
    for (int o=0;o<3;o++){   // coarse bands only — distant spheres need just the big craters
        float aa = 1.0 - smoothstep(0.5, 1.1, freq * fp);
        if (aa > 0.0) {
            vec3 p = dir * freq; vec3 ip = floor(p); float minBowl=0.0, maxRim=0.0, salt=float(o)*17.0;
            for (int dz=-1;dz<=1;dz++) for (int dy=-1;dy<=1;dy++) for (int dx=-1;dx<=1;dx++) {
                vec3 c = ip + vec3(float(dx),float(dy),float(dz));
                float ex = h13(c + vec3(salt)); if (ex > 0.6) continue;
                vec3 jit = vec3(h13(c+vec3(salt+1.7)), h13(c+vec3(salt+9.1)), h13(c+vec3(salt+4.3)));
                float radius = 0.22 + 0.28 * fract(ex*7.3+0.19);
                float t = length(p - (c + jit)) / radius; if (t >= 1.5) continue;
                float bowl = -(1.0 - smoothstep(0.0, 0.85, min(t,1.0)));
                float e = (t - 0.95) / 0.12; float rim = 0.28 * exp(-0.5*e*e);
                minBowl = min(minBowl, bowl); maxRim = max(maxRim, rim);
            }
            sum += weight * aa * (minBowl + maxRim);
        }
        wsum += weight; freq *= 1.9; weight *= 0.62;
    }
    return sum / max(wsum, 1e-4);
}

void main() {
    vec3 N = normalize(vWorld - uPlanetCenter);
    vec3 L = normalize(uSunCenter - vWorld);
    vec3 col = uColor;

    if (uEmissive < 0.5 && uHasMap > 0.5) {
        // Exact baked surface (same source as the terrain) — distant view matches the surface.
        vec2 uv = dirToUv(N);
        col = texture(uAlbedoMap, uv).rgb;
        N = normalize(texture(uNormalMap, uv).rgb * 2.0 - 1.0);
    } else if (uEmissive < 0.5 && (uReliefAmp > 0.0 || uCraterStrength > 0.0)) {
        vec3 dir = N + uSeedOffset;                                   // per-planet variety
        float fp = max(max(fwidth(N.x), fwidth(N.y)), fwidth(N.z));   // pixel footprint
        vec3 up = abs(N.y) < 0.99 ? vec3(0,1,0) : vec3(1,0,0);
        vec3 T1 = normalize(cross(N, up)); vec3 T2 = cross(N, T1);
        float eps = max(fp, 1e-5);
        float r0 = reliefF(dir, uReliefFreq, fp), rA = reliefF(dir+T1*eps, uReliefFreq, fp), rB = reliefF(dir+T2*eps, uReliefFreq, fp);
        float invArc = uReliefAmp / (eps * uPlanetRadiusM);
        N = normalize(N - ((rA-r0)*T1 + (rB-r0)*T2) * invArc);       // relief normal (cheap fBm, no cell search)
        col *= 1.0 + 0.12 * (fbm(dir * 4.0) * 2.0 - 1.0);            // subtle large-scale mottle
        if (uCraterStrength > 0.001) {
            // Craters as albedo only (one crater-field evaluation) — the expensive 3×3×3 search runs once.
            float c0 = craterF(dir, uCraterFreq, fp);
            col *= 1.0 - 0.45 * uCraterStrength * max(0.0, -c0);     // crater floors darker
            col *= 1.0 + 0.30 * uCraterStrength * max(0.0,  c0) / 0.28; // rims/ejecta brighter
        }
        if (uMariaStrength > 0.001) {
            float m = smoothstep(0.08, 0.5, fbm(dir * 2.2) * 2.0 - 1.0);
            col = mix(col, col * vec3(0.55, 0.56, 0.60), m * uMariaStrength);
        }
    }

    float diff = max(dot(N, L), 0.0);
    vec3 lit = col * (0.04 + diff);
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
    private readonly uint _dummyTex; // 1×1 placeholder for the map sampler units when no surface map is bound

    public SystemRenderer(GL gl)
    {
        _gl = gl;
        _planetShader = new Shader(gl, PlanetVertex, PlanetFragment);
        _orbitShader = new Shader(gl, OrbitVertex, OrbitFragment);
        _ringShader = new Shader(gl, RingVertex, RingFragment);
        _sphere = Primitives.BuildSphere(gl, stacks: 24, slices: 48);
        _orbit = Primitives.BuildCircleLine(gl, segments: 128);
        _ring = Primitives.BuildRingAnnulus(gl, segments: 256);

        // A 1×1 white texture to bind to the map sampler units (uAlbedoMap/uNormalMap) when a body has no
        // baked surface map: the samplers are unused then (uHasMap = 0) but must still reference a COMPLETE
        // texture, or macOS GL logs "unloadable texture, using zero texture" for the empty unit.
        _dummyTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _dummyTex);
        unsafe
        {
            Span<byte> px = stackalloc byte[] { 255, 255, 255, 255 };
            fixed (byte* p = px)
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, 1, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Render(Camera camera, SolarSystem system, CelestialBody? terrainBody = null,
        PlanetSurfaceMap? map = null)
    {
        UniversePosition cam = camera.Position;
        Vector3D<float> sunRel = system.Sun.Position.ToCameraRelative(cam);
        ulong mapId = map is { Ready: true } ? map.BodyId : ulong.MaxValue;

        Matrix4X4<float> proj = FitProjection(camera, system);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);

        // --- Sun (emissive) ---
        _planetShader.Use();
        _planetShader.SetVector3("uSunCenter", sunRel);
        // Bind the surface map (focused body) or a complete 1×1 placeholder to the map sampler units, so the
        // samplers always reference a valid texture even when a body draws with uHasMap = 0 (no warning).
        uint albedoTex = map is { Ready: true } ? map.AlbedoTex : _dummyTex;
        uint normalTex = map is { Ready: true } ? map.NormalTex : _dummyTex;
        _gl.ActiveTexture(TextureUnit.Texture0); _gl.BindTexture(TextureTarget.Texture2D, albedoTex);
        _gl.ActiveTexture(TextureUnit.Texture1); _gl.BindTexture(TextureTarget.Texture2D, normalTex);
        _planetShader.SetInt("uAlbedoMap", 0);
        _planetShader.SetInt("uNormalMap", 1);
        _gl.ActiveTexture(TextureUnit.Texture0);
        DrawBody(viewProj, sunRel, system.Sun.RadiusMeters, system.Sun.Color, sunRel, emissive: 1f, SurfaceParams.None, useMap: false);

        // --- Planets + moons (lit) ---
        foreach (Planet p in system.Planets)
        {
            // The terrain renderer draws the active body as a detailed cube-sphere instead — skip
            // it here whether it's this planet or one of its moons.
            if (!ReferenceEquals(p, terrainBody))
            {
                Vector3D<float> rel = p.CurrentPosition.ToCameraRelative(cam);
                DrawBody(viewProj, rel, p.RadiusMeters, p.SurfaceAlbedo, sunRel, emissive: 0f, ParamsFor(p), p.Seed == mapId);
            }

            foreach (Moon mn in p.Moons)
            {
                if (ReferenceEquals(mn, terrainBody)) continue;
                Vector3D<float> mrel = mn.CurrentPosition.ToCameraRelative(cam);
                DrawBody(viewProj, mrel, mn.RadiusMeters, mn.SurfaceAlbedo, sunRel, emissive: 0f, ParamsFor(mn), mn.Seed == mapId);
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
        Vector3D<float> color, Vector3D<float> sunRel, float emissive, in SurfaceParams sp, bool useMap)
    {
        Matrix4X4<float> model = Matrix4X4.CreateScale((float)radius) * Matrix4X4.CreateTranslation(rel);
        _planetShader.SetMatrix("uModel", model);
        _planetShader.SetMatrix("uMVP", model * viewProj);
        _planetShader.SetVector3("uPlanetCenter", rel);
        _planetShader.SetVector3("uSunCenter", sunRel);
        _planetShader.SetVector3("uColor", color);
        _planetShader.SetFloat("uEmissive", emissive);
        _planetShader.SetFloat("uReliefAmp", sp.ReliefAmp);
        _planetShader.SetFloat("uReliefFreq", sp.ReliefFreq);
        _planetShader.SetFloat("uCraterStrength", sp.CraterStrength);
        _planetShader.SetFloat("uCraterFreq", sp.CraterFreq);
        _planetShader.SetFloat("uMariaStrength", sp.MariaStrength);
        _planetShader.SetFloat("uPlanetRadiusM", (float)radius);
        _planetShader.SetVector3("uSeedOffset", sp.SeedOffset);
        _planetShader.SetFloat("uHasMap", useMap ? 1f : 0f);
        _sphere.Draw();
    }

    /// <summary>Cheap per-body procedural-surface params for the distant sphere shader, derived from
    /// the body (no PlanetTerrain needed). Gas/ice giants stay flat; airless worlds get craters + maria;
    /// others get gentle relief. The seed offset varies the noise so worlds differ.</summary>
    private readonly record struct SurfaceParams(
        float ReliefAmp, float ReliefFreq, float CraterStrength, float CraterFreq, float MariaStrength,
        Vector3D<float> SeedOffset)
    {
        public static readonly SurfaceParams None = new(0f, 0f, 0f, 0f, 0f, default);
    }

    private static SurfaceParams ParamsFor(CelestialBody b)
    {
        if (!b.HasSurface) return SurfaceParams.None; // gas/ice giants keep their flat banding colour
        bool airless = !b.HasAtmosphere;
        var rng = new DeterministicRng(Hashing.Combine(b.Seed, 0xB0DEu));
        var off = new Vector3D<float>((float)rng.Range(0, 128), (float)rng.Range(0, 128), (float)rng.Range(0, 128));
        return new SurfaceParams(
            ReliefAmp: (float)(b.RadiusMeters * (airless ? 0.020 : 0.012)),
            ReliefFreq: 6f,
            CraterStrength: airless ? 1f : 0f,
            CraterFreq: 18f,
            MariaStrength: airless ? 0.6f : 0f,
            SeedOffset: off);
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
        // The asteroid belt extends past the planets in some systems — keep its rocks inside the far plane.
        if (system.Belt != null) Consider(system.Sun.Position, system.Belt.OuterRadius);

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
        _gl.DeleteTexture(_dummyTex);
    }
}
