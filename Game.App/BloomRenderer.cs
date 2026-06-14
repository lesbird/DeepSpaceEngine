using Engine.Rendering;
using Silk.NET.OpenGL;
using Shader = Engine.Rendering.Shader;

namespace Game.App;

/// <summary>
/// A simple LDR threshold bloom post-process. A bright-pass extracts pixels above a soft threshold
/// from the finished scene (scene + atmosphere) into a half-resolution buffer; a separable Gaussian
/// blur is run back and forth between two ping-pong buffers; and a composite pass adds the blurred
/// result back over the scene to screen. Operates entirely in RGBA8 — bright emissive sources (sun,
/// accretion disk, star/planet glows) already saturate toward white, so they drive the effect.
///
/// All passes draw a single full-screen triangle from <c>gl_VertexID</c> with an empty VAO (the same
/// trick as <see cref="GalaxyBackdrop"/>), so no vertex buffers are needed.
/// </summary>
public sealed class BloomRenderer : IDisposable
{
    public bool Enabled = true;
    /// <summary>Luma above which a pixel contributes to bloom.</summary>
    public float Threshold = 0.75f;
    /// <summary>Soft-knee width below the threshold for a gentle roll-on (0 = hard cutoff).</summary>
    public float Knee = 0.5f;
    /// <summary>How strongly the blurred bloom is added back over the scene.</summary>
    public float Intensity = 0.6f;
    /// <summary>Blur ping-pong pairs; more = wider, softer glow.</summary>
    public int Iterations = 5;

    private const string FullscreenVert = @"#version 410 core
out vec2 vUV;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vUV = p * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    private const string BrightFrag = @"#version 410 core
in vec2 vUV;
uniform sampler2D uScene;
uniform float uThreshold;
uniform float uKnee;
out vec4 FragColor;
void main() {
    vec3 c = texture(uScene, vUV).rgb;
    float b = max(c.r, max(c.g, c.b));
    // Soft-knee threshold (Unity-style): smooth ramp from (threshold - knee) to (threshold + knee).
    float soft = clamp(b - uThreshold + uKnee, 0.0, 2.0 * uKnee);
    soft = soft * soft / (4.0 * uKnee + 1e-4);
    float contrib = max(soft, b - uThreshold) / max(b, 1e-4);
    FragColor = vec4(c * contrib, 1.0);
}";

    private const string BlurFrag = @"#version 410 core
in vec2 vUV;
uniform sampler2D uTex;
uniform vec2 uDir;        // (texelW, 0) horizontal or (0, texelH) vertical
out vec4 FragColor;
void main() {
    // 9-tap separable Gaussian.
    float w0 = 0.227027, w1 = 0.1945946, w2 = 0.1216216, w3 = 0.054054, w4 = 0.016216;
    vec3 c = texture(uTex, vUV).rgb * w0;
    c += texture(uTex, vUV + uDir * 1.0).rgb * w1;
    c += texture(uTex, vUV - uDir * 1.0).rgb * w1;
    c += texture(uTex, vUV + uDir * 2.0).rgb * w2;
    c += texture(uTex, vUV - uDir * 2.0).rgb * w2;
    c += texture(uTex, vUV + uDir * 3.0).rgb * w3;
    c += texture(uTex, vUV - uDir * 3.0).rgb * w3;
    c += texture(uTex, vUV + uDir * 4.0).rgb * w4;
    c += texture(uTex, vUV - uDir * 4.0).rgb * w4;
    FragColor = vec4(c, 1.0);
}";

    private const string CompositeFrag = @"#version 410 core
in vec2 vUV;
uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uIntensity;
out vec4 FragColor;
void main() {
    vec3 scene = texture(uScene, vUV).rgb;
    vec3 bloom = texture(uBloom, vUV).rgb;
    FragColor = vec4(scene + bloom * uIntensity, 1.0);
}";

    private readonly GL _gl;
    private readonly Shader _bright;
    private readonly Shader _blur;
    private readonly Shader _composite;
    private readonly uint _vao;          // empty; vertices come from gl_VertexID
    private readonly ColorTarget _a;     // ping
    private readonly ColorTarget _b;     // pong

    private int _fullW = 1, _fullH = 1;

    public BloomRenderer(GL gl)
    {
        _gl = gl;
        _bright = new Shader(gl, FullscreenVert, BrightFrag);
        _blur = new Shader(gl, FullscreenVert, BlurFrag);
        _composite = new Shader(gl, FullscreenVert, CompositeFrag);
        _vao = gl.GenVertexArray();
        _a = new ColorTarget(gl);
        _b = new ColorTarget(gl);
    }

    /// <summary>Match to the screen size; the blur buffers run at half resolution.</summary>
    public void Resize(int width, int height)
    {
        _fullW = Math.Max(1, width);
        _fullH = Math.Max(1, height);
        int hw = Math.Max(1, width / 2);
        int hh = Math.Max(1, height / 2);
        _a.Resize(hw, hh);
        _b.Resize(hw, hh);
    }

    /// <summary>
    /// Bright-pass <paramref name="sceneColorTex"/> then blur it; returns the bloom texture. The
    /// scene's own colour is composited back in by <see cref="Composite"/>.
    /// </summary>
    public uint Render(uint sceneColorTex)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        // Bright-pass: scene -> _a (half-res).
        _a.Bind();
        _bright.Use();
        BindTex(0, sceneColorTex);
        _bright.SetInt("uScene", 0);
        _bright.SetFloat("uThreshold", Threshold);
        _bright.SetFloat("uKnee", Math.Max(1e-3f, Knee));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Separable Gaussian, ping-ponging _a <-> _b. Each iteration is one H then one V pass,
        // leaving the result back in _a.
        float texW = 1f / _a.Width, texH = 1f / _a.Height;
        _blur.Use();
        _blur.SetInt("uTex", 0);
        int iters = Math.Clamp(Iterations, 1, 16);
        for (int i = 0; i < iters; i++)
        {
            _b.Bind();
            BindTex(0, _a.ColorTexture);
            _blur.SetVector2("uDir", new Silk.NET.Maths.Vector2D<float>(texW, 0f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _a.Bind();
            BindTex(0, _b.ColorTexture);
            _blur.SetVector2("uDir", new Silk.NET.Maths.Vector2D<float>(0f, texH));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        return _a.ColorTexture;
    }

    /// <summary>Draw <c>scene + intensity·bloom</c> to the screen (default framebuffer, full-res).</summary>
    public void Composite(uint sceneColorTex, uint bloomTex)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_fullW, (uint)_fullH);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _composite.Use();
        BindTex(0, sceneColorTex);
        BindTex(1, bloomTex);
        _composite.SetInt("uScene", 0);
        _composite.SetInt("uBloom", 1);
        _composite.SetFloat("uIntensity", Enabled ? Intensity : 0f);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.DepthMask(true);
    }

    private void BindTex(int unit, uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    public void Dispose()
    {
        _bright.Dispose();
        _blur.Dispose();
        _composite.Dispose();
        _gl.DeleteVertexArray(_vao);
        _a.Dispose();
        _b.Dispose();
    }
}
