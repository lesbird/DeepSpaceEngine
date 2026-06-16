using System.Collections.Concurrent;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders one planet as a cube-sphere with quadtree chunked LOD. Each of the 6 cube
/// faces is a quadtree; nodes split as the camera approaches. A patch's vertices are
/// stored relative to that patch's own centre (a <see cref="UniversePosition"/>), so when
/// the camera is at the surface the numbers fed to the GPU are tiny and precise — no
/// jitter, at any distance from the universe origin.
/// </summary>
public sealed class PlanetTerrainRenderer : IDisposable
{
    private const int GridN = 16;          // grid cells per patch edge
    private const int MaxLevel = 22;       // deepest subdivision (≈ sub-metre on an Earth-sized world)
    private const double LodFactor = 2.5;  // split when distance < LodFactor × patch size
    private const double MergeHysteresis = 1.3; // keep children cached until 1.3× the split distance (anti-thrash)
    // The expensive part of a patch — sampling the height field and building its vertex arrays — runs
    // on a background worker pool (see WorkerLoop). Only the cheap GPU upload happens on the render
    // thread, capped at this many finished patches per frame so a burst of bakes can't stall a frame.
    private const int UploadsPerFrame = 8;
    private const int LandFloatsPerVertex = 16; // finePos(3) + coarsePos(3) + fineNrm(3) + coarseNrm(3) + color(3) + relief(1)
    // Coarse anchor for the surface-detail octave stack (~64 m wavelength). The shader runs 8 octaves
    // up from here (×1…×128 → ~64 m down to ~0.5 m) as integer multipliers, so the seamless 4096-cell
    // wrap still cancels at patch edges. A band-pass per octave keeps ~4 active at any distance: fine
    // ones up close, coarse ones far away, so the ground stays textured out to the horizon.
    private const double DetailBaseFreq = 0.015625; // detail-noise cells per metre (1/64 m)

