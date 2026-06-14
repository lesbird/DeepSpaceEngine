using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Engine.Rendering;

/// <summary>A compiled, linked GL shader program with cached uniform locations.</summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniformLocations = new();

    public uint Handle { get; }

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
            throw new Exception($"Program link failed: {_gl.GetProgramInfoLog(Handle)}");

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
            throw new Exception($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    private int Location(string name)
    {
        if (!_uniformLocations.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(Handle, name);
            _uniformLocations[name] = loc;
        }
        return loc;
    }

    public void SetMatrix(string name, in Matrix4X4<float> matrix)
    {
        Span<float> data = stackalloc float[16];
        MatrixHelper.ToRowMajor(matrix, data);
        _gl.UniformMatrix4(Location(name), 1, false, data);
    }

    public void SetVector2(string name, Vector2D<float> v) => _gl.Uniform2(Location(name), v.X, v.Y);

    public void SetVector3(string name, Vector3D<float> v) => _gl.Uniform3(Location(name), v.X, v.Y, v.Z);

    public void SetVector4(string name, Vector4D<float> v) => _gl.Uniform4(Location(name), v.X, v.Y, v.Z, v.W);

    public void SetFloat(string name, float value) => _gl.Uniform1(Location(name), value);

    public void SetInt(string name, int value) => _gl.Uniform1(Location(name), value);

    public void Dispose() => _gl.DeleteProgram(Handle);
}
