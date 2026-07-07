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

    // The generator fragment is split into three reusable pieces so the height pass and the normal pass share
    // the EXACT same field math (compiled into two programs): GenHeaderGlsl (uniforms) + GenFieldGlsl (noise/
    // shape helpers) + a per-target main. Height writes RG=height/BA=crater; normal writes the object-space
    // surface normal of the same field, but sampled at the finer surface-tile octave budget.
    private const string GenHeaderGlsl = @"#version 410 core
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
uniform vec3 uMtnCellBase, uMtnFracBase, uMtnDirC;       // split base for the ridged mountains; DirC is the WARPED patch centre
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
uniform float uRadiusM;    // planet radius (m) — normal pass turns an angular texel step into a metric slope
";

    private const string GenFieldGlsl = @"
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
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
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
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
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
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
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
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
        sum += amp * vnoiseSplit(cb, nc); norm += amp;
        amp *= gain; cb = mod(cb * 2.0, 8192.0); nc *= 2.0;
    }
    if (frac > 0.0) { sum += amp * frac * vnoiseSplit(cb, nc); norm += amp * frac; }
    return norm > 0.0 ? sum / norm : 0.0;
}
// Ridged multifractal on split coordinates (mirror of ridged() below): lets mountain ridges/cliffs resolve
// their fine octaves as real sub-metre geometry past the old float wall. ncBase is the WARPED coordinate
// (dir + domainWarp) expressed relative to the warped patch centre, so it stays small and precise.
float ridgedSplit(vec3 cellBase, vec3 ncBase, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 0.5, prev = 1.0, norm = 0.0;
    vec3 cb = cellBase, nc = ncBase;
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
        float n = 1.0 - abs(vnoiseSplit(cb, nc)); n *= n; n *= prev;
        sum += n * amp; norm += amp; prev = n; amp *= gain;
        cb = mod(cb * 2.0, 8192.0); nc *= 2.0;
    }
    if (frac > 0.0) { float n = 1.0 - abs(vnoiseSplit(cb, nc)); n *= n; n *= prev; sum += n * amp * frac; norm += amp * frac; }
    return norm > 0.0 ? clamp(sum / norm, 0.0, 1.0) : 0.0;
}

