using System.Collections.Concurrent;
using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Renders the globular clusters of the galaxy you're in. Each cluster cross-fades by apparent size
/// between a <b>fuzzy sprite</b> (a single soft point — what you see from thousands of ly away) and a
/// <b>resolved star cloud</b> (its actual stars, as you close in). Clusters live in the halo, so only a
/// few are ever near the camera; their star clouds are generated off-thread and cached for the nearest.
/// Drawn additive with a wide-far-plane projection (clusters can be tens of thousands of ly off but with
/// real parallax), depth off.
/// </summary>
public sealed class GlobularClusterRenderer : IDisposable
{
    public bool Enabled = true;
    public float SpriteBrightness = 1.0f;
    public float CloudBrightness = 1.0f;
    public float CloudPointScale = 1.6f;

    public int LastSprites { get; private set; }
    public int LastResolved { get; private set; }

    /// <summary>Clusters currently shown predominantly as a sprite this frame — the HUD draws a reticle
    /// on each. Cleared and refilled every <see cref="Render"/>.</summary>
    public readonly List<GlobularCluster> Marks = new();

    // Gather clusters within this range; cross-fade sprite→resolved across these apparent-radius bounds.
    private static readonly double MaxRangeM = 25_000.0 * MathUtil.LightYear;
    private static readonly double ReticleRangeM = 15_000.0 * MathUtil.LightYear; // HUD reticle only within this
    private const float ResolveLo = 0.012f;  // ~ radius/0.012 ≈ 4000 ly for a 50 ly cluster: resolve begins
    private const float ResolveHi = 0.040f;  // ~1250 ly: fully resolved into stars
    private const float MinSpritePx = 3f, MaxSpritePx = 48f;
    private const int ResolveCap = 8;        // resolved star clouds for at most this many nearest clusters

    private const float ClusterNear = 1.0e6f;
    private const float ClusterFar = 1.0e21f;

    // Per-point near-fade band (m). The decorative cloud and the injected catalog stars are the SAME
    // stars (same positions/colours), so the cloud can carry the bright, dense look right up until you're
    // among them — only the points within ~8–50 ly of the camera yield to the real (now-bright) catalog
    // stars. Keeping it this close avoids the cluster appearing to dim while you approach.
    private static readonly float NearFadeLo = (float)(8.0 * MathUtil.LightYear);
    private static readonly float NearFadeHi = (float)(50.0 * MathUtil.LightYear);
    private const int FloatsPerSprite = 8;   // rel(3) + color(3) + size(1) + bright(1)

    private const string SpriteVert = @"#version 410 core
layout(location = 0) in vec3 aRel;
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
layout(location = 3) in float aBright;
uniform mat4 uViewProj;
out vec3 vColor; out float vBright;
void main() {
    vColor = aColor; vBright = aBright;
    gl_Position = uViewProj * vec4(aRel, 1.0);
    gl_PointSize = aSize;
}";

    private const string SpriteFrag = @"#version 410 core
in vec3 vColor; in float vBright;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    float a = exp(-r2 * 3.0) * smoothstep(1.0, 0.2, r2); // soft fuzzy core
    FragColor = vec4(vColor * (vBright * uBrightness), 1.0) * a;
}";

    private const string CloudVert = @"#version 410 core
layout(location = 0) in vec3 aPos;   // offset from cluster centre (m)
layout(location = 1) in vec3 aColor;
layout(location = 2) in float aSize;
uniform mat4 uViewProj;
uniform vec3 uClusterRel;            // cluster centre relative to camera (m)
uniform float uPointScale;
uniform float uNear0; uniform float uNear1;
out vec3 vColor; out float vFade;
void main() {
    vec3 world = uClusterRel + aPos;
    gl_Position = uViewProj * vec4(world, 1.0);
    gl_PointSize = aSize * uPointScale;
    // Near-fade so the decorative cloud yields to the REAL catalog stars (injected per cluster) as you
    // arrive. Scale before length() so squaring can't overflow float at cluster distances (~1e20 m).
    float distM = length(world * 1e-15) * 1e15;
    vFade = smoothstep(uNear0, uNear1, distM);
    vColor = aColor;
}";

    private const string CloudFrag = @"#version 410 core
