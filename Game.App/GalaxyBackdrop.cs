using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// The distant-galaxy backdrop: the far field beyond the streamed <see cref="StarRenderer"/>
/// bubble, so deep space isn't flat black. Two additive passes, both drawn <b>first</b> into the
/// scene buffer with depth writes off:
///
///  1. a fullscreen <b>Milky-Way band</b> — a soft glow concentrated on the galactic plane
///     (world XZ), mottled by procedural dust lanes and a two-arm modulation, brightening into a
///     bulge toward the galactic centre; and
///  2. a <b>dome of distant stars</b> (<see cref="BackdropStars"/>) rendered as direction-only
///     point sprites, so they sit fixed on the celestial sphere with no parallax.
///
/// Both are direction-only (built from the camera basis, like <see cref="AtmosphereRenderer"/>),
/// so they're immune to the floating-origin problem and never move as you fly. Because they write
/// no depth, the near-field stars, the solar system, and the atmosphere all composite over them,
/// and any opaque body drawn later correctly occludes the sky behind it.
/// </summary>
public sealed class GalaxyBackdrop : IDisposable
{
    public bool Enabled = true;
    /// <summary>Overall multiplier on the Milky-Way band glow.</summary>
    public float BandBrightness = 0.6f;
    /// <summary>Overall multiplier on the distant dome stars.</summary>
    public float StarBrightness = 1.0f;
    /// <summary>Overall multiplier on the distant emission nebulae.</summary>
    public float NebulaBrightness = 0.7f;

    /// <summary>Extra multiplier applied to the whole painted sky, set each frame by the caller. This
    /// backdrop is the view from <i>inside</i> the Milky Way; out in intergalactic space it makes no
    /// sense, so the caller fades it toward zero once you leave the galaxy — which also stops the fake
    /// band + dome stars from drowning out the real galaxy point sprites.</summary>
    public float ExternalDim = 1.0f;

    // Far enough to sit behind the near-field star bubble; exact value is irrelevant because the
    // rotation-only view has no translation, so dome points project by direction alone.
    private const float DomeRadius = 1.0e15f;

    /// <summary>Maximum nebulae the fragment shader sums (matches the GLSL array size).</summary>
    private const int MaxNebulae = 8;

    private const string BandVert = @"#version 410 core
out vec2 vNdc;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string BandFrag = @"#version 410 core
in vec2 vNdc;
uniform vec3  uForward, uRight, uUp;   // camera basis (render space == world axes)
uniform float uTanHalfFov, uAspect;
uniform vec3  uCenterDir;              // world-axis direction toward the galactic centre
uniform float uBrightness;
out vec4 FragColor;

float hash(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i + vec3(0,0,0)), hash(i + vec3(1,0,0)), f.x),
                   mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x),
                   mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 3; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; }
    return s;
}

void main() {
    vec3 rd = normalize(uForward + vNdc.x * uTanHalfFov * uAspect * uRight + vNdc.y * uTanHalfFov * uUp);

    // Galactic latitude: the plane is world XZ, so rd.y is sin(latitude). A narrow Gaussian makes
    // the band; squared so the falloff edges stay soft.
    float lat = rd.y;
    float band = exp(-(lat * lat) / (2.0 * 0.10 * 0.10));

    // Two-arm longitude modulation + procedural dust lanes biting dark notches out of the band.
    float phi = atan(rd.z, rd.x);
    float arm = 0.70 + 0.30 * cos(2.0 * phi);
    float dust = 0.45 + 0.55 * fbm(rd * 6.0);

    // Central bulge: a warm brightening toward the galactic centre.
    float toCenter = max(dot(rd, uCenterDir), 0.0);
    float bulge = pow(toCenter, 6.0);

    vec3 bandColor  = vec3(0.62, 0.60, 0.52);
    vec3 bulgeColor = vec3(0.85, 0.74, 0.52);
    vec3 col = bandColor * band * arm * dust + bulgeColor * bulge * (0.4 + 0.6 * band);

    FragColor = vec4(col * uBrightness, 1.0); // additive (blend handled by caller)
}";

    // Distant emission nebulae: a fullscreen additive pass summing a handful of seeded, coloured
    // cloud patches fixed on the celestial sphere. Each nebula is a soft angular core (Gaussian in
    // the angle from its direction) textured by domain-warped fbm so it reads as wispy filaments
    // rather than a flat blob. Direction-only, so — like the band — it never parallaxes as you fly.
    private const string NebulaFrag = @"#version 410 core
in vec2 vNdc;
uniform vec3  uForward, uRight, uUp;
uniform float uTanHalfFov, uAspect;
uniform float uBrightness;
uniform int   uCount;
uniform vec3  uDir[8];     // unit direction to each nebula centre
uniform vec3  uColor[8];   // emission colour
uniform vec3  uParams[8];  // x = angular radius (rad), y = brightness, z = noise seed
out vec4 FragColor;

