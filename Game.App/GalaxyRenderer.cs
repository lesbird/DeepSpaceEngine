using System.Collections.Concurrent;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws the resident galaxies (from <see cref="GalaxyCatalogPager"/>) across the three farthest LOD
/// tiers, cross-faded by apparent angular size (<c>ratio = galaxyRadius / distance</c>):
///
///  • <b>Point</b> — far away: a direction-only sprite on a dome radius inside the frustum, sized by
///    apparent flux. A faint star that swells into a bright point as you approach.
///  • <b>Impostor</b> — mid: an oriented disk billboard in the galaxy's real disk plane, procedurally
///    shaded (bulge + spiral arms + dust), scaled to the galaxy's true angular size.
///  • <b>Cloud</b> — near: a real 3-D point cloud (<see cref="GalaxyCloud"/>) positioned camera-relative,
///    so the galaxy gains parallax and volume as you fly in. A per-point near-fade hides cloud points
///    inside the catalog bubble, so the streamed real stars take over up close with no double image.
///
/// Each tier overlaps the next so the swaps never pop. The point and impostor tiers skip the galaxy the
/// camera is inside, but its cloud still renders (near-faded) to provide the far galaxy body around you.
/// Cloud point sets are generated off-thread and cached per galaxy for the nearest few.
/// </summary>
public sealed class GalaxyRenderer : IDisposable
{
    public bool Enabled = true;
    public float Brightness = 2.2f;          // point sprites
    public float SizeScale = 22.0f;
    public float MinSizePx = 6.0f;
    public float MaxSizePx = 30.0f;
    public float ImpostorBrightness = 1.3f;  // impostor disks
    public float CloudBrightness = 1.0f;     // volumetric clouds
    public float CloudPointScale = 2.5f;     // size multiplier on cloud points (fewer, larger = cheaper)

    public int LastDrawn { get; private set; }
    public int LastImpostors { get; private set; }
    public int LastClouds { get; private set; }

    // Cross-fade bands in apparent angular radius (galaxyRadius / distance):
    //   point → impostor across [RatioLo, RatioHi]; impostor → cloud across [CloudLo, CloudHi].
    // The impostor (the textured bulge+dust disk) is the dominant look across most of the approach;
    // the volumetric cloud only takes over for the final close fly-in (within ~3 radii).
    private const float RatioLo = 0.010f;
    private const float RatioHi = 0.040f;
    private const float CloudLo = 0.300f;   // ~3 radii out, cloud begins to take over
    private const float CloudHi = 0.700f;   // ~1.4 radii out, cloud is the whole galaxy

    // When the camera is inside a galaxy, its own stars wash the sky out, so distant external galaxies
    // fade and only the near/bright ones survive. ExtGateCue is the apparent-brightness cue at which a
    // galaxy is fully kept while you are deep inside another galaxy; fainter galaxies fade out below it.
    private const float ExtGateCue = 0.55f;

    private const float PointDomeRadius = 1.0e15f;
    private const float ImpostorRenderDist = 1.0e14f;

    // The cloud uses its own projection with a huge far plane so a whole galaxy (up to ~hundreds of
    // kly across) fits in the frustum; it draws additive with depth off, so depth precision is moot.
    private const float CloudNear = 1.0e9f;
    private const float CloudFar = 3.0e22f;

    // Per-point near-fade band (metres): fade cloud points in across ~300 → 1500 ly, so the dense
    // streamed catalog (which renders real stars out to the resident-block extent, several hundred ly)
    // carries the foreground and the coarse cloud only fills the galaxy body beyond it. The slight
    // overlap is preferable to an empty bubble between the two. This bridges cloud → resolved catalog
    // as you enter a galaxy.
    private static readonly float CloudNearFadeLo = (float)(300.0 * MathUtil.LightYear);
    private static readonly float CloudNearFadeHi = (float)(1500.0 * MathUtil.LightYear);

    // Render only the nearest few impostor disks (each runs a per-pixel fbm shader, so an uncapped
    // cluster of galaxies tanks the frame rate). Farther galaxies stay as cheap point sprites.
    private const int ImpostorCap = 4;

