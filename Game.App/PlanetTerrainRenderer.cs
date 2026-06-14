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
    private const double LodFactor = 2.5;  // split when distance < LodFactor × patch size
    private const double MergeHysteresis = 1.3; // keep children cached until 1.3× the split distance (anti-thrash)
    private const int GenBudgetPerFrame = 8;
    private const int LandFloatsPerVertex = 15; // finePos(3) + coarsePos(3) + fineNrm(3) + coarseNrm(3) + color(3)

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
uniform mat4 uMVP;
uniform float uMorph;
out vec3 vNormal;
out vec3 vColor;
void main() {
    vec3 pos = mix(aFinePos, aCoarsePos, uMorph);
    vNormal = normalize(mix(aFineNormal, aCoarseNormal, uMorph));
    vColor = aColor;
    gl_Position = uMVP * vec4(pos, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vNormal;
in vec3 vColor;
uniform vec3 uSunDir;
out vec4 FragColor;
void main() {
    float d = max(dot(normalize(vNormal), uSunDir), 0.0);
    FragColor = vec4(vColor * (0.06 + 0.95 * d), 1.0);
}";

    // Translucent ocean surface: diffuse + a sharp sun glint + a fresnel edge that fades the
    // water more opaque at grazing angles. Colour (sand-shallow → deep-blue) is baked per vertex.
    private const string WaterVertexSource = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec3 aColor;
uniform mat4 uMVP;
uniform mat4 uModel;
out vec3 vNormal;
out vec3 vColor;
out vec3 vWorld;
void main() {
    vNormal = aNormal;
    vColor = aColor;
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

    private const string WaterFragmentSource = @"#version 410 core
in vec3 vNormal;
in vec3 vColor;
in vec3 vWorld;
uniform vec3 uSunDir;
uniform float uAlpha;
out vec4 FragColor;
void main() {
    vec3 N = normalize(vNormal);
    vec3 L = normalize(uSunDir);
    vec3 V = normalize(-vWorld);                 // camera sits at the origin in camera-relative space
    float diff = max(dot(N, L), 0.0);
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 90.0); // tight sun glint
    float fres = pow(1.0 - max(dot(N, V), 0.0), 3.0);
    vec3 col = vColor * (0.12 + 0.9 * diff) + vec3(1.0) * spec * 0.7;
    float a = clamp(uAlpha + fres * 0.35, 0.0, 1.0);
    FragColor = vec4(col, a);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _waterShader;
    private readonly List<(TerrainPatch patch, Vector3D<float> rel)> _waterFrame = new();

    private Planet? _planet;
    private PlanetTerrain? _terrain;
    private QuadNode[]? _roots;

    public int LeafCount { get; private set; }
    public int PatchCount { get; private set; }
    public Planet? Active => _planet;

    /// <summary>
    /// Optional extra LOD focus (e.g. the rover) — patches refine toward whichever is closer, the
    /// camera or this point, so the ground directly under the vehicle stays at fine vertex spacing
    /// even when the chase camera is a little farther back. Null disables it.
    /// </summary>
    public UniversePosition? FocusPoint;

    public PlanetTerrainRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _waterShader = new Shader(gl, WaterVertexSource, WaterFragmentSource);
    }

    /// <summary>Switch the terrain target (null = none). No-op if it's already this planet.</summary>
    public void SetPlanet(Planet? planet)
    {
        if (ReferenceEquals(planet, _planet)) return;
        DisposeTree();
        _planet = planet;
        if (planet == null) { _terrain = null; _roots = null; return; }

        _terrain = new PlanetTerrain(planet);
        _roots = new QuadNode[6];
        for (int f = 0; f < 6; f++)
        {
            _roots[f] = MakeNode(f, 0, 0, 0, 1, 1);
            _roots[f].Patch = Generate(_roots[f]); // roots eager so render never has a hole
        }
    }

    /// <summary>
    /// Regenerate all terrain meshes for the active planet. Call after the global
    /// <see cref="Game.Universe.TerrainTuning"/> knobs change so the new relief takes effect
    /// (deeper LOD repopulates over the next frames via the per-frame generation budget).
    /// </summary>
    public void Rebuild()
    {
        if (_planet == null || _terrain == null) return;
        DisposeTree();
        _roots = new QuadNode[6];
        for (int f = 0; f < 6; f++)
        {
            _roots[f] = MakeNode(f, 0, 0, 0, 1, 1);
            _roots[f].Patch = Generate(_roots[f]);
        }
    }

    public void Render(Camera camera, Vector3D<float> sunDir)
    {
        if (_planet == null || _roots == null) return;

        Matrix4X4<float> proj = FitProjection(camera);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;
        ExtractFrustum(viewProj);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _shader.Use();
        _shader.SetVector3("uSunDir", sunDir);

        int budget = GenBudgetPerFrame;
        LeafCount = 0;
        _waterFrame.Clear();
        foreach (QuadNode root in _roots)
            Process(root, camera, viewProj, ref budget);

        // Translucent ocean pass over the opaque sea floor. Depth-tested (so land/terrain in
        // front occludes it) but not depth-writing (so it blends and the atmosphere reads behind it).
        if (_waterFrame.Count > 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            _waterShader.Use();
            _waterShader.SetVector3("uSunDir", sunDir);
            _waterShader.SetFloat("uAlpha", 0.62f);
            foreach ((TerrainPatch patch, Vector3D<float> rel) in _waterFrame)
            {
                Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
                _waterShader.SetMatrix("uModel", model);
                _waterShader.SetMatrix("uMVP", model * viewProj);
                patch.DrawWater(_gl);
            }
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.Disable(EnableCap.DepthTest);
    }

    private void Process(QuadNode node, Camera camera, in Matrix4X4<float> viewProj, ref int budget)
    {
        UniversePosition center = _planet!.CurrentPosition.Translated(node.CenterLocal);

        // Visibility cull: a node outside the view frustum or beyond the planet's horizon can't be
        // seen. We skip drawing it and skip refining its subtree (so the per-frame budget goes to
        // detail the camera can actually see — the bulk of the work when hugging the surface), but
        // still let the distance-based merge below collapse it, so off-screen detail doesn't leak.
        Vector3D<float> rel = center.ToCameraRelative(camera.Position);
        float boundR = (float)(node.WorldSize * 0.75 + _terrain!.Amplitude + 50.0);
        bool visible = IsNodeVisible(node, rel, boundR, camera);

        double dist = camera.Position.DistanceTo(center);
        if (FocusPoint is { } fp) dist = Math.Min(dist, fp.DistanceTo(center));

        double splitDist = LodFactor * node.WorldSize;
        bool wantSplit = visible && node.Level < MaxLevel && dist < splitDist;

        if (wantSplit && budget > 0)
        {
            node.Children ??= Split(node);

            bool allReady = true;
            foreach (QuadNode c in node.Children)
            {
                if (c.Patch == null)
                {
                    if (budget > 0) { c.Patch = Generate(c); budget--; }
                    else allReady = false;
                }
            }

            if (allReady)
            {
                foreach (QuadNode c in node.Children)
                    Process(c, camera, viewProj, ref budget);
                return;
            }
            // Children not all ready this frame — draw this node, refine next frame.
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

    private void RenderPatch(QuadNode node, Vector3D<float> rel, in Matrix4X4<float> viewProj, float morph)
    {
        if (node.Patch == null) return;
        Matrix4X4<float> model = Matrix4X4.CreateTranslation(rel);
        _shader.SetMatrix("uMVP", model * viewProj);
        _shader.SetFloat("uMorph", morph);
        node.Patch.Draw(_gl);
        if (node.Patch.HasWater) _waterFrame.Add((node.Patch, rel)); // drawn in the later water pass
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
        Vector3D<double> camVec = camera.Position.DeltaMeters(_planet!.CurrentPosition); // centre → camera
        double Dc = camVec.Length;
        if (Dc <= R) return true; // at/under the surface — nothing is over the horizon
        double cosA = Vector3D.Dot(Vector3D.Normalize(node.CenterLocal), Vector3D.Normalize(camVec));
        double a = Math.Acos(Math.Clamp(cosA, -1.0, 1.0));
        double aHorizon = Math.Acos(Math.Clamp(R / Dc, -1.0, 1.0));
        double patchAng = node.WorldSize * 0.75 / R;
        double heightAng = Math.Sqrt(2.0 * Math.Max(0.0, _terrain.Amplitude) / R);
        return a <= aHorizon + patchAng + heightAng + 0.01;
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

    private TerrainPatch Generate(QuadNode node)
    {
        (float[] land, float[] water) = BuildPatchVertices(node);
        PatchCount++;
        return new TerrainPatch(_gl, land, water);
    }

    private (float[] land, float[] water) BuildPatchVertices(QuadNode node)
    {
        int n = GridN;
        double radius = _terrain!.Radius;
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
        for (int gj = -1; gj <= n + 1; gj++)
        for (int gi = -1; gi <= n + 1; gi++)
        {
            double u = node.U0 + (node.U1 - node.U0) * (gi / (double)n);
            double v = node.V0 + (node.V1 - node.V0) * (gj / (double)n);
            Vector3D<double> d = FacePoint(node.Face, u, v);
            double hF = _terrain.HeightAt(d, spacing);
            Vector3D<double> pF = d * (radius + hF);
            Vector3D<double> pC = d * (radius + _terrain.HeightAt(d, coarseSpacing));
            exF[gi + 1, gj + 1] = pF;
            exC[gi + 1, gj + 1] = pC;
            if (gi >= 0 && gi <= n && gj >= 0 && gj <= n)
            {
                dir[gi, gj] = d;
                posF[gi, gj] = pF;
                posC[gi, gj] = pC;
                hgt[gi, gj] = hF;
            }
        }

        var nrmF = new Vector3D<double>[n + 1, n + 1];
        var nrmC = new Vector3D<double>[n + 1, n + 1];
        var col = new Vector3D<float>[n + 1, n + 1];
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
        {
            Vector3D<double> outward = Vector3D.Normalize(posF[i, j]);
            nrmF[i, j] = GridNormal(exF, i, j, outward);
            nrmC[i, j] = GridNormal(exC, i, j, outward);

            // Slope = cos(angle from vertical): 1 on flats, → 0 on cliffs. Drives rock vs snow.
            double slope = Vector3D.Dot(nrmF[i, j], outward);
            col[i, j] = _terrain.ColorAt(hgt[i, j], slope);
        }

        var v3 = new List<float>((n * n * 6 + n * 16) * LandFloatsPerVertex);
        void Emit(Vector3D<double> pF, Vector3D<double> pC, Vector3D<double> nF, Vector3D<double> nC, Vector3D<float> c)
        {
            v3.Add((float)(pF.X - centerLocal.X)); v3.Add((float)(pF.Y - centerLocal.Y)); v3.Add((float)(pF.Z - centerLocal.Z));
            v3.Add((float)(pC.X - centerLocal.X)); v3.Add((float)(pC.Y - centerLocal.Y)); v3.Add((float)(pC.Z - centerLocal.Z));
            v3.Add((float)nF.X); v3.Add((float)nF.Y); v3.Add((float)nF.Z);
            v3.Add((float)nC.X); v3.Add((float)nC.Y); v3.Add((float)nC.Z);
            v3.Add(c.X); v3.Add(c.Y); v3.Add(c.Z);
        }
        void EmitVert(int i, int j) => Emit(posF[i, j], posC[i, j], nrmF[i, j], nrmC[i, j], col[i, j]);

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
            Emit(t0F, t0C, n0F, n0C, c0); Emit(t1F, t1C, n1F, n1C, c1); Emit(b1F, b1C, n1F, n1C, c1);
            Emit(t0F, t0C, n0F, n0C, c0); Emit(b1F, b1C, n1F, n1C, c1); Emit(b0F, b0C, n0F, n0C, c0);
        }
        for (int j = 0; j < n; j++) { SkirtQuad(0, j, 0, j + 1); SkirtQuad(n, j + 1, n, j); }
        for (int i = 0; i < n; i++) { SkirtQuad(i + 1, 0, i, 0); SkirtQuad(i, n, i + 1, n); }

        return (v3.ToArray(), BuildWaterVertices(node, dir, hgt, centerLocal, radius));
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
    private float[] BuildWaterVertices(QuadNode node, Vector3D<double>[,] dir, double[,] hgt,
        Vector3D<double> centerLocal, double radius)
    {
        if (_terrain is not { HasOcean: true }) return Array.Empty<float>();

        int n = GridN;
        double seaLevel = _terrain.SeaLevelMeters;
        double amp = _terrain.Amplitude;
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
            w.Add((float)(p.X - centerLocal.X)); w.Add((float)(p.Y - centerLocal.Y)); w.Add((float)(p.Z - centerLocal.Z));
            w.Add((float)nrm.X); w.Add((float)nrm.Y); w.Add((float)nrm.Z);
            w.Add(c.X); w.Add(c.Y); w.Add(c.Z);
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
        double camToCenter = camera.Position.DistanceTo(_planet!.CurrentPosition);

        // Altitude above the LOCAL terrain under the camera, not the base radius — otherwise
        // standing on high ground (a mountain / raised continent) inflates the altitude by that
        // elevation and pushes the near plane far out, clipping the rover and nearby surface.
        Vector3D<double> camDir = camera.Position.DeltaMeters(_planet.CurrentPosition);
        double surfaceR = _terrain!.Radius;
        if (camDir.LengthSquared > 0)
            surfaceR += _terrain.HeightAt(Vector3D.Normalize(camDir));
        double alt = Math.Max(1.0, camToCenter - surfaceR);

        double horizon = Math.Sqrt(alt * alt + 2 * _terrain.Radius * alt);
        double far = horizon * 1.25 + 5000.0;
        double near = Math.Clamp(alt * 0.25, 0.2, far * 0.5);
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
        if (node.Children != null) { foreach (QuadNode c in node.Children) DisposeNode(c); node.Children = null; }
        if (node.Patch != null) { node.Patch.Dispose(_gl); node.Patch = null; PatchCount--; }
    }

    private void DisposeTree()
    {
        if (_roots == null) return;
        foreach (QuadNode r in _roots) DisposeNode(r);
        _roots = null;
        PatchCount = 0;
    }

    public void Dispose()
    {
        DisposeTree();
        _shader.Dispose();
        _waterShader.Dispose();
    }

    private sealed class QuadNode
    {
        public int Face, Level;
        public double U0, V0, U1, V1;
        public Vector3D<double> CenterLocal;
        public double WorldSize;
        public TerrainPatch? Patch;
        public QuadNode[]? Children;
    }

    private sealed class TerrainPatch
    {
        private readonly uint _vao, _vbo, _count;
        private readonly uint _waterVao, _waterVbo, _waterCount;

        public bool HasWater => _waterCount > 0;

        // Land: finePos, coarsePos, fineNrm, coarseNrm, colour (5 × vec3). Water: pos, nrm, colour.
        private static readonly int[] LandLayout = { 3, 3, 3, 3, 3 };
        private static readonly int[] WaterLayout = { 3, 3, 3 };

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
