using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Engine.Platform;

/// <summary>
/// Thin wrapper around a Silk.NET window + GL context + input. Creates an OpenGL 4.1
/// core, forward-compatible context — the highest version macOS supports (and a safe
/// cross-platform baseline). Exposes lifecycle events the game layer subscribes to.
/// </summary>
public sealed class GameWindow : IDisposable
{
    public IWindow Window { get; }
    public GL Gl { get; private set; } = null!;
    public IInputContext Input { get; private set; } = null!;
    public IKeyboard Keyboard { get; private set; } = null!;
    public IMouse Mouse { get; private set; } = null!;

    public event Action? Load;
    public event Action<double>? Update;
    public event Action<double>? Render;
    public event Action<Vector2D<int>>? Resize;

    public GameWindow(string title, int width, int height, bool fullscreen = false)
    {
        var options = WindowOptions.Default;
        options.Title = title;
        options.Size = new Vector2D<int>(width, height);
        options.VSync = true;
        if (fullscreen)
        {
            // Borderless/exclusive fullscreen at the requested resolution. GLFW selects the closest
            // video mode the display supports; the viewport and offscreen buffer follow the actual
            // framebuffer size reported on load/resize, so a different native size still renders right
            // (on a macOS Retina display the framebuffer comes back in native pixels).
            options.WindowState = WindowState.Fullscreen;
            options.VideoMode = new VideoMode(new Vector2D<int>(width, height));
        }
        // 4.1 core + forward-compatible is mandatory for modern GL on macOS.
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.ForwardCompatible,
            new APIVersion(4, 1));

        Window = Silk.NET.Windowing.Window.Create(options);

        Window.Load += OnLoad;
        Window.Update += dt => Update?.Invoke(dt);
        Window.Render += dt => Render?.Invoke(dt);
        Window.FramebufferResize += size =>
        {
            Gl.Viewport(size);
            Resize?.Invoke(size);
        };
    }

    private void OnLoad()
    {
        Gl = GL.GetApi(Window);
        Input = Window.CreateInput();
        Keyboard = Input.Keyboards[0];
        Mouse = Input.Mice[0];
        Load?.Invoke();
    }

    public void Run() => Window.Run();

    public void Dispose()
    {
        Input?.Dispose();
        Window?.Dispose();
    }
}
