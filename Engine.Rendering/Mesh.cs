using Silk.NET.OpenGL;

namespace Engine.Rendering;

/// <summary>
/// A simple static mesh: an interleaved vertex buffer of [position.xyz, color.rgb]
/// floats, drawn with a fixed primitive type. Attribute 0 = position, 1 = color.
/// </summary>
public sealed class Mesh : IDisposable
{
    public const int FloatsPerVertex = 6;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _vertexCount;
    private readonly PrimitiveType _primitive;

    public unsafe Mesh(GL gl, ReadOnlySpan<float> vertices, PrimitiveType primitive)
    {
        _gl = gl;
        _primitive = primitive;
        _vertexCount = (uint)(vertices.Length / FloatsPerVertex);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);

        uint stride = FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(_primitive, 0, _vertexCount);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
