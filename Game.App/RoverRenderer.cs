using Engine.Core;
using Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// Draws the surface rover as a small static mesh (chassis + four wheels + a sensor mast) in
/// camera-relative space. Oriented by the body basis supplied by <see cref="RoverController"/>
/// (forward = heading, up = terrain normal) so it conforms to slopes. Rendered with the same
/// near/far as the terrain — drawn inside the scene framebuffer right after it — so it shares the
/// depth buffer and the depth-aware atmosphere composites over it correctly.
/// </summary>
public sealed class RoverRenderer : IDisposable
{
    private const string VertexSource = @"#version 410 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec3 aColor;
uniform mat4 uMVP;
uniform mat4 uModel;
out vec3 vN;
out vec3 vColor;
void main() {
    vN = mat3(uModel) * aNormal;
    vColor = aColor;
    gl_Position = uMVP * vec4(aPos, 1.0);
}";

    private const string FragmentSource = @"#version 410 core
in vec3 vN;
in vec3 vColor;
uniform vec3 uSunDir;
out vec4 FragColor;
void main() {
    float d = max(dot(normalize(vN), uSunDir), 0.0);
    FragColor = vec4(vColor * (0.15 + 0.9 * d), 1.0);
}";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _chassisVao, _chassisVbo, _chassisCount; // chassis + accent + mast (one rigid piece)
    private readonly uint _wheelVao, _wheelVbo, _wheelCount;        // one wheel, drawn four times with travel

    // Wheel rest layout in the body frame (+X right, +Y up, +Z forward), in renderer order FR, FL, BR, BL
    // — matching Rover.WheelTravel. Bottom at restY − halfY = −0.75 = −Rover.RideHeight, so a wheel at
    // zero travel rests on the sampled ground; suspension travel shifts each wheel's Y in its well.
    private const float RestY = -0.30f;
    private static readonly Vector3D<float>[] WheelXZ =
    {
        new(1.05f, 0f, 1.05f), new(-1.05f, 0f, 1.05f),
        new(1.05f, 0f, -1.05f), new(-1.05f, 0f, -1.05f),
    };

