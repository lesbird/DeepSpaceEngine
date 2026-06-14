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
/// The wheel always sets a single logarithmic <i>desired</i> speed (<see cref="SpeedExponent"/>),
/// from 1 m/s up to interstellar velocities. The actual speed is that desired value clamped to a
/// <see cref="MaxSpeed"/> cap supplied each frame by <see cref="SetSpeedContext"/> / <see cref="SpeedPolicy"/>:
/// far from every body the cap is infinite (you fly at the full desired speed); as you near a star,
/// planet or moon it shrinks, automatically decelerating you into the approach. A per-frame
/// anti-tunnelling clamp also bounds how much of the remaining distance to the nearest surface you
/// may cross in one step, so even at extreme speed (or a low frame-rate) you can never skip past a body.
/// </summary>
public sealed class FreeFlyController
{
    private readonly Camera _camera;
    private readonly IKeyboard _keyboard;
    private readonly IMouse _mouse;

    private Vector2D<float> _lookDelta;
    private Vector2D<float>? _lastMousePos;

    /// <summary>Desired speed = 10^Exponent metres/second (wheel-controlled).</summary>
    public float SpeedExponent = 1.0f;
    public float Sensitivity = 0.0025f;
    public float RollSpeed = 1.5f; // radians/sec
    public bool MouseLookEnabled = true;

    private const float MinSpeedExp = 0f;   // 1 m/s
    private const float MaxSpeedExp = 18f;  // 1e18 m/s (~100 ly/s)

    // Never advance more than this fraction of the distance to the nearest engaged surface in a single
    // frame. Keeps a fast approach from tunnelling through a body before the cap can react, regardless
    // of frame-rate; in normal flight the proportional cap is far below this, so it rarely engages.
    private const double AntiTunnelFraction = 0.5;

    /// <summary>Proximity speed cap (m/s) for this frame; +Infinity when nothing is near (unlimited).</summary>
    public double MaxSpeed { get; private set; } = double.PositiveInfinity;

    /// <summary>Distance (m) to the nearest body surface currently limiting us; +Infinity if none.</summary>
    private double _approachGap = double.PositiveInfinity;

    /// <summary>The wheel-commanded speed (m/s) before any proximity cap.</summary>
    public double DesiredSpeed => Math.Pow(10, SpeedExponent);

    /// <summary>True when the proximity cap is actually holding the speed below what the wheel commands.</summary>
    public bool SpeedCapped => MaxSpeed < DesiredSpeed;

    /// <summary>Actual speed this frame: the desired speed clamped to the proximity cap.</summary>
    public double CurrentSpeed => Math.Min(DesiredSpeed, MaxSpeed);

    public FreeFlyController(Camera camera, IKeyboard keyboard, IMouse mouse)
    {
        _camera = camera;
        _keyboard = keyboard;
        _mouse = mouse;
        _mouse.MouseMove += OnMouseMove;
        _mouse.Scroll += OnScroll;
    }

    /// <summary>
    /// Supply this frame's proximity limit: <paramref name="maxSpeed"/> is the speed cap (m/s,
    /// +Infinity when unlimited) and <paramref name="approachGap"/> is the distance to the nearest
    /// limiting surface (m, +Infinity if none). Call each frame after body positions are up to date.
    /// </summary>
    public void SetSpeedContext(double maxSpeed, double approachGap)
    {
        MaxSpeed = maxSpeed;
        _approachGap = approachGap;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
        => SpeedExponent = Math.Clamp(SpeedExponent + wheel.Y * 0.25f, MinSpeedExp, MaxSpeedExp);

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
            // Anti-tunnelling: never cross more than a fraction of the gap to the nearest surface in one
            // frame, so a fast approach (or a stalled frame) can't jump straight through a body.
            if (!double.IsInfinity(_approachGap))
                dist = Math.Min(dist, AntiTunnelFraction * _approachGap);
            _camera.Position.Translate(new Vector3D<double>(worldDir.X * dist, worldDir.Y * dist, worldDir.Z * dist));
        }
    }
}
