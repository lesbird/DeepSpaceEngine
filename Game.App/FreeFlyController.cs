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
    // Top speed reaches intergalactic scale: galaxies are millions of ly apart (true scale), so ~100 ly/s
    // would take hours to cross the void. 1e24 m/s ≈ 1e8 ly/s ≈ 100 Mly/s lets you reach a neighbour in
    // seconds; wheel down for fine control. The proximity cap still clamps you on approach to any body.
    private const float MaxSpeedExp = 24f;  // 1e24 m/s (~100 Mly/s)

    // Never advance more than this fraction of the distance to the nearest engaged surface in a single
    // frame. Keeps a fast approach from tunnelling through a body before the cap can react, regardless
    // of frame-rate; in normal flight the proportional cap is far below this, so it rarely engages.
    private const double AntiTunnelFraction = 0.5;

    // Exponential easing time-constants (seconds) for ramping the actual flight speed toward the target.
    // Accelerating eases in gently; braking uses a much shorter constant so slowing down bites hard.
    private const double AccelSmoothTime = 0.5;
    private const double BrakeSmoothTime = 0.12;

    // Smoothed flight speed (m/s) and the world-space direction it carries, so releasing the keys coasts
    // to a stop along the last heading rather than freezing instantly.
    private double _smoothSpeed;
    private Vector3D<double> _moveDir;

    /// <summary>Proximity speed cap (m/s) for this frame; +Infinity when nothing is near (unlimited).</summary>
    public double MaxSpeed { get; private set; } = double.PositiveInfinity;

    /// <summary>Distance (m) to the nearest body surface currently limiting us; +Infinity if none.</summary>
    private double _approachGap = double.PositiveInfinity;

    /// <summary>The wheel-commanded speed (m/s) before any proximity cap.</summary>
    public double DesiredSpeed => Math.Pow(10, SpeedExponent);

    /// <summary>True when the proximity cap is actually holding the speed below what the wheel commands.</summary>
    public bool SpeedCapped => MaxSpeed < DesiredSpeed;

    /// <summary>Desired speed clamped to the proximity cap — the speed we'd travel at while thrusting.</summary>
    public double CurrentSpeed => Math.Min(DesiredSpeed, MaxSpeed);

    /// <summary>Actual ground speed (m/s) measured from the distance moved last frame; 0 when coasting/idle.</summary>
    public double ActualSpeed { get; private set; }

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

        // Ease the flight speed toward its target instead of snapping: hold a key and we ramp up to the
        // (proximity-clamped) desired speed; release and the target drops to zero so we glide to a stop
        // along the last heading. Exponential smoothing is frame-rate independent and scale-free, so it
        // feels the same at 1 m/s or interstellar velocity.
        bool thrusting = move != Vector3D<float>.Zero;
        if (thrusting)
        {
            move = Vector3D.Normalize(move);
            var worldDir = Vector3D.Transform(move, _camera.Orientation);
            _moveDir = new Vector3D<double>(worldDir.X, worldDir.Y, worldDir.Z);
        }

        double targetSpeed = thrusting ? CurrentSpeed : 0;
        // Brake hard when the target is below our current speed (released, or a proximity cap kicking in);
        // ease in gently when speeding up.
        double tau = targetSpeed < _smoothSpeed ? BrakeSmoothTime : AccelSmoothTime;
        double blend = tau > 0 ? 1 - Math.Exp(-dt / tau) : 1;
        _smoothSpeed += (targetSpeed - _smoothSpeed) * blend;
        // The proximity cap is a hard ceiling, not a smoothing target: as you near a body it shrinks fast,
        // and the eased speed must obey it immediately or you sail straight past before braking catches up.
        // MaxSpeed is +Infinity in open space, so this never interferes with free-flight ramp-up/coast-down.
        if (_smoothSpeed > MaxSpeed) _smoothSpeed = MaxSpeed;
        // Snap the asymptotic tail to a dead stop once coasting drops below the slowest meaningful speed.
        if (!thrusting && _smoothSpeed < SpeedPolicy.MinApproachSpeed) _smoothSpeed = 0;

        ActualSpeed = 0;
        if (_smoothSpeed > 0 && _moveDir != Vector3D<double>.Zero)
        {
            double dist = _smoothSpeed * dt;
            // Anti-tunnelling: never cross more than a fraction of the gap to the nearest surface in one
            // frame, so a fast approach (or a stalled frame) can't jump straight through a body. But keep
            // a floor of the surface-creep speed: right at a surface the gap is ~0, so without this you'd
            // freeze solid and couldn't pull away. That floor is metres-per-frame — far too small to
            // tunnel — and it reopens the moment you start moving off the surface.
            if (!double.IsInfinity(_approachGap))
            {
                double maxStep = Math.Max(AntiTunnelFraction * _approachGap, SpeedPolicy.MinApproachSpeed * dt);
                dist = Math.Min(dist, maxStep);
            }
            _camera.Position.Translate(_moveDir * dist);
            if (dt > 0) ActualSpeed = dist / dt;
        }
    }
}
