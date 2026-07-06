using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders a planet's procedural terrain into a <see cref="TerrainTileCache"/> layer on the GPU — the
/// generation half of the SpaceEngine-style tile path. A fullscreen fragment pass evaluates the noise
/// stack per texel and writes <c>RG = (fineHeight, coarseHeight)</c> (metres above the base radius), the
/// two band-limits the terrain shader blends for geomorphing.
///
/// <para><b>Precision.</b> The surface direction is computed <i>exactly</i> in the shader
/// (<c>facePoint</c>), so adjacent tiles agree on their shared edge (no seams), and the noise is sampled
/// at <c>dir·frequency</c> directly in <c>float</c>. To keep that precise, the CPU clamps each layer's
/// octave count so its top frequency stays under <see cref="FloatSafeFreq"/> cells — beyond that float
/// loses the fractional cell and the lattice would shimmer. Geometry therefore resolves to ~metre-scale
/// features; finer roughness comes from the per-pixel detail shader (the same one the CPU path uses).
/// This is the heightmap-plus-detail-texture split SpaceEngine uses.</para>
///
/// <para><b>Scope.</b> Continents (fBm) + ridged mountains on the highland mask + domain warp + regional
/// ruggedness + eroded detail (slope-damped fBm) + impact craters + volcanoes + micro-relief + strata +
/// aeolian dunes (desert) + fracture lineae (ice). The GPU uses its own GLSL hash, so the look has the
/// same character as the CPU terrain rather than matching it bit-for-bit.</para>
/// </summary>
public sealed class TerrainTileGenerator : IDisposable
{
    /// <summary>Highest noise frequency (cells over the unit sphere) that stays precise in float: beyond
    /// ~2^23 / 10 the fractional cell position is lost. Octaves past this are clamped off (and covered by
    /// the per-pixel detail shader instead).</summary>
    public const double FloatSafeFreq = 700_000.0;

    /// <summary>Ceiling for the DETAIL layer only, which samples in split (patch-local) coordinates and so
    /// stays precise far past <see cref="FloatSafeFreq"/> — this is what lets fine roughness be real baked
    /// geometry down to sub-metre features instead of a fragment normal-bump. Still bounded so the eroded
    /// octave loop and the physics mirror stay affordable; kept in sync with PlanetTerrain.DetailSafeFreq.</summary>
    public const double DetailSafeFreq = 32_000_000.0;