// Fractional-octave ridged multifractal in [0,1] (creases at zero crossings, detail riding ridges).
float ridged(vec3 dir, float freq, float oct, float gain) {
    if (oct <= 0.0) return 0.0;
    int full = int(floor(oct));
    float frac = oct - float(full);
    float sum = 0.0, amp = 0.5, f = freq, prev = 1.0, norm = 0.0;
    for (int i = 0; i < full; i++) {   // dynamic bound (full is uniform-derived) so Metal can't unroll
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

// Impact-crater cascade in ≈[-1, rim]: up to 15 size classes of a 3×3×3 cellular bowl+rim field (one
// crater per cell, combined by deepest-bowl / highest-rim), the top class faded in by octCount's fraction
// so the craters geomorph as the tile resolves finer. Normalised by the FULL cascade weight (so a coarse
// tile reads a shallow basin and a deep tile the full pit), matching the CPU CraterField.
//
// The loop runs a DYNAMIC bound ceil(octCount), NOT the constant 15: this cellular 3×3×3 body is the
// heaviest per-texel work in the whole generator, and unrolling 15 copies of it (on top of the split-noise
// layers) overflows Apple's GLSL→Metal shader compiler and crashes the GPU process. A uniform-derived bound
// can't be unrolled, so the shader stays small AND only the octaves this tile actually resolves run.
float craterField(vec3 dir, float baseFreq, float octCount, float density) {
    if (octCount <= 0.0) return 0.0;
    const float wnorm = 2.6296;  // Σ_{o=0..14} 0.62^o
    float sum = 0.0, freq = baseFreq, weight = 1.0;
    int maxo = int(min(15.0, ceil(octCount)));
    for (int o = 0; o < maxo; o++) {
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
    // Mountains on split coordinates: nc = (warped*mountainFreq) rebuilt relative to the WARPED patch
    // centre (uMtnDirC), which cancels algebraically at any shared edge → seam-safe and sub-metre precise.
    vec3 mtnNc = uMtnFracBase + (warped - uMtnDirC) * uFreq.y;
    float mtn  = ridgedSplit(uMtnCellBase, mtnNc, oct.y, uGain.y);
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

// Total FINE height (m): the shape stack + baked craters + volcano, i.e. exactly what the height tile's R
// channel holds. Used by the height main and (perturbed) by the normal main's finite difference.
float fineHeightAt(vec3 dir) {
    float craterF = uCraterWeight > 0.0 ? craterField(dir, uCraterFreq, uCraterOctFine, uCraterDensity) : 0.0;
    float volcano = uVolcanoWeight > 0.0 ? volcanoField(dir, uVolcanoFreq, uVolcanoDensity).x : 0.0;
    return uScale * (shape(dir, uOctFine, uMicroOctFine, uMicroGateFine, uDuneGateFine, uCrackOctFine, uCrackGateFine)
                     + uCraterWeight * craterF + uVolcanoWeight * volcano);
}
";

    private const string HeightMainGlsl = @"
layout(location = 0) out vec4 oHeight;
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

    private const string FragmentSource = GenHeaderGlsl + GenFieldGlsl + HeightMainGlsl;

    // Extra uniforms the COLOR half of the surface pass needs (the biome/regolith albedo params) — declared
    // separately so they append to GenHeaderGlsl without duplicating any of its field uniforms.
    private const string ColorUniformsGlsl = @"
uniform float uAmplitude, uSurfaceTempK, uHasLife;
uniform vec3 uBaseColor, uLowland, uSubstrateTint, uRock, uSnow, uCliff;
uniform float uSnowLine, uCliffThreshold, uCliffStrength;
uniform float uMoistureFreq, uMoistureBias;
uniform float uIsCratered, uCraterAlbedo, uMariaStrength, uMariaFreq;
uniform float uIsIcy, uCrackFreqR, uCrackWeightR;
uniform float uIsDesert, uErgFreqR, uDuneWeightR;
uniform vec3 uSeedR;
";

    // Albedo helpers — a verbatim port of the render fragment's colour path (its own value-noise fbm3 + the
    // biome/elevation/slope ramp), so the baked colour matches what the per-pixel path produced.
    private const string ColorHelpersGlsl = @"
float hash13(vec3 p) {
    p = mod(p, 4096.0);
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}
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
vec3 biomeColor(vec3 dir, float elevM, float slope) {
    float tBase = clamp((uSurfaceTempK - 215.0) / 105.0, 0.0, 1.0);
    float lat = abs(dir.y);
    float elevAbove = max(0.0, elevM / uAmplitude);
    float temp = clamp(tBase - 0.55 * pow(lat, 1.3) - 0.55 * elevAbove, 0.0, 1.0);
    float tropics = 1.0 - smoothstep(0.0, 0.45, lat);
    float temperateBelt = smoothstep(0.5, 0.7, lat) * (1.0 - smoothstep(0.8, 0.97, lat));
    float rainBand = clamp(max(tropics, 0.75 * temperateBelt), 0.0, 1.0);
    float regional = 0.5 + 0.5 * fbm3(dir + vec3(17.3, 5.9, 42.1), uMoistureFreq);
    float moist = clamp(rainBand * (1.0 - 0.55 * elevAbove) * (0.6 + 0.8 * regional) + uMoistureBias, 0.0, 1.0);
    vec3 temperate = uBaseColor * uLowland;
    vec3 hot = vec3(0.80, 0.62, 0.40);
    vec3 substrate = temp < 0.5 ? mix(vec3(0.78,0.82,0.86), temperate, temp / 0.5)
                                : mix(temperate, hot, (temp - 0.5) / 0.5);
    substrate = mix(substrate, uSubstrateTint, 0.45);
    vec3 ground = substrate;
    if (uHasLife > 0.5) {
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
";

    // SURFACE pass (MRT): bakes BOTH the object-space normal (attachment0) and the biome/regolith colour
    // (attachment1) of the SAME field, at the surface tile's (finer) octave budget, from ONE set of finite-
    // difference height taps. Grid mapping identical to the height tile (interior texel 1..N spans the patch
    // inclusive → seam-free bilinear). This replaces the per-pixel detail + orbital-relief + colour work.
    private const string SurfaceMainGlsl = @"
layout(location = 0) out vec4 oNormal;
layout(location = 1) out vec4 oColor;
void main() {
    float gridN = uTexelN - 3.0;
    float gi = min(floor(vUV.x * uTexelN), uTexelN - 1.0);
    float gj = min(floor(vUV.y * uTexelN), uTexelN - 1.0);
    float u = mix(uRect.x, uRect.z, (gi - 1.0) / gridN);
    float v = mix(uRect.y, uRect.w, (gj - 1.0) / gridN);
    vec3 dir = facePoint(uFace, u, v);
    float du = (uRect.z - uRect.x) / gridN;
    float dv = (uRect.w - uRect.y) / gridN;
    vec3 dirE = facePoint(uFace, u + du, v);
    vec3 dirN = facePoint(uFace, u, v + dv);
    float h0 = fineHeightAt(dir);
    float hE = fineHeightAt(dirE);
    float hN = fineHeightAt(dirN);
    vec3 tE = dirE - dir; float dsE = uRadiusM * length(tE); tE = normalize(tE);
    vec3 tN = dirN - dir; float dsN = uRadiusM * length(tN); tN = normalize(tN);
    vec3 nrm = normalize(dir - tE * ((hE - h0) / max(dsE, 1e-3)) - tN * ((hN - h0) / max(dsN, 1e-3)));
    oNormal = vec4(nrm * 0.5 + 0.5, 1.0);

    // COLOUR: verbatim port of the render fragment's albedo (biome + regolith + maria + ice lineae + erg).
    vec3 up = dir;
    float slope = clamp(dot(nrm, up), 0.0, 1.0);
    float vCrater = uCraterWeight > 0.0 ? craterField(dir, uCraterFreq, uCraterOctFine, uCraterDensity) : 0.0;
    vec3 col = biomeColor(up, h0, slope);
    if (uIsCratered > 0.5) {
        if (uCraterAlbedo > 0.0) {
            float dark   = max(0.0, -vCrater);
            float bright = max(0.0,  vCrater) / 0.28;
            col *= 1.0 - 0.45 * uCraterAlbedo * dark;
            col *= 1.0 + 0.30 * uCraterAlbedo * bright;
        }
        if (uMariaStrength > 0.0) {
            float m = fbm3(up + vec3(23.7, 88.1, 4.3), uMariaFreq);
            float maria = smoothstep(0.08, 0.5, m);
            vec3 mare = vec3(col.r * 0.55, col.g * 0.56, col.b * 0.60);
            col = mix(col, mare, maria * uMariaStrength);
        }
    }
    if (uIsIcy > 0.5 && uCrackWeightR > 0.0) {
        float vA = 1.0 - abs(tfFbm(up + vec3(2.7, 33.1, 8.9), uCrackFreqR, 4.0, 0.5, uSeedR));
        float vB = 1.0 - abs(tfFbm(up + vec3(19.3, 4.7, 27.7), uCrackFreqR, 4.0, 0.5, uSeedR));
        float lin = max(smoothstep(0.88, 0.97, vA), 0.7 * smoothstep(0.88, 0.97, vB));
        col = mix(col, vec3(0.48, 0.30, 0.20), 0.55 * lin);
    }
    if (uIsDesert > 0.5 && uDuneWeightR > 0.0) {
        float erg = smoothstep(0.05, 0.40, tfFbm(up + vec3(91.7, 23.3, 55.1), uErgFreqR, 3.0, 0.5, uSeedR));
        col = mix(col, col * vec3(1.06, 0.82, 0.55), 0.5 * erg);
    }
    oColor = vec4(col, 1.0);
}";

    private const string SurfaceSource =
        GenHeaderGlsl + ColorUniformsGlsl + GenFieldGlsl + FieldGlsl + ColorHelpersGlsl + SurfaceMainGlsl;

    // EQUIRECT map pass (MRT albedo + object-space normal): the distant sphere's baked surface, rendered
    // straight from the SAME field + biome/regolith GLSL the quadtree tiles use — so the sphere and the
    // quadtree it hands off to are the one surface, not two divergent CPU/GPU ports. Direction comes from the
    // equirect UV (inverse of the sphere shader's dirToUv), and the shape is sampled in DIRECT coordinates:
    // the tile pass's split-coordinate precision only matters at the sub-metre octaves a whole-sphere map
    // never resolves, and fine layers (micro / dune / lineae GEOMETRY) are sub-texel here (their ALBEDO
    // tints still apply). Band-limited to one texel arc, so it matches the coarse orbital look.
    private const string EquirectMainGlsl = @"
uniform int uMapW;
uniform int uMapH;
uniform float uHasOcean;
uniform float uSeaLevel;
uniform vec3 uOctMap;         // continent / mountain / detail octave counts at the map band-limit
uniform float uCraterOctMap;  // crater cascade octaves at the map band-limit

vec3 mapDir(vec2 uv) {
    float lon = (uv.x - 0.5) * 6.28318530718;
    float lat = (uv.y - 0.5) * 3.14159265359;
    float cl = cos(lat);
    return vec3(cl * cos(lon), sin(lat), cl * sin(lon));
}
float shapeMap(vec3 dir) {
    float cont = fbm(dir, uFreq.x, uOctMap.x, uGain.x);
    float rugged = ruggedness(dir);
    float mask = smoothstep(-0.2, 0.4, cont);
    vec3 warped = dir + domainWarp(dir);
    float mtn = ridged(warped, uFreq.y, uOctMap.y, uGain.y);
    float det = erodedFbm(dir, uFreq.z, uOctMap.z, uGain.z);
    float detailGate = uDetailFloor + (1.0 - uDetailFloor) * rugged;
    float strata = uStrataWeight > 0.0
        ? terrace(fbm(dir + vec3(8.2, 71.5, 3.6), uStrataFreq, 4.0, 0.5), uStrataSteps, uStrataSharp) : 0.0;
    return uWeight.x * cont + uWeight.y * mtn * mask * rugged + uWeight.z * det * detailGate
         + uStrataWeight * strata;
}
float heightMap(vec3 dir) {
    float craterF = uCraterWeight > 0.0 ? craterField(dir, uCraterFreq, uCraterOctMap, uCraterDensity) : 0.0;
    float volcano = uVolcanoWeight > 0.0 ? volcanoField(dir, uVolcanoFreq, uVolcanoDensity).x : 0.0;
    return uScale * (shapeMap(dir) + uCraterWeight * craterF + uVolcanoWeight * volcano);
}
layout(location = 0) out vec4 oAlbedo;
layout(location = 1) out vec4 oNormal;
void main() {
    vec3 dir = mapDir(vUV);
    // Object-space normal from a central height difference along two surface tangents, one texel-arc wide.
    vec3 upRef = abs(dir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 east = normalize(cross(upRef, dir));
    vec3 north = cross(dir, east);
    float d = 6.28318530718 / float(uMapW);            // angular texel step (radians)
    vec3 dE = normalize(dir + east * d),  dW = normalize(dir - east * d);
    vec3 dN = normalize(dir + north * d), dS = normalize(dir - north * d);
    float h0 = heightMap(dir);
    if (uHasOcean > 0.5 && h0 < uSeaLevel) {
        // Below the waterline the sphere (no separate water pass) must show the WATER surface, flat + blue.
        float f = clamp((uSeaLevel - h0) / (uAmplitude * 0.12 + 1.0), 0.0, 1.0);
        oAlbedo = vec4(mix(vec3(0.20, 0.55, 0.62), vec3(0.02, 0.10, 0.26), f), 1.0);
        oNormal = vec4(dir * 0.5 + 0.5, 1.0);
        return;
    }
    float R = uRadiusM;
    vec3 pE = dE * (R + heightMap(dE)), pW = dW * (R + heightMap(dW));
    vec3 pN = dN * (R + heightMap(dN)), pS = dS * (R + heightMap(dS));
    vec3 nrm = cross(pE - pW, pN - pS);
    if (dot(nrm, dir) < 0.0) nrm = -nrm;
    nrm = length(nrm) > 0.0 ? normalize(nrm) : dir;

    float slope = clamp(dot(nrm, dir), 0.0, 1.0);
    vec3 up = dir;
    float vCrater = uCraterWeight > 0.0 ? craterField(dir, uCraterFreq, uCraterOctMap, uCraterDensity) : 0.0;
    vec3 col = biomeColor(up, h0, slope);
    if (uIsCratered > 0.5) {
        if (uCraterAlbedo > 0.0) {
            float dark   = max(0.0, -vCrater);
            float bright = max(0.0,  vCrater) / 0.28;
            col *= 1.0 - 0.45 * uCraterAlbedo * dark;
            col *= 1.0 + 0.30 * uCraterAlbedo * bright;
        }
        if (uMariaStrength > 0.0) {
            float m = fbm3(up + vec3(23.7, 88.1, 4.3), uMariaFreq);
            float maria = smoothstep(0.08, 0.5, m);
            vec3 mare = vec3(col.r * 0.55, col.g * 0.56, col.b * 0.60);
            col = mix(col, mare, maria * uMariaStrength);
        }
    }
    if (uIsIcy > 0.5 && uCrackWeightR > 0.0) {
        float vA = 1.0 - abs(tfFbm(up + vec3(2.7, 33.1, 8.9), uCrackFreqR, 4.0, 0.5, uSeedR));
        float vB = 1.0 - abs(tfFbm(up + vec3(19.3, 4.7, 27.7), uCrackFreqR, 4.0, 0.5, uSeedR));
        float lin = max(smoothstep(0.88, 0.97, vA), 0.7 * smoothstep(0.88, 0.97, vB));
        col = mix(col, vec3(0.48, 0.30, 0.20), 0.55 * lin);
    }
    if (uIsDesert > 0.5 && uDuneWeightR > 0.0) {
        float erg = smoothstep(0.05, 0.40, tfFbm(up + vec3(91.7, 23.3, 55.1), uErgFreqR, 3.0, 0.5, uSeedR));
        col = mix(col, col * vec3(1.06, 0.82, 0.55), 0.5 * erg);
    }
    oAlbedo = vec4(col, 1.0);
    oNormal = vec4(nrm * 0.5 + 0.5, 1.0);
}";

    private const string EquirectSource =
        GenHeaderGlsl + ColorUniformsGlsl + GenFieldGlsl + FieldGlsl + ColorHelpersGlsl + EquirectMainGlsl;

    private readonly GL _gl;
    private readonly Shader _shader;       // height pass (RG=height, BA=crater), at the mesh vertex resolution
    private readonly Shader _surfShader;   // surface pass (MRT normal + color), at the finer surface-tile res
    private readonly Shader _equirectShader; // equirect albedo+normal map pass (distant sphere's baked surface)
    private uint _equirectFbo;             // FBO for the equirect pass (attaches the caller's two map textures)
    private readonly uint _emptyVao; // attributeless fullscreen-triangle draws need a bound VAO in core

    public TerrainTileGenerator(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _surfShader = new Shader(gl, VertexSource, SurfaceSource);
        _equirectShader = new Shader(gl, VertexSource, EquirectSource);
        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>Bake a body's equirectangular albedo (attachment0) + object-space normal (attachment1) map
    /// into the two provided <paramref name="width"/>×<paramref name="height"/> RGB textures — the distant
    /// sphere's surface, from the SAME field/biome GLSL the quadtree bakes, so the two can't diverge. Runs on
    /// the render thread in a few ms (vs seconds for the old CPU bake). The caller owns the textures (must be
    /// allocated at width×height) and sets their filtering/mipmaps after.</summary>
    public unsafe void BakeEquirect(uint albedoTex, uint normalTex, int width, int height,
        in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain, float craterAlbedo, float mariaStrength)
    {
        if (_equirectFbo == 0) _equirectFbo = _gl.GenFramebuffer();

        Span<int> prevFbo = stackalloc int[1];
        Span<int> prevVp = stackalloc int[4];
        _gl.GetInteger(GetPName.DrawFramebufferBinding, prevFbo);
        _gl.GetInteger(GetPName.Viewport, prevVp);
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        if (depth) _gl.Disable(EnableCap.DepthTest);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _equirectFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, albedoTex, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, normalTex, 0);
        Span<GLEnum> bufs = stackalloc GLEnum[] { GLEnum.ColorAttachment0, GLEnum.ColorAttachment1 };
        fixed (GLEnum* pb = bufs) _gl.DrawBuffers(2, pb);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        _equirectShader.Use();
        SetEquirectUniforms(_equirectShader, p, terrain, craterAlbedo, mariaStrength, width, height);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        _gl.Viewport(prevVp[0], prevVp[1], (uint)prevVp[2], (uint)prevVp[3]);
        if (depth) _gl.Enable(EnableCap.DepthTest);
    }

    private static void SetEquirectUniforms(Shader sh, in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain,
        float craterAlbedo, float mariaStrength, int width, int height)
    {
        double texArc = 2.0 * Math.PI * terrain.Radius / width; // metres across one texel at the equator
        // Field uniforms the direct-coordinate shapeMap/heightMap read (a subset of SetGenUniforms — the
        // split-coordinate bases aren't used by the equirect main).
        sh.SetVector3("uSeed", SeedOffset(p.Seed));
        sh.SetVector3("uFreq", new Vector3D<float>((float)p.ContinentFreq, (float)p.MountainFreq, (float)p.DetailFreq));
        sh.SetVector3("uWeight", new Vector3D<float>((float)p.ContinentWeight, (float)p.MountainWeight, (float)p.DetailWeight));
        sh.SetVector3("uGain", new Vector3D<float>((float)p.ContinentGain, (float)p.MountainGain, (float)p.DetailGain));
        sh.SetFloat("uScale", (float)p.Scale);
        sh.SetFloat("uWarpFreq", (float)p.WarpFreq);
        sh.SetFloat("uWarpStrength", (float)p.WarpStrength);
        sh.SetFloat("uRuggedFreq", (float)p.RuggedFreq);
        sh.SetFloat("uRuggedLo", (float)p.RuggedLo);
        sh.SetFloat("uRuggedHi", (float)p.RuggedHi);
        sh.SetFloat("uDetailFloor", (float)p.DetailFloor);
        sh.SetFloat("uCraterWeight", (float)p.CraterWeight);
        sh.SetFloat("uCraterDensity", (float)p.CraterDensity);
        sh.SetFloat("uCraterFreq", (float)p.CraterFreq);
        sh.SetFloat("uVolcanoWeight", (float)p.VolcanoWeight);
        sh.SetFloat("uVolcanoFreq", (float)p.VolcanoFreq);
        sh.SetFloat("uVolcanoDensity", (float)p.VolcanoDensity);
        sh.SetFloat("uStrataWeight", (float)p.StrataWeight);
        sh.SetFloat("uStrataFreq", (float)p.StrataFreq);
        sh.SetFloat("uStrataSteps", p.StrataSteps);
        sh.SetFloat("uStrataSharp", (float)p.StrataSharp);
        sh.SetFloat("uRadiusM", (float)terrain.Radius);
        sh.SetVector3("uOctMap", new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, texArc, p.MaxContinentOctaves),
            (float)OctClamp(terrain, p.MountainFreq, texArc, p.MaxMountainOctaves),
            (float)OctClamp(terrain, p.DetailFreq, texArc, p.MaxDetailOctaves)));
        sh.SetFloat("uCraterOctMap", (float)(p.CraterWeight > 0.0 ? terrain.CraterOctavesForSpacing(texArc) : 0.0));
        sh.SetInt("uMapW", width);
        sh.SetInt("uMapH", height);
        sh.SetFloat("uHasOcean", terrain.HasOcean ? 1f : 0f);
        sh.SetFloat("uSeaLevel", (float)terrain.SeaLevelMeters);
        // Biome / regolith colour block (identical to the surface tile pass).
        SetColorUniforms(sh, p, craterAlbedo, mariaStrength);
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
        _shader.Use();
        SetGenUniforms(_shader, face, u0, v0, u1, v1, p, terrain, spacingFine, spacingCoarse);
        _shader.SetFloat("uTexelN", cache.TileSize);
        RenderInto(cache, layer);
    }

    /// <summary>Bake the surface tile (MRT normal + color) for a node into <paramref name="surfCache"/> (its
    /// primary + secondary atlases). The spacings are the surface tile's (finer) fine/coarse vertex spacings,
    /// so the baked normal carries the crater/detail octaves the coarse mesh can't — SpaceEngine's high-res-
    /// surface-over-coarse-mesh split. <paramref name="craterAlbedo"/>/<paramref name="mariaStrength"/> are the
    /// live regolith sliders, baked in (they take effect as tiles regenerate).</summary>
    public void GenerateSurface(TerrainTileCache surfCache, int layer, int face,
        double u0, double v0, double u1, double v1,
        in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain, double spacingFine, double spacingCoarse,
        float craterAlbedo, float mariaStrength)
    {
        _surfShader.Use();
        SetGenUniforms(_surfShader, face, u0, v0, u1, v1, p, terrain, spacingFine, spacingCoarse);
        _surfShader.SetFloat("uTexelN", surfCache.TileSize);
        _surfShader.SetFloat("uRadiusM", (float)terrain.Radius);
        SetColorUniforms(_surfShader, p, craterAlbedo, mariaStrength);
        RenderInto(surfCache, layer);
    }

    /// <summary>Set the biome/regolith albedo uniforms for the surface pass's colour half (mirror of the
    /// render fragment's colour uniform block, so the baked colour matches the old per-pixel look).</summary>
    private static void SetColorUniforms(Shader sh, in PlanetTerrain.GpuTerrainParams p,
        float craterAlbedo, float mariaStrength)
    {
        sh.SetVector3("uBaseColor", p.BaseColor);
        sh.SetVector3("uSubstrateTint", p.SubstrateTint);
        sh.SetVector3("uRock", p.Rock);
        sh.SetVector3("uSnow", p.Snow);
        sh.SetVector3("uCliff", p.Cliff);
        sh.SetVector3("uLowland", p.Lowland);
        sh.SetFloat("uSnowLine", p.SnowLine);
        sh.SetFloat("uCliffThreshold", p.CliffThreshold);
        sh.SetFloat("uCliffStrength", p.CliffStrength);
        sh.SetFloat("uSurfaceTempK", p.SurfaceTempK);
        sh.SetFloat("uHasLife", p.HasLife);
        sh.SetFloat("uMoistureFreq", (float)p.MoistureFreq);
        sh.SetFloat("uMoistureBias", (float)p.MoistureBias);
        sh.SetFloat("uAmplitude", (float)Math.Max(1.0, p.Amplitude));
        sh.SetFloat("uIsCratered", p.IsCratered);
        sh.SetFloat("uCraterAlbedo", Math.Max(0f, craterAlbedo));
        sh.SetFloat("uMariaStrength", Math.Max(0f, mariaStrength));
        sh.SetFloat("uMariaFreq", (float)(p.ContinentFreq * 0.6));
        sh.SetFloat("uIsIcy", p.IsIcy);
        sh.SetFloat("uCrackFreqR", (float)p.CrackFreq);
        sh.SetFloat("uCrackWeightR", (float)p.CrackWeight);
        sh.SetFloat("uIsDesert", p.IsDesert);
        sh.SetFloat("uErgFreqR", (float)p.ErgFreq);
        sh.SetFloat("uDuneWeightR", (float)p.DuneWeight);
        sh.SetVector3("uSeedR", new Vector3D<float>(
            (p.Seed & 1023) / 1024f, ((p.Seed >> 10) & 1023) / 1024f, ((p.Seed >> 20) & 1023) / 1024f));
    }

    /// <summary>Draw the fullscreen generation pass into <paramref name="cache"/>'s <paramref name="layer"/>,
    /// then hard-restore the scene framebuffer + viewport (generation runs inside the terrain render pass).</summary>
    private void RenderInto(TerrainTileCache cache, int layer)
    {
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

    private static void SetGenUniforms(Shader sh, int face, double u0, double v0, double u1, double v1,
        in PlanetTerrain.GpuTerrainParams p, PlanetTerrain terrain, double spacingFine, double spacingCoarse)
    {
        var octFine = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingFine, p.MaxContinentOctaves),
            (float)SplitOctClamp(terrain, p.MountainFreq, spacingFine, p.MaxMountainOctaves),
            (float)SplitOctClamp(terrain, p.DetailFreq, spacingFine, p.MaxDetailOctaves));
        var octCoarse = new Vector3D<float>(
            (float)OctClamp(terrain, p.ContinentFreq, spacingCoarse, p.MaxContinentOctaves),
            (float)SplitOctClamp(terrain, p.MountainFreq, spacingCoarse, p.MaxMountainOctaves),
            (float)SplitOctClamp(terrain, p.DetailFreq, spacingCoarse, p.MaxDetailOctaves));

        // Split-coordinate base for the split layers: the patch centre's noise cell (integer part wrapped to
        // the hash period, fraction kept precise), so the shader rebuilds a small, precise coordinate from
        // dir - dirC. This is what keeps the extra (sub-metre) octaves seam-free and swim-free.
        Vector3D<double> dirC = FacePointD(face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
        Vector3D<double> q0 = dirC * p.DetailFreq;
        double dfx = Math.Floor(q0.X), dfy = Math.Floor(q0.Y), dfz = Math.Floor(q0.Z);
        Vector3D<double> mq0 = dirC * p.MicroFreq;
        double mfx = Math.Floor(mq0.X), mfy = Math.Floor(mq0.Y), mfz = Math.Floor(mq0.Z);
        // Mountains are sampled at the WARPED direction; base the split off the warped patch centre so the
        // warp offset (which would otherwise blow up the local coordinate) is folded into the constant that
        // cancels at edges. Computed via the physics mirror's warp so both agree; float is fine here (the
        // warpedC value cancels in the shader's reconstruction — only the integer cell must be precise).
        Vector3D<float> warpedC = terrain.GpuWarpedDir(dirC);
        Vector3D<double> wq0 = new Vector3D<double>(warpedC.X, warpedC.Y, warpedC.Z) * p.MountainFreq;
        double wfx = Math.Floor(wq0.X), wfy = Math.Floor(wq0.Y), wfz = Math.Floor(wq0.Z);

        sh.SetInt("uFace", face);
        sh.SetVector4("uRect", new Vector4D<float>((float)u0, (float)v0, (float)u1, (float)v1));
        sh.SetVector3("uSeed", SeedOffset(p.Seed));
        sh.SetVector3("uFreq", new Vector3D<float>((float)p.ContinentFreq, (float)p.MountainFreq, (float)p.DetailFreq));
        sh.SetVector3("uWeight", new Vector3D<float>((float)p.ContinentWeight, (float)p.MountainWeight, (float)p.DetailWeight));
        sh.SetVector3("uGain", new Vector3D<float>((float)p.ContinentGain, (float)p.MountainGain, (float)p.DetailGain));
        sh.SetFloat("uScale", (float)p.Scale);
        sh.SetVector3("uOctFine", octFine);
        sh.SetVector3("uOctCoarse", octCoarse);
        sh.SetVector3("uDetCellBase", new Vector3D<float>(WrapCell8192(dfx), WrapCell8192(dfy), WrapCell8192(dfz)));
        sh.SetVector3("uDetFracBase", new Vector3D<float>((float)(q0.X - dfx), (float)(q0.Y - dfy), (float)(q0.Z - dfz)));
        sh.SetVector3("uDetDirC", new Vector3D<float>((float)dirC.X, (float)dirC.Y, (float)dirC.Z));
        sh.SetVector3("uMicroCellBase", new Vector3D<float>(WrapCell8192(mfx), WrapCell8192(mfy), WrapCell8192(mfz)));
        sh.SetVector3("uMicroFracBase", new Vector3D<float>((float)(mq0.X - mfx), (float)(mq0.Y - mfy), (float)(mq0.Z - mfz)));
        sh.SetVector3("uMicroDirC", new Vector3D<float>((float)dirC.X, (float)dirC.Y, (float)dirC.Z));
        sh.SetVector3("uMtnCellBase", new Vector3D<float>(WrapCell8192(wfx), WrapCell8192(wfy), WrapCell8192(wfz)));
        sh.SetVector3("uMtnFracBase", new Vector3D<float>((float)(wq0.X - wfx), (float)(wq0.Y - wfy), (float)(wq0.Z - wfz)));
        sh.SetVector3("uMtnDirC", warpedC);
        sh.SetFloat("uWarpFreq", (float)p.WarpFreq);
        sh.SetFloat("uWarpStrength", (float)p.WarpStrength);
        sh.SetFloat("uRuggedFreq", (float)p.RuggedFreq);
        sh.SetFloat("uRuggedLo", (float)p.RuggedLo);
        sh.SetFloat("uRuggedHi", (float)p.RuggedHi);
        sh.SetFloat("uDetailFloor", (float)p.DetailFloor);
        sh.SetFloat("uCraterWeight", (float)p.CraterWeight);
        sh.SetFloat("uCraterDensity", (float)p.CraterDensity);
        sh.SetFloat("uCraterFreq", (float)p.CraterFreq);
        sh.SetFloat("uCraterOctFine", (float)(p.CraterWeight > 0.0 ? terrain.CraterOctavesForSpacing(spacingFine) : 0.0));
        sh.SetFloat("uCraterOctCoarse", (float)(p.CraterWeight > 0.0 ? terrain.CraterOctavesForSpacing(spacingCoarse) : 0.0));
        sh.SetFloat("uVolcanoWeight", (float)p.VolcanoWeight);
        sh.SetFloat("uVolcanoFreq", (float)p.VolcanoFreq);
        sh.SetFloat("uVolcanoDensity", (float)p.VolcanoDensity);
        sh.SetFloat("uMicroWeight", (float)p.MicroWeight);
        sh.SetFloat("uMicroFreq", (float)p.MicroFreq);
        sh.SetFloat("uMicroGain", (float)p.MicroGain);
        sh.SetFloat("uMicroOctFine", (float)(p.MicroWeight > 0.0 ? terrain.OctavesForSpacing(p.MicroFreq, spacingFine, p.MaxMicroOctaves) : 0.0));
        sh.SetFloat("uMicroOctCoarse", (float)(p.MicroWeight > 0.0 ? terrain.OctavesForSpacing(p.MicroFreq, spacingCoarse, p.MaxMicroOctaves) : 0.0));
        sh.SetFloat("uMicroGateFine", (float)(p.MicroWeight > 0.0 ? terrain.LayerGateForSpacing(p.MicroFreq, spacingFine) : 0.0));
        sh.SetFloat("uMicroGateCoarse", (float)(p.MicroWeight > 0.0 ? terrain.LayerGateForSpacing(p.MicroFreq, spacingCoarse) : 0.0));
        sh.SetFloat("uStrataWeight", (float)p.StrataWeight);
        sh.SetFloat("uStrataFreq", (float)p.StrataFreq);
        sh.SetFloat("uStrataSteps", p.StrataSteps);
        sh.SetFloat("uStrataSharp", (float)p.StrataSharp);
        sh.SetFloat("uDuneWeight", (float)p.DuneWeight);
        sh.SetFloat("uDuneFreq", (float)p.DuneFreq);
        sh.SetFloat("uDuneWarpFreq", (float)p.DuneWarpFreq);
        sh.SetFloat("uDuneWarpAmp", (float)p.DuneWarpAmp);
        sh.SetFloat("uErgFreq", (float)p.ErgFreq);
        sh.SetVector3("uDuneDir", p.DuneDir);
        sh.SetFloat("uDuneGateFine", (float)(p.DuneWeight > 0.0 ? terrain.LayerGateForSpacing(p.DuneFreq, spacingFine) : 0.0));
        sh.SetFloat("uDuneGateCoarse", (float)(p.DuneWeight > 0.0 ? terrain.LayerGateForSpacing(p.DuneFreq, spacingCoarse) : 0.0));
        sh.SetFloat("uCrackWeight", (float)p.CrackWeight);
        sh.SetFloat("uCrackFreq", (float)p.CrackFreq);
        sh.SetFloat("uCrackOctFine", (float)(p.CrackWeight > 0.0 ? terrain.OctavesForSpacing(p.CrackFreq, spacingFine, 5) : 0.0));
        sh.SetFloat("uCrackOctCoarse", (float)(p.CrackWeight > 0.0 ? terrain.OctavesForSpacing(p.CrackFreq, spacingCoarse, 5) : 0.0));
        sh.SetFloat("uCrackGateFine", (float)(p.CrackWeight > 0.0 ? terrain.LayerGateForSpacing(p.CrackFreq * 8.0, spacingFine) : 0.0));
        sh.SetFloat("uCrackGateCoarse", (float)(p.CrackWeight > 0.0 ? terrain.LayerGateForSpacing(p.CrackFreq * 8.0, spacingCoarse) : 0.0));
    }

    /// <summary>Octave count for a band-limit, clamped to both the LOD budget and the float-safe ceiling
    /// (direct-coordinate layers: continents, mountains).</summary>
    private static double OctClamp(PlanetTerrain terrain, double baseFreq, double spacing, int max)
    {
        double lod = terrain.OctavesForSpacing(baseFreq, spacing, max);
        double safe = Math.Floor(Math.Log2(FloatSafeFreq / Math.Max(1.0, baseFreq))) + 1.0;
        return Math.Max(0.0, Math.Min(lod, safe));
    }

    /// <summary>Octave count for a split-coordinate layer (detail, mountains) — same LOD band-limit, but
    /// against the higher <see cref="DetailSafeFreq"/> ceiling since these sample in precise split
    /// coordinates rather than a planet-scale dir*freq.</summary>
    private static double SplitOctClamp(PlanetTerrain terrain, double baseFreq, double spacing, int max)
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
        if (_equirectFbo != 0) _gl.DeleteFramebuffer(_equirectFbo);
        _shader.Dispose();
        _surfShader.Dispose();
        _equirectShader.Dispose();
    }
}
