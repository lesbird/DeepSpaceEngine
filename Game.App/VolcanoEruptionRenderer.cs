using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Lava fountains erupting from the volcano vents on lava worlds. Sites come from
/// <see cref="PlanetTerrain.VolcanoSites"/> — the same cellular field the GPU tiles bake — so every
/// plume rises from exactly the summit caldera you see glowing in the terrain shader.
///
/// The particles are <b>stateless</b>: each lava bomb's position is a pure function of
/// (particle id, time) evaluated in the vertex shader — a ballistic arc from a hashed launch
/// velocity, looping over its lifetime — so there is no CPU simulation, no buffers to update, and
/// the fountains cost one small instanced draw per erupting vent. Eruptions pulse on a per-volcano
/// seeded duty cycle (each vent erupts for a while, rests, repeats) so the planet feels alive rather
/// than uniformly on. Bombs render additively and are pushed bright so the bloom pass haloes them,
/// and they depth-test against the terrain (drawn in the same pass), so the cone occludes its own
/// far-side fountain. Vent heights are read from the GPU-terrain float mirror lazily per site.
/// </summary>
public sealed class VolcanoEruptionRenderer : IDisposable
{
    private const int BombsPerVent = 1600;      // instanced lava bombs per erupting vent
    private const int PuffsPerVent = 48;        // big soft glow puffs forming the eruption column
    private const float VentMaskMin = 0.15f;    // buried calderas don't erupt

