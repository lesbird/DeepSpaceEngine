using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws the resident galaxies (from <see cref="GalaxyCatalogPager"/>) across the two farthest LOD
/// tiers, cross-faded by apparent angular size:
///
///  • <b>Point</b> — a galaxy millions of light-years away is far beyond the camera's far plane, so
///    (like the backdrop dome) it is projected by <i>direction only</i> onto a fixed dome radius inside
///    the frustum, sized/brightened by apparent flux (star count ÷ distance²). A faint star far off,
///    swelling into a bright point as you approach.
///  • <b>Impostor</b> — once the galaxy is large enough to resolve, an oriented disk billboard fades in:
///    a quad lying in the galaxy's real disk plane (so it shows the disk's inclination — face-on down
///    the normal, edge-on along it), shaded with a procedural spiral/elliptical pattern (bulge + arms +
///    dust), scaled to subtend the galaxy's true angular size.
///
/// The two overlap across an angular-size band so the swap never pops. The galaxy the camera is inside
/// is excluded entirely — its own stars stream via the catalog. A later phase adds a volumetric star
/// cloud between the impostor and the resolved catalog.
/// </summary>
public sealed class GalaxyRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>Overall brightness multiplier on the galaxy point sprites.</summary>
    public float Brightness = 1.8f;
    /// <summary>Scales apparent point size (px) with the brightness cue.</summary>
    public float SizeScale = 16.0f;
    /// <summary>Floor size (px): even a far galaxy stays a clearly visible dot, distinct from a backdrop star.</summary>
    public float MinSizePx = 4.0f;
    public float MaxSizePx = 28.0f;
    /// <summary>Overall brightness multiplier on the impostor disks.</summary>
    public float ImpostorBrightness = 1.3f;

    /// <summary>Galaxy point sprites drawn last frame (HUD/debug).</summary>
    public int LastDrawn { get; private set; }
    /// <summary>Galaxy impostor disks drawn last frame (HUD/debug).</summary>
    public int LastImpostors { get; private set; }

    // Apparent angular radius (galaxy radius ÷ distance) over which the point fades out and the impostor
    // fades in. Below Lo: point only; above Hi: impostor only; between: both, complementary alphas.
    private const float RatioLo = 0.010f;
    private const float RatioHi = 0.030f;

    // Direction-only projection radii — well inside the camera far plane (1e18 m), like the backdrop
    // dome. Impostors sit a little nearer than points; both are additive with depth off, so their exact
    // depths don't matter, only that each is inside the frustum.
    private const float PointDomeRadius = 1.0e15f;
    private const float ImpostorRenderDist = 1.0e14f;

    // Brightness-cue reference: a Milky-Way-class galaxy (this many stars) at this distance reads as
    // cue ≈ 1 — a clear point. Closer ⇒ brighter/bigger (clamped); farther ⇒ fades to the floor.
    private const double RefStarCount = 2.0e11;
    private const double RefDistLy = 1.0e6;

    private const int FloatsPerPoint = 8; // dir(3) + color(3) + size(1) + brightness(1)

    private const string PointVert = @"#version 410 core
layout(location = 0) in vec3 aDir;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
layout(location = 3) in float aBright;
uniform mat4 uViewProj;
uniform float uRadius;
out vec3 vColor;
out float vBright;
void main() {
    vColor = aColor;
    vBright = aBright;
    gl_Position = uViewProj * vec4(aDir * uRadius, 1.0);
    gl_PointSize = aSize;
}";

    private const string PointFrag = @"#version 410 core
in vec3 vColor;
in float vBright;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.35, r2);
    FragColor = vec4(vColor * (vBright * uBrightness), 1.0) * a;
}";

    // Impostor: a unit quad (from gl_VertexID, triangle strip) placed in the galaxy's disk plane via
    // two in-plane half-axes, then shaded as a disk in [-1,1]² uv space.
    private const string ImpostorVert = @"#version 410 core
uniform mat4 uViewProj;
uniform vec3 uCenter;
uniform vec3 uAxisU;
uniform vec3 uAxisV;
out vec2 vUv;
void main() {
    vec2 c = vec2((gl_VertexID == 1 || gl_VertexID == 3) ? 1.0 : -1.0,
                  (gl_VertexID >= 2) ? 1.0 : -1.0);
    vUv = c;
    vec3 world = uCenter + c.x * uAxisU + c.y * uAxisV;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

    private const string ImpostorFrag = @"#version 410 core
in vec2 vUv;
uniform vec3  uColor;
uniform float uArmCount;
uniform float uArmStrength;
uniform float uSwirl;
uniform float uBrightness;
uniform float uAlpha;
out vec4 FragColor;

float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 34.56); return fract(p.x * p.y); }
float vnoise(vec2 x) {
    vec2 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x),
               mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; } return s; }

