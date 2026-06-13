using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// An arcade-grounded surface vehicle bound to one planet. The whole simulation runs in
/// <b>planet-centred coordinates</b> — <see cref="LocalPos"/> is a metre offset from the planet
/// centre — so it stays in plain doubles (planet radii are ~1e6–1e7 m, giving sub-micron
/// resolution at the surface) and rides the planet's orbital frame for free: the owner just
/// recomputes the absolute position as <c>planet.CurrentPosition.Translated(LocalPos)</c> each
/// frame from the planet's already-advanced position.
///
/// It is deliberately not a full rigid body (no per-wheel suspension — that's a later milestone):
/// gravity pulls toward the centre, the body snaps to <see cref="RideHeight"/> above the terrain,
/// throttle/steer drive it across the tangent plane, and it falls off ledges and lands. The terrain
/// is injected as a height function so the sim is pure and unit-testable with no GL or input.
/// </summary>
public sealed class Rover
{
    // Tuning (SI: metres, seconds, radians).
    public const double RideHeight = 0.75;  // body centre above the ground its wheels rest on
    public const double DriveAccel = 9.0;   // throttle acceleration, m/s²
    public const double MaxSpeed = 26.0;    // tangential top speed (~94 km/h)
    public const double SteerRate = 1.1;    // heading turn rate at full lock, rad/s
    private const double RollingDrag = 0.8; // tangential velocity decay/s when coasting
    private const double BrakeDrag = 3.4;   // stronger decay/s when braking

    // Wheel footprint half-extents (must match RoverRenderer's wheel placement). The body rests
    // on the terrain sampled under these four corners rather than a single centre point, so it sits
    // on slopes and bumps without a wheel poking through the drawn mesh.
    private const double HalfWidth = 1.05;
    private const double HalfLength = 1.05;

    private readonly double _radius;
    private readonly double _g;
    private readonly Func<Vector3D<double>, double> _heightAt;

    /// <summary>Position as a metre offset from the planet centre.</summary>
    public Vector3D<double> LocalPos;
    /// <summary>Velocity in the planet frame, m/s.</summary>
    public Vector3D<double> Velocity;

    private Vector3D<double> _forward; // tangent heading (unit)

    /// <summary>True when the body is resting on (or pressed into) the terrain this step.</summary>
    public bool Grounded { get; private set; }

    /// <param name="radius">Planet base radius (m).</param>
    /// <param name="surfaceGravity">Downward acceleration at the surface (m/s²).</param>
    /// <param name="heightAt">Signed terrain height (m) at a unit direction.</param>
    public Rover(double radius, double surfaceGravity, Func<Vector3D<double>, double> heightAt)
    {
        _radius = radius;
        _g = surfaceGravity;
        _heightAt = heightAt;
    }

    /// <summary>Tangent heading direction (unit, planet frame).</summary>
    public Vector3D<double> Forward => _forward;

    /// <summary>Radial up (from planet centre through the body).</summary>
    public Vector3D<double> RadialUp => Normalize(LocalPos);

    /// <summary>Speed across the ground (tangential component), m/s.</summary>
    public double SpeedMps
    {
        get
        {
            Vector3D<double> up = RadialUp;
            return (Velocity - up * Vector3D.Dot(Velocity, up)).Length;
        }
    }

    /// <summary>
    /// Place the rover on the ground below <paramref name="groundDir"/>, heading toward
    /// <paramref name="headingHint"/> projected onto the tangent plane. Called when driving begins.
    /// </summary>
    public void Seed(Vector3D<double> groundDir, Vector3D<double> headingHint)
    {
        Vector3D<double> up = Normalize(groundDir);
        LocalPos = up * (_radius + _heightAt(up) + RideHeight);
        Velocity = Vector3D<double>.Zero;
        Vector3D<double> f = headingHint - up * Vector3D.Dot(headingHint, up);
        _forward = f.LengthSquared > 1e-12 ? Normalize(f) : AnyTangent(up);
        Grounded = true;
    }

    /// <summary>
    /// Advance one step. <paramref name="throttle"/> and <paramref name="steer"/> are in [-1, 1];
    /// <paramref name="brake"/> applies a stronger drag. Driving forces only apply while grounded —
    /// in the air the body is purely ballistic.
    /// </summary>
    public void Update(double dt, double throttle, double steer, bool brake)
    {
        if (dt <= 0) return;
        Vector3D<double> up = RadialUp;

        // Gravity always pulls toward the centre.
        Velocity -= up * (_g * dt);

        if (Grounded)
        {
            // Keep the heading on the tangent plane, then steer it around the up axis.
            _forward = TangentNormalize(_forward, up);
            if (steer != 0.0)
                _forward = Normalize(RotateAround(_forward, up, steer * SteerRate * dt));

            if (throttle != 0.0)
                Velocity += _forward * (throttle * DriveAccel * dt);

            // Drag acts on the tangential (ground) velocity only; radial is gravity/contact.
            Vector3D<double> vRad = up * Vector3D.Dot(Velocity, up);
            Vector3D<double> vTan = Velocity - vRad;
            vTan *= Math.Exp(-(brake ? BrakeDrag : RollingDrag) * dt);
            double s = vTan.Length;
            if (s > MaxSpeed) vTan *= MaxSpeed / s;
            Velocity = vRad + vTan;
        }

        // Integrate, then resolve against the ground under the wheel footprint.
        LocalPos += Velocity * dt;

        up = RadialUp;
        double r = LocalPos.Length;
        double targetR = Footprint(up).groundR + RideHeight;
        if (r <= targetR)
        {
            LocalPos = up * targetR;             // snap to ride height above terrain
            double vr = Vector3D.Dot(Velocity, up);
            if (vr < 0.0) Velocity -= up * vr;   // kill only inward velocity (land), keep tangential
            Grounded = true;
        }
        else
        {
            Grounded = false;                    // drove off a ledge — keep falling
        }
    }

