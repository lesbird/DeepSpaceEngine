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

    // Far enough to sit behind the near-field star bubble; exact value is irrelevant because the
    // rotation-only view has no translation, so dome points project by direction alone.
    private const float DomeRadius = 1.0e15f;

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
void main() {
    vColor = aColor;
    gl_Position = uViewProj * vec4(aDir * uRadius, 1.0);
    gl_PointSize = clamp(aSize * uPointScale, uMinSize, uMaxSize);
}";

    private const string DomeFrag = @"#version 410 core
in vec3 vColor;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float a = exp(-r2 * 2.5);
    FragColor = vec4(vColor * uBrightness, 1.0) * a;
}";

    private readonly GL _gl;
    private readonly Shader _band;
    private readonly Shader _dome;
    private readonly uint _bandVao;
    private readonly uint _domeVao;
    private readonly uint _domeVbo;
    private readonly int _domeCount;

    public unsafe GalaxyBackdrop(GL gl, ulong worldSeed)
    {
        _gl = gl;
        _band = new Shader(gl, BandVert, BandFrag);
        _dome = new Shader(gl, DomeVert, DomeFrag);
        _bandVao = gl.GenVertexArray(); // empty; vertices come from gl_VertexID

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
    /// Draw the band + dome. Call first, into the bound scene buffer, before the streamed stars.
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
        _band.SetFloat("uBrightness", BandBrightness);
        _gl.BindVertexArray(_bandVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // --- Dome stars: direction-only point sprites fixed on the celestial sphere.
        _gl.Enable(EnableCap.ProgramPointSize);
        _dome.Use();
        Matrix4X4<float> viewProj = camera.ViewMatrix * camera.ProjectionMatrix;
        _dome.SetMatrix("uViewProj", viewProj);
        _dome.SetFloat("uRadius", DomeRadius);
        _dome.SetFloat("uPointScale", 1.4f);
        _dome.SetFloat("uMinSize", 1f);
        _dome.SetFloat("uMaxSize", 4f);
        _dome.SetFloat("uBrightness", StarBrightness);
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
        _dome.Dispose();
        _gl.DeleteBuffer(_domeVbo);
        _gl.DeleteVertexArray(_domeVao);
        _gl.DeleteVertexArray(_bandVao);
    }
}