    public RoverRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        (_chassisVao, _chassisVbo, _chassisCount) = MakeBuffer(gl, BuildChassisMesh());
        (_wheelVao, _wheelVbo, _wheelCount) = MakeBuffer(gl, BuildWheelMesh());
    }

    /// <param name="bodyPos">World position of the rover body.</param>
    /// <param name="forward">Heading (driving) direction, world space.</param>
    /// <param name="up">Body up (terrain normal), world space.</param>
    /// <param name="wheelTravel">Per-wheel suspension offset (FR, FL, BR, BL), from <see cref="Game.Universe.Rover.WheelTravel"/>.</param>
    /// <param name="near">/<param name="far">Must match the terrain pass for a coherent depth buffer.</param>
    public void Render(Camera camera, UniversePosition bodyPos, Vector3D<float> forward, Vector3D<float> up,
        IReadOnlyList<double> wheelTravel, Vector3D<float> sunDir, float near, float far)
    {
        Vector3D<float> rel = bodyPos.ToCameraRelative(camera.Position);

        // Orthonormal body basis → rotation rows (Silk row-vector convention: rows are the images
        // of the local X/Y/Z axes). Local +Z is the rover's forward.
        Vector3D<float> f = Vector3D.Normalize(forward);
        Vector3D<float> r = Vector3D.Normalize(Vector3D.Cross(f, up));
        Vector3D<float> u = Vector3D.Cross(r, f);
        var rot = new Matrix4X4<float>(
            r.X, r.Y, r.Z, 0f,
            u.X, u.Y, u.Z, 0f,
            f.X, f.Y, f.Z, 0f,
            0f, 0f, 0f, 1f);
        Matrix4X4<float> model = rot * Matrix4X4.CreateTranslation(rel);

        Matrix4X4<float> proj = MatrixHelper.PerspectiveGL(camera.FovRadians, camera.AspectRatio, near, far);
        Matrix4X4<float> viewProj = camera.ViewMatrix * proj;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _shader.Use();
        _shader.SetVector3("uSunDir", sunDir);

        // Chassis (rigid).
        _shader.SetMatrix("uModel", model);
        _shader.SetMatrix("uMVP", model * viewProj);
        _gl.BindVertexArray(_chassisVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, _chassisCount);

        // Four wheels, each dropped into its well by its suspension travel (+ = hangs lower). The body-
        // local offset is pre-multiplied so it rotates with the chassis, then placed by the body model.
        _gl.BindVertexArray(_wheelVao);
        for (int i = 0; i < WheelXZ.Length; i++)
        {
            float travel = wheelTravel != null && i < wheelTravel.Count ? (float)wheelTravel[i] : 0f;
            var offset = new Vector3D<float>(WheelXZ[i].X, RestY - travel, WheelXZ[i].Z);
            Matrix4X4<float> wheelModel = Matrix4X4.CreateTranslation(offset) * model;
            _shader.SetMatrix("uModel", wheelModel);
            _shader.SetMatrix("uMVP", wheelModel * viewProj);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, _wheelCount);
        }

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.DepthTest);
    }

    // --- mesh (metres, local frame: +X right, +Y up, +Z forward) ---

    private static float[] BuildChassisMesh()
    {
        var v = new List<float>();
        var body = new Vector3D<float>(0.72f, 0.74f, 0.78f);     // light grey chassis
        var mast = new Vector3D<float>(0.45f, 0.47f, 0.50f);     // sensor mast
        var accent = new Vector3D<float>(0.85f, 0.55f, 0.15f);   // front marker (shows heading)

        // Chassis.
        AddBox(v, new Vector3D<float>(0f, 0.15f, 0f), new Vector3D<float>(1.05f, 0.42f, 1.55f), body);
        // Front accent strip at +Z so the driving direction is obvious.
        AddBox(v, new Vector3D<float>(0f, 0.18f, 1.55f), new Vector3D<float>(0.9f, 0.22f, 0.08f), accent);
        // Sensor mast toward the rear.
        AddBox(v, new Vector3D<float>(0f, 1.0f, -0.7f), new Vector3D<float>(0.09f, 0.62f, 0.09f), mast);
        AddBox(v, new Vector3D<float>(0f, 1.7f, -0.7f), new Vector3D<float>(0.22f, 0.07f, 0.22f), mast);

        return v.ToArray();
    }

    /// <summary>One wheel box centred at the origin (half-height = <see cref="Game.Universe.Rover.WheelRadius"/>),
    /// drawn four times by <see cref="Render"/> with a per-wheel body-local offset.</summary>
    private static float[] BuildWheelMesh()
    {
        var v = new List<float>();
        var wheel = new Vector3D<float>(0.10f, 0.10f, 0.11f); // dark tyre
        AddBox(v, Vector3D<float>.Zero, new Vector3D<float>(0.28f, 0.45f, 0.55f), wheel);
        return v.ToArray();
    }

    private static void AddBox(List<float> v, Vector3D<float> c, Vector3D<float> h, Vector3D<float> col)
    {
        var hx = new Vector3D<float>(h.X, 0, 0);
        var hy = new Vector3D<float>(0, h.Y, 0);
        var hz = new Vector3D<float>(0, 0, h.Z);
        void Quad(Vector3D<float> o, Vector3D<float> e1, Vector3D<float> e2, Vector3D<float> n)
        {
            Emit(v, o, n, col); Emit(v, o + e1, n, col); Emit(v, o + e1 + e2, n, col);
            Emit(v, o, n, col); Emit(v, o + e1 + e2, n, col); Emit(v, o + e2, n, col);
        }
        Quad(c + hx - hy - hz, hy * 2, hz * 2, new Vector3D<float>(1, 0, 0));
        Quad(c - hx - hy - hz, hz * 2, hy * 2, new Vector3D<float>(-1, 0, 0));
        Quad(c + hy - hx - hz, hz * 2, hx * 2, new Vector3D<float>(0, 1, 0));
        Quad(c - hy - hx - hz, hx * 2, hz * 2, new Vector3D<float>(0, -1, 0));
        Quad(c + hz - hx - hy, hx * 2, hy * 2, new Vector3D<float>(0, 0, 1));
        Quad(c - hz - hx - hy, hy * 2, hx * 2, new Vector3D<float>(0, 0, -1));
    }

    private static void Emit(List<float> v, Vector3D<float> p, Vector3D<float> n, Vector3D<float> c)
    {
        v.Add(p.X); v.Add(p.Y); v.Add(p.Z);
        v.Add(n.X); v.Add(n.Y); v.Add(n.Z);
        v.Add(c.X); v.Add(c.Y); v.Add(c.Z);
    }

    private static unsafe (uint vao, uint vbo, uint count) MakeBuffer(GL gl, float[] data)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);

        uint stride = 9 * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.BindVertexArray(0);
        return (vao, vbo, (uint)(data.Length / 9));
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_chassisVbo);
        _gl.DeleteVertexArray(_chassisVao);
        _gl.DeleteBuffer(_wheelVbo);
        _gl.DeleteVertexArray(_wheelVao);
        _shader.Dispose();
    }
}