    /// <summary>
    /// The terrain noise/field GLSL — hash, value noise, fBm, ridged multifractal, regional ruggedness,
    /// domain warp, and the LOD octave count — exposed as a SHARED module so the render shader can evaluate
    /// the EXACT field this generator bakes (the per-pixel orbital macro-relief that matches the real
    /// mountains). The functions take an explicit <c>seed</c> (vec3) instead of reading a uniform, so they
    /// drop into any shader; <c>tf</c>-prefixed to avoid clashing with a host shader's own helpers. The math
    /// is identical to the inline functions in <see cref="FragmentSource"/> and the C# mirror in
    /// <c>PlanetTerrain.GpuHeightAt</c> — <b>keep all three in sync</b>.
    /// </summary>
    public const string FieldGlsl = @"
float tfHash(vec3 c, vec3 seed) {
    c = mod(c, 8192.0) + seed;
    c = fract(c * 0.1031);
    c += dot(c, c.yzx + 33.33);
    return fract((c.x + c.y) * c.z);
}
float tfVnoise(vec3 p, vec3 seed) {
    vec3 c = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = tfHash(c, seed),               n100 = tfHash(c + vec3(1,0,0), seed);
    float n010 = tfHash(c + vec3(0,1,0), seed), n110 = tfHash(c + vec3(1,1,0), seed);
    float n001 = tfHash(c + vec3(0,0,1), seed), n101 = tfHash(c + vec3(1,0,1), seed);
    float n011 = tfHash(c + vec3(0,1,1), seed), n111 = tfHash(c + vec3(1,1,1), seed);
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z) * 2.0 - 1.0;
}
float tfFbm(vec3 dir, float freq, float oct, float gain, vec3 seed) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, f = freq, norm = 0.0;
    for (int i = 0; i < 32; i++) {
        if (i >= full) break;
        sum += amp * tfVnoise(dir * f, seed); norm += amp; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { sum += amp * frac * tfVnoise(dir * f, seed); norm += amp * frac; }
    return norm > 0.0 ? sum / norm : 0.0;
}
float tfRidged(vec3 dir, float freq, float oct, float gain, vec3 seed) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 0.5, f = freq, prev = 1.0, norm = 0.0;
    for (int i = 0; i < 32; i++) {
        if (i >= full) break;
        float n = 1.0 - abs(tfVnoise(dir * f, seed)); n *= n; n *= prev;
        sum += n * amp; norm += amp; prev = n; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { float n = 1.0 - abs(tfVnoise(dir * f, seed)); n *= n; n *= prev; sum += n * amp * frac; norm += amp * frac; }
    return norm > 0.0 ? clamp(sum / norm, 0.0, 1.0) : 0.0;
}
float tfRuggedness(vec3 dir, float rfreq, float rlo, float rhi, vec3 seed) {
    float r = tfFbm(dir + vec3(53.1, 12.7, 91.3), rfreq, 4.0, 0.5, seed);
    return smoothstep(rlo, rhi, 0.5 + 0.5 * r);
}
vec3 tfDomainWarp(vec3 dir, float wfreq, float wstr, vec3 seed) {
    float wx = tfFbm(dir, wfreq, 3.0, 0.5, seed);
    float wy = tfFbm(dir + vec3(31.4, 11.7, 5.2), wfreq, 3.0, 0.5, seed);
    float wz = tfFbm(dir + vec3(-7.1, 23.9, 17.3), wfreq, 3.0, 0.5, seed);
    return vec3(wx, wy, wz) * wstr;
}
// Fractional fBm/ridged octave count a vertex spacing resolves, clamped to the layer budget AND the
// float-safe ceiling — mirrors PlanetTerrain.OctavesFor + TerrainTileGenerator.OctClamp.
float tfOctFor(float baseFreq, float spacingM, float maxOct, float radius) {
    if (spacingM <= 0.0) return maxOct;
    float maxFreq = 3.14159265 * radius / spacingM;
    float lod = (maxFreq <= baseFreq) ? 1.0 : clamp(log2(maxFreq / baseFreq) + 1.0, 1.0, maxOct);
    float safe = floor(log2(700000.0 / max(1.0, baseFreq))) + 1.0;
    return max(0.0, min(lod, safe));
}
// Impact-crater cascade — same field as the generator's craterField, returning BOTH the value (.w, in
// ≈[-1, rim]) and its analytic gradient w.r.t. dir (.xyz) in a SINGLE 3×3×3 pass. The orbital relief needs
// the gradient for its normal; computing it analytically here (each bowl/rim is an analytic function of the
// distance to its crater centre) avoids the 3-4 extra full-cascade taps a finite difference would cost —
// the cascade is the dominant per-pixel cost, so one pass instead of four is the difference between
// interactive and a slideshow. The min/max combiner gives a continuous value with mild gradient kinks where
// the dominant crater switches — acceptable for a lighting hint.
vec4 tfCraterFieldN(vec3 dir, float baseFreq, float octCount, float density, vec3 seed) {
    if (octCount <= 0.0) return vec4(0.0);
    const float wnorm = 2.6094;
    float sumV = 0.0; vec3 sumG = vec3(0.0);
    float freq = baseFreq, weight = 1.0;
    for (int o = 0; o < 10; o++) {
        float ofade = clamp(octCount - float(o), 0.0, 1.0);
        if (ofade > 0.0) {
            vec3 p = dir * freq;
            vec3 ip = floor(p);
            float salt = float(o) * 17.0;
            float minBowl = 0.0, maxRim = 0.0;
            vec3 minBowlG = vec3(0.0), maxRimG = vec3(0.0);  // gradients w.r.t. p
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) {
                vec3 c = ip + vec3(float(dx), float(dy), float(dz));
                float ex = tfHash(c + vec3(salt), seed);
                if (ex > density) continue;
                vec3 jit = vec3(tfHash(c + vec3(salt + 1.7), seed), tfHash(c + vec3(salt + 9.1), seed), tfHash(c + vec3(salt + 4.3), seed));
                float radius = 0.22 + 0.28 * fract(ex * 7.3 + 0.19);
                vec3 d = p - (c + jit);
                float dist = length(d);
                float t = dist / radius;
                if (t >= 1.5) continue;
                vec3 dtdp = d / (dist * radius + 1e-9);           // dt/dp
                float bowl = 0.0, dBowl = 0.0;                    // bowl(t) and d(bowl)/dt
                if (t < 0.85) { float u = t / 0.85; bowl = u * u * (3.0 - 2.0 * u) - 1.0; dBowl = 6.0 * u * (1.0 - u) / 0.85; }
                float e = (t - 0.95) / 0.12;
                float rim = 0.28 * exp(-0.5 * e * e);
                float dRim = rim * (-e) / 0.12;                   // d(rim)/dt
                if (bowl < minBowl) { minBowl = bowl; minBowlG = dBowl * dtdp; }
                if (rim > maxRim)   { maxRim = rim;   maxRimG = dRim * dtdp; }
            }
            sumV += weight * ofade * (minBowl + maxRim);
            sumG += weight * ofade * (minBowlG + maxRimG) * freq; // d/d(dir) = d/dp · (dp/ddir = freq)
        }
        freq *= 1.9; weight *= 0.62;
    }
    return vec4(sumG / wnorm, sumV / wnorm);
}
// Volcano cones (lava worlds) — identical to the generator's volcanoField. .x = height [0,1], .y = vent mask
// (1 at the caldera floor → 0 at the rim) the render shader turns into glowing summit lava.
vec2 tfVolcano(vec3 dir, float freq, float density, vec3 seed) {
    vec3 p = dir * freq;
    vec3 ip = floor(p);
    float h = 0.0, vent = 0.0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++) {
        vec3 c = ip + vec3(float(dx), float(dy), float(dz));
        float ex = tfHash(c + vec3(7.0), seed);
        if (ex > density) continue;
        vec3 jit = vec3(tfHash(c + vec3(3.1), seed), tfHash(c + vec3(8.7), seed), tfHash(c + vec3(1.9), seed));
        float radius = 0.45 + 0.25 * fract(ex * 5.0 + 0.3);
        float t = length(p - (c + jit)) / radius;
        if (t >= 1.0) continue;
        float rimT = 0.30;
        float flank = smoothstep(1.0, rimT, t);
        float cone = (t < rimT) ? mix(0.55, 1.0, smoothstep(0.0, rimT, t)) : flank;
        float ch = cone * (0.7 + 0.6 * fract(ex * 11.0));
        if (ch > h) { h = ch; vent = (t < rimT) ? (1.0 - smoothstep(0.0, rimT, t)) : 0.0; }
    }
    return vec2(h, vent);
}
";

    private const string VertexSource = @"#version 410 core
