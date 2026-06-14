using Engine.Rendering;
using Game.Systems;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>
/// True 6-DOF free-fly camera. Mouse pitch/yaw and Q/E roll are applied as incremental
/// rotations in the camera's LOCAL frame and composed onto the orientation quaternion —
/// there is no world "up", no pitch clamp, and no pole, so you can rotate freely in any
/// direction (loop, roll, point straight up) with no gimbal lock. WASD translate.
///
/// Speed has two regimes (see <see cref="SetSpeedContext"/> / <see cref="SpeedPolicy"/>):
/// <list type="bullet">
/// <item>Open galaxy — unlimited. The wheel sets a logarithmic absolute speed from 1 m/s up
///   to interstellar velocities (<see cref="SpeedExponent"/>).</item>
/// <item>Inside a system — an auto cap kicks in (up to 1,000,000 c, falling toward 1,000 km/s near a
///   planet). The wheel instead scales a <see cref="ThrottleFraction"/> percentage of that cap,
///   so you can ease below the auto value for a fine approach.</item>
/// </list>
/// Entering a system resets the throttle to 100% so the speed auto-drops to the cap; leaving it
/// resumes log control from whatever speed you were doing.
/// </summary>
public sealed class FreeFlyController
{
    private readonly Camera _camera;
    private readonly IKeyboard _keyboard;
    private readonly IMouse _mouse;

    private Vector2D<float> _lookDelta;
    private Vector2D<float>? _lastMousePos;

    /// <summary>Open-galaxy speed = 10^Exponent metres/second.</summary>
    public float SpeedExponent = 1.0f;
    public float Sensitivity = 0.0025f;
    public float RollSpeed = 1.5f; // radians/sec
    public bool MouseLookEnabled = true;

    private const float MinSpeedExp = 0f;   // 1 m/s
    private const float MaxSpeedExp = 18f;  // 1e18 m/s (~100 ly/s)

    private const double ThrottleStep = 1.15;   // per wheel notch (~15%)
    private const double MinThrottle = 0.001;   // floor so you can creep in for a landing

    /// <summary>Auto max-speed cap (m/s). +Infinity in open galaxy (unlimited).</summary>
    public double MaxSpeed { get; private set; } = double.PositiveInfinity;

    /// <summary>Wheel-controlled fraction [MinThrottle, 1] of <see cref="MaxSpeed"/> when capped.</summary>
    public double ThrottleFraction { get; private set; } = 1.0;

    /// <summary>True when an auto cap is in force (inside a system).</summary>
    public bool SpeedCapped => !double.IsInfinity(MaxSpeed);

    /// <summary>Throttle as a percentage for the HUD.</summary>
    public double ThrottlePercent => ThrottleFraction * 100.0;

    public double CurrentSpeed => SpeedCapped ? ThrottleFraction * MaxSpeed : Math.Pow(10, SpeedExponent);

    public FreeFlyController(Camera camera, IKeyboard keyboard, IMouse mouse)
    {
        _camera = camera;
        _keyboard = keyboard;
        _mouse = mouse;
        _mouse.MouseMove += OnMouseMove;
        _mouse.Scroll += OnScroll;
    }

    /// <summary>
    /// Refresh the auto speed cap from the flight context. Call each frame after the system and
    /// planet positions are up to date.
    /// </summary>
    public void SetSpeedContext(bool inSystem, double nearestSurfaceMeters)
    {
        double cap = SpeedPolicy.MaxSpeed(inSystem, nearestSurfaceMeters);
        bool wasCapped = SpeedCapped;
        bool nowCapped = !double.IsInfinity(cap);

        if (nowCapped && !wasCapped)
        {
            ThrottleFraction = 1.0; // entering a system: auto-drop to the cap
        }
        else if (wasCapped && !nowCapped)
        {
            // Leaving: resume logarithmic control from the speed we were actually doing.
            double resume = ThrottleFraction * MaxSpeed;
            SpeedExponent = (float)Math.Clamp(Math.Log10(Math.Max(1.0, resume)), MinSpeedExp, MaxSpeedExp);
        }
        MaxSpeed = cap;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (SpeedCapped)
            ThrottleFraction = Math.Clamp(ThrottleFraction * Math.Pow(ThrottleStep, wheel.Y), MinThrottle, 1.0);
        else
            SpeedExponent = Math.Clamp(SpeedExponent + wheel.Y * 0.25f, MinSpeedExp, MaxSpeedExp);
    }

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        var p = new Vector2D<float>(position.X, position.Y);
        if (_lastMousePos is { } last && MouseLookEnabled)
            _lookDelta += p - last;
        _lastMousePos = p;
    }

    public void Update(double dt)
    {
        // --- Orientation: incremental local-frame rotation (no clamp, no pole) ---
        float yawAngle = -_lookDelta.X * Sensitivity;
        float pitchAngle = -_lookDelta.Y * Sensitivity;
        _lookDelta = default;

        float rollAngle = 0f;
        if (_keyboard.IsKeyPressed(Key.Q)) rollAngle += RollSpeed * (float)dt; // roll left
        if (_keyboard.IsKeyPressed(Key.E)) rollAngle -= RollSpeed * (float)dt; // roll right

        // Increments are about the LOCAL axes (X = right, Y = up, Z = forward); composing on
        // the right (orientation * delta) applies them in the camera's own frame.
        var delta =
            Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitY, yawAngle) *
            Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, pitchAngle) *
            Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitZ, rollAngle);
        _camera.Orientation = Quaternion<float>.Normalize(_camera.Orientation * delta);

        // --- Translation from WASD in camera-local space ---
        var move = Vector3D<float>.Zero;
        if (_keyboard.IsKeyPressed(Key.W)) move.Z -= 1f;
        if (_keyboard.IsKeyPressed(Key.S)) move.Z += 1f;
        if (_keyboard.IsKeyPressed(Key.A)) move.X -= 1f;
        if (_keyboard.IsKeyPressed(Key.D)) move.X += 1f;

        if (move != Vector3D<float>.Zero)
        {
            move = Vector3D.Normalize(move);
            var worldDir = Vector3D.Transform(move, _camera.Orientation);
            double dist = CurrentSpeed * dt;
            _camera.Position.Translate(new Vector3D<double>(worldDir.X * dist, worldDir.Y * dist, worldDir.Z * dist));
        }
    }
}