    private const int CloudCount = 20_000;  // sample points per galaxy — few + large (via CloudPointScale) for perf
    private const int CloudCap = 1;         // only the single nearest galaxy gets a volumetric cloud

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
    vColor = aColor; vBright = aBright;
    gl_Position = uViewProj * vec4(aDir * uRadius, 1.0);
    gl_PointSize = aSize;
}";

    private const string PointFrag = @"#version 410 core
in vec3 vColor; in float vBright;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    // Fuzzy galaxy: a soft Gaussian core plus a wide low falloff halo, so a distant galaxy reads as a
    // diffuse smudge rather than a hard pixel. Low exponents = soft, bloomy edge out to the sprite rim.
    float core = exp(-r2 * 3.2);
    float halo = exp(-r2 * 0.9);
    float a = (core + 0.45 * halo) * smoothstep(1.0, 0.05, r2);
    FragColor = vec4(vColor * (vBright * uBrightness), 1.0) * a;
}";

    private const string ImpostorVert = @"#version 410 core
uniform mat4 uViewProj;
uniform vec3 uCenter; uniform vec3 uAxisU; uniform vec3 uAxisV;
out vec2 vUv;
void main() {
    vec2 c = vec2((gl_VertexID == 1 || gl_VertexID == 3) ? 1.0 : -1.0, (gl_VertexID >= 2) ? 1.0 : -1.0);
    vUv = c;
    gl_Position = uViewProj * vec4(uCenter + c.x * uAxisU + c.y * uAxisV, 1.0);
}";

    // A Space-Engine-style galaxy disk: a bright warm central bulge over a cool stellar haze, with
    // dark dust lanes (fbm warped along the spiral) carved into the disk. Additive over black, so the
    // dust reads as 'dark' by emitting less light where it lies.
    private const string ImpostorFrag = @"#version 410 core
in vec2 vUv;
uniform vec3 uColor; uniform float uArmCount; uniform float uArmStrength; uniform float uSwirl;
uniform float uBrightness; uniform float uAlpha;
out vec4 FragColor;
float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 34.56); return fract(p.x * p.y); }
float vnoise(vec2 x) { vec2 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1,0)), f.x), mix(hash(i + vec2(0,1)), hash(i + vec2(1,1)), f.x), f.y); }
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 3; i++) { s += a * vnoise(p); p *= 2.1; a *= 0.5; } return s; }
void main() {
    float r = length(vUv);
    if (r >= 1.0) { FragColor = vec4(0.0); return; }
    float phi = atan(vUv.y, vUv.x);

    // Bright warm bulge.
    float bulge = exp(-r * r / (2.0 * 0.11 * 0.11));
    vec3 bulgeCol = vec3(1.0, 0.86, 0.66);

    // Spiral phase → arm brightness + a coordinate to warp the dust along the arms.
    float phase = (uArmCount > 0.5) ? (uArmCount * phi - uSwirl * log(r + 0.08)) : 0.0;
    float armBright = (uArmCount > 0.5) ? (0.65 + 0.35 * cos(phase)) : 1.0;

    // Cool stellar disk haze, brighter on the arms, fading to the rim.
    float haze = exp(-r / 0.33) * smoothstep(1.0, 0.8, r) * armBright;
    vec3 hazeCol = mix(vec3(0.62, 0.70, 0.92), uColor, 0.4);

    // Dust lanes: warped fbm, dark, only in the disk (not the bright core).
    vec2 sp = vUv * 3.2 + vec2(cos(phase), sin(phase)) * 0.8 + 5.0;
    float dust = smoothstep(0.42, 0.72, fbm(sp)) * smoothstep(0.10, 0.35, r) * (1.0 - bulge);
    vec3 dustCol = vec3(0.18, 0.12, 0.07);

    vec3 col = bulgeCol * (bulge * 1.6)
             + hazeCol * (haze * (1.0 - 0.8 * dust))
             + dustCol * (dust * haze * 1.2);
    float edge = smoothstep(1.0, 0.72, r);
    FragColor = vec4(col * uBrightness, 1.0) * (edge * uAlpha);
}";

    private const string CloudVert = @"#version 410 core
