using Engine.Core;
using Engine.Rendering;
using Game.Universe;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>
/// Drives the <see cref="Rover"/> sim from keyboard input and parks a third-person <b>chase
/// camera</b> behind it. Active only while the app is in driving mode; the rest of the time the
/// <see cref="FreeFlyController"/> owns the camera. The rover lives in planet-centred coordinates,
/// so each frame the absolute body position is recomputed from the planet's current orbital
/// position — the camera rides the planet's frame with no extra bookkeeping.
/// </summary>
public sealed class RoverController
{
    private const double G = 6.674e-11;     // gravitational constant
    private const double FollowDist = 14.0; // chase camera distance, m
    private const double BasePitch = 0.42;  // chase elevation above the horizon, rad
    private const double LookUp = 2.2;      // aim point above the body, m
    private const double TurnSmoothing = 6.0; // body-orientation slew rate, 1/s
    private const float OrbitSensitivity = 0.005f;

    private readonly Camera _camera;
    private readonly IKeyboard _keyboard;
    private readonly IMouse _mouse;
    private readonly PlanetTerrainRenderer _terrainRenderer;

    private CelestialBody _planet = null!;
    private PlanetTerrain _terrain = null!;
    private Rover _rover = null!;

    private Vector3D<double> _bodyFwd, _bodyUp; // smoothed render basis
    private double _orbitYaw, _orbitPitch;
    private Vector2D<float>? _lastMouse;

    /// <summary>True while driving — gates mouse-orbit accumulation.</summary>
    public bool Active;

    public RoverController(Camera camera, IKeyboard keyboard, IMouse mouse, PlanetTerrainRenderer terrainRenderer)
    {
        _camera = camera;
        _keyboard = keyboard;
        _mouse = mouse;
        _terrainRenderer = terrainRenderer;
        _mouse.MouseMove += OnMouseMove;
    }

    /// <summary>World position of the rover body (for the renderer).</summary>
    public UniversePosition BodyPosition => _planet.CurrentPosition.Translated(_rover.LocalPos);
    public Vector3D<float> Forward => ToF(_bodyFwd);
    public Vector3D<float> Up => ToF(_bodyUp);
    public double SpeedKph => _rover.SpeedMps * 3.6;

    /// <summary>Per-wheel suspension travel (FR, FL, BR, BL) for the renderer's independent wheels.</summary>
    public IReadOnlyList<double> WheelTravel => _rover.WheelTravel;

    /// <summary>
    /// Begin driving on <paramref name="body"/> (a planet or a moon), seeding the rover on the
    /// ground directly below the camera and the heading from where the camera was looking. The
    /// height field is rebuilt from the body (deterministic, so it matches the renderer's terrain).
    /// </summary>
    public void Enter(CelestialBody body)
    {
        _planet = body;
        _terrain = new PlanetTerrain(body);

        double g = Math.Clamp(G * body.MassKg / (body.RadiusMeters * body.RadiusMeters), 0.5, 30.0);
        // Sample the height the RENDERER draws (the LOD/leaf actually on screen), falling back to the
        // raw height field until the mesh exists — so the body rests on the visible surface, not on a
        // finer surface the coarse mesh hasn't resolved yet (which made it sink in / float above).
        _rover = new Rover(body.RadiusMeters, g,
            d => _terrainRenderer.TrySurfaceHeight(d, out double h) ? h : _terrain.HeightAt(d));

        // Direction from the body centre to the camera = the ground point to drop onto.
        Vector3D<double> groundDir = _camera.Position.DeltaMeters(body.CurrentPosition);
        Vector3D<double> heading = ToD(_camera.Forward);
        _rover.Seed(groundDir, heading);

        (_bodyFwd, _bodyUp) = _rover.SurfaceBasis();
        _orbitYaw = 0;
        _orbitPitch = 0;
        Active = true;
    }