out vec2 vUV;
void main() {
    // Attributeless fullscreen triangle: ids 0,1,2 → (0,0),(2,0),(0,2) in UV, covering the viewport.
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vUV;
uniform int uFace;
uniform vec4 uRect;        // (u0, v0, u1, v1) of this tile on the cube face
uniform vec3 uSeed;        // per-planet hash offset, so worlds differ
uniform vec3 uFreq;        // (continent, mountain, detail) base frequencies
uniform vec3 uWeight;      // (continent, mountain, detail) layer weights
uniform vec3 uGain;        // (continent, mountain, detail) per-octave gains
uniform float uScale;      // metres of relief (height = scale * shape)
uniform vec3 uOctFine;     // (continent, mountain, detail) octave counts at the fine band-limit
uniform vec3 uOctCoarse;   // ... at the parent (coarse) band-limit
uniform float uTexelN;     // texels per tile edge — snap each texel to its mesh-vertex (u,v) so seams match
// Split-coordinate base for the DETAIL layer: the patch-centre's (wrapped, integer) noise cell + fraction,
// plus the patch-centre direction, so the detail fBm can be sampled in small patch-local coordinates and
// resolve sub-metre geometry without the float precision loss of a planet-scale dir*freq (see vnoiseDSplit).
uniform vec3 uDetCellBase; // fract-free integer cell of (patchCentreDir * detailFreq), wrapped to 8192
uniform vec3 uDetFracBase; // its fractional part
uniform vec3 uDetDirC;     // patch-centre unit direction (to rebuild dir - dirC locally)
uniform vec3 uMicroCellBase, uMicroFracBase, uMicroDirC; // same split base for the fine micro-relief layer
uniform float uWarpFreq, uWarpStrength;            // domain warp (bends the mountain ranges)
uniform float uRuggedFreq, uRuggedLo, uRuggedHi;   // regional ruggedness mask: flat plains vs rugged highlands
uniform float uDetailFloor;                        // min detail roughness in the flattest regions
uniform float uCraterWeight, uCraterDensity, uCraterFreq; // impact craters (0 weight = none)
uniform float uCraterOctFine, uCraterOctCoarse;    // crater size classes resolved at each band-limit
uniform float uVolcanoWeight, uVolcanoFreq, uVolcanoDensity; // raised volcano cones (lava worlds; 0 = none)
uniform float uMicroWeight, uMicroFreq, uMicroGain;         // fine micro-relief (LOD-gated; 0 = none)
uniform float uMicroOctFine, uMicroOctCoarse, uMicroGateFine, uMicroGateCoarse; // micro octave count + LOD gate
uniform float uStrataWeight, uStrataFreq, uStrataSteps, uStrataSharp; // sedimentary terracing (mesas/canyons)
uniform float uDuneWeight, uDuneFreq, uDuneWarpFreq, uDuneWarpAmp, uErgFreq; // aeolian dunes (0 weight = none)
uniform vec3 uDuneDir;                                      // prevailing-wind axis
uniform float uDuneGateFine, uDuneGateCoarse;               // dune LOD gates per band-limit
uniform float uCrackWeight, uCrackFreq;                     // ice fracture lineae (0 weight = none)
uniform float uCrackOctFine, uCrackOctCoarse, uCrackGateFine, uCrackGateCoarse; // lineae octaves + LOD gates
layout(location = 0) out vec4 oHeight;

const int MaxOct = 32;

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

// Small-input value hash; cells are wrapped to a large period so the float math stays precise and the
// per-planet uSeed offset varies the field between worlds.
float hash(vec3 c) {
    c = mod(c, 8192.0) + uSeed;
    c = fract(c * 0.1031);
    c += dot(c, c.yzx + 33.33);
    return fract((c.x + c.y) * c.z);
}

float vnoise(vec3 p) {
    vec3 c = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash(c),               n100 = hash(c + vec3(1,0,0));
    float n010 = hash(c + vec3(0,1,0)), n110 = hash(c + vec3(1,1,0));
    float n001 = hash(c + vec3(0,0,1)), n101 = hash(c + vec3(1,0,1));
    float n011 = hash(c + vec3(0,1,1)), n111 = hash(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z) * 2.0 - 1.0; // [-1, 1]
}

// Fractional-octave fBm in ~[-1,1] (the top octave fades in by its fraction, matching the CPU path).
float fbm(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, f = freq, norm = 0.0;
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        sum += amp * vnoise(dir * f); norm += amp; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { sum += amp * frac * vnoise(dir * f); norm += amp * frac; }
    return norm > 0.0 ? sum / norm : 0.0;
}

// Value noise in [-1,1] (.x) plus its analytic gradient w.r.t. the input (.yzw), for the erosion damping.
// Mirrors PlanetTerrain Noise.ValueD: trilinear of the 8 corner hashes, gradient via the smoothstep
// derivative (6t(1-t)). The value is scaled to [-1,1], so the gradient carries the matching factor 2.
vec4 vnoiseD(vec3 p) {
    vec3 c = floor(p), ff = fract(p);
    vec3 u = ff * ff * (3.0 - 2.0 * ff);
    vec3 du = 6.0 * ff * (1.0 - ff);
    float n000 = hash(c),               n100 = hash(c + vec3(1,0,0));
    float n010 = hash(c + vec3(0,1,0)), n110 = hash(c + vec3(1,1,0));
    float n001 = hash(c + vec3(0,0,1)), n101 = hash(c + vec3(1,0,1));
    float n011 = hash(c + vec3(0,1,1)), n111 = hash(c + vec3(1,1,1));
    float x00 = mix(n000, n100, u.x), x10 = mix(n010, n110, u.x);
    float x01 = mix(n001, n101, u.x), x11 = mix(n011, n111, u.x);
    float y0 = mix(x00, x10, u.y), y1 = mix(x01, x11, u.y);
    float val = mix(y0, y1, u.z);
    float dfu = mix(mix(n100 - n000, n110 - n010, u.y), mix(n101 - n001, n111 - n011, u.y), u.z);
    float dfv = mix(x10 - x00, x11 - x01, u.z);
    float dfw = y1 - y0;
    return vec4(val * 2.0 - 1.0, 2.0 * vec3(dfu * du.x, dfv * du.y, dfw * du.z));
}

// Erosive fBm in ~[-1,1]: ordinary fBm, but each octave is damped by 1/(1 + k·|Σgrad|²) — so detail is
// suppressed where the accumulated slope is already steep, carving smooth valley floors with roughness
// riding the shoulders/ridges (a cheap erosion model). Mirrors PlanetTerrain Noise.ErodedFbm (k = 1.4).
float erodedFbm(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    const float k = 1.4;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, f = freq, norm = 0.0;
    vec3 gradSum = vec3(0.0);
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        vec4 ng = vnoiseD(dir * f);
        gradSum += ng.yzw;
        float damp = 1.0 / (1.0 + k * dot(gradSum, gradSum));
        sum += amp * ng.x * damp; norm += amp; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) {
        vec4 ng = vnoiseD(dir * f);
        gradSum += ng.yzw * frac;
        float damp = 1.0 / (1.0 + k * dot(gradSum, gradSum));
        sum += amp * frac * ng.x * damp; norm += amp * frac;
    }
    return norm > 0.0 ? sum / norm : 0.0;
}