    // Geomorphing: each vertex carries both a FINE position/normal (this patch's resolution) and a
    // COARSE one (its parent's resolution). uMorph (0 = full detail, 1 = exactly the parent's
    // surface) blends them, so a leaf reaches its parent's shape precisely at the merge boundary —
    // the LOD swap is then invisible instead of a pop.
    private const string VertexSource = @"#version 410 core
layout(location = 0) in vec3 aFinePos;
layout(location = 1) in vec3 aCoarsePos;
layout(location = 2) in vec3 aFineNormal;
layout(location = 3) in vec3 aCoarseNormal;
layout(location = 4) in vec3 aColor;
layout(location = 5) in float aRelief;   // macro-relief mask (0 ocean/plains → 1 rugged highlands)
uniform mat4 uMVP;
uniform mat4 uModel;          // camera-relative patch translation — gives the view vector for specular
uniform float uMorph;
uniform vec3 uPatchFracBase;  // fract(patchCenter * detailFreq) — integer part travels in uPatchCellBase
uniform float uDetailFreq;    // detail-noise cells per metre
uniform vec3 uPatchCenter;    // patch centre in planet-local space (so we can rebuild the direction)
out vec3 vNormal;
out vec3 vColor;
out vec3 vNoiseCoord;         // planet-stable, precise fractional noise coordinate (no swim, no seams)
out vec3 vDir;                // planet-local surface direction (unit) — drives the orbital macro relief
out vec3 vWorld;              // camera-relative world position (camera at the origin) — for the view dir
out float vRelief;
void main() {
    vec3 pos = mix(aFinePos, aCoarsePos, uMorph);
    vNormal = normalize(mix(aFineNormal, aCoarseNormal, uMorph));
    vColor = aColor;
    // pos is patch-centre-relative (small, precise); adding fracBase reconstructs the planet-local
    // noise coordinate without the precision loss of using full planet-scale positions.
    vNoiseCoord = uPatchFracBase + pos * uDetailFreq;
    // pos + patch centre is the planet-local position; normalised it is the surface direction. Low
    // frequencies only, so float precision here is ample.
    vDir = normalize(pos + uPatchCenter);
    vWorld = (uModel * vec4(pos, 1.0)).xyz;
    vRelief = aRelief;
    gl_Position = uMVP * vec4(pos, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vNormal;
in vec3 vColor;
in vec3 vNoiseCoord;
in vec3 vDir;
in vec3 vWorld;
in float vRelief;
uniform vec3 uSunDir;
uniform vec3 uPatchCellBase;   // wrapped integer cell base (planet-local), keeps the hash precise
uniform float uDetailStrength; // detail-normal bump strength (0 = off)
uniform float uDetailAlbedo;   // albedo break-up amount
uniform float uDetailLowCut;   // smallest cells-per-pixel an octave shows at (smaller = detail reaches further)
uniform float uMaterialStrength; // procedural material breakup: cracks/cavity + mineral tint (0 = off)
uniform float uSurfaceSpecular;  // close-up specular highlight strength (0 = off)
uniform float uSurfaceAmbient;   // hemispheric ambient floor (sky/ground fill)
// Orbital macro relief: mountain-scale relief that shows from space via lighting + albedo only.
uniform float uReliefBaseFreq;     // base noise frequency (cells over the unit sphere)
uniform float uReliefAmp;          // relief height scale (metres) used to turn slopes into a normal
uniform float uVertexSpacingDir;   // this patch's vertex spacing in unit-direction units (spacing / R)
uniform float uPlanetRadius;       // metres (converts a direction-space step to surface arc length)
uniform float uOrbitalStrength;    // macro-relief normal strength (0 = off)
uniform float uOrbitalAlbedo;      // macro-relief albedo mottle amount
uniform float uCraterStrength;     // orbital crater shading (0 = not a cratered world)
uniform float uCraterFreq;         // largest-crater base frequency (cells over the unit sphere)
// Baked surface map (albedo + planet-local normal), sampled on coarse far patches where the mesh is
// too sparse to carry craters/relief. Same source as the geometry, so the orbital view matches the
// surface exactly. uHasMap = 0 falls back to the procedural orbital relief below.
uniform float uHasMap;
uniform sampler2D uAlbedoMap;
uniform sampler2D uNormalMap;
out vec4 FragColor;

const float PI_M = 3.14159265359;
vec2 dirToUv(vec3 d) {
    return vec2(atan(d.z, d.x) / (2.0 * PI_M) + 0.5, asin(clamp(d.y, -1.0, 1.0)) / PI_M + 0.5);
}

// Small-input integer hash (Dave Hoskins style) — cell coords are wrapped below so it stays precise.
float hash13(vec3 p) {
    p = mod(p, 4096.0);
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

// Value noise in [0,1]. `base` is the (large, integer) planet-local cell; `nc` the precise small
// fractional coordinate. Splitting them this way avoids the catastrophic cancellation that using a
// single planet-scale float would cause, and the mod() in hash13 keeps the result seamless across
// patches (a shared planet-local point maps to the same cell + fraction from either patch).
float vnoise(vec3 base, vec3 nc) {
    vec3 c = base + floor(nc);
    vec3 f = fract(nc);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(c),                 n100 = hash13(c + vec3(1,0,0));
    float n010 = hash13(c + vec3(0,1,0)),   n110 = hash13(c + vec3(1,1,0));
    float n001 = hash13(c + vec3(0,0,1)),   n101 = hash13(c + vec3(1,0,1));
    float n011 = hash13(c + vec3(0,1,1)),   n111 = hash13(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}

// One detail octave at integer frequency multiplier m. Because the per-patch cell base is an integer,
// scaling both it and the fractional coordinate by an integer m reconstructs the exact same higher-
// frequency lattice from any patch — so finer octaves stay seamless and swim-free like the base one.
// Returns the value in .w (0..1) and its scaled-space gradient in .xyz (for the normal bump).
vec4 detailSample(float m) {
    vec3 bm = uPatchCellBase * m;
    vec3 sc = vNoiseCoord * m;
    float e = 0.15;
    float n0 = vnoise(bm, sc);
    float nx = vnoise(bm, sc + vec3(e, 0.0, 0.0));
    float ny = vnoise(bm, sc + vec3(0.0, e, 0.0));
    float nz = vnoise(bm, sc + vec3(0.0, 0.0, e));
    return vec4(vec3(nx - n0, ny - n0, nz - n0) / e, n0);
}

// Low-frequency 3D value noise in [0,1] over the planet direction. Coordinates stay small (a few ×
// the base frequency), so a single-coordinate hash is precise — no split-coordinate trick needed.
float vnoise3(vec3 p) {
    vec3 c = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(c),                 n100 = hash13(c + vec3(1,0,0));
    float n010 = hash13(c + vec3(0,1,0)),   n110 = hash13(c + vec3(1,1,0));
    float n001 = hash13(c + vec3(0,0,1)),   n101 = hash13(c + vec3(1,0,1));
    float n011 = hash13(c + vec3(0,1,1)),   n111 = hash13(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}

// Multi-octave macro relief height in ≈[-1,1] over a direction. Each octave fades out once it
// approaches the pixel footprint (fp = cells per pixel at the base), so the field is always smooth
// at pixel scale and never shimmers — the orbital analogue of the geometry band-limiter.
float reliefFbm(vec3 dir, float baseFreq, float fp) {
    float sum = 0.0, freq = baseFreq, amp = 1.0, norm = 0.0;
    for (int o = 0; o < 8; o++) {
        float aa = 1.0 - smoothstep(0.4, 0.9, freq * fp);   // anti-alias by the pixel footprint
        sum += aa * amp * (vnoise3(dir * freq) * 2.0 - 1.0);
        norm += amp;
        freq *= 2.0;
        amp *= 0.6;                                          // > 0.5 keeps ridges crisp
    }
    return sum / max(norm, 1e-6);
}

// Multi-octave impact-crater relief over a direction, in ≈[-1, rim]: a 3x3x3 cellular field of
// bowls + raised rims per octave (matching the CPU CraterField), each octave anti-aliased by the
// pixel footprint so only the scales a pixel can resolve contribute — large basins from orbit,
// finer craters as you approach. Lets the cratered look show from space, where the mesh is too
// coarse to carry baked craters.
float craterField(vec3 dir, float baseFreq, float fp) {
    float sum = 0.0, wsum = 0.0, freq = baseFreq, weight = 1.0;
    for (int o = 0; o < 3; o++) {   // coarse crater bands only — these read from orbit; finer ones are baked geometry
        float aa = 1.0 - smoothstep(0.5, 1.1, freq * fp);
        if (aa > 0.0) {
            vec3 p = dir * freq;
            vec3 ip = floor(p);
            float minBowl = 0.0, maxRim = 0.0, salt = float(o) * 17.0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) {
                vec3 c = ip + vec3(float(dx), float(dy), float(dz));
                float ex = hash13(c + vec3(salt));
                if (ex > 0.6) continue;                       // crater density
                vec3 jit = vec3(hash13(c + vec3(salt + 1.7)), hash13(c + vec3(salt + 9.1)), hash13(c + vec3(salt + 4.3)));
                float radius = 0.22 + 0.28 * fract(ex * 7.3 + 0.19);
                float t = length(p - (c + jit)) / radius;
                if (t >= 1.5) continue;
                float bowl = -(1.0 - smoothstep(0.0, 0.85, min(t, 1.0)));
                float e = (t - 0.95) / 0.12;
                float rim = 0.28 * exp(-0.5 * e * e);
                minBowl = min(minBowl, bowl);
                maxRim = max(maxRim, rim);
            }
            sum += weight * aa * (minBowl + maxRim);
        }
        wsum += weight; freq *= 1.9; weight *= 0.62;
    }
    return sum / max(wsum, 1e-4);
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 col = vColor;

    // --- Orbital surface detail the smooth far-LOD mesh can't show: baked map, else procedural. ---
    if (uHasMap > 0.5) {
        // Coarse (far) patches blend toward the baked map (which carries crater/relief the mesh lacks);
        // fine (near) patches keep the real geometry. Same source, so the crossfade is seamless and a
        // crater seen from orbit is exactly the one you land in.
        vec2 uv = dirToUv(normalize(vDir));
        vec3 mapAlb = texture(uAlbedoMap, uv).rgb;
        vec3 mapNrm = normalize(texture(uNormalMap, uv).rgb * 2.0 - 1.0);
        float w = smoothstep(0.0015, 0.006, uVertexSpacingDir); // patch coarser than a map texel → use map
        col = mix(col, mapAlb, w);
        N = normalize(mix(N, mapNrm, w));
    } else {
        vec3 dir = normalize(vDir);
        float fp = max(max(fwidth(dir.x), fwidth(dir.y)), fwidth(dir.z)); // pixel footprint (dir units)
        float geomCut = 1.0 / max(2.0 * uVertexSpacingDir, 1e-9);         // freq the mesh already carries
        // Each layer hands off to the real geometry as it resolves that band. Relief rides the
        // ruggedness mask (mountains live on highlands); craters cover the whole airless body.
        float reliefM = (uOrbitalStrength > 0.0001 ? vRelief : 0.0)
                      * (1.0 - smoothstep(uReliefBaseFreq, uReliefBaseFreq * 32.0, geomCut));
        float craterM = (uCraterStrength > 0.0001 ? 1.0 : 0.0)
                      * (1.0 - smoothstep(uCraterFreq, uCraterFreq * 64.0, geomCut));
        if (reliefM > 0.001 || craterM > 0.001) {
            // Surface-tangent gradient of the combined height → a normal perturbation. The step is the
            // pixel footprint, so the slope we light is exactly the one a pixel can resolve.
            vec3 up = abs(dir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
            vec3 T1 = normalize(cross(dir, up));
            vec3 T2 = cross(dir, T1);
            float eps = max(fp, 1e-5);
            float r0 = reliefM > 0.001 ? reliefFbm(dir, uReliefBaseFreq, fp) : 0.0;
            float rA = reliefM > 0.001 ? reliefFbm(normalize(dir + T1 * eps), uReliefBaseFreq, fp) : 0.0;
            float rB = reliefM > 0.001 ? reliefFbm(normalize(dir + T2 * eps), uReliefBaseFreq, fp) : 0.0;
            float sR = uOrbitalStrength * reliefM;
            float invArc = (uReliefAmp / (eps * uPlanetRadius));
            vec3 bump = ((rA - r0) * T1 + (rB - r0) * T2) * sR * invArc;
            N = normalize(N - bump);
            col *= 1.0 + uOrbitalAlbedo * sR * r0;                  // relief: valleys dark, ridges light

            // Craters as ALBEDO ONLY (one evaluation, not a 3-tap normal): from orbit the floor/rim
            // tone is what reads, and up close the crater GEOMETRY cascade already supplies the relief.
            // The 3×3×3 crater field is the dominant cost over a full-screen planet, so we evaluate it once.
            if (craterM > 0.001) {
                float c0 = craterField(dir, uCraterFreq, fp);
                float sC = uCraterStrength * craterM;
                col *= 1.0 - 0.45 * sC * max(0.0, -c0);            // crater floors darker
                col *= 1.0 + 0.30 * sC * max(0.0, c0) / 0.28;      // crater rims/ejecta brighter
            }
        }
    }

    if (uDetailStrength > 0.0001 || uMaterialStrength > 0.0001) {
        // Band-limit by the pixel footprint in noise cells: fade the detail out once a pixel spans
        // more than ~a cell, so it adds crisp roughness up close but never shimmers from orbit.
        float footprint = max(max(fwidth(vNoiseCoord.x), fwidth(vNoiseCoord.y)), fwidth(vNoiseCoord.z));
        // Band-pass octave stack: each octave shows only while its features span between ~2 px and
        // ~(2/uDetailLowCut) px. So whatever scale a pixel can resolve at THIS distance, an octave
        // covers it — fine grain up close, coarser texture far away — and the ground never collapses
        // to smooth plastic in the mid-distance. The low cut also hands coarse scales back to the
        // real mesh up close (where geometry already carries them), like the orbital layer's fade.
        vec3 tangAccum = vec3(0.0);
        float nDetail = 0.0, wsum = 0.0, m = 1.0;
        for (int o = 0; o < 8; o++) {   // 8 octaves: ~64 m (×1) down to ~0.5 m (×128)
            float c = footprint * m;                                  // this octave's cells per pixel
            float oct = (1.0 - smoothstep(0.45, 1.3, c))             // anti-alias (drop sub-pixel octaves)
                      * smoothstep(uDetailLowCut, uDetailLowCut * 3.0, c); // hand coarse scales to geometry
            if (oct > 0.0) {
                vec4 s = detailSample(m);
                tangAccum += oct * (s.xyz - dot(s.xyz, N) * N);       // tangential component
                nDetail += oct * (s.w - 0.5) * 2.0;                   // signed, ≈[-1,1]
                wsum += oct;
            }
            m *= 2.0;
        }
        float baseFade = clamp(wsum, 0.0, 1.0);   // material/albedo presence (0 only when nothing resolves)
        if (wsum > 0.0) {
            nDetail = nDetail / wsum;

            // (A) Multi-octave normal bump.
            N = normalize(N - uDetailStrength * tangAccum);

            // (B) Material breakup: brightness mottle, plus dark cracks/cavity that deepen on steep
            // faces, plus a faint mineral tint — so the surface reads as rock/sediment, not flat paint.
            float steep = 1.0 - clamp(dot(normalize(vNormal), normalize(vDir)), 0.0, 1.0); // 0 flat→1 cliff
            col *= 1.0 + uDetailAlbedo * nDetail * baseFade;
            float crack = smoothstep(0.25, -0.1, nDetail);                  // deep crevices (low noise)
            col *= 1.0 - uMaterialStrength * baseFade * crack * (0.35 + 0.65 * steep);
            col += uMaterialStrength * baseFade * 0.04 * nDetail * vec3(0.6, 0.5, 0.4);
        }
    }

    // (C) Lighting: hemispheric ambient (up catches sky, down gets ground bounce) so shadowed slopes
    // keep their form, plus a subtle specular that makes all the normal detail above actually read.
    vec3 up = normalize(vDir);
    vec3 V = normalize(-vWorld);
    float diff = max(dot(N, uSunDir), 0.0);
    float ambient = uSurfaceAmbient * mix(0.5, 1.0, 0.5 + 0.5 * dot(N, up));
    vec3 H = normalize(uSunDir + V);
    float gloss = mix(18.0, 70.0, clamp(col.b - col.r + 0.3, 0.0, 1.0)); // icy/wet (bluer) → tighter glint
    float spec = uSurfaceSpecular * pow(max(dot(N, H), 0.0), gloss) * diff;
    FragColor = vec4(col * (ambient + 0.95 * diff) + vec3(spec), 1.0);
}";

    // Translucent ocean surface: diffuse + a sharp sun glint + a fresnel edge that fades the
    // water more opaque at grazing angles. Colour (sand-shallow → deep-blue) is baked per vertex.
    // Animated ocean: three travelling sine swells displace the flat sea radially and bend its
    // normal analytically (moving glints). Phase is reconstructed planet-stably from a per-patch base
    // (uPhase = wave·patchCentre, mod 2π, in double on the CPU) plus the small patch-relative vertex
    // offset — so it neither swims with the floating origin nor seams between patches. Wave vectors,
    // speeds and amplitudes are uniforms (single source of truth shared with the CPU phase base).
    private const string WaterVertexSource = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aColor;   // rgb = water colour, a = depth-based base opacity
uniform mat4 uMVP;
uniform mat4 uModel;
uniform float uTime;
uniform float uWaveAmp;     // overall swell height scale (metres)
uniform vec3 uWD[3];        // wave directions (planet-local, unit)
uniform vec3 uWK;           // angular wavenumbers (2*pi / wavelength)
uniform vec3 uWW;           // angular speeds (2*pi / period)
uniform vec3 uWA;           // per-wave amplitude weights
uniform vec3 uPhase;        // per-patch phase base for the 3 waves
out vec3 vNormal;
out vec4 vColor;
out vec3 vWorld;
void main() {
    vec3 N = normalize(aNormal);
    float h = 0.0;
    vec3 grad = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        float ph = uPhase[i] + uWK[i] * dot(aPos, uWD[i]) + uTime * uWW[i];
        float a = uWA[i] * uWaveAmp;
        h += a * sin(ph);
        grad += a * cos(ph) * uWK[i] * uWD[i];   // d(height)/d(pos)
    }
    vec3 pos = aPos + N * h;
    vec3 tang = grad - dot(grad, N) * N;          // tangential slope of the swell
    vNormal = normalize(N - tang);
    vColor = aColor;
    vWorld = (uModel * vec4(pos, 1.0)).xyz;
    gl_Position = uMVP * vec4(pos, 1.0);
}";

    private const string WaterFragmentSource = @"#version 410 core
in vec3 vNormal;
in vec4 vColor;
in vec3 vWorld;
uniform vec3 uSunDir;
out vec4 FragColor;
void main() {
    vec3 N = normalize(vNormal);
    vec3 L = normalize(uSunDir);
    vec3 V = normalize(-vWorld);                 // camera sits at the origin in camera-relative space
    float diff = max(dot(N, L), 0.0);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 90.0); // tight sun glint
    float fres = pow(1.0 - max(dot(N, V), 0.0), 3.0);
    vec3 col = vColor.rgb * (0.12 + 0.9 * diff) + vec3(1.0) * spec * 0.7;
    // Base opacity is baked per vertex (deep water nearly opaque so the rugged sea floor never shows
    // through; shallows stay clear). Grazing angles firm up further via fresnel.
    float a = clamp(vColor.a + fres * 0.2, 0.0, 1.0);
    FragColor = vec4(col, a);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _waterShader;
    private readonly List<(TerrainPatch patch, Vector3D<float> rel, Vector3D<double> centerLocal)> _waterFrame = new();

    // Ocean swell waves (planet-local). The CPU phase base and the shader's spatial term share these,
    // so they must be the single source of truth — passed to the shader as uniforms each frame.
    private static readonly Vector3D<double>[] WaveDir =
    {
        Vector3D.Normalize(new Vector3D<double>(1.0, 0.0, 0.35)),
        Vector3D.Normalize(new Vector3D<double>(0.25, 0.0, 1.0)),
        Vector3D.Normalize(new Vector3D<double>(-0.8, 0.0, 0.6)),
    };
    private static readonly double[] WaveLen = { 55.0, 34.0, 21.0 };   // metres
    private static readonly double[] WavePeriod = { 5.5, 4.0, 3.0 };   // seconds
    private static readonly Vector3D<float> WaveAmp = new(0.9f, 0.5f, 0.3f); // metres

    // Background patch baking. Worker threads pull jobs, sample the (immutable) height field and build
    // the vertex arrays — all pure CPU work with no GL calls — then post the float arrays back here.
    // The render thread drains finished arrays and does the actual GPU upload (the only part that must
    // touch the single-threaded GL context). _epoch is bumped whenever the terrain target changes so
    // bakes still in flight for the old planet are discarded instead of uploaded against a stale tree.
    private readonly BlockingCollection<BuildJob> _jobQueue = new();
    private readonly ConcurrentQueue<BuildResult> _ready = new();
    private readonly Task[] _workers;
    private int _epoch;

    private CelestialBody? _body;
    private PlanetTerrain? _terrain;
    private QuadNode[]? _roots;
    private double _detailNoiseFreq; // this frame's detail-normal frequency (set in Render)

    public int LeafCount { get; private set; }
    public int PatchCount { get; private set; }
    public CelestialBody? Active => _body;

    /// <summary>When off, the ocean swell amplitude is zeroed so the water sits perfectly flat
    /// (still depth-writing for a clean horizon). Off by default for now.</summary>
    public bool AnimateWater = false;

    /// <summary>Max relief (metres) of the active terrain — how far below the base radius a valley can
    /// sit. The atmosphere uses this to drop its ground/clip radius so low terrain still gets hazed.</summary>
    public double ActiveAmplitude => _terrain?.Amplitude ?? 0.0;

    /// <summary>
    /// Optional extra LOD focus (e.g. the rover) — patches refine toward whichever is closer, the
    /// camera or this point, so the ground directly under the vehicle stays at fine vertex spacing
    /// even when the chase camera is a little farther back. Null disables it.
    /// </summary>
    public UniversePosition? FocusPoint;

    public PlanetTerrainRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _waterShader = new Shader(gl, WaterVertexSource, WaterFragmentSource);

        // Leave a core for the render/main thread; the rest bake patches. At least one worker always.
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        _workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
            _workers[i] = Task.Run(WorkerLoop);
    }

    /// <summary>Background bake loop: build a patch's vertex arrays off the render thread. Jobs whose
    /// epoch no longer matches (the terrain target changed) are dropped — the result is also re-checked
    /// on the render thread before any GPU upload, so a stale bake can never reach a swapped-out tree.</summary>
    private void WorkerLoop()
    {
        foreach (BuildJob job in _jobQueue.GetConsumingEnumerable())
        {
            if (job.Epoch != Volatile.Read(ref _epoch)) continue;
            (float[] land, float[] water) = BuildPatchVertices(job.Node, job.Terrain);
            _ready.Enqueue(new BuildResult(job.Epoch, job.Node, land, water));
        }
    }

    /// <summary>Queue a node to be baked on a worker. Called once per node (guarded by GenPending).</summary>
    private void RequestGenerate(QuadNode node)
    {
        node.GenPending = true;
        _jobQueue.Add(new BuildJob(_epoch, node, _terrain!));
    }

    /// <summary>Upload finished bakes to the GPU (render thread only). Stale results — for a swapped
    /// terrain (epoch) or a node merged away while baking (Disposed) — are dropped rather than uploaded,
    /// so no orphaned GPU buffer leaks. Bounded per frame so a backlog can't stall one.</summary>
    private void DrainReadyPatches()
    {
        int uploads = 0;
        while (uploads < UploadsPerFrame && _ready.TryDequeue(out BuildResult r))
        {
            if (r.Epoch != _epoch || r.Node.Disposed || !r.Node.GenPending) continue;
            r.Node.Patch = new TerrainPatch(_gl, r.Land, r.Water);
            r.Node.GenPending = false;
            PatchCount++;
            uploads++;
        }
    }

    /// <summary>Switch the terrain target (null = none). No-op if it's already this body.</summary>
    public void SetBody(CelestialBody? body)
    {
        if (ReferenceEquals(body, _body)) return;
        DisposeTree();
        _body = body;
        if (body == null) { _terrain = null; _roots = null; return; }

        _terrain = new PlanetTerrain(body);
        _roots = new QuadNode[6];
        for (int f = 0; f < 6; f++)
        {
            _roots[f] = MakeNode(f, 0, 0, 0, 1, 1);
            _roots[f].Patch = GenerateSync(_roots[f]); // roots eager so render never has a hole
        }
    }

    /// <summary>
    /// Regenerate all terrain meshes for the active planet. Call after the global
    /// <see cref="Game.Universe.TerrainTuning"/> knobs change so the new relief takes effect
    /// (deeper LOD repopulates over the next frames via the per-frame generation budget).
    /// </summary>
    public void Rebuild()
    {
        if (_body == null || _terrain == null) return;
        DisposeTree();
        _roots = new QuadNode[6];
        for (int f = 0; f < 6; f++)
        {
            _roots[f] = MakeNode(f, 0, 0, 0, 1, 1);
            _roots[f].Patch = GenerateSync(_roots[f]);
        }
    }

    public void Render(Camera camera, Vector3D<float> sunDir, float time = 0f, PlanetSurfaceMap? map = null)
    {
        if (_body == null || _roots == null) return;

        Matrix4X4<float> proj = FitProjection(camera);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;
        ExtractFrustum(viewProj);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _shader.Use();
        _shader.SetVector3("uSunDir", sunDir);
        _detailNoiseFreq = DetailBaseFreq * Math.Max(0.01f, TerrainTuning.DetailNormalScale);
        _shader.SetFloat("uDetailFreq", (float)_detailNoiseFreq);
        _shader.SetFloat("uDetailStrength", Math.Max(0f, TerrainTuning.DetailNormalStrength));
        _shader.SetFloat("uDetailAlbedo", TerrainTuning.DetailAlbedo);
        // Larger "range" → smaller low-cut → coarser octaves stay on → detail reaches further out.
        _shader.SetFloat("uDetailLowCut", 0.16f / Math.Clamp(TerrainTuning.SurfaceDetailRange, 1f, 16f));
        _shader.SetFloat("uMaterialStrength", Math.Max(0f, TerrainTuning.MaterialDetail));
        _shader.SetFloat("uSurfaceSpecular", Math.Max(0f, TerrainTuning.SurfaceSpecular));
        _shader.SetFloat("uSurfaceAmbient", Math.Max(0f, TerrainTuning.SurfaceAmbient));

        // Orbital macro relief (live; no rebuild). Base frequency tracks the planet's mountain scale.
        double reliefFreq = _terrain!.MacroReliefFrequency * Math.Max(0.05f, TerrainTuning.OrbitalReliefScale);
        _shader.SetFloat("uReliefBaseFreq", (float)reliefFreq);
        _shader.SetFloat("uReliefAmp", (float)_terrain.Amplitude);
        _shader.SetFloat("uPlanetRadius", (float)_terrain.Radius);
        _shader.SetFloat("uOrbitalStrength", Math.Max(0f, TerrainTuning.OrbitalReliefStrength));
        _shader.SetFloat("uOrbitalAlbedo", TerrainTuning.OrbitalReliefAlbedo);
        // Orbital craters: only on airless cratered worlds, scaled by the same live crater knobs.
        bool cratered = _terrain.IsCratered && TerrainTuning.CraterScale > 0f;
        _shader.SetFloat("uCraterStrength", cratered ? Math.Max(0f, TerrainTuning.OrbitalReliefStrength) : 0f);
        _shader.SetFloat("uCraterFreq", (float)_terrain.CraterOrbitalFrequency);

        // Baked surface map for this body (if ready): coarse far patches sample it instead of the
        // procedural orbital relief, so the orbital view matches the surface exactly.
        bool useMap = map is { Ready: true } && _body != null && map.BodyId == _body.Seed;
        _shader.SetFloat("uHasMap", useMap ? 1f : 0f);
        if (useMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture0); _gl.BindTexture(TextureTarget.Texture2D, map!.AlbedoTex);
            _gl.ActiveTexture(TextureUnit.Texture1); _gl.BindTexture(TextureTarget.Texture2D, map!.NormalTex);
            _shader.SetInt("uAlbedoMap", 0);
            _shader.SetInt("uNormalMap", 1);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        DrainReadyPatches(); // upload any patches baked since last frame before we traverse
        LeafCount = 0;
        _waterFrame.Clear();
        foreach (QuadNode root in _roots)
            Process(root, camera, viewProj);

        // Ocean pass over the opaque sea floor. Depth-tested AND depth-writing: the flat sea-level
        // surface must form the horizon silhouette (a smooth limb) and occlude the rugged sea floor
        // behind/below it — otherwise the bumpy floor shows through and makes the "sea" look like
        // hills. It still alpha-blends over the floor it covers (deep water nearly opaque, shallows
        // clear), and the depth it writes is the true surface, so the atmosphere hazes the sea top.
        if (_waterFrame.Count > 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(true);
            _waterShader.Use();
            _waterShader.SetVector3("uSunDir", sunDir);

            // Per-frame wave parameters (shared with the per-patch phase base computed below).
            double twoPi = 2.0 * Math.PI;
            var wk = new Vector3D<float>((float)(twoPi / WaveLen[0]), (float)(twoPi / WaveLen[1]), (float)(twoPi / WaveLen[2]));
            _waterShader.SetFloat("uTime", time);
            _waterShader.SetFloat("uWaveAmp", AnimateWater ? 1.0f : 0.0f);
            for (int i = 0; i < 3; i++)
                _waterShader.SetVector3($"uWD[{i}]", new Vector3D<float>((float)WaveDir[i].X, (float)WaveDir[i].Y, (float)WaveDir[i].Z));
            _waterShader.SetVector3("uWK", wk);
            _waterShader.SetVector3("uWW", new Vector3D<float>((float)(twoPi / WavePeriod[0]), (float)(twoPi / WavePeriod[1]), (float)(twoPi / WavePeriod[2])));
            _waterShader.SetVector3("uWA", WaveAmp);

            foreach ((TerrainPatch patch, Vector3D<float> rel, Vector3D<double> centerLocal) in _waterFrame)
            {
                // Phase base = wave·patchCentre (mod 2π) in double, so phase stays precise and seamless.
                var phase = new Vector3D<float>(
                    (float)PhaseBase(0, centerLocal), (float)PhaseBase(1, centerLocal), (float)PhaseBase(2, centerLocal));
                Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
                _waterShader.SetMatrix("uModel", model);
                _waterShader.SetMatrix("uMVP", model * viewProj);
                _waterShader.SetVector3("uPhase", phase);
                patch.DrawWater(_gl);
            }
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.Disable(EnableCap.DepthTest);
    }

    private void Process(QuadNode node, Camera camera, in Matrix4X4<float> viewProj)
    {
        UniversePosition center = _body!.CurrentPosition.Translated(node.CenterLocal);

        // Visibility cull: a node outside the view frustum or beyond the planet's horizon can't be
        // seen. We skip drawing it and skip refining its subtree (so the per-frame budget goes to
        // detail the camera can actually see — the bulk of the work when hugging the surface), but
        // still let the distance-based merge below collapse it, so off-screen detail doesn't leak.
        Vector3D<float> rel = center.ToCameraRelative(camera.Position);
        float boundR = (float)(node.WorldSize * 0.75 + _terrain!.Amplitude + 50.0);
        bool visible = IsNodeVisible(node, rel, boundR, camera);

        double dist = camera.Position.DistanceTo(center);
        if (FocusPoint is { } fp) dist = Math.Min(dist, fp.DistanceTo(center));

        double splitDist = LodFactor * node.WorldSize;
        bool wantSplit = visible && node.Level < MaxLevel && dist < splitDist;

        if (wantSplit)
        {
            node.Children ??= Split(node);

            // Request a background bake for any child not yet baked (once — GenPending guards repeats).
            // We only descend once all four are ready; until then this node draws as the coarse stand-in.
            bool allReady = true;
            foreach (QuadNode c in node.Children)
            {
                if (c.Patch == null)
                {
                    allReady = false;
                    if (!c.GenPending) RequestGenerate(c);
                }
            }

            if (allReady)
            {
                foreach (QuadNode c in node.Children)
                    Process(c, camera, viewProj);
                return;
            }
            // Children still baking — draw this node, refine once they upload.
        }
        else if (node.Children != null && dist > splitDist * MergeHysteresis)
        {
            DisposeChildren(node); // merged back up (with hysteresis so we don't thrash at the line)
        }

        if (!visible) return; // off-screen: kept cached (instant when it swings back) but not drawn

        // Geomorph factor: 0 just after this node stops subdividing (full detail), ramping to 1 as
        // it nears its parent's split distance (2× its own), where it equals the parent's surface —
        // so the eventual merge is seamless.
        float morph = (float)Smoothstep(splitDist, 2.0 * splitDist, dist);
        RenderPatch(node, rel, in viewProj, morph);
        LeafCount++;
    }

    private void RenderPatch(QuadNode node, Vector3D<float> rel, in Matrix4X4<float> viewProj, float morph)
    {
        if (node.Patch == null) return;
        Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
        _shader.SetMatrix("uMVP", model * viewProj);
        _shader.SetMatrix("uModel", model);
        _shader.SetFloat("uMorph", morph);
        SetPatchDetailBase(node);
        node.Patch.Draw(_gl);
        if (node.Patch.HasWater) _waterFrame.Add((node.Patch, rel, node.CenterLocal)); // drawn in the later water pass
    }

    /// <summary>Per-patch swell phase base for wave <paramref name="i"/>: (2π/λ)·(direction·patchCentre),
    /// reduced mod 2π in double so the shader can add the small patch-relative offset without losing
    /// precision or seaming between patches.</summary>
    private static double PhaseBase(int i, Vector3D<double> centerLocal)
    {
        double k = 2.0 * Math.PI / WaveLen[i];
        double p = k * Vector3D.Dot(WaveDir[i], centerLocal);
        p %= 2.0 * Math.PI;
        return p < 0 ? p + 2.0 * Math.PI : p;
    }

    /// <summary>
    /// Per-patch detail-noise base: the patch centre's planet-local cell (integer part, wrapped to a
    /// large period so the shader hash stays precise) and fraction. The fragment shader reconstructs a
    /// seamless, swim-free noise coordinate from this plus the (small, precise) patch-relative vertex
    /// position — so detail neither slides as you move nor seams at patch boundaries.
    /// </summary>
    private void SetPatchDetailBase(QuadNode node)
    {
        double freq = _detailNoiseFreq;
        Vector3D<double> c = node.CenterLocal;
        double fx = Math.Floor(c.X * freq), fy = Math.Floor(c.Y * freq), fz = Math.Floor(c.Z * freq);
        _shader.SetVector3("uPatchCellBase", new Vector3D<float>(WrapCell(fx), WrapCell(fy), WrapCell(fz)));
        _shader.SetVector3("uPatchFracBase", new Vector3D<float>(
            (float)(c.X * freq - fx), (float)(c.Y * freq - fy), (float)(c.Z * freq - fz)));

        // Orbital macro relief needs the patch centre (to rebuild the surface direction) and this
        // patch's vertex spacing in direction units (so the shader knows what the mesh already shows).
        _shader.SetVector3("uPatchCenter", new Vector3D<float>((float)c.X, (float)c.Y, (float)c.Z));
        _shader.SetFloat("uVertexSpacingDir", (float)(node.WorldSize / GridN / _terrain!.Radius));
    }

    /// <summary>Wrap an integer cell index into [0, 4096) (matching the shader's hash period).</summary>
    private static float WrapCell(double flooredCell)
    {
        double m = flooredCell % 4096.0;
        if (m < 0) m += 4096.0;
        return (float)m;
    }

    private static double Smoothstep(double lo, double hi, double x)
    {
        double t = Math.Clamp((x - lo) / (hi - lo), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    // --- visibility culling ---

    private readonly Vector4D<float>[] _frustum = new Vector4D<float>[6];

    /// <summary>Gribb–Hartmann frustum planes from the (camera-relative) view-projection. Column j
    /// of the row-vector matrix yields clip coordinate j, so each plane is columns 4±j. Stored
    /// normalized so the plane equation gives a true signed distance for the sphere test.</summary>
    private void ExtractFrustum(in Matrix4X4<float> m)
    {
        _frustum[0] = NormPlane(m.M11 + m.M14, m.M21 + m.M24, m.M31 + m.M34, m.M41 + m.M44); // left
        _frustum[1] = NormPlane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
        _frustum[2] = NormPlane(m.M12 + m.M14, m.M22 + m.M24, m.M32 + m.M34, m.M42 + m.M44); // bottom
        _frustum[3] = NormPlane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top
        _frustum[4] = NormPlane(m.M13 + m.M14, m.M23 + m.M24, m.M33 + m.M34, m.M43 + m.M44); // near
        _frustum[5] = NormPlane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43); // far
    }

    private static Vector4D<float> NormPlane(float a, float b, float c, float d)
    {
        float inv = 1f / MathF.Sqrt(a * a + b * b + c * c);
        return new Vector4D<float>(a * inv, b * inv, c * inv, d * inv);
    }

    /// <summary>True unless the node's bounding sphere is wholly outside the frustum, or its whole
    /// extent lies beyond the planet's curved horizon.</summary>
    private bool IsNodeVisible(QuadNode node, Vector3D<float> rel, float boundR, Camera camera)
    {
        foreach (Vector4D<float> p in _frustum)
            if (p.X * rel.X + p.Y * rel.Y + p.Z * rel.Z + p.W < -boundR)
                return false;

        // Horizon cull: a point at angle a from the sub-camera point is hidden once a exceeds the
        // horizon angle. Pad by the patch's angular half-size and a height allowance so mountains
        // poking over the limb (and partially-visible patches) are never wrongly culled.
        double R = _terrain!.Radius;
        Vector3D<double> camVec = camera.Position.DeltaMeters(_body!.CurrentPosition); // centre → camera
        double Dc = camVec.Length;
        if (Dc <= R) return true; // at/under the surface — nothing is over the horizon
        double cosA = Vector3D.Dot(Vector3D.Normalize(node.CenterLocal), Vector3D.Normalize(camVec));
        double a = Math.Acos(Math.Clamp(cosA, -1.0, 1.0));
        double aHorizon = Math.Acos(Math.Clamp(R / Dc, -1.0, 1.0));
        double patchAng = node.WorldSize * 0.75 / R;
        double heightAng = Math.Sqrt(2.0 * Math.Max(0.0, _terrain.Amplitude) / R);
        return a <= aHorizon + patchAng + heightAng + 0.01;
    }

    // --- quadtree ---

    private QuadNode MakeNode(int face, int level, double u0, double v0, double u1, double v1)
    {
        Vector3D<double> centerDir = FacePoint(face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
        return new QuadNode
        {
            Face = face,
            Level = level,
            U0 = u0, V0 = v0, U1 = u1, V1 = v1,
            CenterLocal = centerDir * _terrain!.Radius,
            WorldSize = _terrain.Radius * 1.5707963 * (u1 - u0),
        };
    }

    private QuadNode[] Split(QuadNode n)
    {
        double um = (n.U0 + n.U1) * 0.5, vm = (n.V0 + n.V1) * 0.5;
        return new[]
        {
            MakeNode(n.Face, n.Level + 1, n.U0, n.V0, um, vm),
            MakeNode(n.Face, n.Level + 1, um, n.V0, n.U1, vm),
            MakeNode(n.Face, n.Level + 1, n.U0, vm, um, n.V1),
            MakeNode(n.Face, n.Level + 1, um, vm, n.U1, n.V1),
        };
    }

    // --- patch mesh generation (CPU) ---

    /// <summary>Bake and upload a patch on the calling (render) thread. Used only for the 6 roots,
    /// which must exist before the first frame; all other patches bake on the worker pool.</summary>
    private TerrainPatch GenerateSync(QuadNode node)
    {
        (float[] land, float[] water) = BuildPatchVertices(node, _terrain!);
        PatchCount++;
        return new TerrainPatch(_gl, land, water);
    }

    // Static + terrain-by-parameter: this runs on worker threads, so it must touch no mutable renderer
    // state. PlanetTerrain.HeightAt and Noise are immutable pure reads, safe to call from many threads.
    private static (float[] land, float[] water) BuildPatchVertices(QuadNode node, PlanetTerrain terrain)
    {
        int n = GridN;
        double radius = terrain.Radius;
        Vector3D<double> centerLocal = node.CenterLocal;

        // Two band-limits per patch for geomorphing: the patch's own fine spacing, and the COARSE
        // spacing its parent uses (2× — one level up). Each vertex therefore carries both the fine
        // surface and the surface the parent would draw, and the shader blends between them.
        double spacing = node.WorldSize / n;
        double coarseSpacing = spacing * 2.0;

        // Sample positions on an EXTENDED grid with a one-vertex guard ring beyond every edge
        // (grid indices gi/gj run -1..n+1, stored at [gi+1, gj+1]). Edge-vertex normals are then
        // computed from a centred difference using the guard ring, identical to what the
        // neighbouring patch computes for the same shared vertex — so there is no lighting seam
        // between patches. The height field is sampled in shared sphere space, so a guard-ring
        // sample exactly reproduces the adjacent patch's edge row.
        var exF = new Vector3D<double>[n + 3, n + 3];
        var exC = new Vector3D<double>[n + 3, n + 3];
        var dir = new Vector3D<double>[n + 1, n + 1];
        var posF = new Vector3D<double>[n + 1, n + 1];
        var posC = new Vector3D<double>[n + 1, n + 1];
        var hgt = new double[n + 1, n + 1]; // fine height drives colour + water
        for (int gj = -1; gj <= n + 1; gj++)
        for (int gi = -1; gi <= n + 1; gi++)
        {
            double u = node.U0 + (node.U1 - node.U0) * (gi / (double)n);
            double v = node.V0 + (node.V1 - node.V0) * (gj / (double)n);
            Vector3D<double> d = FacePoint(node.Face, u, v);
            double hF = terrain.HeightAt(d, spacing);
            Vector3D<double> pF = d * (radius + hF);
            Vector3D<double> pC = d * (radius + terrain.HeightAt(d, coarseSpacing));
            exF[gi + 1, gj + 1] = pF;
            exC[gi + 1, gj + 1] = pC;
            if (gi >= 0 && gi <= n && gj >= 0 && gj <= n)
            {
                dir[gi, gj] = d;
                posF[gi, gj] = pF;
                posC[gi, gj] = pC;
                hgt[gi, gj] = hF;
            }
        }

        var nrmF = new Vector3D<double>[n + 1, n + 1];
        var nrmC = new Vector3D<double>[n + 1, n + 1];
        var col = new Vector3D<float>[n + 1, n + 1];
        var rel = new float[n + 1, n + 1]; // macro-relief mask (low-freq → safe to interpolate)
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
        {
            Vector3D<double> outward = Vector3D.Normalize(posF[i, j]);
            nrmF[i, j] = GridNormal(exF, i, j, outward);
            nrmC[i, j] = GridNormal(exC, i, j, outward);

            // Slope = cos(angle from vertical): 1 on flats, → 0 on cliffs. Drives rock vs snow.
            double slope = Vector3D.Dot(nrmF[i, j], outward);
            col[i, j] = terrain.ColorAt(dir[i, j], hgt[i, j], slope, spacing);
            rel[i, j] = (float)terrain.MacroReliefMask(dir[i, j]);
        }

        var v3 = new List<float>((n * n * 6 + n * 16) * LandFloatsPerVertex);
        void Emit(Vector3D<double> pF, Vector3D<double> pC, Vector3D<double> nF, Vector3D<double> nC, Vector3D<float> c, float r)
        {
            v3.Add((float)(pF.X - centerLocal.X)); v3.Add((float)(pF.Y - centerLocal.Y)); v3.Add((float)(pF.Z - centerLocal.Z));
            v3.Add((float)(pC.X - centerLocal.X)); v3.Add((float)(pC.Y - centerLocal.Y)); v3.Add((float)(pC.Z - centerLocal.Z));
            v3.Add((float)nF.X); v3.Add((float)nF.Y); v3.Add((float)nF.Z);
            v3.Add((float)nC.X); v3.Add((float)nC.Y); v3.Add((float)nC.Z);
            v3.Add(c.X); v3.Add(c.Y); v3.Add(c.Z);
            v3.Add(r);
        }
        void EmitVert(int i, int j) => Emit(posF[i, j], posC[i, j], nrmF[i, j], nrmC[i, j], col[i, j], rel[i, j]);

        // Surface triangles.
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            EmitVert(i, j); EmitVert(i + 1, j); EmitVert(i + 1, j + 1);
            EmitVert(i, j); EmitVert(i + 1, j + 1); EmitVert(i, j + 1);
        }

        // Skirts hide the T-junction cracks that remain between patches of different LOD. The skirt
        // top tracks the morphing surface (both fine and coarse), so it never lifts off the ground.
        double skirt = node.WorldSize * 0.06 + 60.0;
        void SkirtQuad(int i0, int j0, int i1, int j1)
        {
            Vector3D<double> t0F = posF[i0, j0], t0C = posC[i0, j0];
            Vector3D<double> t1F = posF[i1, j1], t1C = posC[i1, j1];
            Vector3D<double> b0F = t0F - Vector3D.Normalize(t0F) * skirt, b0C = t0C - Vector3D.Normalize(t0C) * skirt;
            Vector3D<double> b1F = t1F - Vector3D.Normalize(t1F) * skirt, b1C = t1C - Vector3D.Normalize(t1C) * skirt;
            Vector3D<double> n0F = nrmF[i0, j0], n0C = nrmC[i0, j0], n1F = nrmF[i1, j1], n1C = nrmC[i1, j1];
            Vector3D<float> c0 = col[i0, j0], c1 = col[i1, j1];
            float r0 = rel[i0, j0], r1 = rel[i1, j1];
            Emit(t0F, t0C, n0F, n0C, c0, r0); Emit(t1F, t1C, n1F, n1C, c1, r1); Emit(b1F, b1C, n1F, n1C, c1, r1);
            Emit(t0F, t0C, n0F, n0C, c0, r0); Emit(b1F, b1C, n1F, n1C, c1, r1); Emit(b0F, b0C, n0F, n0C, c0, r0);
        }
        for (int j = 0; j < n; j++) { SkirtQuad(0, j, 0, j + 1); SkirtQuad(n, j + 1, n, j); }
        for (int i = 0; i < n; i++) { SkirtQuad(i + 1, 0, i, 0); SkirtQuad(i, n, i + 1, n); }

        return (v3.ToArray(), BuildWaterVertices(node, terrain, dir, hgt, centerLocal, radius));
    }

    /// <summary>Outward-oriented vertex normal from a centred difference on the extended position
    /// grid. Surface vertex (i, j) lives at extended index (i + 1, j + 1); the guard ring lets the
    /// difference stay centred at the patch edges, so shared-edge normals match the neighbour.</summary>
    private static Vector3D<double> GridNormal(Vector3D<double>[,] ex, int i, int j, Vector3D<double> outward)
    {
        Vector3D<double> du = ex[i + 2, j + 1] - ex[i, j + 1];
        Vector3D<double> dv = ex[i + 1, j + 2] - ex[i + 1, j];
        Vector3D<double> nd = Vector3D.Cross(du, dv);
        if (Vector3D.Dot(nd, outward) < 0) nd = -nd;
        return nd.LengthSquared > 0 ? Vector3D.Normalize(nd) : outward;
    }

    /// <summary>
    /// Flat sea-level quads for the cells of this patch that dip below the waterline. Land
    /// triangles (drawn opaque first) occlude the water wherever they rise above it, so the
    /// coastline is simply where the rugged terrain pierces this flat surface. Returns an empty
    /// array for dry worlds / fully-dry patches.
    /// </summary>
    private static float[] BuildWaterVertices(QuadNode node, PlanetTerrain terrain, Vector3D<double>[,] dir, double[,] hgt,
        Vector3D<double> centerLocal, double radius)
    {
        if (terrain is not { HasOcean: true }) return Array.Empty<float>();

        int n = GridN;
        double seaLevel = terrain.SeaLevelMeters;
        double amp = terrain.Amplitude;
        // Sit the water a touch below sea level so the shoreline doesn't z-fight the land.
        double waterR = radius + seaLevel - (node.WorldSize * 0.0004 + 0.3);
        var shallow = new Vector3D<float>(0.20f, 0.55f, 0.62f);
        var deep = new Vector3D<float>(0.02f, 0.10f, 0.26f);

        var w = new List<float>();
        void EmitW(int i, int j)
        {
            Vector3D<double> p = dir[i, j] * waterR;          // flat ocean surface
            Vector3D<double> nrm = dir[i, j];                 // radial (outward) normal
            float f = (float)Math.Clamp((seaLevel - hgt[i, j]) / (amp * 0.12 + 1.0), 0f, 1f);
            Vector3D<float> c = shallow + (deep - shallow) * f; // shallows pale, deeps dark blue
            float alpha = 0.5f + 0.48f * f;                     // shallows see-through, deeps ~opaque
            w.Add((float)(p.X - centerLocal.X)); w.Add((float)(p.Y - centerLocal.Y)); w.Add((float)(p.Z - centerLocal.Z));
            w.Add((float)nrm.X); w.Add((float)nrm.Y); w.Add((float)nrm.Z);
            w.Add(c.X); w.Add(c.Y); w.Add(c.Z); w.Add(alpha);
        }

        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            double cellMin = Math.Min(Math.Min(hgt[i, j], hgt[i + 1, j]),
                                      Math.Min(hgt[i + 1, j + 1], hgt[i, j + 1]));
            if (cellMin >= seaLevel) continue; // cell entirely above water — no ocean here
            EmitW(i, j); EmitW(i + 1, j); EmitW(i + 1, j + 1);
            EmitW(i, j); EmitW(i + 1, j + 1); EmitW(i, j + 1);
        }

        // NOTE: no water skirts. The ocean pass is translucent and writes no depth, so a downward
        // skirt at every patch edge blends as pure overdraw — and because same-level neighbours
        // share their edge vertices (the flat surface is already watertight there), those skirts
        // only darkened the seams, painting a visible grid over the whole sea. Same-level edges
        // need no skirt; at the rarer LOD-level seams the opaque sea floor / land skirts drawn
        // behind (with depth) fill any arc-vs-chord gap, so the worst case is a faint line at a
        // level change rather than a grid on every edge.
        return w.ToArray();
    }

    /// <summary>Cube face (u,v)∈[0,1] → point on the unit sphere.</summary>
    private static Vector3D<double> FacePoint(int face, double u, double v)
    {
        double a = u * 2 - 1, b = v * 2 - 1;
        Vector3D<double> p = face switch
        {
            0 => new Vector3D<double>(1, b, -a),
            1 => new Vector3D<double>(-1, b, a),
            2 => new Vector3D<double>(a, 1, -b),
            3 => new Vector3D<double>(a, -1, b),
            4 => new Vector3D<double>(a, b, 1),
            _ => new Vector3D<double>(-a, b, -1),
        };
        return Vector3D.Normalize(p);
    }

    /// <summary>Near/far planes from the most recent <see cref="FitProjection"/> (for depth reconstruction).</summary>
    public float LastNear { get; private set; }
    public float LastFar { get; private set; }

    /// <summary>Near/far fit to the camera's altitude and horizon for usable depth precision.</summary>
    private Matrix4X4<float> FitProjection(Camera camera)
    {
        double camToCenter = camera.Position.DistanceTo(_body!.CurrentPosition);

        // Altitude above the LOCAL terrain under the camera, not the base radius — otherwise
        // standing on high ground (a mountain / raised continent) inflates the altitude by that
        // elevation and pushes the near plane far out, clipping the rover and nearby surface.
        Vector3D<double> camDir = camera.Position.DeltaMeters(_body.CurrentPosition);
        double surfaceR = _terrain!.Radius;
        if (camDir.LengthSquared > 0)
            surfaceR += _terrain.HeightAt(Vector3D.Normalize(camDir));
        double alt = Math.Max(1.0, camToCenter - surfaceR);

        double horizon = Math.Sqrt(alt * alt + 2 * _terrain.Radius * alt);
        double far = horizon * 1.25 + 5000.0;
        double near = Math.Clamp(alt * 0.25, 0.2, far * 0.5);
        near = Math.Max(near, far / 5.0e5);
        LastNear = (float)near; LastFar = (float)far;
        return MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, (float)near, (float)far);
    }

    // --- cleanup ---

    private void DisposeChildren(QuadNode node)
    {
        if (node.Children == null) return;
        foreach (QuadNode c in node.Children) DisposeNode(c);
        node.Children = null;
    }

    private void DisposeNode(QuadNode node)
    {
        // Mark first so a bake still in flight for this node is dropped (not uploaded) when it lands.
        node.Disposed = true;
        if (node.Children != null) { foreach (QuadNode c in node.Children) DisposeNode(c); node.Children = null; }
        if (node.Patch != null) { node.Patch.Dispose(_gl); node.Patch = null; PatchCount--; }
    }

    private void DisposeTree()
    {
        // New epoch: in-flight bakes for the outgoing tree are now stale and will be discarded.
        Interlocked.Increment(ref _epoch);
        if (_roots == null) return;
        foreach (QuadNode r in _roots) DisposeNode(r);
        _roots = null;
        PatchCount = 0;
    }

    public void Dispose()
    {
        _jobQueue.CompleteAdding();
        try { Task.WaitAll(_workers); } catch { /* a faulted worker must not block teardown */ }
        _jobQueue.Dispose();
        DisposeTree();
        _shader.Dispose();
        _waterShader.Dispose();
    }

    private sealed class QuadNode
    {
        public int Face, Level;
        public double U0, V0, U1, V1;
        public Vector3D<double> CenterLocal;
        public double WorldSize;
        public TerrainPatch? Patch;
        public QuadNode[]? Children;
        public bool GenPending; // a background bake for this node is queued or running
        public bool Disposed;   // node removed from the tree — discard any bake that lands afterwards
    }

    // Geometry-only inputs handed to a worker (immutable once created), and the float arrays it returns.
    private readonly record struct BuildJob(int Epoch, QuadNode Node, PlanetTerrain Terrain);
    private readonly record struct BuildResult(int Epoch, QuadNode Node, float[] Land, float[] Water);

    private sealed class TerrainPatch
    {
        private readonly uint _vao, _vbo, _count;
        private readonly uint _waterVao, _waterVbo, _waterCount;

        public bool HasWater => _waterCount > 0;

        // Land: finePos, coarsePos, fineNrm, coarseNrm, colour (5 × vec3). Water: pos, nrm, colour+alpha.
        private static readonly int[] LandLayout = { 3, 3, 3, 3, 3, 1 };
        private static readonly int[] WaterLayout = { 3, 3, 4 };

        public TerrainPatch(GL gl, float[] land, float[] water)
        {
            (_vao, _vbo, _count) = MakeBuffer(gl, land, LandLayout);
            if (water.Length > 0)
                (_waterVao, _waterVbo, _waterCount) = MakeBuffer(gl, water, WaterLayout);
        }

        private static unsafe (uint vao, uint vbo, uint count) MakeBuffer(GL gl, float[] data, int[] layout)
        {
            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            uint vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);

            int floatsPerVert = 0;
            foreach (int s in layout) floatsPerVert += s;
            uint stride = (uint)(floatsPerVert * sizeof(float));
            int offset = 0;
            for (uint a = 0; a < layout.Length; a++)
            {
                gl.VertexAttribPointer(a, layout[a], VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
                gl.EnableVertexAttribArray(a);
                offset += layout[a];
            }
            gl.BindVertexArray(0);
            return (vao, vbo, (uint)(data.Length / floatsPerVert));
        }

        public void Draw(GL gl)
        {
            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, _count);
            gl.BindVertexArray(0);
        }

        public void DrawWater(GL gl)
        {
            if (_waterCount == 0) return;
            gl.BindVertexArray(_waterVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, _waterCount);
            gl.BindVertexArray(0);
        }

        public void Dispose(GL gl)
        {
            gl.DeleteBuffer(_vbo);
            gl.DeleteVertexArray(_vao);
            if (_waterCount > 0)
            {
                gl.DeleteBuffer(_waterVbo);
                gl.DeleteVertexArray(_waterVao);
            }
        }
    }
}