in vec3 vColor; in float vFade;
uniform float uBrightness;
uniform float uAlpha;
out vec4 FragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.35, r2);
    FragColor = vec4(vColor * uBrightness, 1.0) * (a * uAlpha * vFade);
}";

    private readonly GL _gl;
    private readonly Shader _spriteShader;
    private readonly Shader _cloudShader;
    private readonly uint _spriteVao, _spriteVbo;
    private float[] _spriteData = new float[FloatsPerSprite * 64];

    private GlobularCluster[] _clusters = Array.Empty<GlobularCluster>();

    private struct ResolveJob { public GlobularCluster Cluster; public Vector3D<float> Rel; public double DistM; public float Alpha; }
    private readonly List<ResolveJob> _resolveJobs = new();

    private struct CloudVbo { public uint Vao, Vbo; public int Count; }
    private readonly Dictionary<ulong, CloudVbo> _clouds = new();
    private readonly HashSet<ulong> _pending = new();
    private readonly ConcurrentQueue<(ulong id, float[] data)> _ready = new();
    private readonly HashSet<ulong> _keep = new();
    private readonly List<ulong> _evict = new();

    public unsafe GlobularClusterRenderer(GL gl)
    {
        _gl = gl;
        _spriteShader = new Shader(gl, SpriteVert, SpriteFrag);
        _cloudShader = new Shader(gl, CloudVert, CloudFrag);

        _spriteVao = gl.GenVertexArray();
        _spriteVbo = gl.GenBuffer();
        gl.BindVertexArray(_spriteVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
        uint stride = FloatsPerSprite * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.BindVertexArray(0);
    }

    /// <summary>Swap in the clusters of the galaxy you've entered (empty array in intergalactic space).</summary>
    public void SetClusters(GlobularCluster[] clusters) => _clusters = clusters ?? Array.Empty<GlobularCluster>();

    public unsafe void Render(Camera camera, float viewportHeight)
    {
        LastSprites = LastResolved = 0;
        Marks.Clear();
        if (!Enabled || _clusters.Length == 0) { IntegrateReady(); EvictUnused(); return; }

        IntegrateReady();

        // Pixels per radian of angular size, for sizing the fuzzy sprites by apparent diameter.
        float pxPerRad = viewportHeight / (2f * MathF.Tan(camera.FovRadians * 0.5f));
        double ly = MathUtil.LightYear;

        int s = 0;
        _resolveJobs.Clear();
        foreach (GlobularCluster c in _clusters)
        {
            Vector3D<double> rel = c.Center.DeltaMeters(camera.Position);
            double distM = rel.Length;
            if (distM < 1.0 || distM > MaxRangeM) continue;

            double theta = c.RadiusMeters / distM;             // apparent angular radius
            float resolveMix = Smoothstep(ResolveLo, ResolveHi, (float)theta);
            var relF = new Vector3D<float>((float)rel.X, (float)rel.Y, (float)rel.Z);

            float spriteAlpha = 1f - resolveMix;
            // HUD reticle while it's a clear fuzzy sprite (not yet mostly resolved) and close enough to read.
            if (spriteAlpha > 0.15f && distM < ReticleRangeM) Marks.Add(c);
            if (spriteAlpha > 0.001f)
            {
                double distLy = distM / ly;
                float sizePx = Math.Clamp((float)(2.0 * theta * pxPerRad), MinSpritePx, MaxSpritePx);
                float bright = Math.Clamp((float)(3000.0 / distLy), 0.25f, 2.0f) * spriteAlpha;
                EnsureSprite(s + 1);
                int o = s * FloatsPerSprite;
                _spriteData[o + 0] = relF.X; _spriteData[o + 1] = relF.Y; _spriteData[o + 2] = relF.Z;
                _spriteData[o + 3] = c.Color.X; _spriteData[o + 4] = c.Color.Y; _spriteData[o + 5] = c.Color.Z;
                _spriteData[o + 6] = sizePx; _spriteData[o + 7] = bright;
                s++;
            }

            if (resolveMix > 0.001f)
                _resolveJobs.Add(new ResolveJob { Cluster = c, Rel = relF, DistM = distM, Alpha = resolveMix });
        }

        if (s == 0 && _resolveJobs.Count == 0) { EvictUnused(); return; }

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.ProgramPointSize);

        Matrix4X4<float> vp = camera.ViewMatrix *
            MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, ClusterNear, ClusterFar);

        if (s > 0)
        {
            _gl.BindVertexArray(_spriteVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(_spriteData, 0, s * FloatsPerSprite), BufferUsageARB.StreamDraw);
            _spriteShader.Use();
            _spriteShader.SetMatrix("uViewProj", vp);
            _spriteShader.SetFloat("uBrightness", SpriteBrightness);
            _gl.DrawArrays(PrimitiveType.Points, 0, (uint)s);
            LastSprites = s;
        }

        if (_resolveJobs.Count > 0)
        {
            _resolveJobs.Sort((a, b) => a.DistM.CompareTo(b.DistM));
            bool ready = false;
            int drawn = 0;
            for (int i = 0; i < _resolveJobs.Count && drawn < ResolveCap; i++)
            {
                ResolveJob job = _resolveJobs[i];
                _keep.Add(job.Cluster.Id);
                if (!_clouds.TryGetValue(job.Cluster.Id, out CloudVbo cloud)) { Request(job.Cluster); continue; }
                if (!ready)
                {
                    _cloudShader.Use();
                    _cloudShader.SetMatrix("uViewProj", vp);
                    _cloudShader.SetFloat("uPointScale", CloudPointScale);
                    _cloudShader.SetFloat("uNear0", NearFadeLo);
                    _cloudShader.SetFloat("uNear1", NearFadeHi);
                    ready = true;
                }
                _cloudShader.SetVector3("uClusterRel", job.Rel);
                _cloudShader.SetFloat("uBrightness", CloudBrightness);
                _cloudShader.SetFloat("uAlpha", job.Alpha);
                _gl.BindVertexArray(cloud.Vao);
                _gl.DrawArrays(PrimitiveType.Points, 0, (uint)cloud.Count);
                drawn++;
            }
            LastResolved = drawn;
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        EvictUnused();
    }

    private void Request(GlobularCluster c)
    {
        if (_pending.Contains(c.Id) || _clouds.ContainsKey(c.Id)) return;
        _pending.Add(c.Id);
        GlobularCluster cc = c;
        Task.Run(() => _ready.Enqueue((cc.Id, GlobularClusters.Stars(cc))));
    }

    private unsafe void IntegrateReady()
    {
        while (_ready.TryDequeue(out (ulong id, float[] data) item))
        {
            _pending.Remove(item.id);
            if (_clouds.ContainsKey(item.id)) continue;
            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(item.data), BufferUsageARB.StaticDraw);
            uint stride = GlobularClusters.FloatsPerStar * sizeof(float);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.BindVertexArray(0);
            _clouds[item.id] = new CloudVbo { Vao = vao, Vbo = vbo, Count = item.data.Length / GlobularClusters.FloatsPerStar };
        }
    }

    private void EvictUnused()
    {
        if (_clouds.Count == 0) { _keep.Clear(); return; }
        _evict.Clear();
        foreach (ulong id in _clouds.Keys)
            if (!_keep.Contains(id)) _evict.Add(id);
        foreach (ulong id in _evict)
        {
            CloudVbo c = _clouds[id];
            _gl.DeleteBuffer(c.Vbo);
            _gl.DeleteVertexArray(c.Vao);
            _clouds.Remove(id);
        }
        _keep.Clear();
    }

    private static float Smoothstep(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void EnsureSprite(int sprites)
    {
        int needed = sprites * FloatsPerSprite;
        if (_spriteData.Length >= needed) return;
        int len = _spriteData.Length;
        while (len < needed) len *= 2;
        Array.Resize(ref _spriteData, len);
    }

    public void Dispose()
    {
        _spriteShader.Dispose();
        _cloudShader.Dispose();
        _gl.DeleteBuffer(_spriteVbo);
        _gl.DeleteVertexArray(_spriteVao);
        foreach (CloudVbo c in _clouds.Values) { _gl.DeleteBuffer(c.Vbo); _gl.DeleteVertexArray(c.Vao); }
    }
}
