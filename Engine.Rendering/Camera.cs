using Engine.Core;
using Silk.NET.Maths;

namespace Engine.Rendering;

/// <summary>
/// A floating-origin camera. Its world position is a full-precision
/// <see cref="UniversePosition"/>; its orientation is a quaternion. The view matrix
/// carries ONLY rotation — every object is pre-translated relative to the camera on
/// the CPU (see <see cref="UniversePosition.ToCameraRelative"/>), so the camera is
/// always effectively at the render-space origin.
/// </summary>
public sealed class Camera
{
    public UniversePosition Position;
    public Quaternion<float> Orientation = Quaternion<float>.Identity;

    public float FovRadians = MathF.PI / 3f; // 60°
    public float AspectRatio = 16f / 9f;

    // The near/far span is deliberately modest for M0. True-scale scenes will need a
    // logarithmic / reversed-Z depth buffer (added in a later milestone) to avoid
    // z-fighting across the enormous depth range.
    public float NearPlane = 0.5f;
    public float FarPlane = 1.0e7f;

    public Vector3D<float> Forward => Vector3D.Transform(new Vector3D<float>(0f, 0f, -1f), Orientation);
    public Vector3D<float> Right => Vector3D.Transform(new Vector3D<float>(1f, 0f, 0f), Orientation);
    public Vector3D<float> Up => Vector3D.Transform(new Vector3D<float>(0f, 1f, 0f), Orientation);

    /// <summary>View matrix (rotation only — translation is handled by camera-relative positions).</summary>
    public Matrix4X4<float> ViewMatrix => Matrix4X4.CreateFromQuaternion(Quaternion<float>.Inverse(Orientation));

    public Matrix4X4<float> ProjectionMatrix => MatrixHelper.PerspectiveGL(FovRadians, AspectRatio, NearPlane, FarPlane);
}
