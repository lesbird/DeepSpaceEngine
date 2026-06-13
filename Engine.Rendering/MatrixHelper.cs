using Silk.NET.Maths;

namespace Engine.Rendering;

/// <summary>
/// Matrix helpers that keep our GPU upload convention unambiguous.
///
/// Convention used throughout the engine:
///  - Silk.NET.Maths matrices use the row-vector convention (p' = p * M), so we
///    compose as <c>mvp = model * view * projection</c> (model applied first).
///  - We upload the matrix's row-major storage with <c>transpose = false</c>. GL then
///    reads it column-major, which is exactly the transpose, so in GLSL the natural
///    <c>gl_Position = uMVP * vec4(pos, 1.0)</c> yields the correct result.
/// </summary>
public static class MatrixHelper
{
    /// <summary>
    /// OpenGL perspective projection (clip-space z in [-1, 1]) expressed in Silk's
    /// row-vector convention (i.e. the transpose of the classic column-major GL matrix).
    /// </summary>
    public static Matrix4X4<float> PerspectiveGL(float fovYRadians, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovYRadians * 0.5f);
        float nf = 1f / (near - far);
        return new Matrix4X4<float>(
            f / aspect, 0f, 0f,                     0f,
            0f,         f,  0f,                     0f,
            0f,         0f, (far + near) * nf,     -1f,
            0f,         0f, (2f * far * near) * nf, 0f);
    }

    /// <summary>Copy the 16 matrix elements into <paramref name="dst"/> in row-major order.</summary>
    public static void ToRowMajor(in Matrix4X4<float> m, Span<float> dst)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }
}