float hash(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i + vec3(0,0,0)), hash(i + vec3(1,0,0)), f.x),
                   mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x),
                   mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; }
    return s;
}

void main() {
    vec3 rd = normalize(uForward + vNdc.x * uTanHalfFov * uAspect * uRight + vNdc.y * uTanHalfFov * uUp);

    vec3 acc = vec3(0.0);
    for (int k = 0; k < uCount; k++) {
        float ca = clamp(dot(rd, uDir[k]), -1.0, 1.0);
        float ang = acos(ca);
        float r = ang / uParams[k].x;        // 0 at centre, 1 at nominal edge
        if (r > 1.5) continue;               // outside this nebula's footprint — skip the noise
        float core = exp(-r * r * 2.3);

        // Domain-warped fbm in a per-nebula-offset sky-direction basis → drifting filaments.
        float seed = uParams[k].z;
        vec3 sp = rd * 4.0 + vec3(seed, seed * 1.7, seed * 0.3);
        float warp = fbm(sp * 0.7);
        float n = fbm(sp + warp * 1.2);
        n = pow(clamp(n * 1.3, 0.0, 1.0), 1.7);

        acc += uColor[k] * (core * n * uParams[k].y);
    }
    FragColor = vec4(acc * uBrightness, 1.0); // additive (blend handled by caller)
}";

    private const string DomeVert = @"#version 410 core
layout(location = 0) in vec3 aDir;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
uniform mat4 uViewProj;
uniform float uRadius;
uniform float uPointScale;
uniform float uMinSize;
uniform float uMaxSize;
out vec3 vColor;
out float vAtten;
void main() {
    vColor = aColor;
    gl_Position = uViewProj * vec4(aDir * uRadius, 1.0);

    // A star that ""wants"" to be smaller than the floor can't be drawn faithfully as a point
    // (sub-pixel points pop in and out as their center crosses pixel boundaries when the camera
    // rotates). Instead we pin it to the floor size and dim it so its total emitted light
    // (~ area * brightness) is preserved. The result glides smoothly instead of scintillating.
    float desired = aSize * uPointScale;
    gl_PointSize = clamp(desired, uMinSize, uMaxSize);
    float shrink = min(desired, uMinSize) / uMinSize; // 1.0 once at/above the floor
    vAtten = shrink * shrink;
}";

    private const string DomeFrag = @"#version 410 core