// --- Split-coordinate detail: sub-metre geometry without float precision loss --------------------------
// The value noise above samples dir*freq directly; at the frequencies fine geometry needs (~1e6+ cells over
// the sphere) that product overflows float's mantissa and the lattice shimmers, which is why the octave
// count is clamped at FloatSafeFreq (~metre geometry, finer faked by the normal-bump shader). These split
// versions carry the (large, integer) cell base separately from a small fractional coordinate — the same
// trick the detail-normal shader uses — so the fraction stays precise however deep we subdivide. Value +
// analytic gradient (for the erosion damping) from the 8 corner hashes; mirrors vnoiseD.
vec4 vnoiseDSplit(vec3 cellBase, vec3 nc) {
    vec3 fl = floor(nc);
    vec3 c = cellBase + fl;
    vec3 ff = nc - fl;
    vec3 u = ff * ff * (3.0 - 2.0 * ff);
    vec3 du = 6.0 * ff * (1.0 - ff);
    float n000 = hash(c),               n100 = hash(c + vec3(1,0,0));
    float n010 = hash(c + vec3(0,1,0)), n110 = hash(c + vec3(1,1,0));
    float n001 = hash(c + vec3(0,0,1)), n101 = hash(c + vec3(1,0,1));
    float n011 = hash(c + vec3(0,1,1)), n111 = hash(c + vec3(1,1,1));
    float x00 = mix(n000, n100, u.x), x10 = mix(n010, n110, u.x);
    float x01 = mix(n001, n101, u.x), x11 = mix(n011, n111, u.x);
    float y0 = mix(x00, x10, u.y), y1 = mix(x01, x11, u.y);
    float val = mix(y0, y1, u.z);
    float dfu = mix(mix(n100 - n000, n110 - n010, u.y), mix(n101 - n001, n111 - n011, u.y), u.z);
    float dfv = mix(x10 - x00, x11 - x01, u.z);
    float dfw = y1 - y0;
    return vec4(val * 2.0 - 1.0, 2.0 * vec3(dfu * du.x, dfv * du.y, dfw * du.z));
}
// erodedFbm on split coordinates. Each octave doubles the wrapped cell base (mod 8192 — kept small so it
// stays integer-precise) and the small fractional coord, reconstructing the identical lattice fbm() would
// sample, but precise for the many octaves sub-metre geometry needs. ncBase is (dir*detailFreq) expressed
// relative to the patch centre; matches erodedFbm() in exact arithmetic (differs only by float ULP).
float erodedFbmSplit(vec3 cellBase, vec3 ncBase, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    const float k = 1.4;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, norm = 0.0;
    vec3 cb = cellBase, nc = ncBase, gradSum = vec3(0.0);
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        vec4 ng = vnoiseDSplit(cb, nc);
        gradSum += ng.yzw;
        float damp = 1.0 / (1.0 + k * dot(gradSum, gradSum));
        sum += amp * ng.x * damp; norm += amp;
        amp *= gain; cb = mod(cb * 2.0, 8192.0); nc *= 2.0;
    }
    if (frac > 0.0) {
        vec4 ng = vnoiseDSplit(cb, nc);
        gradSum += ng.yzw * frac;
        float damp = 1.0 / (1.0 + k * dot(gradSum, gradSum));
        sum += amp * frac * ng.x * damp; norm += amp * frac;
    }
    return norm > 0.0 ? sum / norm : 0.0;
}
// Plain (gradient-free) split-coordinate value noise + fBm for the fine micro-relief layer — same precision
// trick as vnoiseDSplit/erodedFbmSplit but cheaper (no erosion damping). Lets micro-relief carry clean
// sub-decimetre grain instead of shimmering at its top octaves (which sat right at the float wall).
float vnoiseSplit(vec3 cellBase, vec3 nc) {
    vec3 c = cellBase + floor(nc);
    vec3 f = fract(nc);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash(c),               n100 = hash(c + vec3(1,0,0));
    float n010 = hash(c + vec3(0,1,0)), n110 = hash(c + vec3(1,1,0));
    float n001 = hash(c + vec3(0,0,1)), n101 = hash(c + vec3(1,0,1));
    float n011 = hash(c + vec3(0,1,1)), n111 = hash(c + vec3(1,1,1));
    float x00 = mix(n000, n100, f.x), x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x), x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z) * 2.0 - 1.0;
}
float fbmSplit(vec3 cellBase, vec3 ncBase, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 1.0, norm = 0.0;
    vec3 cb = cellBase, nc = ncBase;
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        sum += amp * vnoiseSplit(cb, nc); norm += amp;
        amp *= gain; cb = mod(cb * 2.0, 8192.0); nc *= 2.0;
    }
    if (frac > 0.0) { sum += amp * frac * vnoiseSplit(cb, nc); norm += amp * frac; }
    return norm > 0.0 ? sum / norm : 0.0;
}

// Fractional-octave ridged multifractal in [0,1] (creases at zero crossings, detail riding ridges).
float ridged(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 0.5, f = freq, prev = 1.0, norm = 0.0;
    for (int i = 0; i < MaxOct; i++) {
        if (i >= full) break;
        float n = 1.0 - abs(vnoise(dir * f)); n *= n; n *= prev;
        sum += n * amp; norm += amp; prev = n; amp *= gain; f *= 2.0;
    }
    if (frac > 0.0) { float n = 1.0 - abs(vnoise(dir * f)); n *= n; n *= prev; sum += n * amp * frac; norm += amp * frac; }
    return norm > 0.0 ? clamp(sum / norm, 0.0, 1.0) : 0.0;
}

