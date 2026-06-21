using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws the procedurally-placed <see cref="NebulaField"/> nebulae as large, soft, additive camera-facing
/// billboards with a procedural fBm structure — colourful glowing clouds you can spot from across the
/// galaxy and fly toward. Drawn after the galaxy backdrop and before the catalog stars: additive blending
/// means the stars (also additive) read as points embedded INSIDE the nebula glow. Depth-test off and its
/// own wide projection, so it never clips and never disturbs the system/terrain depth that follows.
/// As the camera enters a nebula the billboard fades out, so you drift through a gentle haze of coloured
/// light and stars rather than slamming into a flat sheet.
/// </summary>
public sealed class NebulaRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>Overall additive brightness (HUD slider). Bloom haloes the brighter cores.</summary>
    public float Intensity = 0.6f;

    private const string VertexSource = @"#version 410 core
uniform mat4 uViewProj;
uniform vec3 uCenter;   // nebula centre, camera-relative (metres)
uniform vec3 uRight;    // camera right (world)
uniform vec3 uUp;       // camera up (world)
uniform float uRadius;  // metres
out vec2 vUv;
void main() {
    // Two-triangle quad, corners in [-1,1]^2.
    vec2 corners[6] = vec2[6](vec2(-1.0,-1.0), vec2(1.0,-1.0), vec2(1.0,1.0),
                              vec2(-1.0,-1.0), vec2(1.0,1.0), vec2(-1.0,1.0));
    vec2 c = corners[gl_VertexID];
    vUv = c;
    vec3 world = uCenter + (c.x * uRight + c.y * uUp) * uRadius;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec2 vUv;
uniform vec3 uColor;
uniform float uSeed;
uniform float uIntensity;
out vec4 FragColor;

float h(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(h(i + vec3(0,0,0)), h(i + vec3(1,0,0)), f.x),
                   mix(h(i + vec3(0,1,0)), h(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(h(i + vec3(0,0,1)), h(i + vec3(1,0,1)), f.x),
                   mix(h(i + vec3(0,1,1)), h(i + vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { s += a * vnoise(p); p *= 2.07; a *= 0.5; } return s; }

void main() {
    float r = length(vUv);
    if (r > 1.0) discard;
    float edge = 1.0 - smoothstep(0.15, 1.0, r);             // soft radial falloff to the rim
    vec3 q = vec3(vUv * 2.2, uSeed);
    float cloud = fbm(q) * 0.6 + fbm(q * 3.1 + 5.0) * 0.4;   // layered wisps
    cloud = clamp(cloud * 1.35 - 0.25, 0.0, 1.0);
    float a = edge * cloud;
    a *= a;                                                  // tighten the wisps, darken the gaps
    FragColor = vec4(uColor * uIntensity * a, 1.0);          // premultiplied; blended additively by the caller
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao; // empty VAO; the vertex shader generates the quad from gl_VertexID

    public NebulaRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
    }

    public void Render(Camera camera, NebulaField field)
    {
        if (!Enabled) return;

        UniversePosition cam = camera.Position;
        Matrix4X4<float> viewProj = camera.ViewMatrix * WideProjection(camera);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); // additive
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetVector3("uRight", camera.Right);
        _shader.SetVector3("uUp", camera.Up);
        _gl.BindVertexArray(_vao);

        foreach (Nebula n in field.Nebulae)
        {
            double dist = cam.DistanceTo(n.Position);
            // Fade out as the camera moves inside, so you pass through a soft haze instead of a flat wall.
            float fade = (float)Math.Clamp(dist / n.RadiusMeters, 0.12, 1.0);

            _shader.SetVector3("uCenter", n.Position.ToCameraRelative(cam));
            _shader.SetFloat("uRadius", (float)n.RadiusMeters);
            _shader.SetVector3("uColor", n.Color);
            _shader.SetFloat("uSeed", n.Seed);
            _shader.SetFloat("uIntensity", Intensity * fade);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6); // billboards behind the camera are clipped by w<=0
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    // A wide, depth-irrelevant projection: the pass writes no depth, so near/far only need to avoid clipping
    // the (galaxy-scale) billboards. A tiny near and an enormous far keep every nebula in frame.
    private static Matrix4X4<float> WideProjection(Camera camera)
        => MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, 1.0e6f, 1.0e22f);

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteVertexArray(_vao);
    }
}