void main() {
    float r = length(vUv);
    if (r > 1.0) { FragColor = vec4(0.0); return; }
    float phi = atan(vUv.y, vUv.x);

    float core = exp(-r * r / (2.0 * 0.05 * 0.05)); // bright nucleus
    float disk = exp(-r / 0.32);                     // exponential disk
    float arm = 1.0;
    if (uArmCount > 0.5)
        arm = 1.0 + uArmStrength * cos(uArmCount * phi - uSwirl * log(r + 0.06));
    float dust = 0.55 + 0.45 * fbm(vUv * 5.0 + 3.0);

    float intensity = core * 1.3 + disk * max(arm, 0.12) * dust;
    float edge = smoothstep(1.0, 0.6, r);
    FragColor = vec4(uColor * (intensity * uBrightness), 1.0) * (edge * uAlpha);
}";

    private readonly GL _gl;
    private readonly Shader _pointShader;
    private readonly Shader _impostorShader;
    private readonly uint _pointVao;
    private readonly uint _pointVbo;
    private readonly uint _impostorVao;
    private float[] _data = new float[FloatsPerPoint * 64];

    // One impostor to draw this frame.
    private struct Impostor
    {
        public Vector3D<float> Center, AxisU, AxisV, Color;
        public float ArmCount, ArmStrength, Swirl, Alpha;
    }
    private readonly List<Impostor> _impostors = new();

    public unsafe GalaxyRenderer(GL gl)
    {
        _gl = gl;
        _pointShader = new Shader(gl, PointVert, PointFrag);
        _impostorShader = new Shader(gl, ImpostorVert, ImpostorFrag);

        _pointVao = gl.GenVertexArray();
        _pointVbo = gl.GenBuffer();
        gl.BindVertexArray(_pointVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _pointVbo);
        uint stride = FloatsPerPoint * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.BindVertexArray(0);

        _impostorVao = gl.GenVertexArray(); // empty; vertices come from gl_VertexID
    }

    /// <summary>Build this frame's point + impostor draws from the resident galaxies and render them.
    /// Skips the galaxy the camera is inside (its stars render via the catalog). Call in the early
    /// additive slot, alongside the backdrop, into the bound scene buffer.</summary>
    public unsafe void Render(Camera camera, GalaxyCatalogPager galaxies)
    {
        LastDrawn = 0;
        LastImpostors = 0;
        if (!Enabled) return;

        ulong? exclude = galaxies.IsInside ? galaxies.Containing.Id : null;

        int n = 0;
        _impostors.Clear();
        foreach (GalaxyCatalog block in galaxies.LoadedBlocks)
            foreach (Galaxy g in block.Galaxies)
            {
                if (exclude is { } ex && g.Id == ex) continue;

                Vector3D<double> rel = g.Center.DeltaMeters(camera.Position); // camera → galaxy
                double distM = rel.Length;
                if (distM < 1.0) continue;

                // Normalise in DOUBLE then cast: at ~1e22 m a float-cast component squared (~1e44)
                // overflows float's ~3.4e38 max to infinity, collapsing the sprite to a NaN direction.
                var dir = new Vector3D<float>(
                    (float)(rel.X / distM), (float)(rel.Y / distM), (float)(rel.Z / distM));

                // Cross-fade weight by apparent angular radius (galaxy radius ÷ distance).
                double ratio = g.RadiusMeters / distM;
                float impostorMix = Smoothstep(RatioLo, RatioHi, (float)ratio);

                // --- Point sprite (faded out as the impostor takes over) ---
                float pointFade = 1f - impostorMix;
                if (pointFade > 0.001f)
                {
                    double distLy = distM / MathUtil.LightYear;
                    float cue = (float)(Math.Sqrt(g.StarCount / RefStarCount) * (RefDistLy / distLy));
                    float size = Math.Clamp(MinSizePx + SizeScale * cue, MinSizePx, MaxSizePx);
                    float bright = Math.Clamp(0.8f + 1.2f * cue, 0.7f, 3.0f) * pointFade;

                    EnsureCapacity(n + 1);
                    int o = n * FloatsPerPoint;
                    _data[o + 0] = dir.X; _data[o + 1] = dir.Y; _data[o + 2] = dir.Z;
                    _data[o + 3] = g.Color.X; _data[o + 4] = g.Color.Y; _data[o + 5] = g.Color.Z;
                    _data[o + 6] = size; _data[o + 7] = bright;
                    n++;
                }

                // --- Impostor disk (faded in) ---
                if (impostorMix > 0.001f)
                {
                    DiskBasis(g.DiskNormal, out Vector3D<float> u, out Vector3D<float> v);
                    // Half-extent at the render distance that reproduces the galaxy's true angular size:
                    // half / ImpostorRenderDist = radius / distance. Clamp the ratio so a near pass (or
                    // the boundary where you enter the galaxy) can't blow the quad up past the frustum.
                    float half = (float)(ImpostorRenderDist * Math.Min(ratio, 1.2));
                    GalaxyShape(g.Type, out float armCount, out float armStrength, out float swirl);
                    _impostors.Add(new Impostor
                    {
                        Center = dir * ImpostorRenderDist,
                        AxisU = u * half,
                        AxisV = v * half,
                        Color = g.Color,
                        ArmCount = armCount,
                        ArmStrength = armStrength,
                        Swirl = swirl,
                        Alpha = impostorMix,
                    });
                }
            }

        if (n == 0 && _impostors.Count == 0) return;

        // Additive, depth-independent — the same regime as the backdrop dome / near-field star sprites.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        Matrix4X4<float> vp = camera.ViewMatrix * camera.ProjectionMatrix;

        // --- Points ---
        if (n > 0)
        {
            _gl.Enable(EnableCap.ProgramPointSize);
            _gl.BindVertexArray(_pointVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _pointVbo);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(_data, 0, n * FloatsPerPoint), BufferUsageARB.StreamDraw);
            _pointShader.Use();
            _pointShader.SetMatrix("uViewProj", vp);
            _pointShader.SetFloat("uRadius", PointDomeRadius);
            _pointShader.SetFloat("uBrightness", Brightness);
            _gl.DrawArrays(PrimitiveType.Points, 0, (uint)n);
            LastDrawn = n;
        }

        // --- Impostors (one draw each; usually only a handful are close enough) ---
        if (_impostors.Count > 0)
        {
            _impostorShader.Use();
            _impostorShader.SetMatrix("uViewProj", vp);
            _impostorShader.SetFloat("uBrightness", ImpostorBrightness);
            _gl.BindVertexArray(_impostorVao);
            foreach (Impostor imp in _impostors)
            {
                _impostorShader.SetVector3("uCenter", imp.Center);
                _impostorShader.SetVector3("uAxisU", imp.AxisU);
                _impostorShader.SetVector3("uAxisV", imp.AxisV);
                _impostorShader.SetVector3("uColor", imp.Color);
                _impostorShader.SetFloat("uArmCount", imp.ArmCount);
                _impostorShader.SetFloat("uArmStrength", imp.ArmStrength);
                _impostorShader.SetFloat("uSwirl", imp.Swirl);
                _impostorShader.SetFloat("uAlpha", imp.Alpha);
                _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
            LastImpostors = _impostors.Count;
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>Procedural arm parameters per morphology (spirals get arms; ellipticals/dwarfs are smooth).</summary>
    private static void GalaxyShape(GalaxyType type, out float armCount, out float armStrength, out float swirl)
    {
        switch (type)
        {
            case GalaxyType.Spiral: armCount = 2; armStrength = 0.6f; swirl = 5f; break;
            case GalaxyType.Irregular: armCount = 1; armStrength = 0.45f; swirl = 8f; break;
            default: armCount = 0; armStrength = 0; swirl = 0; break; // Elliptical, Dwarf
        }
    }

    /// <summary>A stable orthonormal basis (u, v) spanning the galaxy's disk plane (⊥ to the normal).</summary>
    private static void DiskBasis(Vector3D<float> normal, out Vector3D<float> u, out Vector3D<float> v)
    {
        Vector3D<float> n = normal.LengthSquared < 1e-12f ? new Vector3D<float>(0, 1, 0) : Vector3D.Normalize(normal);
        Vector3D<float> a = Math.Abs(n.Y) < 0.99f ? new Vector3D<float>(0, 1, 0) : new Vector3D<float>(1, 0, 0);
        u = Vector3D.Normalize(Vector3D.Cross(a, n));
        v = Vector3D.Cross(n, u);
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void EnsureCapacity(int points)
    {
        int needed = points * FloatsPerPoint;
        if (_data.Length >= needed) return;
        int len = _data.Length;
        while (len < needed) len *= 2;
        Array.Resize(ref _data, len);
    }

    public void Dispose()
    {
        _pointShader.Dispose();
        _impostorShader.Dispose();
        _gl.DeleteBuffer(_pointVbo);
        _gl.DeleteVertexArray(_pointVao);
        _gl.DeleteVertexArray(_impostorVao);
    }
}