// Regional ruggedness in [0,1]: 0 = flat plains here, 1 = rugged highlands (low-frequency, fixed octaves).
float ruggedness(vec3 dir) {
    float r = fbm(dir + vec3(53.1, 12.7, 91.3), uRuggedFreq, 4.0, 0.5);
    return smoothstep(uRuggedLo, uRuggedHi, 0.5 + 0.5 * r);
}
// A low-frequency noise offset that bends mountain ranges organically (fixed octaves).
vec3 domainWarp(vec3 dir) {
    float wx = fbm(dir, uWarpFreq, 3.0, 0.5);
    float wy = fbm(dir + vec3(31.4, 11.7, 5.2), uWarpFreq, 3.0, 0.5);
    float wz = fbm(dir + vec3(-7.1, 23.9, 17.3), uWarpFreq, 3.0, 0.5);
    return vec3(wx, wy, wz) * uWarpStrength;
}

// Impact-crater cascade in ≈[-1, rim]: 10 size classes of a 3×3×3 cellular bowl+rim field (one crater
// per cell, combined by deepest-bowl / highest-rim), the top class faded in by octCount's fraction so the
// craters geomorph as the tile resolves finer. Normalised by the FULL cascade weight (so a coarse tile
// reads a shallow basin and a deep tile the full pit), matching the CPU CraterField.
float craterField(vec3 dir, float baseFreq, float octCount, float density) {
    if (octCount <= 0.0) return 0.0;
    const float wnorm = 2.6094;  // Σ_{o=0..9} 0.62^o
    float sum = 0.0, freq = baseFreq, weight = 1.0;
    for (int o = 0; o < 10; o++) {
        float ofade = clamp(octCount - float(o), 0.0, 1.0);
        if (ofade > 0.0) {
            vec3 p = dir * freq;
            vec3 ip = floor(p);
            float salt = float(o) * 17.0;
            float minBowl = 0.0, maxRim = 0.0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) {
                vec3 c = ip + vec3(float(dx), float(dy), float(dz));
                float ex = hash(c + vec3(salt));
                if (ex > density) continue;                  // only some cells bear a crater
                vec3 jit = vec3(hash(c + vec3(salt + 1.7)), hash(c + vec3(salt + 9.1)), hash(c + vec3(salt + 4.3)));
                float radius = 0.22 + 0.28 * fract(ex * 7.3 + 0.19);
                float t = length(p - (c + jit)) / radius;
                if (t >= 1.5) continue;
                float bowl = -(1.0 - smoothstep(0.0, 0.85, min(t, 1.0)));  // depressed floor
                float e = (t - 0.95) / 0.12;
                float rim = 0.28 * exp(-0.5 * e * e);                       // raised rim ring
                minBowl = min(minBowl, bowl);
                maxRim = max(maxRim, rim);
            }
            sum += weight * ofade * (minBowl + maxRim);
        }
        freq *= 1.9; weight *= 0.62;
    }
    return sum / wnorm;
}

// Sparse raised volcano cones with a summit caldera (lava worlds): one per occupied cell, max-combined so
// the tallest cone wins. .x = height in [0,1] (× uVolcanoWeight·uScale outside); .y = a vent mask (1 at the
// caldera floor → 0 at the rim) the render shader turns into glowing lava. Low frequency → big volcanoes.
vec2 volcanoField(vec3 dir, float freq, float density) {
    vec3 p = dir * freq;
    vec3 ip = floor(p);
    float h = 0.0, vent = 0.0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++) {
        vec3 c = ip + vec3(float(dx), float(dy), float(dz));
        float ex = hash(c + vec3(7.0));
        if (ex > density) continue;                       // only some cells bear a volcano
        vec3 jit = vec3(hash(c + vec3(3.1)), hash(c + vec3(8.7)), hash(c + vec3(1.9)));
        float radius = 0.45 + 0.25 * fract(ex * 5.0 + 0.3);
        float t = length(p - (c + jit)) / radius;         // 0 at the summit → 1 at the base
        if (t >= 1.0) continue;
        float rimT = 0.30;
        float flank = smoothstep(1.0, rimT, t);           // 0 base → 1 rim
        float cone = (t < rimT) ? mix(0.55, 1.0, smoothstep(0.0, rimT, t)) : flank; // caldera dip inside the rim
        float ch = cone * (0.7 + 0.6 * fract(ex * 11.0)); // per-volcano height variation
        if (ch > h) { h = ch; vent = (t < rimT) ? (1.0 - smoothstep(0.0, rimT, t)) : 0.0; }
    }
    return vec2(h, vent);
}

// Sedimentary terrace: snap a value to `steps` levels with a smooth (no vertical cliff) riser biased toward
// the flat tread — gives mesas and banded canyon walls. Mirrors PlanetTerrain.Terrace.
float terrace(float v, float steps, float sharp) {
    float s = v * steps;
    float fl = floor(s);
    float frac = pow(s - fl, sharp);                    // bias toward the flat tread
    float riser = frac * frac * (3.0 - 2.0 * frac);     // smoothstep the rise
    return (fl + riser) / steps;
}

