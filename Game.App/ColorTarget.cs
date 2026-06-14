using Silk.NET.OpenGL;

namespace Game.App;

/// <summary>
/// A minimal offscreen colour-only framebuffer (RGBA8, linear-filtered, clamped) with no depth
/// attachment. Used as a sampleable render target for the post-process chain: capturing the final
/// scene + atmosphere, and as the bloom blur ping-pong buffers. Recreated on resize.
/// </summary>
public sealed class ColorTarget : IDisposable
{
    private readonly GL _gl;
    private uint _fbo, _colorTex;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint ColorTexture => _colorTex;
    public uint Fbo => _fbo;

    public ColorTarget(GL gl) => _gl = gl;

    /// <summary>(Re)create the texture + FBO at the given size. No-op if unchanged.</summary>
    public unsafe void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height && _fbo != 0)) return;
        Width = width; Height = height;
        Dispose();

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        // Linear so the half-res blur upsamples smoothly; clamp so the blur kernel doesn't wrap.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Bind for rendering into this target, viewport matched to its size.</summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void Dispose()
    {
        if (_colorTex != 0) { _gl.DeleteTexture(_colorTex); _colorTex = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
    }
}