    private const string VertexSource = @"#version 410 core
uniform mat4 uViewProj;      // camera-relative view-projection (model is the vent translation below)
uniform vec3 uVentRel;       // vent position, camera-relative (metres)
uniform vec3 uUp;            // planet-local up at the vent
uniform vec3 uT1, uT2;       // surface tangents at the vent
uniform vec3 uCamRight, uCamUp; // billboard axes
uniform float uTime;         // seconds
uniform float uSeed;         // per-volcano seed
uniform float uEnv;          // eruption envelope [0,1] — fraction of bombs in flight
uniform float uV0;           // launch speed (m/s)
uniform float uG;            // effective gravity (m/s^2)
uniform float uLife;         // full arc duration (s) — also the column puffs' rise time
uniform float uSizeM;        // base bomb size (metres)
uniform float uMinSize;      // distance-scaled minimum size so far fountains keep a sparkle (metres)
uniform float uPlumeH;       // plume height (metres) — sizes/positions the glow column
uniform float uMode;         // 0 = ballistic lava bombs, 1 = rising glow-column puffs
out vec2 vUV;
out float vAge;
out float vRand;

float hash(float n) { return fract(sin(n) * 43758.5453123); }

void main() {
    // Unit-quad corner from the vertex id (two triangles), particle identity from the instance id.
    int corner = gl_VertexID;
    vec2 c = vec2((corner == 1 || corner == 2 || corner == 4) ? 1.0 : -1.0,
                  (corner == 2 || corner == 4 || corner == 5) ? 1.0 : -1.0);
    float id = float(gl_InstanceID);
    float h1 = hash(id * 127.1 + uSeed);
    float h2 = hash(id * 311.7 + uSeed + 1.3);
    float h3 = hash(id * 74.7 + uSeed + 2.6);
    float h4 = hash(id * 269.5 + uSeed + 3.9);
    float h5 = hash(id * 183.3 + uSeed + 5.2);

    // Envelope thins the fountain rather than snapping it: each particle has a fixed threshold.
    if (h4 > uEnv) { gl_Position = vec4(2.0, 2.0, 2.0, 0.0); vUV = vec2(0.0); vAge = 0.0; vRand = 0.0; return; }

    vec3 pos;
    float age, size;
    if (uMode > 0.5) {
        // Glow column: huge soft puffs rising from the vent to the plume top, drifting outward as
        // they climb — the part of the eruption that reads from far away.
        age = fract(uTime / uLife + h1);                 // 0 at the vent → 1 at the top
        float ang = 6.2831853 * h2;
        float drift = (0.08 + 0.45 * age) * uPlumeH * (0.3 + 0.7 * h5);
        pos = uVentRel + uUp * (age * uPlumeH * (0.85 + 0.3 * h3))
            + (uT1 * cos(ang) + uT2 * sin(ang)) * drift * 0.4;
        size = max(uPlumeH * mix(0.12, 0.32, age) * (0.6 + 0.8 * h3), uMinSize);
    } else {
        // Ballistic lava bombs looping over their arc.
        float tau = mod(uTime + h1 * uLife, uLife);
        float ang = 6.2831853 * h2;
        const float spread = 0.30;                       // tangential spread (fraction of up speed)
        vec3 vel = uUp * (uV0 * (0.75 + 0.5 * h3))
                 + (uT1 * cos(ang) + uT2 * sin(ang)) * (uV0 * spread * (0.2 + 0.8 * h5));
        pos = uVentRel + vel * tau - uUp * (0.5 * uG * tau * tau);
        age = tau / uLife;
        size = max(uSizeM * (0.6 + 0.8 * h5) * (1.0 + 2.2 * age), uMinSize);
    }
    pos += (uCamRight * c.x + uCamUp * c.y) * size;

    vUV = c; vAge = age; vRand = h2;
    gl_Position = uViewProj * vec4(pos, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vUV;
in float vAge;
in float vRand;
uniform float uGlow;       // emissive brightness (pushed >1 so the bloom pass haloes the fountain)
uniform float uMode;       // 0 = bombs (hot, tight), 1 = column puffs (soft, dimmer)
out vec4 FragColor;
void main() {
    float r2 = dot(vUV, vUV);
    if (r2 > 1.0) discard;
    // Cooling ramp: white-hot at launch → orange → dark red as the particle ages/climbs.
    vec3 hot = vec3(1.9, 1.55, 0.95);
    vec3 mid = vec3(1.6, 0.55, 0.10);
    vec3 cold = vec3(0.45, 0.06, 0.015);
    vec3 col = mix(hot, mid, smoothstep(0.05, 0.45, vAge));
    col = mix(col, cold, smoothstep(0.45, 0.95, vAge));
    float core, strength;
    if (uMode > 0.5) {
        core = exp(-r2 * 1.6);                           // big soft puff
        strength = 0.30 * (1.0 - smoothstep(0.70, 1.0, vAge)); // column fades out at the top
    } else {
        core = exp(-r2 * 3.5);                           // tight bomb with a hot centre
        strength = 1.0 - smoothstep(0.82, 1.0, vAge);    // bombs dim out as they land
    }
    FragColor = vec4(col * (core * uGlow * strength * (0.7 + 0.6 * vRand)), 1.0);
}";

    private struct Site
    {
        public Vector3D<double> Dir;
        public double FootprintM;
        public double PeakM;
        public float Vent;
        public float Rand;
        public double VentHeightM;   // GPU-terrain surface height at the vent (lazily filled)
        public bool HeightKnown;
    }

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _emptyVao;   // attributeless instanced quad needs a bound VAO in core profile

    private CelestialBody? _body;
    private PlanetTerrain? _terrain;
    private Site[] _sites = Array.Empty<Site>();

    /// <summary>Erupting vents drawn last frame (HUD diagnostic).</summary>
    public int ActiveVents { get; private set; }

    public VolcanoEruptionRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>Track the active terrain body. Only lava worlds get eruption sites; anything else
    /// clears them. Cheap when the body hasn't changed.</summary>
    public void SetBody(CelestialBody? body, PlanetTerrain? terrain)
    {
        if (ReferenceEquals(body, _body) && ReferenceEquals(terrain, _terrain)) return;
        _body = body;
        _terrain = terrain;
        _sites = Array.Empty<Site>();
        if (body == null || terrain == null || body.Type != PlanetType.Lava) return;

        PlanetTerrain.VolcanoSite[] all = terrain.VolcanoSites();
        var sites = new List<Site>(all.Length);
        foreach (PlanetTerrain.VolcanoSite s in all)
        {
            if (s.VentMask < VentMaskMin) continue; // caldera never opens at the surface — dormant
            sites.Add(new Site
            {
                Dir = s.Direction, FootprintM = s.FootprintRadiusM, PeakM = s.PeakHeightM,
                Vent = s.VentMask, Rand = s.Rand,
            });
        }
        _sites = sites.ToArray();
    }

    /// <summary>Drop cached vent heights (call after a terrain rebuild — live tuning may have moved
    /// the surface the fountains sit on).</summary>
    public void Invalidate()
    {
        for (int i = 0; i < _sites.Length; i++) _sites[i].HeightKnown = false;
    }

    /// <summary>Draw the fountains of every erupting vent in range. Runs inside the terrain pass —
    /// same projection (near/far) and depth buffer, so bombs are occluded by the cones and the
    /// atmosphere composites over them. Depth-tested but not depth-writing (additive light).</summary>
    public void Render(Camera camera, float time, float near, float far, int viewportHeight)
    {
        ActiveVents = 0;
        if (_body == null || _terrain == null || _sites.Length == 0) return;
        // The volcano cones are baked only by the GPU tile path; the CPU fallback terrain has no
        // cones, so fountains there would rise out of empty ground.
        if (!TerrainTuning.GpuTerrain) return;
        float strength = Math.Max(0f, TerrainTuning.EruptionStrength);
        if (strength <= 0f) return;

        Matrix4X4<float> viewProj = camera.ViewMatrix *
            MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far);
        float pixelArc = camera.FovRadians / Math.Max(1, viewportHeight);
        bool first = true;

        for (int i = 0; i < _sites.Length; i++)
        {
            ref Site s = ref _sites[i];

            // Seeded duty cycle: erupt for ~40% of a 1.5–3.5 minute period, smoothly ramping in/out.
            double period = 90.0 + 120.0 * s.Rand;
            float u = (float)(((time + s.Rand * 977.0) % period) / period);
            const float duty = 0.42f;
            float env = Smoothstep(0f, 0.08f, u) * (1f - Smoothstep(duty - 0.10f, duty, u));
            env *= s.Vent; // half-buried calderas erupt weakly
            if (env <= 0.02f) continue;

            // The plume rises to a healthy fraction of the cone's own height — big volcanoes throw
            // higher, and at these game-scale cones (tens of km) the plumes are mountain-sized.
            double plumeH = Math.Clamp(0.55 * s.PeakM, 4000.0, 45_000.0);
            if (!s.HeightKnown)
            {
                s.VentHeightM = _terrain.GpuHeightAt(s.Dir, 0.0); // caldera floor of the baked surface
                s.HeightKnown = true;
            }

            UniversePosition vent = _body.CurrentPosition.Translated(s.Dir * (_terrain.Radius + s.VentHeightM));
            Vector3D<float> rel = vent.ToCameraRelative(camera.Position);
            float dist = rel.Length;
            float range = (float)Math.Max(200_000.0, plumeH * 40.0);
            if (dist > range) continue;

            if (first)
            {
                first = false;
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); // additive: bombs are light sources
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(false);
                _gl.BindVertexArray(_emptyVao);
                _shader.Use();
                _shader.SetMatrix("uViewProj", viewProj);
                _shader.SetVector3("uCamRight", camera.Right);
                _shader.SetVector3("uCamUp", camera.Up);
                _shader.SetFloat("uTime", time);
                _shader.SetFloat("uGlow", 4.0f * strength);
            }

            // Stylised ballistics: pick the arc duration, derive speed/gravity from the plume height
            // (h = v0·T/4, g = 2·v0/T). Real surface gravity at these game-scale cones would give
            // many-minute arcs; a fixed 22–40 s loop keeps the fountains reading as violent.
            double life = 22.0 + 18.0 * Frac(s.Rand * 7.31);
            double v0 = 4.0 * plumeH / life;
            double gEff = 2.0 * v0 / life;

            var up = new Vector3D<float>((float)s.Dir.X, (float)s.Dir.Y, (float)s.Dir.Z);
            Vector3D<float> t1 = Vector3D.Normalize(Vector3D.Cross(
                Math.Abs(s.Dir.Y) < 0.99 ? new Vector3D<float>(0, 1, 0) : new Vector3D<float>(1, 0, 0), up));
            Vector3D<float> t2 = Vector3D.Cross(up, t1);

            _shader.SetVector3("uVentRel", rel);
            _shader.SetVector3("uUp", up);
            _shader.SetVector3("uT1", t1);
            _shader.SetVector3("uT2", t2);
            _shader.SetFloat("uSeed", s.Rand * 113f);
            _shader.SetFloat("uEnv", env);
            _shader.SetFloat("uV0", (float)v0);
            _shader.SetFloat("uG", (float)gEff);
            _shader.SetFloat("uPlumeH", (float)plumeH);
            _shader.SetFloat("uMinSize", dist * pixelArc * 3.0f); // a few pixels even from far out

            // Pass 1 — ballistic lava bombs (big glowing blobs, tight cores).
            _shader.SetFloat("uMode", 0f);
            _shader.SetFloat("uLife", (float)life);
            _shader.SetFloat("uSizeM", (float)Math.Clamp(plumeH * 0.035, 40.0, 900.0));
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, BombsPerVent);

            // Pass 2 — the glow column: few huge soft puffs rising through the plume, so the
            // eruption reads as a fire column long before individual bombs resolve.
            _shader.SetFloat("uMode", 1f);
            _shader.SetFloat("uLife", (float)(life * 1.4)); // puffs climb a little slower than the bombs
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, PuffsPerVent);
            ActiveVents++;
        }

        if (!first)
        {
            _gl.BindVertexArray(0);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static double Frac(double x) => x - Math.Floor(x);

    public void Dispose()
    {
        _gl.DeleteVertexArray(_emptyVao);
        _shader.Dispose();
    }
}