// Transverse-dune profile in [0,1]: the fractional phase of a stripe field perpendicular to the
// prevailing wind, bent by a low-frequency warp. Asymmetric like a real dune — a long windward rise
// to the crest at 0.7 of the wavelength, then a short steep slip face. Mirrors PlanetTerrain.DuneProfile.
float duneProfile(vec3 dir) {
    float warp = fbm(dir + vec3(12.9, 78.2, 44.6), uDuneWarpFreq, 3.0, 0.5);
    float s = uDuneFreq * dot(dir, uDuneDir) + uDuneWarpAmp * warp;
    float t = fract(s);
    return t < 0.7 ? smoothstep(0.0, 0.7, t) : 1.0 - smoothstep(0.7, 1.0, t);
}
// Dune-sea (erg) provinces in [0,1]: patchy regions where the sand gathers. Mirrors PlanetTerrain.ErgMask.
float ergMask(vec3 dir) {
    return smoothstep(0.05, 0.40, fbm(dir + vec3(91.7, 23.3, 55.1), uErgFreq, 3.0, 0.5));
}
// One lineae system's cross-section from a folded fBm value: raised shoulder pair flanking a deep
// central groove (the classic double-ridge fracture). In [-1, 0.55]. Mirrors PlanetTerrain.CrackShape.
float crackShape(float n) {
    float v = 1.0 - abs(n);
    float shoulder = smoothstep(0.86, 0.94, v) * (1.0 - smoothstep(0.955, 0.995, v));
    float trough = smoothstep(0.94, 0.995, v);
    return 0.55 * shoulder - trough;
}
// Two cross-cutting lineae systems, averaged (in [-1, 0.55]). Same offsets as the CPU/albedo paths.
float iceCracks(vec3 dir, float oct) {
    float nA = fbm(dir + vec3(2.7, 33.1, 8.9), uCrackFreq, oct, 0.5);
    float nB = fbm(dir + vec3(19.3, 4.7, 27.7), uCrackFreq, oct, 0.5);
    return 0.5 * (crackShape(nA) + crackShape(nB));
}

float shape(vec3 dir, vec3 oct, float microOct, float microGate, float duneGate, float crackOct, float crackGate) {
    float cont = fbm(dir, uFreq.x, oct.x, uGain.x);     // broad continents / basins
    float rugged = ruggedness(dir);                     // where rugged terrain belongs
    float mask = smoothstep(-0.2, 0.4, cont);           // highlands carry the mountains
    vec3 warped = dir + domainWarp(dir);                // bend the ranges
    float mtn  = ridged(warped, uFreq.y, oct.y, uGain.y);
    // Detail on split coordinates: nc = (dir*detailFreq) rebuilt relative to the patch centre, so it stays
    // small (precise) however deep the patch. Sub-metre roughness is now real baked geometry, not a bump.
    vec3 detNc = uDetFracBase + (dir - uDetDirC) * uFreq.z;
    float det  = erodedFbmSplit(uDetCellBase, detNc, oct.z, uGain.z); // high-freq roughness, slope-damped (eroded)
    float detailGate = uDetailFloor + (1.0 - uDetailFloor) * rugged; // calmer detail on plains
    // Fine micro-relief (LOD-gated so it only resolves up close) + sedimentary strata (fixed-octave terrace).
    // Split coordinates too, so its fine grain stays precise (sub-decimetre) rather than shimmering.
    vec3 microNc = uMicroFracBase + (dir - uMicroDirC) * uMicroFreq;
    float micro = (uMicroWeight > 0.0 && microGate > 0.0)
        ? fbmSplit(uMicroCellBase, microNc, microOct, uMicroGain) * microGate * detailGate : 0.0;
    float strata = (uStrataWeight > 0.0)
        ? terrace(fbm(dir + vec3(8.2, 71.5, 3.6), uStrataFreq, 4.0, 0.5), uStrataSteps, uStrataSharp) : 0.0;
    // Aeolian dunes gather in ergs on flat lowland; ice lineae cut across everything. Both LOD-gated.
    float dune = (uDuneWeight > 0.0 && duneGate > 0.0)
        ? duneProfile(dir) * ergMask(dir) * (1.0 - smoothstep(0.05, 0.45, cont)) * (1.0 - 0.75 * rugged) * duneGate : 0.0;
    float cracks = (uCrackWeight > 0.0 && crackGate > 0.0) ? iceCracks(dir, crackOct) * crackGate : 0.0;
    return uWeight.x * cont + uWeight.y * mtn * mask * rugged + uWeight.z * det * detailGate
         + uMicroWeight * micro + uStrataWeight * strata + uDuneWeight * dune + uCrackWeight * cracks;
}

