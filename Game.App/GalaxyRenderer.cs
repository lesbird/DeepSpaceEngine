using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws the resident galaxies (from <see cref="GalaxyCatalogPager"/>) as bright point sprites — the
/// first, farthest LOD tier of the galaxy hierarchy. A galaxy millions of light-years away is far
/// beyond the camera's far plane, so (like the backdrop dome) each is projected by <i>direction only</i>
/// onto a fixed dome radius inside the frustum; its size and brightness come from its apparent flux
/// (star count ÷ distance²), so a galaxy reads as a faint star far off and swells into a bright point
/// as you approach. The galaxy you're currently inside is excluded — its own stars stream in instead.
///
/// Later phases cross-fade this point into an oriented impostor and then a volumetric star cloud; for
/// now it is the whole galaxy: fly out of the Milky Way and the others appear as bright stars.
/// </summary>
public sealed class GalaxyRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>Overall brightness multiplier on the galaxy sprites.</summary>
    public float Brightness = 1.8f;
    /// <summary>Scales apparent point size (px) with the brightness cue.</summary>
    public float SizeScale = 16.0f;
    /// <summary>Floor size (px): even a far galaxy stays a clearly visible dot, distinct from a backdrop star.</summary>
    public float MinSizePx = 4.0f;
    public float MaxSizePx = 28.0f;

    /// <summary>Galaxies drawn last frame (HUD/debug).</summary>
    public int LastDrawn { get; private set; }

    // Brightness-cue reference: a Milky-Way-class galaxy (this many stars) at this distance reads as
    // cue ≈ 1 — a clear point. Closer ⇒ brighter/bigger (clamped); farther ⇒ fades to the floor.
    private const double RefStarCount = 2.0e11;
    private const double RefDistLy = 1.0e6;

    // Direction-only projection radius — well inside the camera far plane (1e18 m), like the backdrop dome.
    private const float DomeRadius = 1.0e15f;
    private const int FloatsPerGalaxy = 8; // dir(3) + color(3) + size(1) + brightness(1)

    private const string Vert = @"#version 410 core
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

    private const string Frag = @"#version 410 core
in vec3 vColor;
in float vBright;
uniform float uBrightness;
out vec4 FragColor;
void main() {
    // Soft Gaussian core faded to zero at the rim (no hard discard, which shimmers under motion).
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    float a = exp(-r2 * 2.5) * smoothstep(1.0, 0.35, r2);
    FragColor = vec4(vColor * (vBright * uBrightness), 1.0) * a;
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private float[] _data = new float[FloatsPerGalaxy * 64];

    public unsafe GalaxyRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vert, Frag);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = FloatsPerGalaxy * sizeof(float);
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

    /// <summary>Build this frame's sprite buffer from the resident galaxies and draw them. Skips the
    /// galaxy the camera is inside (its stars render via the catalog). Call in the early additive slot,
    /// alongside the backdrop, into the bound scene buffer.</summary>
    public unsafe void Render(Camera camera, GalaxyCatalogPager galaxies)
    {
        LastDrawn = 0;
        if (!Enabled) return;

        ulong? exclude = galaxies.IsInside ? galaxies.Containing.Id : null;

        int n = 0;
        foreach (GalaxyCatalog block in galaxies.LoadedBlocks)
            foreach (Galaxy g in block.Galaxies)
            {
                if (exclude is { } ex && g.Id == ex) continue;

                Vector3D<double> rel = g.Center.DeltaMeters(camera.Position); // camera → galaxy
                double distM = rel.Length;
                if (distM < 1.0) continue; // degenerate (effectively at the centre)

                double distLy = distM / MathUtil.LightYear;
                // Apparent brightness cue = sqrt(flux), flux ∝ luminosity / distance². Star count is the
                // luminosity proxy, normalised against a Milky-Way-class reference so the magnitudes are
                // interpretable: cue ≈ 1 for a ~2e11-star galaxy at 1 Mly (a clear point), clamping up
                // huge just outside a galaxy and fading toward the floor tens of Mly out.
                float cue = (float)(Math.Sqrt(g.StarCount / RefStarCount) * (RefDistLy / distLy));
                float size = Math.Clamp(MinSizePx + SizeScale * cue, MinSizePx, MaxSizePx);
                float bright = Math.Clamp(0.8f + 1.2f * cue, 0.7f, 3.0f);

                var dir = Vector3D.Normalize(new Vector3D<float>((float)rel.X, (float)rel.Y, (float)rel.Z));

                EnsureCapacity(n + 1);
                int o = n * FloatsPerGalaxy;
                _data[o + 0] = dir.X; _data[o + 1] = dir.Y; _data[o + 2] = dir.Z;
                _data[o + 3] = g.Color.X; _data[o + 4] = g.Color.Y; _data[o + 5] = g.Color.Z;
                _data[o + 6] = size; _data[o + 7] = bright;
                n++;
            }

        if (n == 0) return;

        // Additive, depth-independent — the same regime as the backdrop dome / near-field star sprites.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.ProgramPointSize);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer,
            new ReadOnlySpan<float>(_data, 0, n * FloatsPerGalaxy), BufferUsageARB.StreamDraw);

        _shader.Use();
        _shader.SetMatrix("uViewProj", camera.ViewMatrix * camera.ProjectionMatrix);
        _shader.SetFloat("uRadius", DomeRadius);
        _shader.SetFloat("uBrightness", Brightness);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)n);
        _gl.BindVertexArray(0);

        // Restore the defaults the rest of the frame expects.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        LastDrawn = n;
    }

    private void EnsureCapacity(int galaxies)
    {
        int needed = galaxies * FloatsPerGalaxy;
        if (_data.Length >= needed) return;
        int len = _data.Length;
        while (len < needed) len *= 2;
        Array.Resize(ref _data, len);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