    /// <summary>
    /// Body orientation for rendering: <c>up</c> is the normal of the plane through the four wheel
    /// contacts (so the chassis tilts to match the slope it sits on) and <c>forward</c> is the
    /// heading re-projected onto that tilted plane.
    /// </summary>
    public (Vector3D<double> forward, Vector3D<double> up) SurfaceBasis()
    {
        Vector3D<double> nUp = Footprint(RadialUp).normal;
        return (TangentNormalize(_forward, nUp), nUp);
    }

    /// <summary>
    /// Sample the terrain under the four wheel corners. Returns the radius the body should rest its
    /// wheels on (the highest contact, so no corner clips through the drawn mesh) and the normal of
    /// the plane through the contacts (for slope tilt).
    /// </summary>
    private (double groundR, Vector3D<double> normal) Footprint(Vector3D<double> radial)
    {
        Vector3D<double> fwd = TangentNormalize(_forward, radial);
        Vector3D<double> right = Normalize(Vector3D.Cross(fwd, radial));

        Vector3D<double> dFR = WheelDir(radial, right, fwd, HalfWidth, HalfLength);
        Vector3D<double> dFL = WheelDir(radial, right, fwd, -HalfWidth, HalfLength);
        Vector3D<double> dBR = WheelDir(radial, right, fwd, HalfWidth, -HalfLength);
        Vector3D<double> dBL = WheelDir(radial, right, fwd, -HalfWidth, -HalfLength);

        double hFR = _heightAt(dFR), hFL = _heightAt(dFL), hBR = _heightAt(dBR), hBL = _heightAt(dBL);
        double hMax = Math.Max(Math.Max(hFR, hFL), Math.Max(hBR, hBL));
        double hAvg = 0.25 * (hFR + hFL + hBR + hBL);
        // Lean toward the highest contact so a wheel never sinks below the mesh, but keep some of
        // the average so the body doesn't perch on every tiny pebble.
        double groundH = 0.35 * hAvg + 0.65 * hMax;

        Vector3D<double> pFR = dFR * (_radius + hFR), pFL = dFL * (_radius + hFL);
        Vector3D<double> pBR = dBR * (_radius + hBR), pBL = dBL * (_radius + hBL);
        Vector3D<double> n = Vector3D.Cross(pFR - pBL, pFL - pBR); // diagonals of the footprint
        if (Vector3D.Dot(n, radial) < 0) n = -n;
        Vector3D<double> normal = n.LengthSquared > 1e-12 ? Normalize(n) : radial;
        return (_radius + groundH, normal);
    }

    /// <summary>Unit direction to a wheel contact at local offset (<paramref name="ox"/>, <paramref name="oz"/>) metres.</summary>
    private Vector3D<double> WheelDir(Vector3D<double> radial, Vector3D<double> right, Vector3D<double> fwd,
        double ox, double oz)
        => Normalize(radial + (right * ox + fwd * oz) * (1.0 / _radius));

    // --- vector helpers ---

    private static Vector3D<double> Normalize(Vector3D<double> v) => Vector3D.Normalize(v);

    /// <summary>Component of <paramref name="v"/> in the plane perpendicular to <paramref name="up"/>, normalized.</summary>
    private static Vector3D<double> TangentNormalize(Vector3D<double> v, Vector3D<double> up)
    {
        Vector3D<double> t = v - up * Vector3D.Dot(v, up);
        return t.LengthSquared > 1e-12 ? Normalize(t) : AnyTangent(up);
    }

    /// <summary>Some unit vector perpendicular to <paramref name="up"/>.</summary>
    private static Vector3D<double> AnyTangent(Vector3D<double> up)
    {
        Vector3D<double> a = Math.Abs(up.Y) < 0.9 ? Vector3D<double>.UnitY : Vector3D<double>.UnitX;
        return Normalize(Vector3D.Cross(a, up));
    }

    /// <summary>Rotate <paramref name="v"/> about the unit <paramref name="axis"/> by <paramref name="angle"/> (Rodrigues).</summary>
    private static Vector3D<double> RotateAround(Vector3D<double> v, Vector3D<double> axis, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + Vector3D.Cross(axis, v) * s + axis * (Vector3D.Dot(axis, v) * (1 - c));
    }
}