void main() {
    // Snap to the mesh vertex grid, with a 1-texel guard ring: texel t holds the height at grid fraction
    // (t-1)/GridN (GridN = uTexelN-3), so interior texel 1 = vertex 0 (u0) and texel uTexelN-2 = the last
    // vertex (u1); texels 0 and uTexelN-1 are the guard just outside the patch (for edge-vertex normals).
    // A shared patch edge samples the identical direction from both sides → no height seam.
    float gridN = uTexelN - 3.0;
    float gi = min(floor(vUV.x * uTexelN), uTexelN - 1.0);
    float gj = min(floor(vUV.y * uTexelN), uTexelN - 1.0);
    float u = mix(uRect.x, uRect.z, (gi - 1.0) / gridN);
    float v = mix(uRect.y, uRect.w, (gj - 1.0) / gridN);
    vec3 dir = facePoint(uFace, u, v);
    // Craters are baked geometry (added to the height) AND carried in B/A so the render shader can tint
    // crater floors/rims without re-evaluating the field. craterFine geomorphs to craterCoarse via the
    // same morph the heights use; weight 0 on worlds without craters leaves B/A at zero (no tint).
    float craterFine = 0.0, craterCoarse = 0.0;
    if (uCraterWeight > 0.0) {
        craterFine   = craterField(dir, uCraterFreq, uCraterOctFine,   uCraterDensity);
        craterCoarse = craterField(dir, uCraterFreq, uCraterOctCoarse, uCraterDensity);
    }
    // Volcano cones: large, LOD-independent → added equally to both band-limits (no pop). Lava worlds only.
    float volcano = uVolcanoWeight > 0.0 ? volcanoField(dir, uVolcanoFreq, uVolcanoDensity).x : 0.0;
    float hFine   = uScale * (shape(dir, uOctFine,   uMicroOctFine,   uMicroGateFine,   uDuneGateFine,   uCrackOctFine,   uCrackGateFine)   + uCraterWeight * craterFine   + uVolcanoWeight * volcano);
    float hCoarse = uScale * (shape(dir, uOctCoarse, uMicroOctCoarse, uMicroGateCoarse, uDuneGateCoarse, uCrackOctCoarse, uCrackGateCoarse) + uCraterWeight * craterCoarse + uVolcanoWeight * volcano);
    oHeight = vec4(hFine, hCoarse, craterFine, craterCoarse);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _emptyVao; // attributeless fullscreen-triangle draws need a bound VAO in core

    public TerrainTileGenerator(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>
    /// Generate the tile for a quadtree node into <paramref name="layer"/> of <paramref name="cache"/>.
    /// <paramref name="face"/> + (<paramref name="u0"/>…<paramref name="v1"/>) locate the node on the cube;
    /// the spacings are the node's fine and parent-coarse vertex spacings (metres), which clamp the octave
    /// counts to what each band-limit resolves (and the float-safe ceiling).
    /// </summary>
    public void Generate(TerrainTileCache cache, int layer, int face, double u0, double v0, double u1, double v1,
        in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain, double spacingFine, double spacingCoarse)
    {
        var octFine = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingFine, p.MaxContinentOctaves),
            (float)OctClamp(terrain, p.MountainFreq, spacingFine, p.MaxMountainOctaves),
            (float)DetailOctClamp(terrain, p.DetailFreq, spacingFine, p.MaxDetailOctaves));
        var octCoarse = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingCoarse, p.MaxContinentOctaves),
            (float)OctClamp(terrain, p.MountainFreq, spacingCoarse, p.MaxMountainOctaves),
            (float)DetailOctClamp(terrain, p.DetailFreq, spacingCoarse, p.MaxDetailOctaves));

        // Split-coordinate base for the detail layer: the patch centre's noise cell (integer part wrapped to
        // the hash period, fraction kept precise), so the shader rebuilds a small, precise coordinate from
        // dir - dirC. This is what keeps the extra (sub-metre) detail octaves seam-free and swim-free.
        Vector3D<double> dirC = FacePointD(face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
        Vector3D<double> q0 = dirC * p.DetailFreq;
        double dfx = Math.Floor(q0.X), dfy = Math.Floor(q0.Y), dfz = Math.Floor(q0.Z);
        Vector3D<double> mq0 = dirC * p.MicroFreq;
        double mfx = Math.Floor(mq0.X), mfy = Math.Floor(mq0.Y), mfz = Math.Floor(mq0.Z);

        _shader.Use();
        _shader.SetInt("uFace", face);
        _shader.SetVector4("uRect", new Vector4D<float>((float)u0, (float)v0, (float)u1, (float)v1));
        _shader.SetVector3("uSeed", SeedOffset(p.Seed));
        _shader.SetVector3("uFreq", new Vector3D<float>((float)p.ContinentFreq, (float)p.MountainFreq, (float)p.DetailFreq));
        _shader.SetVector3("uWeight", new Vector3D<float>((float)p.ContinentWeight, (float)p.MountainWeight, (float)p.DetailWeight));
        _shader.SetVector3("uGain", new Vector3D<float>((float)p.ContinentGain, (float)p.MountainGain, (float)p.DetailGain));
        _shader.SetFloat("uScale", (float)p.Scale);
        _shader.SetVector3("uOctFine", octFine);
        _shader.SetVector3("uOctCoarse", octCoarse);
        _shader.SetVector3("uDetCellBase", new Vector3D<float>(WrapCell8192(dfx), WrapCell8192(dfy), WrapCell8192(dfz)));
        _shader.SetVector3("uDetFracBase", new Vector3D<float>((float)(q0.X - dfx), (float)(q0.Y - dfy), (float)(q0.Z - dfz)));
        _shader.SetVector3("uDetDirC", new Vector3D<float>((float)dirC.X, (float)dirC.Y, (float)dirC.Z));
        _shader.SetVector3("uMicroCellBase", new Vector3D<float>(WrapCell8192(mfx), WrapCell8192(mfy), WrapCell8192(mfz)));
        _shader.SetVector3("uMicroFracBase", new Vector3D<float>((float)(mq0.X - mfx), (float)(mq0.Y - mfy), (float)(mq0.Z - mfz)));
        _shader.SetVector3("uMicroDirC", new Vector3D<float>((float)dirC.X, (float)dirC.Y, (float)dirC.Z));
        _shader.SetFloat("uTexelN", cache.TileSize);
        _shader.SetFloat("uWarpFreq", (float)p.WarpFreq);
        _shader.SetFloat("uWarpStrength", (float)p.WarpStrength);
        _shader.SetFloat("uRuggedFreq", (float)p.RuggedFreq);
        _shader.SetFloat("uRuggedLo", (float)p.RuggedLo);
        _shader.SetFloat("uRuggedHi", (float)p.RuggedHi);
        _shader.SetFloat("uDetailFloor", (float)p.DetailFloor);
        _shader.SetFloat("uCraterWeight", (float)p.CraterWeight);
        _shader.SetFloat("uCraterDensity", (float)p.CraterDensity);
        _shader.SetFloat("uCraterFreq", (float)p.CraterFreq);
        _shader.SetFloat("uCraterOctFine", (float)(p.CraterWeight > 0.0 ? terrain.CraterOctavesForSpacing(spacingFine) : 0.0));
        _shader.SetFloat("uCraterOctCoarse", (float)(p.CraterWeight > 0.0 ? terrain.CraterOctavesForSpacing(spacingCoarse) : 0.0));
        _shader.SetFloat("uVolcanoWeight", (float)p.VolcanoWeight);
        _shader.SetFloat("uVolcanoFreq", (float)p.VolcanoFreq);
        _shader.SetFloat("uVolcanoDensity", (float)p.VolcanoDensity);
        _shader.SetFloat("uMicroWeight", (float)p.MicroWeight);
        _shader.SetFloat("uMicroFreq", (float)p.MicroFreq);
        _shader.SetFloat("uMicroGain", (float)p.MicroGain);
        _shader.SetFloat("uMicroOctFine", (float)(p.MicroWeight > 0.0 ? terrain.OctavesForSpacing(p.MicroFreq, spacingFine, p.MaxMicroOctaves) : 0.0));
        _shader.SetFloat("uMicroOctCoarse", (float)(p.MicroWeight > 0.0 ? terrain.OctavesForSpacing(p.MicroFreq, spacingCoarse, p.MaxMicroOctaves) : 0.0));
        _shader.SetFloat("uMicroGateFine", (float)(p.MicroWeight > 0.0 ? terrain.LayerGateForSpacing(p.MicroFreq, spacingFine) : 0.0));
        _shader.SetFloat("uMicroGateCoarse", (float)(p.MicroWeight > 0.0 ? terrain.LayerGateForSpacing(p.MicroFreq, spacingCoarse) : 0.0));
        _shader.SetFloat("uStrataWeight", (float)p.StrataWeight);
        _shader.SetFloat("uStrataFreq", (float)p.StrataFreq);
        _shader.SetFloat("uStrataSteps", p.StrataSteps);
        _shader.SetFloat("uStrataSharp", (float)p.StrataSharp);
        _shader.SetFloat("uDuneWeight", (float)p.DuneWeight);
        _shader.SetFloat("uDuneFreq", (float)p.DuneFreq);
        _shader.SetFloat("uDuneWarpFreq", (float)p.DuneWarpFreq);
        _shader.SetFloat("uDuneWarpAmp", (float)p.DuneWarpAmp);
        _shader.SetFloat("uErgFreq", (float)p.ErgFreq);
        _shader.SetVector3("uDuneDir", p.DuneDir);
        _shader.SetFloat("uDuneGateFine", (float)(p.DuneWeight > 0.0 ? terrain.LayerGateForSpacing(p.DuneFreq, spacingFine) : 0.0));
        _shader.SetFloat("uDuneGateCoarse", (float)(p.DuneWeight > 0.0 ? terrain.LayerGateForSpacing(p.DuneFreq, spacingCoarse) : 0.0));
        _shader.SetFloat("uCrackWeight", (float)p.CrackWeight);
        _shader.SetFloat("uCrackFreq", (float)p.CrackFreq);
        _shader.SetFloat("uCrackOctFine", (float)(p.CrackWeight > 0.0 ? terrain.OctavesForSpacing(p.CrackFreq, spacingFine, 5) : 0.0));
        _shader.SetFloat("uCrackOctCoarse", (float)(p.CrackWeight > 0.0 ? terrain.OctavesForSpacing(p.CrackFreq, spacingCoarse, 5) : 0.0));
        _shader.SetFloat("uCrackGateFine", (float)(p.CrackWeight > 0.0 ? terrain.LayerGateForSpacing(p.CrackFreq * 8.0, spacingFine) : 0.0));
        _shader.SetFloat("uCrackGateCoarse", (float)(p.CrackWeight > 0.0 ? terrain.LayerGateForSpacing(p.CrackFreq * 8.0, spacingCoarse) : 0.0));

        // Render the noise into the tile, then restore exactly the framebuffer + viewport that were bound
        // (the scene FBO mid-render): generation can run inside the terrain pass, so it must leave no trace.
        Span<int> prevFbo = stackalloc int[1];
        Span<int> prevVp = stackalloc int[4];
        _gl.GetInteger(GetPName.DrawFramebufferBinding, prevFbo);
        _gl.GetInteger(GetPName.Viewport, prevVp);
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        if (depth) _gl.Disable(EnableCap.DepthTest);

        cache.BeginRender(layer); // bind the atlas FBO + clip to this tile's sub-rect
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        cache.EndRender();        // disable scissor

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        _gl.Viewport(prevVp[0], prevVp[1], (uint)prevVp[2], (uint)prevVp[3]);
        if (depth) _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Octave count for a band-limit, clamped to both the LOD budget and the float-safe ceiling
    /// (direct-coordinate layers: continents, mountains).</summary>
    private static double OctClamp(PlanetTerrain terrain, double baseFreq, double spacing, int max)
    {
        double lod = terrain.OctavesForSpacing(baseFreq, spacing, max);
        double safe = Math.Floor(Math.Log2(FloatSafeFreq / Math.Max(1.0, baseFreq))) + 1.0;
        return Math.Max(0.0, Math.Min(lod, safe));
    }

    /// <summary>Octave count for the DETAIL layer — same LOD band-limit, but against the higher
    /// <see cref="DetailSafeFreq"/> ceiling since it samples in precise split coordinates.</summary>
    private static double DetailOctClamp(PlanetTerrain terrain, double baseFreq, double spacing, int max)
    {
        double lod = terrain.OctavesForSpacing(baseFreq, spacing, max);
        double safe = Math.Floor(Math.Log2(DetailSafeFreq / Math.Max(1.0, baseFreq))) + 1.0;
        return Math.Max(0.0, Math.Min(lod, safe));
    }

    /// <summary>Unit cube-sphere direction for face + (u,v) in double — mirrors the shader's facePoint, for
    /// computing the split-coordinate base at the patch centre.</summary>
    private static Vector3D<double> FacePointD(int f, double u, double v)
    {
        double a = u * 2.0 - 1.0, b = v * 2.0 - 1.0;
        Vector3D<double> p = f switch
        {
            0 => new(1.0, b, -a),
            1 => new(-1.0, b, a),
            2 => new(a, 1.0, -b),
            3 => new(a, -1.0, b),
            4 => new(a, b, 1.0),
            _ => new(-a, b, -1.0),
        };
        return Vector3D.Normalize(p);
    }

    /// <summary>Wrap an integer cell index into [0, 8192) — the generator hash's period, keeping the
    /// shader's cell base small enough to stay integer-precise across the octave doublings.</summary>
    private static float WrapCell8192(double flooredCell)
    {
        double m = flooredCell % 8192.0;
        if (m < 0) m += 8192.0;
        return (float)m;
    }

    /// <summary>Three fractional offsets in [0,1) from the body seed, shifting the hash so worlds differ.</summary>
    private static Vector3D<float> SeedOffset(ulong seed)
        => new(((seed) & 1023) / 1024f, ((seed >> 10) & 1023) / 1024f, ((seed >> 20) & 1023) / 1024f);

    public void Dispose()
    {
        _gl.DeleteVertexArray(_emptyVao);
        _shader.Dispose();
    }
}