    public void Update(double dt)
    {
        double throttle = (_keyboard.IsKeyPressed(Key.W) ? 1.0 : 0.0) - (_keyboard.IsKeyPressed(Key.S) ? 1.0 : 0.0);
        // A steers left, D steers right (the previous mapping was inverted).
        double steer = (_keyboard.IsKeyPressed(Key.A) ? 1.0 : 0.0) - (_keyboard.IsKeyPressed(Key.D) ? 1.0 : 0.0);
        bool brake = _keyboard.IsKeyPressed(Key.Space);

        _rover.Update(dt, throttle, steer, brake);

        // Slew the render basis toward the terrain-aligned target so the body settles onto slopes
        // smoothly instead of snapping vertex-to-vertex.
        (Vector3D<double> tf, Vector3D<double> tu) = _rover.SurfaceBasis();
        double k = Math.Clamp(TurnSmoothing * dt, 0.0, 1.0);
        _bodyUp = Vector3D.Normalize(_bodyUp + (tu - _bodyUp) * k);
        _bodyFwd = Vector3D.Normalize(_bodyFwd + (tf - _bodyFwd) * k);

        UpdateChaseCamera();
    }

    private void UpdateChaseCamera()
    {
        Vector3D<double> up = _rover.RadialUp;
        Vector3D<double> heading = Tangent(_rover.Forward, up);

        // Spherical orbit around the body: yaw about up, pitch raises the camera off the horizon.
        double pitch = Math.Clamp(BasePitch + _orbitPitch, 0.12, 1.45);
        Vector3D<double> back = RotateAround(-heading, up, _orbitYaw);
        Vector3D<double> offsetDir = back * Math.Cos(pitch) + up * Math.Sin(pitch);

        UniversePosition body = BodyPosition;
        UniversePosition camPos = body.Translated(offsetDir * FollowDist);
        UniversePosition target = body.Translated(up * LookUp);

        Vector3D<double> fwdD = target.DeltaMeters(camPos);
        Vector3D<float> camFwd = Vector3D.Normalize(ToF(fwdD));
        Vector3D<float> camUp = ToF(up);

        _camera.Position = camPos;
        _camera.Orientation = LookOrientation(camFwd, camUp);
    }

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        var p = new Vector2D<float>(position.X, position.Y);
        if (_lastMouse is { } last && Active)
        {
            Vector2D<float> d = p - last;
            _orbitYaw += d.X * OrbitSensitivity;   // swing around the rover
            _orbitPitch += -d.Y * OrbitSensitivity; // drag up to look down
        }
        _lastMouse = p;
    }

    // --- math helpers ---

    /// <summary>Orientation whose local -Z looks along <paramref name="forward"/>, +Y near <paramref name="up"/>.</summary>
    private static Quaternion<float> LookOrientation(Vector3D<float> forward, Vector3D<float> up)
    {
        Vector3D<float> f = Vector3D.Normalize(forward);
        Vector3D<float> r = Vector3D.Normalize(Vector3D.Cross(f, up));
        Vector3D<float> u = Vector3D.Cross(r, f);
        // Rows = images of the local X/Y/Z axes (Silk's row-vector convention), matching
        // Camera.ViewMatrix = CreateFromQuaternion(Inverse(Orientation)).
        var m = new Matrix4X4<float>(
            r.X, r.Y, r.Z, 0f,
            u.X, u.Y, u.Z, 0f,
            -f.X, -f.Y, -f.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion<float>.Normalize(Quaternion<float>.CreateFromRotationMatrix(m));
    }

    private static Vector3D<double> Tangent(Vector3D<double> v, Vector3D<double> up)
    {
        Vector3D<double> t = v - up * Vector3D.Dot(v, up);
        return t.LengthSquared > 1e-12 ? Vector3D.Normalize(t) : v;
    }

    private static Vector3D<double> RotateAround(Vector3D<double> v, Vector3D<double> axis, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + Vector3D.Cross(axis, v) * s + axis * (Vector3D.Dot(axis, v) * (1 - c));
    }

    private static Vector3D<double> ToD(Vector3D<float> v) => new(v.X, v.Y, v.Z);
    private static Vector3D<float> ToF(Vector3D<double> v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