layout(location = 0) in vec3 aPos;   // offset from galaxy centre (m)
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
uniform mat4 uViewProj;
uniform vec3 uGalaxyRel;             // galaxy centre relative to camera (m)
uniform float uNear0; uniform float uNear1;
uniform float uPointScale;          // global multiplier on point size (fewer, larger points = cheaper)
out vec3 vColor; out float vFade;
void main() {
    vec3 world = uGalaxyRel + aPos;
    gl_Position = uViewProj * vec4(world, 1.0);
    gl_PointSize = aSize * uPointScale;
    // Distance for the near-fade: scale before length() so squaring can't overflow float at galaxy
    // scale (a ~1e21 m component squared is ~1e42, past float's ~3.4e38 max).
    float distM = length(world * 1e-18) * 1e18;
    vFade = smoothstep(uNear0, uNear1, distM);
    vColor = aColor;
}";

    private const string CloudFrag = @"#version 410 core
in vec3 vColor; in float vFade;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.4, r2);
    FragColor = vec4(vColor * uBrightness, 1.0) * (a * vFade);
}";

    private readonly GL _gl;
    private readonly Shader _pointShader;
    private readonly Shader _impostorShader;
    private readonly Shader _cloudShader;
    private readonly uint _pointVao, _pointVbo;
    private readonly uint _impostorVao;
    private float[] _data = new float[FloatsPerPoint * 64];

    private struct Impostor
    {
        public Vector3D<float> Center, AxisU, AxisV, Color;
        public float ArmCount, ArmStrength, Swirl, Alpha, Ratio;
    }
    private readonly List<Impostor> _impostors = new();

    private struct CloudJob
    {
        public Galaxy Galaxy;
        public Vector3D<float> Rel;  // galaxy centre relative to camera (m)
        public double DistM;
        public float Alpha;
    }
    private readonly List<CloudJob> _cloudJobs = new();

    private struct CloudVbo { public uint Vao, Vbo; public int Count; }
    private readonly Dictionary<ulong, CloudVbo> _clouds = new();
    private readonly HashSet<ulong> _cloudPending = new();
    private readonly ConcurrentQueue<(ulong id, float[] data)> _cloudReady = new();
    private readonly HashSet<ulong> _cloudKeep = new();
    private readonly List<ulong> _cloudEvict = new();

    public unsafe GalaxyRenderer(GL gl)
    {
        _gl = gl;
        _pointShader = new Shader(gl, PointVert, PointFrag);
        _impostorShader = new Shader(gl, ImpostorVert, ImpostorFrag);
        _cloudShader = new Shader(gl, CloudVert, CloudFrag);

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

        _impostorVao = gl.GenVertexArray(); // empty; vertices from gl_VertexID
    }

    public unsafe void Render(Camera camera, GalaxyCatalogPager galaxies)
    {
        LastDrawn = LastImpostors = LastClouds = 0;
        if (!Enabled) return;

        IntegrateReadyClouds();

        ulong? exclude = galaxies.IsInside ? galaxies.Containing.Id : null;

        // How deep inside a galaxy the camera is (0 = intergalactic / at the rim, 1 = well inside). Drives
        // the external-galaxy fade: outside, every galaxy shows; the deeper in, the more the faint distant
        // ones recede until only the near/bright ones remain. Leaving a galaxy reverses it (they fade in).
        float insideAmount = 0f;
        if (galaxies.IsInside && galaxies.Containing.RadiusMeters > 0)
        {
            double d = galaxies.Containing.Center.DeltaMeters(camera.Position).Length;
            insideAmount = Smoothstep(1.0f, 0.55f, (float)(d / galaxies.Containing.RadiusMeters));
        }

        int n = 0;
        _impostors.Clear();
        _cloudJobs.Clear();
        foreach (GalaxyCatalog block in galaxies.LoadedBlocks)
            foreach (Galaxy g in block.Galaxies)
            {
                Vector3D<double> rel = g.Center.DeltaMeters(camera.Position); // camera → galaxy
                double distM = rel.Length;
                if (distM < 1.0) continue;

                // Normalise in DOUBLE then cast (a float-cast component squared overflows at ~1e22 m).
                var dir = new Vector3D<float>(
                    (float)(rel.X / distM), (float)(rel.Y / distM), (float)(rel.Z / distM));
                var relF = new Vector3D<float>((float)rel.X, (float)rel.Y, (float)rel.Z);

                double ratio = g.RadiusMeters / distM;
                double distLy = distM / MathUtil.LightYear;
                float cue = (float)(Math.Sqrt(g.StarCount / RefStarCount) * (RefDistLy / distLy));

                // Inside-a-galaxy fade: keep is 1 in intergalactic space; the deeper inside a galaxy you
                // are, the more this gates external galaxies by apparent brightness (faint → fades out).
                float gate = Smoothstep(0f, ExtGateCue, cue);
                float keep = 1f - insideAmount * (1f - gate);

                float impostorMix = Smoothstep(RatioLo, RatioHi, (float)ratio);
                float cloudMix = Smoothstep(CloudLo, CloudHi, (float)ratio);
                bool isInside = exclude is { } ex && g.Id == ex;

                // --- Cloud (renders for the galaxy you're inside too, near-faded) ---
                if (cloudMix > 0.001f)
                    _cloudJobs.Add(new CloudJob { Galaxy = g, Rel = relF, DistM = distM, Alpha = cloudMix * keep });

                if (isInside) continue; // point/impostor skip the galaxy you're inside

                // --- Point sprite (faded out as impostor, then cloud, take over) ---
                float pointFade = 1f - impostorMix;
                if (pointFade > 0.001f && keep > 0.002f)
                {
                    float size = Math.Clamp(MinSizePx + SizeScale * cue, MinSizePx, MaxSizePx);
                    float bright = Math.Clamp(0.8f + 1.2f * cue, 0.7f, 3.0f) * pointFade * keep;
                    EnsureCapacity(n + 1);
                    int o = n * FloatsPerPoint;
                    _data[o + 0] = dir.X; _data[o + 1] = dir.Y; _data[o + 2] = dir.Z;
                    _data[o + 3] = g.Color.X; _data[o + 4] = g.Color.Y; _data[o + 5] = g.Color.Z;
                    _data[o + 6] = size; _data[o + 7] = bright;
                    n++;
                }

                // --- Impostor disk (faded in by impostorMix, out again by cloudMix) ---
                float impostorAlpha = impostorMix * (1f - cloudMix) * keep;
                if (impostorAlpha > 0.001f)
                {
                    DiskBasis(g.DiskNormal, out Vector3D<float> u, out Vector3D<float> v);
                    float half = (float)(ImpostorRenderDist * Math.Min(ratio, 1.2));
                    GalaxyShape(g.Type, out float armCount, out float armStrength, out float swirl);
                    _impostors.Add(new Impostor
                    {
                        Center = dir * ImpostorRenderDist, AxisU = u * half, AxisV = v * half,
                        Color = g.Color, ArmCount = armCount, ArmStrength = armStrength, Swirl = swirl,
                        Alpha = impostorAlpha, Ratio = (float)ratio,
                    });
                }
            }

        if (n == 0 && _impostors.Count == 0 && _cloudJobs.Count == 0) { EvictUnusedClouds(); return; }

        // Additive, depth-independent — same regime as the backdrop dome / near-field star sprites.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.ProgramPointSize);

        Matrix4X4<float> vp = camera.ViewMatrix * camera.ProjectionMatrix;

        if (n > 0)
        {
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

        if (_impostors.Count > 0)
        {
            // Draw only the nearest ImpostorCap disks (largest angular radius); the rest stay as the
            // already-drawn point sprites. Bounds the per-pixel fbm fill cost in galaxy clusters.
            _impostors.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
            int count = Math.Min(_impostors.Count, ImpostorCap);
            _impostorShader.Use();
            _impostorShader.SetMatrix("uViewProj", vp);
            _impostorShader.SetFloat("uBrightness", ImpostorBrightness);
            _gl.BindVertexArray(_impostorVao);
            for (int i = 0; i < count; i++)
            {
                Impostor imp = _impostors[i];
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
            LastImpostors = count;
        }

        RenderClouds(camera);

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        EvictUnusedClouds();
    }

    private void RenderClouds(Camera camera)
    {
        if (_cloudJobs.Count == 0) return;

        // Only the nearest few galaxies get a (cap-limited) cloud; sort jobs by distance.
        _cloudJobs.Sort((a, b) => a.DistM.CompareTo(b.DistM));

        Matrix4X4<float> cloudVp = camera.ViewMatrix *
            MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, CloudNear, CloudFar);

        bool shaderReady = false;
        int drawn = 0;
        for (int i = 0; i < _cloudJobs.Count && drawn < CloudCap; i++)
        {
            CloudJob job = _cloudJobs[i];
            ulong id = job.Galaxy.Id;
            _cloudKeep.Add(id);

            if (!_clouds.TryGetValue(id, out CloudVbo cloud))
            {
                RequestCloud(job.Galaxy); // async-generate; drawn next time it's ready
                continue;
            }

            if (!shaderReady)
            {
                _cloudShader.Use();
                _cloudShader.SetMatrix("uViewProj", cloudVp);
                _cloudShader.SetFloat("uNear0", CloudNearFadeLo);
                _cloudShader.SetFloat("uNear1", CloudNearFadeHi);
                _cloudShader.SetFloat("uPointScale", CloudPointScale);
                shaderReady = true;
            }
            _cloudShader.SetVector3("uGalaxyRel", job.Rel);
            _cloudShader.SetFloat("uBrightness", CloudBrightness * job.Alpha);
            _gl.BindVertexArray(cloud.Vao);
            _gl.DrawArrays(PrimitiveType.Points, 0, (uint)cloud.Count);
            drawn++;
        }
        LastClouds = drawn;
    }

    private void RequestCloud(Galaxy g)
    {
        if (_cloudPending.Contains(g.Id) || _clouds.ContainsKey(g.Id)) return;
        _cloudPending.Add(g.Id);
        Galaxy gg = g; // value type captured into the task
        Task.Run(() => _cloudReady.Enqueue((gg.Id, GalaxyCloud.Generate(gg, CloudCount))));
    }

    private unsafe void IntegrateReadyClouds()
    {
        while (_cloudReady.TryDequeue(out (ulong id, float[] data) item))
        {
            _cloudPending.Remove(item.id);
            if (_clouds.ContainsKey(item.id)) continue;

            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(item.data), BufferUsageARB.StaticDraw);
            uint stride = GalaxyCloud.FloatsPerPoint * sizeof(float);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.BindVertexArray(0);
            _clouds[item.id] = new CloudVbo { Vao = vao, Vbo = vbo, Count = item.data.Length / GalaxyCloud.FloatsPerPoint };
        }
    }

    private void EvictUnusedClouds()
    {
        if (_clouds.Count == 0) { _cloudKeep.Clear(); return; }
        _cloudEvict.Clear();
        foreach (ulong id in _clouds.Keys)
            if (!_cloudKeep.Contains(id)) _cloudEvict.Add(id);
        foreach (ulong id in _cloudEvict)
        {
            CloudVbo c = _clouds[id];
            _gl.DeleteBuffer(c.Vbo);
            _gl.DeleteVertexArray(c.Vao);
            _clouds.Remove(id);
        }
        _cloudKeep.Clear();
    }

    private static void GalaxyShape(GalaxyType type, out float armCount, out float armStrength, out float swirl)
    {
        switch (type)
        {
            case GalaxyType.Spiral: armCount = 2; armStrength = 0.6f; swirl = 5f; break;
            case GalaxyType.Irregular: armCount = 1; armStrength = 0.45f; swirl = 8f; break;
            default: armCount = 0; armStrength = 0; swirl = 0; break;
        }
    }

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
        _cloudShader.Dispose();
        _gl.DeleteBuffer(_pointVbo);
        _gl.DeleteVertexArray(_pointVao);
        _gl.DeleteVertexArray(_impostorVao);
        foreach (CloudVbo c in _clouds.Values) { _gl.DeleteBuffer(c.Vbo); _gl.DeleteVertexArray(c.Vao); }
    }
}
