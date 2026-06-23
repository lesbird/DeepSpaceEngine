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
    public float Brightness = 1.8f;          // point sprites
    public float SizeScale = 16.0f;
    public float MinSizePx = 4.0f;
    public float MaxSizePx = 28.0f;
    public float ImpostorBrightness = 1.3f;  // impostor disks
    public float CloudBrightness = 1.0f;     // volumetric clouds

    public int LastDrawn { get; private set; }
    public int LastImpostors { get; private set; }
    public int LastClouds { get; private set; }

    // Cross-fade bands in apparent angular radius (galaxyRadius / distance):
    //   point → impostor across [RatioLo, RatioHi]; impostor → cloud across [CloudLo, CloudHi].
    private const float RatioLo = 0.010f;
    private const float RatioHi = 0.030f;
    private const float CloudLo = 0.050f;   // ~20 galaxy radii out, cloud begins
    private const float CloudHi = 0.150f;   // ~7 radii out, cloud is the whole galaxy

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

    private const int CloudCount = 120_000; // sample points per galaxy
    private const int CloudCap = 3;         // generate/draw clouds for at most this many nearest galaxies

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
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.35, r2);
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

    private const string ImpostorFrag = @"#version 410 core
in vec2 vUv;
uniform vec3 uColor; uniform float uArmCount; uniform float uArmStrength; uniform float uSwirl;
uniform float uBrightness; uniform float uAlpha;
out vec4 FragColor;
float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 34.56); return fract(p.x * p.y); }
float vnoise(vec2 x) { vec2 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1,0)), f.x), mix(hash(i + vec2(0,1)), hash(i + vec2(1,1)), f.x), f.y); }
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; } return s; }
void main() {
    float r = length(vUv);
    if (r > 1.0) { FragColor = vec4(0.0); return; }
    float phi = atan(vUv.y, vUv.x);
    float core = exp(-r * r / (2.0 * 0.05 * 0.05));
    float disk = exp(-r / 0.32);
    float arm = 1.0;
    if (uArmCount > 0.5) arm = 1.0 + uArmStrength * cos(uArmCount * phi - uSwirl * log(r + 0.06));
    float dust = 0.55 + 0.45 * fbm(vUv * 5.0 + 3.0);
    float intensity = core * 1.3 + disk * max(arm, 0.12) * dust;
    float edge = smoothstep(1.0, 0.6, r);
    FragColor = vec4(uColor * (intensity * uBrightness), 1.0) * (edge * uAlpha);
}";

    private const string CloudVert = @"#version 410 core
layout(location = 0) in vec3 aPos;   // offset from galaxy centre (m)
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
uniform mat4 uViewProj;
uniform vec3 uGalaxyRel;             // galaxy centre relative to camera (m)
uniform float uNear0; uniform float uNear1;
out vec3 vColor; out float vFade;
void main() {
    vec3 world = uGalaxyRel + aPos;
    gl_Position = uViewProj * vec4(world, 1.0);
    gl_PointSize = aSize;
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
        public float ArmCount, ArmStrength, Swirl, Alpha;
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
                float impostorMix = Smoothstep(RatioLo, RatioHi, (float)ratio);
                float cloudMix = Smoothstep(CloudLo, CloudHi, (float)ratio);
                bool isInside = exclude is { } ex && g.Id == ex;

                // --- Cloud (renders for the galaxy you're inside too, near-faded) ---
                if (cloudMix > 0.001f)
                    _cloudJobs.Add(new CloudJob { Galaxy = g, Rel = relF, DistM = distM, Alpha = cloudMix });

                if (isInside) continue; // point/impostor skip the galaxy you're inside

                // --- Point sprite (faded out as impostor, then cloud, take over) ---
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

                // --- Impostor disk (faded in by impostorMix, out again by cloudMix) ---
                float impostorAlpha = impostorMix * (1f - cloudMix);
                if (impostorAlpha > 0.001f)
                {
                    DiskBasis(g.DiskNormal, out Vector3D<float> u, out Vector3D<float> v);
                    float half = (float)(ImpostorRenderDist * Math.Min(ratio, 1.2));
                    GalaxyShape(g.Type, out float armCount, out float armStrength, out float swirl);
                    _impostors.Add(new Impostor
                    {
                        Center = dir * ImpostorRenderDist, AxisU = u * half, AxisV = v * half,
                        Color = g.Color, ArmCount = armCount, ArmStrength = armStrength, Swirl = swirl,
                        Alpha = impostorAlpha,
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
