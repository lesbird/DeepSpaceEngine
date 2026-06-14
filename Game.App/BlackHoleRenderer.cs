using Engine.Core;
using Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// The supermassive black hole at the galactic centre (world origin): an opaque event-horizon
/// sphere ringed by a glowing, banded accretion disk in the galactic plane (world XZ). The disk is
/// emissive/additive with a radial temperature gradient (hot blue-white inner edge fading to a
/// cooler orange rim) and a crude relativistic-beaming brightness asymmetry; the horizon carries a
/// faint photon-ring rim at its silhouette. Like <see cref="SystemRenderer"/> it fits its own
/// near/far to the body so depth stays usable across the huge scale span. The horizon is drawn
/// opaque (depth-tested) so it correctly occludes the far side of the disk; the disk is then drawn
/// additively, depth-tested but writing no depth.
/// </summary>
public sealed class BlackHoleRenderer : IDisposable
{
    // Visual scale (metres). Deliberately exaggerated far beyond a real Schwarzschild radius so the
    // hole reads as a dramatic, fly-to landmark rather than the sub-pixel speck true physics gives.
    public double HorizonRadius = 1.0e13;   // ~67 AU
    public double DiskInner = 1.5e13;
    public double DiskOuter = 2.0e14;       // ~0.021 ly

    private const string HorizonVert = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal; // unused; normal derived from position
uniform mat4 uMVP;
uniform mat4 uModel;
out vec3 vWorld;
void main() {
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

    private const string HorizonFrag = @"#version 410 core
in vec3 vWorld;
uniform vec3 uCenter;   // camera-relative
out vec4 FragColor;
void main() {
    vec3 N = normalize(vWorld - uCenter);
    vec3 V = normalize(-vWorld);                 // fragment -> camera (camera at render origin)
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0);
    vec3 photon = vec3(1.0, 0.55, 0.2) * rim * 1.4;
    FragColor = vec4(photon, 1.0);               // pitch-black centre, glowing silhouette
}";

    private const string DiskVert = @"#version 410 core
layout(location = 0) in vec3 aDir;   // unit direction in the disk plane (x,_,z)
layout(location = 1) in vec3 aParam; // .x = t (0 inner .. 1 outer)
uniform mat4 uViewProj;
uniform mat4 uModel;
uniform float uInnerR;
uniform float uOuterR;
out vec3 vWorld;
out float vT;
void main() {
    float radius = mix(uInnerR, uOuterR, aParam.x);
    vec4 world = uModel * vec4(aDir.x * radius, 0.0, aDir.z * radius, 1.0);
    vWorld = world.xyz;
    vT = aParam.x;
    gl_Position = uViewProj * world;
}";

    private const string DiskFrag = @"#version 410 core
in vec3 vWorld;
in float vT;
uniform vec3 uCenter;     // camera-relative disk centre
out vec4 FragColor;

float hash(float n) { return fract(sin(n) * 43758.5453); }

void main() {
    vec3 radial = vWorld - uCenter;
    float ang = atan(radial.z, radial.x);

    // Radial temperature gradient: hot blue-white inner edge -> cooler orange/red outer.
    vec3 hot  = vec3(0.85, 0.92, 1.0);
    vec3 cool = vec3(1.0, 0.45, 0.12);
    vec3 col = mix(hot, cool, smoothstep(0.0, 1.0, vT));

    // Turbulent radial banding for a swirling-gas look.
    float bands = 0.0, f = 14.0;
    for (int i = 0; i < 4; i++) {
        bands += sin(vT * f + ang * 3.0 + hash(float(i) * 7.13) * 6.2831853);
        f *= 1.8;
    }
    float density = clamp(0.6 + bands * 0.18, 0.15, 1.0);

    // Brighter toward the inner edge; soft fade at both rims.
    float intensity = mix(1.8, 0.25, vT);
    float edge = smoothstep(0.0, 0.06, vT) * (1.0 - smoothstep(0.9, 1.0, vT));

    // Crude relativistic beaming: the side of the disk rotating toward the camera is brighter.
    vec3 tangent = normalize(cross(vec3(0.0, 1.0, 0.0), normalize(radial)));
    vec3 toCam = normalize(-vWorld);
    float beam = 1.0 + 0.8 * dot(tangent, toCam);

    vec3 outc = col * intensity * density * beam * edge;
    FragColor = vec4(outc, 1.0);   // additive (blend set by caller)
}";

    private readonly GL _gl;
    private readonly Shader _horizonShader;
    private readonly Shader _diskShader;
    private readonly Mesh _sphere;
    private readonly Mesh _disk;

    public BlackHoleRenderer(GL gl)
    {
        _gl = gl;
        _horizonShader = new Shader(gl, HorizonVert, HorizonFrag);
        _diskShader = new Shader(gl, DiskVert, DiskFrag);
        _sphere = Primitives.BuildSphere(gl, stacks: 32, slices: 64);
        _disk = Primitives.BuildRingAnnulus(gl, segments: 256);
    }

    public void Render(Camera camera)
    {
        Vector3D<float> centerRel = UniversePosition.Origin.ToCameraRelative(camera.Position);
        Matrix4X4<float> viewProj = camera.ViewMatrix * Projection(camera);

        // --- Event horizon: opaque, depth-tested so it occludes the far side of the disk. ---
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        Matrix4X4<float> hModel =
            Matrix4X4.CreateScale((float)HorizonRadius) * Matrix4X4.CreateTranslation(centerRel);
        _horizonShader.Use();
        _horizonShader.SetMatrix("uModel", hModel);
        _horizonShader.SetMatrix("uMVP", hModel * viewProj);
        _horizonShader.SetVector3("uCenter", centerRel);
        _sphere.Draw();

        // --- Accretion disk: additive, depth-tested against the horizon but writing no depth. ---
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);

        Matrix4X4<float> dModel = Matrix4X4.CreateTranslation(centerRel);
        _diskShader.Use();
        _diskShader.SetMatrix("uViewProj", viewProj);
        _diskShader.SetMatrix("uModel", dModel);
        _diskShader.SetFloat("uInnerR", (float)DiskInner);
        _diskShader.SetFloat("uOuterR", (float)DiskOuter);
        _diskShader.SetVector3("uCenter", centerRel);
        _disk.Draw();

        // Restore the frame defaults the rest of the pipeline expects.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
    }

    private Matrix4X4<float> Projection(Camera camera)
    {
        double d = camera.Position.DistanceTo(UniversePosition.Origin);
        float near = (float)Math.Max(HorizonRadius * 0.05, (d - DiskOuter) * 0.5);
        float far = (float)Math.Max((d + DiskOuter) * 2.0, near * 10.0);
        return MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far);
    }

    public void Dispose()
    {
        _horizonShader.Dispose();
        _diskShader.Dispose();
        _sphere.Dispose();
        _disk.Dispose();
    }
}
