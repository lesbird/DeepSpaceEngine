using Silk.NET.OpenGL;

namespace Game.App;

/// <summary>
/// An offscreen framebuffer with a colour texture and a <b>sampleable depth texture</b>. The
/// scene (stars, system, terrain) renders into it; the depth texture then lets the atmosphere
/// pass read the real per-pixel geometry distance and clamp its ray march to the surface —
/// so haze lands on terrain by the correct amount and the sky never overwrites the ground.
/// Recreated on resize to match the framebuffer size.
/// </summary>
public sealed class SceneFramebuffer : IDisposable
{
    private readonly GL _gl;
    private uint _fbo, _colorTex, _depthTex;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint DepthTexture => _depthTex;

    public SceneFramebuffer(GL gl) => _gl = gl;

    /// <summary>(Re)create the textures + FBO at the given size. No-op if unchanged.</summary>
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
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);

        _depthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _depthTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24, (uint)width, (uint)height, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _depthTex, 0);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Bind for rendering the scene into the offscreen colour + depth, viewport matched.</summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    /// <summary>Copy the rendered colour to the default framebuffer (screen) so it's displayed.</summary>
    public void BlitColorToScreen() => BlitColorTo(0);

    /// <summary>Copy the rendered colour into another framebuffer (or 0 for the screen), 1:1 sizes.</summary>
    public void BlitColorTo(uint targetFbo)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height,
            (uint)ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        if (_depthTex != 0) { _gl.DeleteTexture(_depthTex); _depthTex = 0; }
        if (_colorTex != 0) { _gl.DeleteTexture(_colorTex); _colorTex = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
    }
}