in vec3 vColor;
in float vAtten;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    // Soft Gaussian core faded smoothly to zero at the rim (no hard discard, which itself
    // shimmers under sub-pixel motion).
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.55, r2);
    FragColor = vec4(vColor * uBrightness, 1.0) * (a * vAtten);
}";

    private readonly GL _gl;
    private readonly Shader _band;
    private readonly Shader _nebula;
    private readonly Shader _dome;
    private readonly uint _bandVao;
    private readonly uint _domeVao;
    private readonly uint _domeVbo;
    private readonly int _domeCount;

    // Per-nebula params, baked once from the world seed and uploaded each frame.
    private readonly int _nebCount;
    private readonly Vector3D<float>[] _nebDir;
    private readonly Vector3D<float>[] _nebColor;
    private readonly Vector3D<float>[] _nebParams; // x = angular radius, y = brightness, z = seed

    public unsafe GalaxyBackdrop(GL gl, ulong worldSeed)
    {
        _gl = gl;
        _band = new Shader(gl, BandVert, BandFrag);
        _nebula = new Shader(gl, BandVert, NebulaFrag); // shares the band's fullscreen-triangle vertex stage
        _dome = new Shader(gl, DomeVert, DomeFrag);
        _bandVao = gl.GenVertexArray(); // empty; vertices come from gl_VertexID

        BuildNebulae(worldSeed, out _nebCount, out _nebDir, out _nebColor, out _nebParams);

        var stars = new BackdropStars(worldSeed);
        _domeCount = stars.Count;

        _domeVao = gl.GenVertexArray();
        gl.BindVertexArray(_domeVao);
        _domeVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _domeVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)stars.Interleaved, BufferUsageARB.StaticDraw);

        uint stride = BackdropStars.FloatsPerStar * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Bake the fixed nebula set from the world seed: directions concentrated toward the galactic
    /// plane (world XZ, like the band and backdrop stars), with an emission-nebula palette and
    /// varied angular size / brightness. Pure data, computed once.
    /// </summary>
    private static void BuildNebulae(ulong worldSeed, out int count,
        out Vector3D<float>[] dir, out Vector3D<float>[] color, out Vector3D<float>[] param)
    {
        var rng = new DeterministicRng(Hashing.Combine(worldSeed, 0x4EB17A5UL));
        count = 6;

        // Emission/reflection nebula hues: H II reds, reflection blues, teal, and a warm gold.
        Vector3D<float>[] palette =
        {
            new(0.95f, 0.28f, 0.40f), // hydrogen-alpha red/pink
            new(0.35f, 0.50f, 0.95f), // reflection blue
            new(0.30f, 0.80f, 0.70f), // teal/cyan
            new(0.70f, 0.38f, 0.88f), // magenta/violet
            new(0.92f, 0.70f, 0.40f), // warm gold
            new(0.40f, 0.78f, 0.45f), // oxygen green
        };

        dir = new Vector3D<float>[count];
        color = new Vector3D<float>[count];
        param = new Vector3D<float>[count];
        for (int i = 0; i < count; i++)
        {
            // Hug the galactic plane: cube a uniform [-1,1] so most sit near y = 0 (like BackdropStars).
            double u = rng.Range(-1.0, 1.0);
            double y = u * u * u;
            double cosB = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            double phi = rng.Range(0.0, 2.0 * Math.PI);
            dir[i] = new Vector3D<float>(
                (float)(cosB * Math.Cos(phi)), (float)y, (float)(cosB * Math.Sin(phi)));

            color[i] = palette[rng.RangeInt(0, palette.Length)];
            float radius = (float)rng.Range(0.12, 0.34);     // angular radius, radians (~7–19°)
            float bright = (float)rng.Range(0.35, 0.85);
            float seed = (float)rng.Range(0.0, 50.0);
            param[i] = new Vector3D<float>(radius, bright, seed);
        }
    }

    /// <summary>
    /// Draw the band + nebulae + dome. Call first, into the bound scene buffer, before the streamed stars.
    /// </summary>
    public unsafe void Render(Camera camera)
    {
        if (!Enabled) return;

        // Additive, depth-independent — same regime as the near-field star sprites.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        // --- Band: a fullscreen glow. Direction to the galactic centre is the way to the origin;
        // at the very origin it's undefined, so fall back to +X.
        Vector3D<float> toCenter = UniversePosition.Origin.ToCameraRelative(camera.Position);
        toCenter = toCenter.LengthSquared > 1e-6f ? Vector3D.Normalize(toCenter) : new Vector3D<float>(1f, 0f, 0f);

        _band.Use();
        _band.SetVector3("uForward", camera.Forward);
        _band.SetVector3("uRight", camera.Right);
        _band.SetVector3("uUp", camera.Up);
        _band.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
        _band.SetFloat("uAspect", camera.AspectRatio);
        _band.SetVector3("uCenterDir", toCenter);
        _band.SetFloat("uBrightness", BandBrightness * ExternalDim);
        _gl.BindVertexArray(_bandVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // --- Nebulae: another fullscreen additive pass, summing the seeded cloud patches. Shares
        // the band's empty VAO (vertices come from gl_VertexID) and camera basis.
        if (NebulaBrightness > 0f && _nebCount > 0)
        {
            _nebula.Use();
            _nebula.SetVector3("uForward", camera.Forward);
            _nebula.SetVector3("uRight", camera.Right);
            _nebula.SetVector3("uUp", camera.Up);
            _nebula.SetFloat("uTanHalfFov", MathF.Tan(camera.FovRadians * 0.5f));
            _nebula.SetFloat("uAspect", camera.AspectRatio);
            _nebula.SetFloat("uBrightness", NebulaBrightness * ExternalDim);
            _nebula.SetInt("uCount", _nebCount);
            for (int i = 0; i < _nebCount; i++)
            {
                _nebula.SetVector3($"uDir[{i}]", _nebDir[i]);
                _nebula.SetVector3($"uColor[{i}]", _nebColor[i]);
                _nebula.SetVector3($"uParams[{i}]", _nebParams[i]);
            }
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        // --- Dome stars: direction-only point sprites fixed on the celestial sphere.
        _gl.Enable(EnableCap.ProgramPointSize);
        _dome.Use();
        Matrix4X4<float> viewProj = camera.ViewMatrix * camera.ProjectionMatrix;
        _dome.SetMatrix("uViewProj", viewProj);
        _dome.SetFloat("uRadius", DomeRadius);
        _dome.SetFloat("uPointScale", 1.4f);
        // Floor of ~2.5px: below this a star can't be drawn without sub-pixel popping, so the
        // shader holds it at the floor and dims it instead (see DomeVert/DomeFrag).
        _dome.SetFloat("uMinSize", 2.5f);
        _dome.SetFloat("uMaxSize", 4f);
        _dome.SetFloat("uBrightness", StarBrightness * ExternalDim);
        _gl.BindVertexArray(_domeVao);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)_domeCount);
        _gl.BindVertexArray(0);

        // Restore the defaults the rest of the frame expects (matching StarRenderer).
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _band.Dispose();
        _nebula.Dispose();
        _dome.Dispose();
        _gl.DeleteBuffer(_domeVbo);
        _gl.DeleteVertexArray(_domeVao);
        _gl.DeleteVertexArray(_bandVao);
    }
}
