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
    // Split when distance < LodFactor × patch size. Live-tunable (TerrainTuning.LodDistanceFactor) so
    // the player can trade geometry detail on approach against patch/bake cost; snapshot per frame in
    // Render so the whole traversal uses one consistent value.
    private const double MergeHysteresis = 1.3; // keep children cached until 1.3× the split distance (anti-thrash)
    // The expensive part of a patch — sampling the height field and building its vertex arrays — runs
    // on a background worker pool (see WorkerLoop). Only the cheap GPU upload happens on the render
    // thread, capped at this many finished patches per frame so a burst of bakes can't stall a frame.
    // A single upload is just a VAO/VBO create + one BufferData of ~30 KB, so a few dozen per frame is
    // cheap; the cap mainly bounds the worst case. Kept generous so a fresh descent's bake backlog
    // (hundreds of patches) drains in a handful of frames rather than dribbling in at 8/frame.
    private const int UploadsPerFrame = 32;
    // Refinement otherwise advances only ONE level per request→bake→upload round-trip: Process won't
    // queue a node's grandchildren until its children have uploaded, so a deep descent serialises into
    // dozens of frame-gated round-trips and leaves most worker cores idle early on. Each frame we also
    // pre-queue the patches along the path toward the focus this many levels past the drawable frontier
    // — bakes are parent-independent, so queuing them early keeps the whole pool busy and detail lands
    // in far fewer frames. Bounded (one path per level) so we never queue an exponential subtree.
    private const int SpeculativeDepth = 6;
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
uniform float uDetailFreq;     // detail-noise cells per metre (mirrors the vertex-shader uniform)
uniform float uGeomDetailFloor; // metres: finest spacing the baked geometry resolves (its octave budget floor)
uniform float uDetailLowCut;   // mesh-handoff threshold: octaves coarser than ~this×(effective spacing) fade (geometry carries them)
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
        // Anisotropy cap: at a grazing angle (e.g. the ground under the low chase camera while driving)
        // one axis's derivative explodes while the across-view one stays small; taking the plain max
        // there culls all detail, so the ground behind the rover goes smooth. Floor the footprint at
        // the smallest-axis value instead, capping anisotropy at ~5:1, so grazing ground stays textured
        // while a face-on view (min ≈ max) is unchanged.
        float fpMax = max(max(fwidth(vNoiseCoord.x), fwidth(vNoiseCoord.y)), fwidth(vNoiseCoord.z));
        float fpMin = min(min(fwidth(vNoiseCoord.x), fwidth(vNoiseCoord.y)), fwidth(vNoiseCoord.z));
        float footprint = max(fpMin, fpMax * 0.2);
        // Band-pass octave stack with TWO independent gates so whatever scale a pixel can resolve at
        // this distance is covered — fine grain up close, coarser texture far away — without the
        // ground ever collapsing to smooth plastic:
        //   (1) fine end — drop octaves smaller than ~a pixel (would shimmer); keyed to the pixel
        //       footprint, which is correct because aliasing IS a per-pixel phenomenon.
        //   (2) coarse end — hand an octave back to the real mesh once its wavelength is coarser than
        //       what the geometry actually resolves (geomorphed geometry already carries those scales).
        //       Keyed to the MESH resolution, NOT the camera distance — keying it to cells-per-pixel
        //       made the whole stack slide below the cut as you approached, flattening near ground.
        //       The resolution is the COARSER of the patch's vertex spacing and the height field's
        //       octave-budget floor (uGeomDetailFloor): a deeply-subdivided near patch has cm vertex
        //       spacing but the baked detail saturates metres above that, so without the floor the gate
        //       would assume the mesh carries cm-scale relief and cull all procedural detail — the
        //       near-ground blur. Clamping to the floor keeps procedural detail filling that gap.
        float effSpacing = max(uVertexSpacingDir * uPlanetRadius, uGeomDetailFloor); // metres the mesh truly resolves
        float meshCellsPerVertex = effSpacing * uDetailFreq;     // base-octave cells per resolved step
        vec3 tangAccum = vec3(0.0);
        float nDetail = 0.0, wsum = 0.0, m = 1.0;
        for (int o = 0; o < 8; o++) {   // 8 octaves: ~64 m (×1) down to ~0.5 m (×128)
            float aa = 1.0 - smoothstep(0.45, 1.3, footprint * m);   // (1) anti-alias (drop sub-pixel octaves)
            float mc = meshCellsPerVertex * m;                       // (2) this octave's cells per mesh vertex
            float oct = aa * smoothstep(uDetailLowCut, uDetailLowCut * 3.0, mc); // fade coarse scales the mesh carries
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

    // --- experimental GPU tile path (TerrainTuning.GpuTerrain) ---
    // A shared per-patch base-sphere mesh (CPU, no noise → precision-safe, center-relative) is displaced
    // in the vertex shader by a height TILE generated on the GPU (TerrainTileGenerator → TerrainTileCache).
    // Lazily created on first GPU use so the CPU path costs nothing. Phase 3 shades with a flat geometric
    // normal + a simple slope tint to validate geometry/LOD/precision; per-pixel normal/albedo tiles and
    // collision readback follow in later phases.
    private const int GpuTileGensPerFrame = 16; // budget like UploadsPerFrame, so a descent can't stall a frame
    private TerrainTileCache? _tileCache;
    private TerrainTileGenerator? _tileGen;
    private Shader? _gpuShader;
    private Shader? _gpuWaterShader;
    // Ocean leaves collected during the GPU draw pass, drawn in a later translucent water pass (like the
    // CPU _waterFrame). Each carries the node (for its tile/morph/phase) and its camera-relative offset.
    private readonly List<(QuadNode node, Vector3D<float> rel)> _gpuWaterFrame = new();
    private long _nextTileId = 1;
    private int _gpuGenBudget;

    // Near drawn leaves collected during the GPU pass so VegetationRenderer can scatter grass on them by
    // INSTANCING over each leaf's terrain vertices and sampling the SAME height tile the mesh used — exact
    // placement on the drawn surface, no CPU height-mirror divergence. Only leaves within GrassRange.
    public readonly struct GrassLeaf
    {
        public readonly uint BaseVbo;            // the leaf's base mesh (basePos/dir/texel/skirt per vertex)
        public readonly Vector3D<float> Rel;     // camera-relative patch centre (the uModel translation)
        public readonly Vector2D<float> TileOrigin;
        public readonly float Morph;
        public readonly int Face;                // cube face + rect + vertex spacing → surface-normal in shader
        public readonly Vector4D<float> Rect;    // (u0,v0,u1,v1) of this patch on the face
        public readonly float VertexSpacing;     // metres between adjacent tile texels (for the slope normal)
        public GrassLeaf(uint vbo, Vector3D<float> rel, Vector2D<float> origin, float morph,
                         int face, Vector4D<float> rect, float spacing)
        { BaseVbo = vbo; Rel = rel; TileOrigin = origin; Morph = morph; Face = face; Rect = rect; VertexSpacing = spacing; }
    }
    // Gauge "near the camera's ground point" by DIRECTION, not by distance to the patch's base-sphere centre:
    // on high-relief worlds the surface sits tens of km above the base sphere, so node.CenterLocal can be tens
    // of km from the camera even when its surface is underfoot (and that radial offset also inflates the LOD
    // distance, so patch size alone isn't a reliable proxy). Comparing the camera's nadir direction to the
    // patch's direction is relief-independent — the same idea the rover uses to find its ground leaf.
    private const int GrassMaxLeaves = 512;         // cap draw calls per frame
    private Vector3D<double> _grassCamDir;          // camera nadir (planet-local unit), set each GPU draw
    public double MinDrawnWorldSize { get; private set; } // smallest drawn leaf this frame (HUD diagnostic)
    private readonly List<GrassLeaf> _grassLeaves = new();
    public IReadOnlyList<GrassLeaf> GrassLeaves => _grassLeaves;
    public int GrassVertsPerLeaf => (GridN + 1) * (GridN + 1); // grid verts come first in the base VBO
    public int GrassVertexStrideFloats => 9;     // basePos(3)+dir(3)+texel(2)+skirt(1)
    public int GrassGridN => GridN;
    public uint HeightTexture => _tileCache?.HeightTexture ?? 0;

    // The rover's current leaf tile, read back once per leaf during render: all four wheels sample THIS
    // one buffer (clamped to its u,v rect), so the footprint has a single consistent, exact source —
    // mixing per-wheel sources or an analytic fallback either floated the body or ratcheted it upward.
    private float[]? _roverLeafBuf;
    private long _roverLeafTileId = -1;
    private int _roverLeafFace;
    private double _rlU0, _rlV0, _rlU1, _rlV1;
    private float _roverLeafMorph;

    private const string GpuVertexSource = @"#version 410 core
layout(location = 0) in vec3 aBasePos;   // R*(dir - centerDir): patch-centre-relative, small & precise
layout(location = 1) in vec3 aDir;       // outward unit direction at this vertex
layout(location = 2) in vec2 aTexel;     // guard-offset texel (grid index + 1) of this vertex in its tile
layout(location = 3) in float aSkirt;    // 1 = skirt vertex (dropped below the surface to hide LOD cracks)
uniform mat4 uMVP;
uniform mat4 uModel;        // camera-relative patch-centre translation
uniform float uMorph;       // geomorph fine→coarse blend
uniform float uSkirtDepth;
uniform float uVertexSpacing; // metres between adjacent vertices (for the height-slope normal)
uniform float uGridN;       // cells per patch edge (= GridN)
uniform vec4 uRect;         // (u0, v0, u1, v1) of this patch on the cube face
uniform int uFace;
uniform vec2 uTileOrigin;   // this patch's tile origin (texels) in the atlas
uniform float uDetailFreq;  // detail-noise cells per metre (for the fragment detail layer)
uniform vec3 uPatchFracBase;// fract(patchCentre * detailFreq) — integer part travels in uPatchCellBase
uniform sampler2D uHeight;
out vec3 vWorld;
out vec3 vDir;
out vec3 vNormal;           // smooth surface normal from the height field (planet-local)
out float vElev;            // morphed height (for albedo)
out float vCrater;          // morphed crater value (B/A of the tile) for crater-floor/rim albedo
out vec3 vNoiseCoord;       // planet-stable, precise fractional noise coordinate for the detail layer

vec3 facePoint(int f, float u, float v) {
    float a = u * 2.0 - 1.0, b = v * 2.0 - 1.0;
    vec3 p;
    if (f == 0)      p = vec3( 1.0,  b,  -a);
    else if (f == 1) p = vec3(-1.0,  b,   a);
    else if (f == 2) p = vec3(  a, 1.0,  -b);
    else if (f == 3) p = vec3(  a,-1.0,   b);
    else if (f == 4) p = vec3(  a,  b,  1.0);
    else             p = vec3( -a,  b, -1.0);
    return normalize(p);
}

float H(ivec2 t) { vec2 hfc = texelFetch(uHeight, t, 0).rg; return mix(hfc.x, hfc.y, uMorph); }

void main() {
    ivec2 o = ivec2(int(uTileOrigin.x), int(uTileOrigin.y)) + ivec2(int(aTexel.x), int(aTexel.y));
    vec4 hba  = texelFetch(uHeight, o, 0);
    float h   = mix(hba.r, hba.g, uMorph);
    vCrater   = mix(hba.b, hba.a, uMorph);                      // crater value, geomorphed like the height
    float hu1 = H(o + ivec2(1, 0)), hu0 = H(o - ivec2(1, 0));   // guard ring makes these valid at edges
    float hv1 = H(o + ivec2(0, 1)), hv0 = H(o - ivec2(0, 1));

    // Surface tangents from the cube-face mapping (unit-direction differences — float-precise), and the
    // height slope along each → a smooth per-vertex normal (interpolated to per-pixel in the fragment).
    vec2 g = aTexel - vec2(1.0);
    float u = mix(uRect.x, uRect.z, g.x / uGridN);
    float v = mix(uRect.y, uRect.w, g.y / uGridN);
    vec3 tangU = normalize(facePoint(uFace, u + 0.0005, v) - facePoint(uFace, u - 0.0005, v));
    vec3 tangV = normalize(facePoint(uFace, u, v + 0.0005) - facePoint(uFace, u, v - 0.0005));
    float slopeU = (hu1 - hu0) / (2.0 * uVertexSpacing);
    float slopeV = (hv1 - hv0) / (2.0 * uVertexSpacing);
    vNormal = normalize(aDir - tangU * slopeU - tangV * slopeV);

    float hh = h;
    if (aSkirt > 0.5) hh -= uSkirtDepth;
    vec3 posRel = aBasePos + aDir * hh;     // base sphere (small) + radial displacement (small) — float-safe
    vWorld = (uModel * vec4(posRel, 1.0)).xyz;
    vDir = aDir;
    vElev = h;
    // Precise, swim-free noise coordinate (integer part travels in uPatchCellBase) for the detail layer.
    vNoiseCoord = uPatchFracBase + posRel * uDetailFreq;
    gl_Position = uMVP * vec4(posRel, 1.0);
}";

    // Composed at startup: header (ins/uniforms/detail helpers) + the shared terrain field module
    // (TerrainTileGenerator.FieldGlsl, so the orbital relief evaluates the EXACT baked field) + body.
    private static readonly string GpuFragmentSource = GpuFragmentHeaderSrc + TerrainTileGenerator.FieldGlsl + GpuFragmentBodySrc;

    private const string GpuFragmentHeaderSrc = @"#version 410 core
in vec3 vWorld;
in vec3 vDir;
in vec3 vNormal;
in float vElev;
in float vCrater;
in vec3 vNoiseCoord;
uniform vec3 uSunDir;
uniform float uAmbient;
uniform float uScale;            // relief amplitude (metres), to normalise elevation for the albedo ramp
// Airless regolith albedo (cratered worlds): crater-floor/rim tint from the baked crater value, plus
// low-frequency maria provinces. Live (per-pixel) — no rebuild needed unlike the CPU path.
uniform float uIsCratered;       // 1 = apply crater/maria albedo
uniform float uCraterAlbedo;     // crater floor-dark / rim-bright strength
uniform float uMariaStrength;    // basaltic-plains darkening strength
uniform float uMariaFreq;        // low maria province frequency (continentFreq * 0.6)
// Orbital macro-relief: the SAME warped ridged-mountain field the gen shader bakes, evaluated per-pixel
// over the octaves the coarse far mesh can't carry (between its vertex spacing and the pixel footprint) —
// so the mountains seen from orbit are the real ones, and the band fades to zero as the mesh resolves them.
uniform vec3  uSeedR;            // gen seed offset (same as the tile generator's uSeed)
uniform float uReliefStrength;   // normal-bump strength (0 = off)
uniform float uReliefAlbedo;     // valley-dark / ridge-light albedo amount
uniform float uReliefScale;      // pixel-footprint octave reach (>1 shows relief a touch finer/earlier)
uniform float uContFreqR, uContGainR;  uniform float uContMaxOctR;
uniform float uMtnFreqR, uMtnWeightR, uMtnGainR; uniform float uMtnMaxOctR;
uniform float uWarpFreqR, uWarpStrR;
uniform float uRuggedFreqR, uRuggedLoR, uRuggedHiR;
uniform float uCraterFreqR, uCraterWeightR, uCraterDensityR; // crater relief (airless worlds; uIsCratered gates)
// Emissive lava (lava worlds): glowing fissures in the low/cracked ground + glowing volcano vents.
uniform float uIsLava, uLavaGlow, uLavaFreq;
uniform float uVolcanoFreqR, uVolcanoDensityR;
uniform float uCityGlow, uCityFreq; // night-side city lights on inhabited worlds (uHasLife gates)
uniform float uEclipse;             // solar-eclipse coverage [0,1] — dims the sunlit surface to twilight
// Biome / colour (per-pixel port of ColorAt / LandBand).
uniform vec3 uBaseColor, uRock, uSnow, uCliff, uLowland, uSubstrateTint;
uniform float uSnowLine, uCliffThreshold, uCliffStrength, uSurfaceTempK, uHasLife;
uniform float uMoistureFreq, uMoistureBias, uAmplitude;
// Fragment detail layer (shared design with the CPU terrain shader).
uniform vec3 uPatchCellBase;     // wrapped integer cell base (planet-local), keeps the hash precise
uniform float uDetailFreq;       // detail-noise cells per metre
uniform float uDetailStrength;   // detail-normal bump strength (0 = off)
uniform float uDetailAlbedo;     // albedo break-up amount
uniform float uDetailLowCut;     // mesh-handoff threshold (octave-cells per resolved step)
uniform float uMaterialStrength; // cracks/cavity + mineral tint (0 = off)
uniform float uSurfaceSpecular;  // close-up specular highlight strength
uniform float uVertexSpacingDir; // this patch's vertex spacing / planet radius
uniform float uPlanetRadius;     // metres
uniform float uGeomDetailFloor;  // finest spacing the baked geometry resolves (metres)
uniform float uReliefMeshK;      // lodFactor * GridN — converts camera distance → effective mesh spacing
uniform float uPixelArc;         // radians per pixel (vertical FOV / viewport height) — fwidth-free footprint
out vec4 FragColor;

float hash13(vec3 p) {
    p = mod(p, 4096.0);
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}
float vnoise(vec3 base, vec3 nc) {
    vec3 c = base + floor(nc);
    vec3 f = fract(nc);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(c),               n100 = hash13(c + vec3(1,0,0));
    float n010 = hash13(c + vec3(0,1,0)), n110 = hash13(c + vec3(1,1,0));
    float n001 = hash13(c + vec3(0,0,1)), n101 = hash13(c + vec3(1,0,1));
    float n011 = hash13(c + vec3(0,1,1)), n111 = hash13(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
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

// Low-frequency value noise over a direction (coords stay small → a plain hash is precise).
float vnoise3(vec3 p) {
    vec3 c = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(c),               n100 = hash13(c + vec3(1,0,0));
    float n010 = hash13(c + vec3(0,1,0)), n110 = hash13(c + vec3(1,1,0));
    float n001 = hash13(c + vec3(0,0,1)), n101 = hash13(c + vec3(1,0,1));
    float n011 = hash13(c + vec3(0,1,1)), n111 = hash13(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm3(vec3 p, float freq) {
    float s = 0.0, a = 1.0, f = freq, n = 0.0;
    for (int i = 0; i < 4; i++) { s += a * (vnoise3(p * f) * 2.0 - 1.0); n += a; a *= 0.5; f *= 2.0; }
    return s / n;
}
";

    private const string GpuFragmentBodySrc = @"
// Climate + elevation + slope albedo (per-pixel port of PlanetTerrain.LandBand / BiomeGround).
vec3 biomeColor(vec3 dir, float elevM, float slope) {
    float tBase = clamp((uSurfaceTempK - 215.0) / 105.0, 0.0, 1.0);
    float lat = abs(dir.y);
    float elevAbove = max(0.0, elevM / uAmplitude);
    float temp = clamp(tBase - 0.55 * pow(lat, 1.3) - 0.55 * elevAbove, 0.0, 1.0);
    // Geographic moisture: latitude rain belts × orographic drying × regional variation (mirrors Moisture01).
    float tropics = 1.0 - smoothstep(0.0, 0.45, lat);
    float temperateBelt = smoothstep(0.5, 0.7, lat) * (1.0 - smoothstep(0.8, 0.97, lat));
    float rainBand = clamp(max(tropics, 0.75 * temperateBelt), 0.0, 1.0);
    float regional = 0.5 + 0.5 * fbm3(dir + vec3(17.3, 5.9, 42.1), uMoistureFreq);
    float moist = clamp(rainBand * (1.0 - 0.55 * elevAbove) * (0.6 + 0.8 * regional) + uMoistureBias, 0.0, 1.0);

    vec3 temperate = uBaseColor * uLowland;
    vec3 hot = vec3(0.80, 0.62, 0.40);
    vec3 substrate = temp < 0.5 ? mix(vec3(0.78,0.82,0.86), temperate, temp / 0.5)
                                : mix(temperate, hot, (temp - 0.5) / 0.5);
    substrate = mix(substrate, uSubstrateTint, 0.45);            // composition-driven hue
    vec3 ground = substrate;
    if (uHasLife > 0.5) {
        // Whittaker matrix: arid axis (tundra→steppe→desert) blended by moisture to the wet axis
        // (taiga→temperate forest→jungle), each over temperature.
        vec3 aridCold = vec3(0.52,0.52,0.42), aridWarm = vec3(0.66,0.62,0.34), aridHot = hot;
        vec3 wetCold = vec3(0.16,0.34,0.20), wetWarm = vec3(0.22,0.46,0.18), wetHot = vec3(0.10,0.40,0.14);
        vec3 arid = temp < 0.5 ? mix(aridCold, aridWarm, temp / 0.5) : mix(aridWarm, aridHot, (temp - 0.5) / 0.5);
        vec3 wet  = temp < 0.5 ? mix(wetCold, wetWarm, temp / 0.5)   : mix(wetWarm, wetHot, (temp - 0.5) / 0.5);
        vec3 veg = mix(arid, wet, smoothstep(0.2, 0.8, moist));
        float lush = smoothstep(0.12, 0.35, temp) * smoothstep(0.2, 0.55, moist);
        ground = mix(substrate, veg, lush);
    }

    float t = clamp((elevM / uAmplitude + 0.3) / 1.3, 0.0, 1.0);
    vec3 band = mix(ground, uRock, smoothstep(0.0, uSnowLine, t));
    float coldSnow = 1.0 - smoothstep(0.06, 0.24, temp);
    float elevSnow = smoothstep(uSnowLine, min(1.0, uSnowLine + 0.22), t);
    band = mix(band, uSnow, clamp(max(coldSnow, elevSnow), 0.0, 1.0));
    float steep = 1.0 - smoothstep(uCliffThreshold - 0.135, uCliffThreshold + 0.135, slope);
    return mix(band, uCliff, steep * uCliffStrength);
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 up = normalize(vDir);
    float slope = clamp(dot(N, up), 0.0, 1.0);               // 1 on flats → 0 on cliffs
    vec3 col = biomeColor(up, vElev, slope);

    // Airless regolith: dark dust-pooled crater floors + bright rims/ejecta (from the baked crater value),
    // then low-frequency maria provinces (darker basaltic plains that survive at coarse far LOD). Port of
    // PlanetTerrain.RegolithAlbedo; the crater geometry itself is already baked into the mesh.
    if (uIsCratered > 0.5) {
        if (uCraterAlbedo > 0.0) {
            float dark   = max(0.0, -vCrater);               // 0 at rim → 1 deep floor
            float bright = max(0.0,  vCrater) / 0.28;        // 0 → 1 at the rim crest
            col *= 1.0 - 0.45 * uCraterAlbedo * dark;
            col *= 1.0 + 0.30 * uCraterAlbedo * bright;
        }
        if (uMariaStrength > 0.0) {
            float m = fbm3(up + vec3(23.7, 88.1, 4.3), uMariaFreq);
            float maria = smoothstep(0.08, 0.5, m);
            vec3 mare = vec3(col.r * 0.55, col.g * 0.56, col.b * 0.60);  // darker, faintly cooler
            col = mix(col, mare, maria * uMariaStrength);
        }
    }

    // Close-up detail: a band-passed multi-octave noise gives per-pixel normal bump + material breakup
    // below the mesh resolution (handed off to the geometry above it). Same scheme as the CPU shader.
    if (uDetailStrength > 0.0001 || uMaterialStrength > 0.0001) {
        float fpMax = max(max(fwidth(vNoiseCoord.x), fwidth(vNoiseCoord.y)), fwidth(vNoiseCoord.z));
        float fpMin = min(min(fwidth(vNoiseCoord.x), fwidth(vNoiseCoord.y)), fwidth(vNoiseCoord.z));
        float footprint = max(fpMin, fpMax * 0.2);
        float effSpacing = max(uVertexSpacingDir * uPlanetRadius, uGeomDetailFloor);
        float meshCellsPerVertex = effSpacing * uDetailFreq;
        vec3 tangAccum = vec3(0.0);
        float nDetail = 0.0, wsum = 0.0, m = 1.0;
        for (int o = 0; o < 8; o++) {
            float aa = 1.0 - smoothstep(0.45, 1.3, footprint * m);
            float mc = meshCellsPerVertex * m;
            float oct = aa * smoothstep(uDetailLowCut, uDetailLowCut * 3.0, mc);
            if (oct > 0.0) {
                vec4 s = detailSample(m);
                tangAccum += oct * (s.xyz - dot(s.xyz, N) * N);
                nDetail += oct * (s.w - 0.5) * 2.0;
                wsum += oct;
            }
            m *= 2.0;
        }
        float baseFade = clamp(wsum, 0.0, 1.0);
        if (wsum > 0.0) {
            nDetail = nDetail / wsum;
            N = normalize(N - uDetailStrength * tangAccum);
            float steep = 1.0 - slope;
            col *= 1.0 + uDetailAlbedo * nDetail * baseFade;
            float crack = smoothstep(0.25, -0.1, nDetail);
            col *= 1.0 - uMaterialStrength * baseFade * crack * (0.35 + 0.65 * steep);
            col += uMaterialStrength * baseFade * 0.04 * nDetail * vec3(0.6, 0.5, 0.4);
        }
    }

    // Orbital macro-relief: shade the REAL warped ridged-mountain field the tiles bake (so the mountains
    // seen from orbit are exactly the ones you land in), faded in by how much finer a pixel resolves than
    // the coarse mesh (`fade` = pixel octaves − mesh octaves). At orbit the smooth coarse mesh shows little,
    // so the field carries the look; as you descend the mesh resolves those scales and `fade` → 0, so the
    // geometry takes over with no double image. Albedo uses the field VALUE (punchy, ridges light / valleys
    // dark); the normal uses its tangential gradient (adds form). Both scale with the sliders.
    if (uReliefStrength > 0.0 || uReliefAlbedo > 0.0) {
        // Everything here is driven by the CAMERA DISTANCE (length(vWorld)) and a per-frame radians-per-pixel
        // constant — NOT fwidth(). fwidth is the screen-space derivative of the interpolated direction; it is
        // faceted per-triangle and STEPS across an LOD-level boundary (big coarse triangles vs small fine
        // ones), which stepped the normal-gradient finite difference into a hard shading line along the LOD
        // ring. Distance is continuous across every patch/LOD boundary, so the relief is now seam-free.
        float dist   = length(vWorld);
        float meshSp = max(dist / max(uReliefMeshK, 1e-3), 1e-3);            // mesh resolution (dist/(lodFactor·GridN))
        float pixSp  = max(dist * uPixelArc / max(0.01, uReliefScale), 1e-3); // pixel footprint on the surface (m)

        // MOUNTAIN band — highland-masked ridged field. Faded by ABSOLUTE mesh resolution: full while the
        // mesh is coarse, off once it resolves the significant octaves (gain falloff ⇒ ~done by meshOct≈6,
        // long before the float-safe ceiling). Fading over [1,6] hands off to geometry + the close-up detail
        // layer before the surface; lingering kept relief at ~80% at 400 km, carving hard facets. (A
        // pixel-vs-mesh GAP never closes — the quadtree keeps the mesh ~constant octaves behind the pixel.)
        float mMeshOct  = tfOctFor(uMtnFreqR, meshSp, uMtnMaxOctR, uPlanetRadius);
        float mFieldOct = min(tfOctFor(uMtnFreqR, pixSp, uMtnMaxOctR, uPlanetRadius), uMtnMaxOctR);
        float cont = tfFbm(up, uContFreqR, uContMaxOctR, uContGainR, uSeedR);
        float mask = smoothstep(-0.2, 0.4, cont);                            // mountains live on highlands
        float rug  = tfRuggedness(up, uRuggedFreqR, uRuggedLoR, uRuggedHiR, uSeedR);
        float mAmp = (1.0 - smoothstep(1.0, 6.0, mMeshOct)) * uMtnWeightR * mask * rug; // faded mountain amplitude

        // CRATER band — covers the whole airless body (no highland mask). From afar the mesh shows craters
        // only as flat baked albedo (vCrater); this gives them their real 3-D bowl/rim FORM (normal) so a
        // crater seen from orbit looks like the one you descend into. Albedo stays with the baked vCrater
        // tint (not re-added here) so it isn't doubled. Coarse classes only (read from orbit; finer ones are
        // baked geometry up close), and gated to genuinely cratered worlds so other worlds pay nothing.
        float cAmp = 0.0, cFieldOct = 0.0;
        if (uIsCratered > 0.5 && uCraterWeightR > 0.0) {
            float cMeshOct = tfOctFor(uCraterFreqR, meshSp, 10.0, uPlanetRadius);
            cFieldOct = min(tfOctFor(uCraterFreqR, pixSp, 10.0, uPlanetRadius), 3.0); // coarse classes only (cost)
            cAmp = (1.0 - smoothstep(1.0, 6.0, cMeshOct)) * uCraterWeightR;
        }

        if (mAmp > 0.001 || cAmp > 0.001) {
            vec3 grad3 = vec3(0.0);                              // ∂(combined relief height)/∂dir, world axes

            // MOUNTAIN: ridged value drives the albedo; gradient via 3 world-axis taps (ridged is cheap, and
            // a finite difference avoids a fiddly analytic ridged derivative). Step matched to its finest
            // octave (no aliasing); the 3-axis form is basis-free (no pole crease).
            if (mAmp > 0.001) {
                vec3 wp = up + tfDomainWarp(up, uWarpFreqR, uWarpStrR, uSeedR);
                float mtn0 = tfRidged(wp, uMtnFreqR, mFieldOct, uMtnGainR, uSeedR);
                col *= 1.0 + uReliefAlbedo * mAmp * (mtn0 * 2.0 - 1.0);      // ridges bright, valleys dark
                float eps = clamp(0.5 / max(uMtnFreqR * exp2(max(mFieldOct - 1.0, 0.0)), 1.0), 1e-7, 0.05);
                vec3 gx = normalize(up + vec3(eps, 0.0, 0.0));
                vec3 gy = normalize(up + vec3(0.0, eps, 0.0));
                vec3 gz = normalize(up + vec3(0.0, 0.0, eps));
                float mx = tfRidged(gx + tfDomainWarp(gx, uWarpFreqR, uWarpStrR, uSeedR), uMtnFreqR, mFieldOct, uMtnGainR, uSeedR);
                float my = tfRidged(gy + tfDomainWarp(gy, uWarpFreqR, uWarpStrR, uSeedR), uMtnFreqR, mFieldOct, uMtnGainR, uSeedR);
                float mz = tfRidged(gz + tfDomainWarp(gz, uWarpFreqR, uWarpStrR, uSeedR), uMtnFreqR, mFieldOct, uMtnGainR, uSeedR);
                grad3 += mAmp * vec3(mx - mtn0, my - mtn0, mz - mtn0) / eps;
            }
            // CRATER: value + analytic gradient in ONE cascade pass (no taps — the 3×3×3 cascade is the cost).
            // Contributes the 3-D bowl/rim FORM (normal) only; its albedo stays with the baked vCrater tint.
            if (cAmp > 0.001) {
                vec4 cN = tfCraterFieldN(up, uCraterFreqR, cFieldOct, uCraterDensityR, uSeedR);
                grad3 += cAmp * cN.xyz;
            }

            vec3 gradT = grad3 - dot(grad3, up) * up;            // tangential part (radial removed)
            vec3 bump = (uScale / uPlanetRadius) * gradT;        // height-slope tangent vector
            bump *= min(1.0, 0.5 / max(length(bump), 1e-6));     // cap the tilt (no hard self-shadow faces)
            float baseLit = max(dot(N, normalize(uSunDir)), 0.0);
            float reliefMask = smoothstep(0.0, 0.25, baseLit);   // fade out near the terminator (no hard edge)
            N = normalize(N - uReliefStrength * reliefMask * bump);
        }
    }

    // Lighting: hemispheric ambient + diffuse + a subtle specular so the detail normals catch the sun.
    vec3 V = normalize(-vWorld);
    float sunlit = 1.0 - 0.95 * uEclipse;                  // solar eclipse → the sun all but vanishes
    float diff = max(dot(N, normalize(uSunDir)), 0.0) * sunlit;
    float ambient = uAmbient * mix(0.5, 1.0, 0.5 + 0.5 * dot(N, up)) * mix(1.0, 0.2, uEclipse);
    vec3 Hh = normalize(normalize(uSunDir) + V);
    float spec = uSurfaceSpecular * pow(max(dot(N, Hh), 0.0), 30.0) * diff;

    // Lava worlds: emissive molten rock — glowing fissures pooled in the low/cracked ground plus glowing
    // caldera vents at the volcano summits. Added AFTER lighting (so it glows on the night side) and pushed
    // bright so the bloom pass haloes it; the cooled basalt crust (biome colour) stays dark between.
    vec3 emissive = vec3(0.0);
    if (uIsLava > 0.5 && uLavaGlow > 0.0) {
        float lowness = 1.0 - smoothstep(-0.15 * uScale, 0.15 * uScale, vElev); // lava pools in low ground
        float veinN = fbm3(up, uLavaFreq);                                      // [-1,1]
        float cracks = smoothstep(0.82, 1.0, 1.0 - abs(veinN));                 // thin glowing fissures
        float vent = tfVolcano(up, uVolcanoFreqR, uVolcanoDensityR, uSeedR).y;  // glowing caldera lava
        float heat = clamp(max(lowness * cracks, vent), 0.0, 1.0);
        vec3 lavaCol = mix(vec3(0.6, 0.06, 0.0), vec3(1.0, 0.5, 0.1), smoothstep(0.15, 0.65, heat));
        lavaCol = mix(lavaCol, vec3(1.0, 0.92, 0.6), smoothstep(0.65, 1.0, heat));
        emissive = uLavaGlow * heat * lavaCol;
    }

    // City lights: warm clustered glow on the NIGHT side of inhabited worlds (a sign of civilisation from
    // orbit). Habitable land only — temperate latitudes, low coastal ground (not deep ocean or high peaks) —
    // brightest where it is dark. Clusters = low-freq regions × finer sparkle; pushed bright so it blooms.
    if (uHasLife > 0.5 && uCityGlow > 0.0) {
        float sunFace = dot(up, normalize(uSunDir));
        float night = 1.0 - smoothstep(-0.12, 0.10, sunFace);          // 1 on the dark side
        // City lights are an ORBITAL effect: from far the clusters read as points, but up close one cluster
        // magnifies into a smooth glowing blob (we don't model individual windows). So fade them out as you
        // descend toward the surface — full from orbit, gone by low altitude.
        float cityFade = smoothstep(uPlanetRadius * 0.012, uPlanetRadius * 0.08, length(vWorld));
        if (night * cityFade > 0.01) {
            float elevN = vElev / max(1.0, uAmplitude);
            float lowOk = smoothstep(-0.02, 0.05, elevN) * (1.0 - smoothstep(0.15, 0.40, elevN)); // coastal lowland
            float tempOk = 1.0 - smoothstep(0.6, 0.95, abs(up.y));      // not polar
            float region = smoothstep(0.5, 0.85, 0.5 + 0.5 * fbm3(up, uCityFreq * 0.25));   // populated regions
            float sparkle = smoothstep(0.55, 0.95, 0.5 + 0.5 * fbm3(up + vec3(11.0, 4.0, 7.0), uCityFreq * 1.6)); // lights
            float city = night * cityFade * lowOk * tempOk * region * sparkle;
            emissive += uCityGlow * city * vec3(1.0, 0.84, 0.5);       // warm sodium glow
        }
    }

    FragColor = vec4(col * (ambient + 0.95 * diff) + vec3(spec) + emissive, 1.0);
}";

    // --- GPU-path ocean surface ---
    // Reuses the patch's BASE MESH VAO (no separate water mesh): each vertex samples the height tile to
    // read the sea-floor height, displaces to sea level + travelling swell, and carries the depth so the
    // fragment can discard dry land (the coastline is where the floor crosses sea level) and tint shallows
    // pale → deeps dark. Mirrors the CPU WaterVertex/Fragment shaders (same swell uniforms, same colours).
    private const string GpuWaterVertexSource = @"#version 410 core
layout(location = 0) in vec3 aBasePos;   // R*(dir - centreDir): planet-stable, precise
layout(location = 1) in vec3 aDir;       // outward unit direction
layout(location = 2) in vec2 aTexel;     // guard-offset texel of this vertex in its tile
layout(location = 3) in float aSkirt;    // ignored for water (skirt verts collapse onto the patch edge)
uniform mat4 uMVP;
uniform mat4 uModel;
uniform float uMorph;
uniform vec2 uTileOrigin;
uniform sampler2D uHeight;
uniform float uSeaLevel;    // metres (signed) of the ocean surface above the base radius
uniform float uAmp;         // relief amplitude (for the depth → colour ramp)
uniform float uWaterDrop;   // small dip below sea level so the shore doesn't z-fight the land
uniform float uTime, uWaveAmp;
uniform vec3 uWD[3];        // wave directions (planet-local, unit)
uniform vec3 uWK;           // angular wavenumbers
uniform vec3 uWW;           // angular speeds
uniform vec3 uWA;           // per-wave amplitude weights
uniform vec3 uPhase;        // per-patch phase base for the 3 waves
out vec3 vNormal;
out vec4 vColor;
out vec3 vWorld;
out float vDepth;           // seaLevel - floorHeight (m); < 0 = dry → fragment discards
void main() {
    ivec2 o = ivec2(int(uTileOrigin.x), int(uTileOrigin.y)) + ivec2(int(aTexel.x), int(aTexel.y));
    vec2 hfc = texelFetch(uHeight, o, 0).rg;
    float floorH = mix(hfc.x, hfc.y, uMorph);
    vDepth = uSeaLevel - floorH;

    vec3 N = aDir;
    float h = 0.0;
    vec3 grad = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        float ph = uPhase[i] + uWK[i] * dot(aBasePos, uWD[i]) + uTime * uWW[i];
        float a = uWA[i] * uWaveAmp;
        h += a * sin(ph);
        grad += a * cos(ph) * uWK[i] * uWD[i];
    }
    float lift = uSeaLevel - uWaterDrop + h;
    vec3 posRel = aBasePos + aDir * lift;        // base sphere + radial displacement to the swell-lifted sea
    vec3 tang = grad - dot(grad, N) * N;
    vNormal = normalize(N - tang);

    float f = clamp(vDepth / (uAmp * 0.12 + 1.0), 0.0, 1.0);
    vec3 shallow = vec3(0.20, 0.55, 0.62), deep = vec3(0.02, 0.10, 0.26);
    vColor = vec4(mix(shallow, deep, f), 0.5 + 0.48 * f); // shallows pale & clear → deeps dark & opaque
    vWorld = (uModel * vec4(posRel, 1.0)).xyz;
    gl_Position = uMVP * vec4(posRel, 1.0);
}";

    private const string GpuWaterFragmentSource = @"#version 410 core
in vec3 vNormal;
in vec4 vColor;
in vec3 vWorld;
in float vDepth;
uniform vec3 uSunDir;
out vec4 FragColor;
void main() {
    if (vDepth < 0.0) discard;                    // sea floor rises above the waterline here — bare land
    vec3 N = normalize(vNormal);
    vec3 L = normalize(uSunDir);
    vec3 V = normalize(-vWorld);
    float diff = max(dot(N, L), 0.0);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 90.0);  // tight sun glint
    float fres = pow(1.0 - max(dot(N, V), 0.0), 3.0);
    vec3 col = vColor.rgb * (0.12 + 0.9 * diff) + vec3(1.0) * spec * 0.7;
    float a = clamp(vColor.a + fres * 0.2, 0.0, 1.0);
    FragColor = vec4(col, a);
}";

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
    private double _lodFactor = 4.0; // this frame's split-distance factor (snapshot from TerrainTuning)
    private int _renderFrame;        // increments each Render; leaves stamp it so TrySurfaceHeight finds
                                     // the patch actually drawn (and the morph it was drawn with)

    public int LeafCount { get; private set; }
    public int PatchCount { get; private set; }
    public CelestialBody? Active => _body;

    /// <summary>When off, the ocean swell amplitude is zeroed so the water sits perfectly flat
    /// (still depth-writing for a clean horizon). Off by default for now.</summary>
    public bool AnimateWater = false;

    /// <summary>Max relief (metres) of the active terrain — how far below the base radius a valley can
    /// sit. The atmosphere uses this to drop its ground/clip radius so low terrain still gets hazed.</summary>
    public double ActiveAmplitude => _terrain?.Amplitude ?? 0.0;

    /// <summary>The active body's terrain field (height/biome/vegetation queries); null when none is set.</summary>
    public PlanetTerrain? ActiveTerrain => _terrain;

    /// <summary>
    /// Optional extra LOD focus (e.g. the rover) — patches refine toward whichever is closer, the
    /// camera or this point, so the ground directly under the vehicle stays at fine vertex spacing
    /// even when the chase camera is a little farther back. Null disables it.
    /// </summary>
    public UniversePosition? FocusPoint;

    /// <summary>Solar-eclipse coverage in [0,1] (another body occluding the sun as seen from this world):
    /// 0 = full sun, 1 = totality. Dims the lit terrain so the surface goes to twilight during an eclipse.
    /// Set by the host each frame before <see cref="Render"/>.</summary>
    public float Eclipse;

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
            (float[] land, float[] water, float[] collHF, float[] collHC) = BuildPatchVertices(job.Node, job.Terrain);
            _ready.Enqueue(new BuildResult(job.Epoch, job.Node, land, water, collHF, collHC));
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
            r.Node.CollHF = r.CollHF;
            r.Node.CollHC = r.CollHC;
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
            if (TerrainTuning.GpuTerrain) EnsureDrawableGpu(_roots[f], force: true);
            else _roots[f].Patch = GenerateSync(_roots[f]); // roots eager so render never has a hole
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
            if (TerrainTuning.GpuTerrain) EnsureDrawableGpu(_roots[f], force: true);
            else _roots[f].Patch = GenerateSync(_roots[f]);
        }
    }

    public void Render(Camera camera, Vector3D<float> sunDir, float time = 0f, PlanetSurfaceMap? map = null)
    {
        if (_body == null || _roots == null) return;

        // Snapshot the live LOD aggressiveness once so split, merge and morph all agree this frame.
        _lodFactor = Math.Clamp(TerrainTuning.LodDistanceFactor, 1.0, 32.0);
        _renderFrame++; // stamp leaves drawn this frame so the vehicle can find the on-screen surface

        Matrix4X4<float> proj = FitProjection(camera);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;
        ExtractFrustum(viewProj);

        if (TerrainTuning.GpuTerrain) { RenderGpu(camera, viewProj, sunDir, time); return; }

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _shader.Use();
        _shader.SetVector3("uSunDir", sunDir);
        _detailNoiseFreq = DetailBaseFreq * Math.Max(0.01f, TerrainTuning.DetailNormalScale);
        _shader.SetFloat("uDetailFreq", (float)_detailNoiseFreq);
        _shader.SetFloat("uDetailStrength", Math.Max(0f, TerrainTuning.DetailNormalStrength));
        _shader.SetFloat("uDetailAlbedo", TerrainTuning.DetailAlbedo);
        // Mesh-handoff threshold in octave-cells-per-resolved-step (~0.5 = the Nyquist limit). Octaves
        // coarser than this are handed to the geometry; finer ones stay procedural. Larger "range" →
        // lower threshold → coarser octaves stay procedural → detail reaches further past the mesh.
        _shader.SetFloat("uDetailLowCut", 2.0f / Math.Clamp(TerrainTuning.SurfaceDetailRange, 1f, 16f));
        // The geometry can't resolve finer than its detail-octave floor, so the handoff must defer to
        // the COARSER of (patch vertex spacing, this floor). Half the finest geometry wavelength is that
        // floor in spacing terms (Nyquist); the shader clamps the effective spacing up to it so near
        // patches tessellated below the floor keep procedural detail instead of going flat.
        _shader.SetFloat("uGeomDetailFloor", (float)(_terrain!.FinestGeometryWavelength * 0.5));
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

        double splitDist = _lodFactor * node.WorldSize;
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
            // Children still baking — draw this node as the coarse stand-in, but pre-queue the path
            // ahead so deeper levels bake in parallel instead of one level per upload round-trip.
            SpeculativeRefine(node, camera, SpeculativeDepth);
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

    /// <summary>
    /// Pre-create and queue for baking the patches along the path toward the camera/focus, up to
    /// <paramref name="depthBudget"/> levels past the drawable frontier. Bakes are independent of their
    /// parent, so queuing deep patches early lets the whole worker pool work the descent in parallel
    /// rather than one level per upload round-trip. Walks a SINGLE path per level (the child nearest the
    /// focus) to keep the queued count linear — <see cref="Process"/> still requests the per-level
    /// breadth it actually draws. Stops where the LOD no longer wants to split, at max depth, or budget.
    /// </summary>
    private void SpeculativeRefine(QuadNode node, Camera camera, int depthBudget)
    {
        if (depthBudget <= 0 || node.Level >= MaxLevel) return;

        UniversePosition center = _body!.CurrentPosition.Translated(node.CenterLocal);
        double dist = camera.Position.DistanceTo(center);
        if (FocusPoint is { } fp) dist = Math.Min(dist, fp.DistanceTo(center));
        if (dist >= _lodFactor * node.WorldSize) return; // LOD doesn't want to refine here

        node.Children ??= Split(node);
        QuadNode? next = null;
        double best = double.MaxValue;
        foreach (QuadNode c in node.Children)
        {
            if (c.Patch == null && !c.GenPending) RequestGenerate(c); // queue all four (we need them to draw)
            UniversePosition cc = _body!.CurrentPosition.Translated(c.CenterLocal);
            double d = camera.Position.DistanceTo(cc);
            if (FocusPoint is { } f) d = Math.Min(d, f.DistanceTo(cc));
            if (d < best) { best = d; next = c; }
        }
        if (next != null) SpeculativeRefine(next, camera, depthBudget - 1); // continue down the focus path
    }

    private void RenderPatch(QuadNode node, Vector3D<float> rel, in Matrix4X4<float> viewProj, float morph)
    {
        if (node.Patch == null) return;
        Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
        _shader.SetMatrix("uMVP", model * viewProj);
        _shader.SetMatrix("uModel", model);
        _shader.SetFloat("uMorph", morph);
        node.DrawMorph = morph;          // record what the vehicle collision should blend to
        node.DrawnFrame = _renderFrame;  // this node is a drawn leaf this frame
        SetPatchDetailBase(_shader, node);
        node.Patch.Draw(_gl);
        if (node.Patch.HasWater) _waterFrame.Add((node.Patch, rel, node.CenterLocal)); // drawn in the later water pass
    }

    // ============================ GPU tile path (TerrainTuning.GpuTerrain) ============================

    private void EnsureGpuResources()
    {
        // Tile = vertex grid res (GridN+1) plus a 1-texel guard ring on every side (→ GridN+3), so an
        // edge vertex can central-difference its normal from in-tile texels — no lighting seam. Pool is
        // sized above a deep descent's drawn-leaf count (CPU mode reaches ~1100 + frontier); AllocateLayer
        // fails soft if it's ever exceeded.
        _tileCache ??= new TerrainTileCache(_gl, GridN + 3, 4096);
        _tileGen ??= new TerrainTileGenerator(_gl);
        _gpuShader ??= new Shader(_gl, GpuVertexSource, GpuFragmentSource);
        _gpuWaterShader ??= new Shader(_gl, GpuWaterVertexSource, GpuWaterFragmentSource);
    }

    /// <summary>Render the active planet via the GPU tile path. Mirrors <see cref="Process"/>'s split/
    /// merge/cull, but each node draws a shared base-sphere mesh displaced by its GPU-generated height
    /// tile (geomorphing fine→coarse in the vertex shader). No worker pool — tiles generate on the render
    /// thread, budgeted per frame.</summary>
    private void RenderGpu(Camera camera, in Matrix4X4<float> viewProj, Vector3D<float> sunDir, float time)
    {
        EnsureGpuResources();
        _gpuGenBudget = GpuTileGensPerFrame;

        // Pass 1 — GENERATION: walk the tree deciding split/merge and generate any tiles needed this
        // frame (each Generate binds the cache FBO). Generation is fully separated from drawing so it can
        // never leak the fullscreen noise pass onto the scene. Capture the scene framebuffer + viewport
        // first and hard-restore them once afterwards — robust regardless of per-call FBO state.
        Span<int> sceneFbo = stackalloc int[1];
        Span<int> sceneVp = stackalloc int[4];
        _gl.GetInteger(GetPName.DrawFramebufferBinding, sceneFbo);
        _gl.GetInteger(GetPName.Viewport, sceneVp);
        foreach (QuadNode root in _roots!) EnsureSubtreeGpu(root, camera);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)sceneFbo[0]);
        _gl.Viewport(sceneVp[0], sceneVp[1], (uint)sceneVp[2], (uint)sceneVp[3]);

        // Pass 2 — DRAW: no generation, so the scene pass is never disturbed. Only resident tiles draw.
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gpuShader!.Use();
        _gpuShader.SetVector3("uSunDir", sunDir);
        _gpuShader.SetFloat("uAmbient", Math.Max(0f, TerrainTuning.SurfaceAmbient));
        PlanetTerrain.GpuTerrainParams gp = _terrain!.GpuParams();
        _gpuShader.SetFloat("uScale", (float)gp.Scale); // elevation normaliser for albedo
        _gpuShader.SetFloat("uGridN", GridN);

        // Biome / colour (per-pixel albedo, mirroring ColorAt/LandBand).
        _gpuShader.SetVector3("uBaseColor", gp.BaseColor);
        _gpuShader.SetVector3("uSubstrateTint", gp.SubstrateTint);
        _gpuShader.SetVector3("uRock", gp.Rock);
        _gpuShader.SetVector3("uSnow", gp.Snow);
        _gpuShader.SetVector3("uCliff", gp.Cliff);
        _gpuShader.SetVector3("uLowland", gp.Lowland);
        _gpuShader.SetFloat("uSnowLine", gp.SnowLine);
        _gpuShader.SetFloat("uCliffThreshold", gp.CliffThreshold);
        _gpuShader.SetFloat("uCliffStrength", gp.CliffStrength);
        _gpuShader.SetFloat("uSurfaceTempK", gp.SurfaceTempK);
        _gpuShader.SetFloat("uHasLife", gp.HasLife);
        _gpuShader.SetFloat("uMoistureFreq", (float)gp.MoistureFreq);
        _gpuShader.SetFloat("uMoistureBias", (float)gp.MoistureBias);
        _gpuShader.SetFloat("uAmplitude", (float)Math.Max(1.0, gp.Amplitude));

        // Airless regolith albedo (crater floors/rims + maria) — live per-pixel on the GPU path.
        _gpuShader.SetFloat("uIsCratered", gp.IsCratered);
        _gpuShader.SetFloat("uCraterAlbedo", Math.Max(0f, TerrainTuning.CraterAlbedo));
        _gpuShader.SetFloat("uMariaStrength", Math.Max(0f, TerrainTuning.MariaStrength));
        _gpuShader.SetFloat("uMariaFreq", (float)(gp.ContinentFreq * 0.6));

        // Orbital macro-relief from the real baked field (matches the mountains, fades out on descent).
        _gpuShader.SetVector3("uSeedR", new Vector3D<float>(
            (gp.Seed & 1023) / 1024f, ((gp.Seed >> 10) & 1023) / 1024f, ((gp.Seed >> 20) & 1023) / 1024f));
        _gpuShader.SetFloat("uReliefStrength", Math.Max(0f, TerrainTuning.OrbitalReliefStrength));
        _gpuShader.SetFloat("uReliefAlbedo", Math.Max(0f, TerrainTuning.OrbitalReliefAlbedo));
        _gpuShader.SetFloat("uReliefScale", Math.Clamp(TerrainTuning.OrbitalReliefScale, 0.1f, 8f));
        _gpuShader.SetFloat("uReliefMeshK", (float)(_lodFactor * GridN)); // camera dist → effective mesh spacing
        _gpuShader.SetFloat("uPixelArc", camera.FovRadians / Math.Max(1, sceneVp[3])); // radians per pixel (fwidth-free footprint)
        _gpuShader.SetFloat("uContFreqR", (float)gp.ContinentFreq);
        _gpuShader.SetFloat("uContGainR", (float)gp.ContinentGain);
        _gpuShader.SetFloat("uContMaxOctR", gp.MaxContinentOctaves);
        _gpuShader.SetFloat("uMtnFreqR", (float)gp.MountainFreq);
        _gpuShader.SetFloat("uMtnWeightR", (float)gp.MountainWeight);
        _gpuShader.SetFloat("uMtnGainR", (float)gp.MountainGain);
        _gpuShader.SetFloat("uMtnMaxOctR", gp.MaxMountainOctaves);
        _gpuShader.SetFloat("uWarpFreqR", (float)gp.WarpFreq);
        _gpuShader.SetFloat("uWarpStrR", (float)gp.WarpStrength);
        _gpuShader.SetFloat("uRuggedFreqR", (float)gp.RuggedFreq);
        _gpuShader.SetFloat("uRuggedLoR", (float)gp.RuggedLo);
        _gpuShader.SetFloat("uRuggedHiR", (float)gp.RuggedHi);
        _gpuShader.SetFloat("uCraterFreqR", (float)gp.CraterFreq);       // crater relief (gives orbital craters 3-D form)
        _gpuShader.SetFloat("uCraterWeightR", (float)gp.CraterWeight);
        _gpuShader.SetFloat("uCraterDensityR", (float)gp.CraterDensity);

        // Emissive lava (lava worlds) — live glow strength.
        _gpuShader.SetFloat("uIsLava", gp.IsLava);
        _gpuShader.SetFloat("uLavaGlow", Math.Max(0f, TerrainTuning.LavaGlow));
        _gpuShader.SetFloat("uLavaFreq", (float)(gp.MountainFreq * 6.0)); // crack/fissure frequency
        _gpuShader.SetFloat("uVolcanoFreqR", (float)gp.VolcanoFreq);
        _gpuShader.SetFloat("uVolcanoDensityR", (float)gp.VolcanoDensity);
        _gpuShader.SetFloat("uCityGlow", Math.Max(0f, TerrainTuning.CityGlow));
        _gpuShader.SetFloat("uCityFreq", (float)(gp.ContinentFreq * 14.0)); // city-cluster frequency
        _gpuShader.SetFloat("uEclipse", Math.Clamp(Eclipse, 0f, 1f));

        // Fragment detail layer (same knobs/scheme as the CPU shader).
        _detailNoiseFreq = DetailBaseFreq * Math.Max(0.01f, TerrainTuning.DetailNormalScale);
        _gpuShader.SetFloat("uDetailFreq", (float)_detailNoiseFreq);
        _gpuShader.SetFloat("uDetailStrength", Math.Max(0f, TerrainTuning.DetailNormalStrength));
        _gpuShader.SetFloat("uDetailAlbedo", TerrainTuning.DetailAlbedo);
        _gpuShader.SetFloat("uDetailLowCut", 2.0f / Math.Clamp(TerrainTuning.SurfaceDetailRange, 1f, 16f));
        _gpuShader.SetFloat("uMaterialStrength", Math.Max(0f, TerrainTuning.MaterialDetail));
        _gpuShader.SetFloat("uSurfaceSpecular", Math.Max(0f, TerrainTuning.SurfaceSpecular));
        _gpuShader.SetFloat("uPlanetRadius", (float)_terrain.Radius);
        _gpuShader.SetFloat("uGeomDetailFloor", (float)(_terrain.FinestGeometryWavelength * 0.5));

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tileCache!.HeightTexture);
        _gpuShader.SetInt("uHeight", 0);

        LeafCount = 0;
        MinDrawnWorldSize = double.MaxValue;
        _gpuWaterFrame.Clear();
        _grassLeaves.Clear();
        Vector3D<double> camOff = camera.Position.DeltaMeters(_body!.CurrentPosition);
        _grassCamDir = camOff.LengthSquared > 0 ? Vector3D.Normalize(camOff) : new Vector3D<double>(0, 1, 0);
        foreach (QuadNode root in _roots!) DrawSubtreeGpu(root, camera, viewProj);

        // Ocean pass: a translucent sea-level shell over the opaque sea floor (depth-tested + depth-writing
        // so the flat surface forms the limb and hides the rugged floor; alpha-blended so deeps read opaque
        // and shallows clear). Mirrors the CPU water pass; reuses each leaf's base mesh + height tile.
        if (_gpuWaterFrame.Count > 0) DrawGpuWater(viewProj, sunDir, time);
        _gl.Disable(EnableCap.DepthTest);

        if (FocusPoint is { } fp) UpdateRoverLeaf(fp); // cache the rover's tile for exact collision
    }

    /// <summary>Translucent ocean pass for the GPU path: each collected ocean leaf re-draws its base mesh
    /// as a sea-level shell (the water shader samples the leaf's height tile for the sea floor → depth and
    /// discards dry land). Depth-tested + depth-writing + alpha-blended, exactly like the CPU ocean pass.</summary>
    private void DrawGpuWater(in Matrix4X4<float> viewProj, Vector3D<float> sunDir, float time)
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        _gpuWaterShader!.Use();
        _gpuWaterShader.SetVector3("uSunDir", sunDir);
        _gpuWaterShader.SetInt("uHeight", 0); // same atlas the opaque pass left bound on texture unit 0
        _gpuWaterShader.SetFloat("uSeaLevel", (float)_terrain!.SeaLevelMeters);
        _gpuWaterShader.SetFloat("uAmp", (float)Math.Max(1.0, _terrain.Amplitude));

        double twoPi = 2.0 * Math.PI;
        var wk = new Vector3D<float>((float)(twoPi / WaveLen[0]), (float)(twoPi / WaveLen[1]), (float)(twoPi / WaveLen[2]));
        _gpuWaterShader.SetFloat("uTime", time);
        _gpuWaterShader.SetFloat("uWaveAmp", AnimateWater ? 1.0f : 0.0f);
        for (int i = 0; i < 3; i++)
            _gpuWaterShader.SetVector3($"uWD[{i}]", new Vector3D<float>((float)WaveDir[i].X, (float)WaveDir[i].Y, (float)WaveDir[i].Z));
        _gpuWaterShader.SetVector3("uWK", wk);
        _gpuWaterShader.SetVector3("uWW", new Vector3D<float>((float)(twoPi / WavePeriod[0]), (float)(twoPi / WavePeriod[1]), (float)(twoPi / WavePeriod[2])));
        _gpuWaterShader.SetVector3("uWA", WaveAmp);

        foreach ((QuadNode node, Vector3D<float> rel) in _gpuWaterFrame)
        {
            int layer = _tileCache!.TryGetLayer(node.TileId, _renderFrame);
            if (layer < 0 || node.BaseVao == 0) continue; // tile evicted since the opaque draw — skip
            Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
            _gpuWaterShader.SetMatrix("uModel", model);
            _gpuWaterShader.SetMatrix("uMVP", model * viewProj);
            _gpuWaterShader.SetFloat("uMorph", node.DrawMorph);
            _gpuWaterShader.SetFloat("uWaterDrop", (float)(node.WorldSize * 0.0004 + 0.3));
            (int tox, int toy) = _tileCache.TileOrigin(layer);
            _gpuWaterShader.SetVector2("uTileOrigin", new Vector2D<float>(tox, toy));
            _gl.BindVertexArray(node.BaseVao);
            unsafe { _gl.DrawElements(PrimitiveType.Triangles, node.BaseIndexCount, DrawElementsType.UnsignedInt, null); }
            _gl.BindVertexArray(0);
        }
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>Cache the leaf tile under the vehicle (read back from the GPU, in the render pass where
    /// the context is current and the tile is fresh) so <see cref="TrySurfaceHeight"/> can sample the exact
    /// drawn surface. Re-reads only on a leaf change; the morph is refreshed every frame.</summary>
    private void UpdateRoverLeaf(UniversePosition focus)
    {
        DirToFaceUV(Vector3D.Normalize(focus.DeltaMeters(_body!.CurrentPosition)), out int face, out double u, out double v);
        if (FindDrawnLeaf(face, u, v) is not { } leaf) return;
        int slot = _tileCache!.TryGetLayer(leaf.TileId, _renderFrame);
        if (slot < 0) return;

        _roverLeafFace = leaf.Face;
        _rlU0 = leaf.U0; _rlV0 = leaf.V0; _rlU1 = leaf.U1; _rlV1 = leaf.V1;
        _roverLeafMorph = leaf.DrawMorph;
        if (leaf.TileId != _roverLeafTileId)
        {
            int t = _tileCache.TileSize;
            if (_roverLeafBuf == null || _roverLeafBuf.Length != t * t * 4) _roverLeafBuf = new float[t * t * 4];
            _tileCache.ReadTile(slot, _roverLeafBuf);
            _roverLeafTileId = leaf.TileId;
        }
    }

    /// <summary>Standard split/merge/cull state for a node (shared by the ensure and draw passes so they
    /// make identical decisions).</summary>
    private void NodeState(QuadNode node, Camera camera, out Vector3D<float> rel, out bool visible,
        out double dist, out double splitDist)
    {
        UniversePosition center = _body!.CurrentPosition.Translated(node.CenterLocal);
        rel = center.ToCameraRelative(camera.Position);
        float boundR = (float)(node.WorldSize * 0.75 + _terrain!.Amplitude + 50.0);
        visible = IsNodeVisible(node, rel, boundR, camera);
        dist = camera.Position.DistanceTo(center);
        if (FocusPoint is { } fp) dist = Math.Min(dist, fp.DistanceTo(center));
        splitDist = _lodFactor * node.WorldSize;
    }

    /// <summary>Pass 1: decide split/merge and generate the tiles the draw pass will need (no drawing).</summary>
    private void EnsureSubtreeGpu(QuadNode node, Camera camera)
    {
        NodeState(node, camera, out _, out bool visible, out double dist, out double splitDist);
        bool wantSplit = visible && node.Level < MaxLevel && dist < splitDist;

        if (wantSplit)
        {
            node.Children ??= Split(node);
            bool allReady = true;
            foreach (QuadNode c in node.Children)
                if (!EnsureDrawableGpu(c, force: false)) allReady = false;
            if (allReady)
            {
                foreach (QuadNode c in node.Children) EnsureSubtreeGpu(c, camera);
                return;
            }
        }
        else if (node.Children != null && dist > splitDist * MergeHysteresis)
        {
            DisposeChildren(node);
        }

        if (visible) EnsureDrawableGpu(node, force: false); // the stand-in the draw pass will use
    }

    /// <summary>Pass 2: draw the patches the ensure pass settled on, using only resident tiles.</summary>
    private void DrawSubtreeGpu(QuadNode node, Camera camera, in Matrix4X4<float> viewProj)
    {
        NodeState(node, camera, out Vector3D<float> rel, out bool visible, out double dist, out double splitDist);
        bool wantSplit = visible && node.Level < MaxLevel && dist < splitDist;

        if (wantSplit && node.Children != null)
        {
            bool allReady = true;
            foreach (QuadNode c in node.Children)
                if (_tileCache!.TryGetLayer(c.TileId, _renderFrame) < 0 || c.BaseVao == 0) allReady = false;
            if (allReady)
            {
                foreach (QuadNode c in node.Children) DrawSubtreeGpu(c, camera, in viewProj);
                return;
            }
        }

        if (!visible) return;
        float morph = (float)Smoothstep(splitDist, 2.0 * splitDist, dist);
        RenderPatchGpu(node, rel, viewProj, morph);
        LeafCount++;
    }

    private void RenderPatchGpu(QuadNode node, Vector3D<float> rel, in Matrix4X4<float> viewProj, float morph)
    {
        int layer = _tileCache!.TryGetLayer(node.TileId, _renderFrame);
        if (layer < 0 || node.BaseVao == 0) return; // not resident this frame (budget) — parent stands in
        Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
        _gpuShader!.SetMatrix("uMVP", model * viewProj);
        _gpuShader.SetMatrix("uModel", model);
        _gpuShader.SetFloat("uMorph", morph);
        _gpuShader.SetFloat("uSkirtDepth", (float)(node.WorldSize * 0.06 + 60.0));
        _gpuShader.SetFloat("uVertexSpacing", (float)(node.WorldSize / GridN));
        _gpuShader.SetFloat("uGridN", GridN);
        _gpuShader.SetVector4("uRect", new Vector4D<float>((float)node.U0, (float)node.V0, (float)node.U1, (float)node.V1));
        _gpuShader.SetInt("uFace", node.Face);
        (int tox, int toy) = _tileCache.TileOrigin(layer);
        _gpuShader.SetVector2("uTileOrigin", new Vector2D<float>(tox, toy));
        SetPatchDetailBase(_gpuShader, node); // uPatchCellBase/uPatchFracBase/uVertexSpacingDir for the detail layer
        node.DrawMorph = morph;
        node.DrawnFrame = _renderFrame;
        _gl.BindVertexArray(node.BaseVao);
        unsafe { _gl.DrawElements(PrimitiveType.Triangles, node.BaseIndexCount, DrawElementsType.UnsignedInt, null); }
        _gl.BindVertexArray(0);

        // Ocean worlds: this drawn leaf also gets a translucent water shell (reusing its base mesh), drawn
        // in the later water pass over the opaque sea floor.
        if (_terrain!.HasOcean) _gpuWaterFrame.Add((node, rel));

        if (node.WorldSize < MinDrawnWorldSize) MinDrawnWorldSize = node.WorldSize;

        // Near the camera's ground point (tangentially)? Hand it to the vegetation pass — it instances grass
        // over this leaf's terrain vertices and samples this same tile for the exact drawn height. Direction
        // comparison is relief-independent: a patch whose surface is underfoot has patchDir ≈ camDir even when
        // its base-sphere centre is tens of km below. chord ≈ angle for the small angles grass cares about.
        if (_grassLeaves.Count < GrassMaxLeaves)
        {
            Vector3D<double> patchDir = Vector3D.Normalize(node.CenterLocal);
            double chord = (patchDir - _grassCamDir).Length;          // |Δ unit vectors|
            double tangential = chord * _terrain.Radius;              // ≈ along-surface dist to patch CENTRE
            // Subtract the patch's own tangential reach so the patch the camera stands on always qualifies
            // even when the ground point is out near its edge (patches are huge on high-relief worlds).
            double edgeDist = tangential - node.WorldSize * 0.75;
            if (edgeDist < TerrainTuning.ScatterRange)
                _grassLeaves.Add(new GrassLeaf(node.BaseVbo, rel, new Vector2D<float>(tox, toy), morph,
                    node.Face, new Vector4D<float>((float)node.U0, (float)node.V0, (float)node.U1, (float)node.V1),
                    (float)(node.WorldSize / GridN)));
        }
    }

    /// <summary>Ensure a node has its base mesh and a resident height tile so it can be drawn this frame.
    /// The base mesh is built once (cheap, no noise). The tile is (re)generated when absent — always for
    /// <paramref name="force"/> (roots), otherwise only while the per-frame generation budget lasts.
    /// Returns true if the node is drawable now.</summary>
    private bool EnsureDrawableGpu(QuadNode node, bool force)
    {
        EnsureGpuResources(); // SetBody/Rebuild can reach here before the first RenderGpu created them
        if (node.BaseVao == 0) BuildBaseMesh(node);

        int layer = _tileCache!.TryGetLayer(node.TileId, _renderFrame);
        if (layer >= 0) return true;
        if (!force && _gpuGenBudget <= 0) return false;
        if (!force) _gpuGenBudget--;

        layer = _tileCache.AllocateLayer(node.TileId, _renderFrame);
        if (layer < 0) return false; // pool full of tiles needed this frame — parent stands in this frame
        double spacing = node.WorldSize / GridN;
        _tileGen!.Generate(_tileCache, layer, node.Face, node.U0, node.V0, node.U1, node.V1,
            _terrain!.GpuParams(), _terrain, spacing, spacing * 2.0);
        return true;
    }

    /// <summary>Build the patch's base-sphere mesh (no noise): per-vertex centre-relative position
    /// <c>R·(dir−centreDir)</c> (computed in double for precision), the outward direction, the height-tile
    /// texel, and a skirt flag. Skirts hang the patch edges down to hide T-junction cracks. Uploaded to a
    /// per-node VAO/VBO/EBO; the GPU height tile supplies the relief via vertex texture fetch.</summary>
    private unsafe void BuildBaseMesh(QuadNode node)
    {
        int n = GridN;
        double radius = _terrain!.Radius;
        Vector3D<double> centerLocal = node.CenterLocal;

        const int stride = 9; // basePos(3) + dir(3) + texel(2) + skirt(1)
        int gridVerts = (n + 1) * (n + 1);
        var verts = new List<float>(gridVerts * stride * 2);

        void AddVert(Vector3D<double> dir, int ti, int tj, float skirt)
        {
            Vector3D<double> basePos = dir * radius - centerLocal;
            verts.Add((float)basePos.X); verts.Add((float)basePos.Y); verts.Add((float)basePos.Z);
            verts.Add((float)dir.X); verts.Add((float)dir.Y); verts.Add((float)dir.Z);
            verts.Add(ti); verts.Add(tj); verts.Add(skirt);
        }

        var dirGrid = new Vector3D<double>[n + 1, n + 1];
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
        {
            double u = node.U0 + (node.U1 - node.U0) * (i / (double)n);
            double v = node.V0 + (node.V1 - node.V0) * (j / (double)n);
            Vector3D<double> dir = FacePoint(node.Face, u, v);
            dirGrid[i, j] = dir;
            AddVert(dir, i + 1, j + 1, 0f); // +1: the height tile has a 1-texel guard ring
        }

        var idx = new List<uint>(n * n * 6 + n * 24);
        uint Vid(int i, int j) => (uint)(i + j * (n + 1));
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            idx.Add(Vid(i, j)); idx.Add(Vid(i + 1, j)); idx.Add(Vid(i + 1, j + 1));
            idx.Add(Vid(i, j)); idx.Add(Vid(i + 1, j + 1)); idx.Add(Vid(i, j + 1));
        }

        // Skirts: along each edge, a row of dropped duplicates connected to the edge vertices.
        void Skirt(IReadOnlyList<(int i, int j)> edge)
        {
            int start = verts.Count / stride;
            foreach ((int i, int j) in edge) AddVert(dirGrid[i, j], i + 1, j + 1, 1f); // dropped in the shader
            for (int k = 0; k < edge.Count - 1; k++)
            {
                uint e0 = Vid(edge[k].i, edge[k].j), e1 = Vid(edge[k + 1].i, edge[k + 1].j);
                uint s0 = (uint)(start + k), s1 = (uint)(start + k + 1);
                idx.Add(e0); idx.Add(e1); idx.Add(s1);
                idx.Add(e0); idx.Add(s1); idx.Add(s0);
            }
        }
        var bottom = new List<(int, int)>(); var top = new List<(int, int)>();
        var left = new List<(int, int)>(); var right = new List<(int, int)>();
        for (int i = 0; i <= n; i++) { bottom.Add((i, 0)); top.Add((i, n)); }
        for (int j = 0; j <= n; j++) { left.Add((0, j)); right.Add((n, j)); }
        Skirt(bottom); Skirt(top); Skirt(left); Skirt(right);

        float[] vArr = verts.ToArray();
        uint[] iArr = idx.ToArray();

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);
        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vArr, BufferUsageARB.StaticDraw);
        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, iArr, BufferUsageARB.StaticDraw);

        uint st = stride * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, st, (void*)0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, st, (void*)(3 * sizeof(float)));
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, st, (void*)(6 * sizeof(float)));
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, st, (void*)(8 * sizeof(float)));
        for (uint a = 0; a < 4; a++) _gl.EnableVertexAttribArray(a);
        _gl.BindVertexArray(0);

        node.BaseVao = vao; node.BaseVbo = vbo; node.BaseEbo = ebo;
        node.BaseIndexCount = (uint)iArr.Length;
    }

    private void DisposeGpuNode(QuadNode node)
    {
        if (node.BaseVao != 0)
        {
            _gl.DeleteVertexArray(node.BaseVao);
            _gl.DeleteBuffer(node.BaseVbo);
            _gl.DeleteBuffer(node.BaseEbo);
            node.BaseVao = node.BaseVbo = node.BaseEbo = 0;
        }
        _tileCache?.Release(node.TileId);
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
    private void SetPatchDetailBase(Shader shader, QuadNode node)
    {
        double freq = _detailNoiseFreq;
        Vector3D<double> c = node.CenterLocal;
        double fx = Math.Floor(c.X * freq), fy = Math.Floor(c.Y * freq), fz = Math.Floor(c.Z * freq);
        shader.SetVector3("uPatchCellBase", new Vector3D<float>(WrapCell(fx), WrapCell(fy), WrapCell(fz)));
        shader.SetVector3("uPatchFracBase", new Vector3D<float>(
            (float)(c.X * freq - fx), (float)(c.Y * freq - fy), (float)(c.Z * freq - fz)));

        // Orbital macro relief needs the patch centre (to rebuild the surface direction) and this
        // patch's vertex spacing in direction units (so the shader knows what the mesh already shows).
        // (uPatchCenter is unused by the GPU shader — setting an absent uniform is a harmless no-op.)
        shader.SetVector3("uPatchCenter", new Vector3D<float>((float)c.X, (float)c.Y, (float)c.Z));
        shader.SetFloat("uVertexSpacingDir", (float)(node.WorldSize / GridN / _terrain!.Radius));
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

    /// <summary>
    /// Terrain height (metres, signed) at a planet-local unit direction, read from the <b>exact triangle
    /// mesh that was drawn</b> there last frame — a CPU "collision mesh". It finds the leaf patch the
    /// renderer drew over this direction (stamped with the current render frame), then bilinearly samples
    /// that patch's stored per-vertex heights inside the triangle under the point, blending fine→coarse
    /// by the same geomorph factor the patch was drawn with. So a vehicle rests on precisely the surface
    /// on screen — no float, and no analytic re-derivation whose result would feed back into the LOD and
    /// make the body oscillate (drive the focus → refine/merge → height jumps → bounce). Returns false
    /// when nothing is drawn yet (no baked collision data), so the caller can fall back to the raw field.
    /// </summary>
    /// <summary>Walk the (u,v) path on a face to the deepest node drawn as a leaf this frame (ancestors are
    /// internal/recursed and unstamped; deeper nodes are speculative and weren't drawn). Null if none.</summary>
    private QuadNode? FindDrawnLeaf(int face, double u, double v)
    {
        if (_roots == null) return null;
        QuadNode? leaf = null;
        for (QuadNode node = _roots[face]; ; )
        {
            if (node.DrawnFrame == _renderFrame) leaf = node;
            if (node.Children is not { } kids) break;
            double um = (node.U0 + node.U1) * 0.5, vm = (node.V0 + node.V1) * 0.5;
            node = kids[(v >= vm ? 2 : 0) + (u >= um ? 1 : 0)];
        }
        return leaf;
    }

    /// <summary>Height (metres, signed vs base radius) of the DRAWN GPU surface at any direction — sampled at
    /// the drawn leaf's own spacing + geomorph, so it matches what's on screen (a fixed fine spacing would
    /// add octaves the coarse leaf never baked and read too high). For scattering surface props (vegetation).</summary>
    public bool DrawnSurfaceHeight(Vector3D<double> localDir, out double height)
    {
        height = 0.0;
        if (_terrain == null || _roots == null || !TerrainTuning.GpuTerrain) return false;
        DirToFaceUV(localDir, out int face, out double u, out double v);
        QuadNode leaf = FindDrawnLeaf(face, u, v) ?? _roots[face];
        // Never sample finer than the float-safe spacing: the CPU float mirror (GpuHeightAt) diverges from
        // the GPU-baked tile by up to KILOMETRES at the top octaves (which is why the rover uses tile
        // read-back, not this). The coarse cap matches the drawn surface's stable low-frequency shape — the
        // same trick FitProjection uses for the near plane — so scattered props sit near the ground instead
        // of floating on the divergent fine octaves.
        double sp = Math.Max(leaf.WorldSize / GridN, _terrain.Radius * 5e-5);
        double hf = _terrain.GpuHeightAt(localDir, sp);
        double hc = _terrain.GpuHeightAt(localDir, sp * 2.0);
        height = hf + (hc - hf) * leaf.DrawMorph;
        return true;
    }

    public bool TrySurfaceHeight(Vector3D<double> localDir, out double height)
    {
        height = 0.0;
        if (_terrain == null || _roots == null) return false;

        DirToFaceUV(localDir, out int face, out double u, out double v);

        // GPU path: sample the exact tile read back for the vehicle's leaf (UpdateRoverLeaf). All wheels
        // use this one buffer, clamped to its u,v rect, so the footprint has a single consistent source —
        // the readback is the true drawn surface (no chord/float-divergence gap). A wheel on a different
        // cube face (only at cube edges) falls back to the float mirror.
        if (TerrainTuning.GpuTerrain)
        {
            if (_roverLeafBuf == null || face != _roverLeafFace)
            {
                double sp = (FindDrawnLeaf(face, u, v) ?? _roots[face]).WorldSize / GridN;
                double hf2 = _terrain.GpuHeightAt(localDir, sp);
                double hc2 = _terrain.GpuHeightAt(localDir, sp * 2.0);
                height = hf2 + (hc2 - hf2) * _roverLeafMorph;
                return true;
            }
            const int ng = GridN;
            double gu = Math.Clamp((u - _rlU0) / (_rlU1 - _rlU0), 0.0, 1.0) * ng;
            double gv = Math.Clamp((v - _rlV0) / (_rlV1 - _rlV0), 0.0, 1.0) * ng;
            int gi = Math.Clamp((int)gu, 0, ng - 1), gj = Math.Clamp((int)gv, 0, ng - 1);
            double gfu = gu - gi, gfv = gv - gj;
            float gm = _roverLeafMorph;
            int tt = _tileCache!.TileSize;
            double Hg(int ii, int jj) { int k = ((ii + 1) + (jj + 1) * tt) * 4; return _roverLeafBuf[k] + (_roverLeafBuf[k + 1] - _roverLeafBuf[k]) * gm; }
            double b00 = Hg(gi, gj), b10 = Hg(gi + 1, gj), b11 = Hg(gi + 1, gj + 1), b01 = Hg(gi, gj + 1);
            height = gfu >= gfv
                ? b00 * (1.0 - gfu) + b10 * (gfu - gfv) + b11 * gfv
                : b00 * (1.0 - gfv) + b01 * (gfv - gfu) + b11 * gfu;
            return true;
        }

        QuadNode? leaf = FindDrawnLeaf(face, u, v);
        if (leaf?.CollHF is not { } hf || leaf.CollHC is not { } hc) return false; // CPU tile not baked yet

        const int n = GridN;
        double lu = Math.Clamp((u - leaf.U0) / (leaf.U1 - leaf.U0), 0.0, 1.0) * n;
        double lv = Math.Clamp((v - leaf.V0) / (leaf.V1 - leaf.V0), 0.0, 1.0) * n;
        int i = Math.Clamp((int)lu, 0, n - 1), j = Math.Clamp((int)lv, 0, n - 1);
        double fu = lu - i, fv = lv - j;
        float m = leaf.DrawMorph;
        // Morphed height at a grid vertex (matches the drawn position = mix(fine, coarse, morph)).
        double H(int ii, int jj) { int k = ii + jj * (n + 1); return hf[k] + (hc[k] - hf[k]) * m; }
        double h00 = H(i, j), h10 = H(i + 1, j), h11 = H(i + 1, j + 1), h01 = H(i, j + 1);
        // The cell's two triangles share the (i,j)-(i+1,j+1) diagonal (matching the baked winding).
        height = fu >= fv
            ? h00 * (1.0 - fu) + h10 * (fu - fv) + h11 * fv   // lower-right triangle
            : h00 * (1.0 - fv) + h01 * (fv - fu) + h11 * fu;  // upper-left triangle
        return true;
    }

    private static bool AllChildrenReady(QuadNode[] children)
    {
        foreach (QuadNode c in children) if (c.Patch == null) return false;
        return true;
    }

    /// <summary>Cube face + (u,v)∈[0,1] for a direction — the inverse of <see cref="FacePoint"/>.</summary>
    private static void DirToFaceUV(Vector3D<double> d, out int face, out double u, out double v)
    {
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        double a, b;
        if (ax >= ay && ax >= az)
        {
            if (d.X > 0) { face = 0; a = -d.Z / d.X; b = d.Y / d.X; }       // (1, b, -a)
            else { face = 1; a = -d.Z / d.X; b = -d.Y / d.X; }             // (-1, b, a)
        }
        else if (ay >= ax && ay >= az)
        {
            if (d.Y > 0) { face = 2; a = d.X / d.Y; b = -d.Z / d.Y; }       // (a, 1, -b)
            else { face = 3; a = -d.X / d.Y; b = -d.Z / d.Y; }             // (a, -1, b)
        }
        else
        {
            if (d.Z > 0) { face = 4; a = d.X / d.Z; b = d.Y / d.Z; }        // (a, b, 1)
            else { face = 5; a = d.X / d.Z; b = -d.Y / d.Z; }              // (-a, b, -1)
        }
        u = Math.Clamp((a + 1.0) * 0.5, 0.0, 1.0);
        v = Math.Clamp((b + 1.0) * 0.5, 0.0, 1.0);
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
            TileId = _nextTileId++, // unique key into the GPU tile cache (GPU path only)
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
        (float[] land, float[] water, float[] collHF, float[] collHC) = BuildPatchVertices(node, _terrain!);
        node.CollHF = collHF;
        node.CollHC = collHC;
        PatchCount++;
        return new TerrainPatch(_gl, land, water);
    }

    // Static + terrain-by-parameter: this runs on worker threads, so it must touch no mutable renderer
    // state. PlanetTerrain.HeightAt and Noise are immutable pure reads, safe to call from many threads.
    private static (float[] land, float[] water, float[] collHF, float[] collHC) BuildPatchVertices(QuadNode node, PlanetTerrain terrain)
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
        var hcg = new double[n + 1, n + 1]; // coarse (parent-resolution) height — kept for collision morph
        var crat = new double[n + 1, n + 1]; // fine crater value, reused for albedo (no second crater eval)
        for (int gj = -1; gj <= n + 1; gj++)
        for (int gi = -1; gi <= n + 1; gi++)
        {
            double u = node.U0 + (node.U1 - node.U0) * (gi / (double)n);
            double v = node.V0 + (node.V1 - node.V0) * (gj / (double)n);
            Vector3D<double> d = FacePoint(node.Face, u, v);
            // One pass yields both band-limits (fine + parent-coarse) AND the crater value for colour.
            terrain.HeightAt2(d, spacing, coarseSpacing, out double hF, out double hC, out double craterF);
            Vector3D<double> pF = d * (radius + hF);
            Vector3D<double> pC = d * (radius + hC);
            exF[gi + 1, gj + 1] = pF;
            exC[gi + 1, gj + 1] = pC;
            if (gi >= 0 && gi <= n && gj >= 0 && gj <= n)
            {
                dir[gi, gj] = d;
                posF[gi, gj] = pF;
                posC[gi, gj] = pC;
                hgt[gi, gj] = hF;
                hcg[gi, gj] = hC;
                crat[gi, gj] = craterF;
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
            col[i, j] = terrain.ColorAt(dir[i, j], hgt[i, j], slope, spacing, crat[i, j]);
            rel[i, j] = (float)terrain.MacroReliefMask(dir[i, j]);
        }

        // The vertex count is fixed: n×n cells × 2 triangles × 3 verts, plus 4n skirt quads × 6 verts.
        // Writing straight into one exactly-sized array (no List growth, no ToArray copy) roughly halves
        // this patch's transient allocation — across hundreds of bakes that's a real cut in GC churn.
        int landVerts = n * n * 6 + 24 * n;
        var v3 = new float[landVerts * LandFloatsPerVertex];
        int w = 0;
        void Emit(Vector3D<double> pF, Vector3D<double> pC, Vector3D<double> nF, Vector3D<double> nC, Vector3D<float> c, float r)
        {
            v3[w++] = (float)(pF.X - centerLocal.X); v3[w++] = (float)(pF.Y - centerLocal.Y); v3[w++] = (float)(pF.Z - centerLocal.Z);
            v3[w++] = (float)(pC.X - centerLocal.X); v3[w++] = (float)(pC.Y - centerLocal.Y); v3[w++] = (float)(pC.Z - centerLocal.Z);
            v3[w++] = (float)nF.X; v3[w++] = (float)nF.Y; v3[w++] = (float)nF.Z;
            v3[w++] = (float)nC.X; v3[w++] = (float)nC.Y; v3[w++] = (float)nC.Z;
            v3[w++] = c.X; v3[w++] = c.Y; v3[w++] = c.Z;
            v3[w++] = r;
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

        // Flatten the fine/coarse height grids (row-major, i + j*(n+1)) for the collision query.
        var collHF = new float[(n + 1) * (n + 1)];
        var collHC = new float[(n + 1) * (n + 1)];
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
        {
            collHF[i + j * (n + 1)] = (float)hgt[i, j];
            collHC[i + j * (n + 1)] = (float)hcg[i, j];
        }

        return (v3, BuildWaterVertices(node, terrain, dir, hgt, centerLocal, radius), collHF, collHC);
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
        // Altitude above the LOCAL terrain under the camera. The GPU path samples the GPU terrain's own
        // height (a different hash than HeightAt), so the near plane fits the surface actually drawn —
        // basing it on the CPU HeightAt instead either clips close terrain or wrecks the depth precision.
        Vector3D<double> camDir = camera.Position.DeltaMeters(_body.CurrentPosition);
        double surfaceR = _terrain!.Radius;
        if (camDir.LengthSquared > 0)
        {
            Vector3D<double> nadir = Vector3D.Normalize(camDir);
            if (TerrainTuning.GpuTerrain)
                // Sample at a coarse reference spacing so the CPU mirror only evaluates the low-frequency
                // octaves where float (GPU) and double (CPU) agree — the high octaves diverge at the float
                // precision edge and would mis-size the near plane. Matches the GPU's stable surface.
                surfaceR += _terrain.GpuHeightAt(nadir, _terrain.Radius * 5e-5);
            else
                surfaceR += _terrain.HeightAt(nadir);
        }
        double alt = Math.Max(1.0, camToCenter - surfaceR);

        double horizon = Math.Sqrt(alt * alt + 2 * _terrain.Radius * alt);
        double far = horizon * 1.25 + 5000.0;
        // alt × 0.25 assumes the nearest geometry is straight down; but looking toward the horizon, the
        // foreground terrain rises much closer than `alt`, so a quarter-altitude near plane slices through
        // it. The GPU path uses a far smaller fraction (the far plane is the true horizon, so the depth
        // precision is still fine), keeping the close terrain inside the near plane.
        double nearFactor = TerrainTuning.GpuTerrain ? 0.04 : 0.25;
        double near = Math.Clamp(alt * nearFactor, 0.2, far * 0.5);
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
        if (node.BaseVao != 0 || _tileCache != null) DisposeGpuNode(node); // GPU-path resources, if any
    }

    private void DisposeTree()
    {
        // New epoch: in-flight bakes for the outgoing tree are now stale and will be discarded.
        Interlocked.Increment(ref _epoch);
        if (_roots == null) return;
        foreach (QuadNode r in _roots) DisposeNode(r);
        _roots = null;
        PatchCount = 0;
        _tileCache?.Clear(); // every GPU layer is now free (nodes released above)
    }

    public void Dispose()
    {
        _jobQueue.CompleteAdding();
        try { Task.WaitAll(_workers); } catch { /* a faulted worker must not block teardown */ }
        _jobQueue.Dispose();
        DisposeTree();
        _shader.Dispose();
        _waterShader.Dispose();
        _tileCache?.Dispose();
        _tileGen?.Dispose();
        _gpuShader?.Dispose();
        _gpuWaterShader?.Dispose();
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

        // CPU-side collision mesh: the per-vertex fine and coarse terrain heights (signed, metres above
        // the base radius) on this patch's (GridN+1)² grid — the same numbers baked into the GPU buffer.
        // A vehicle samples the triangle under it from these and blends by DrawMorph, so it rests on the
        // exact surface drawn (no analytic re-derivation that would feed back into the LOD and bounce).
        public float[]? CollHF, CollHC;
        public float DrawMorph;  // geomorph factor this patch was last drawn with (fine→coarse blend)
        public int DrawnFrame = -1; // render-frame index this node was last drawn as a leaf

        // GPU tile path: a unique cache key + the per-node base-sphere mesh (height comes from the tile).
        public long TileId;
        public uint BaseVao, BaseVbo, BaseEbo, BaseIndexCount;
    }

    // Geometry-only inputs handed to a worker (immutable once created), and the arrays it returns: the
    // GPU vertex floats plus the per-vertex collision heights (fine/coarse) for the vehicle mesh query.
    private readonly record struct BuildJob(int Epoch, QuadNode Node, PlanetTerrain Terrain);
    private readonly record struct BuildResult(int Epoch, QuadNode Node, float[] Land, float[] Water, float[] CollHF, float[] CollHC);

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
