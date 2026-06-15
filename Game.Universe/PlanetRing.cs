using System.Collections.Generic;
using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A swarm of asteroid particles orbiting a planet inside its ring annulus, lying in the planet's
/// tilted ring plane. The chunky counterpart to the smooth banded annulus drawn by the system
/// renderer: from a distance the particles blur into that band, but up close the hybrid-LOD asteroid
/// renderer resolves them into individual sun-lit rocks. Pure data; <see cref="Collect"/> projects
/// the rocks to camera-relative instances in double precision (no float jitter at planet scale).
/// </summary>
public sealed class PlanetRing
{
    private struct Rock
    {
        public double A;            // orbital radius from the planet centre, metres
        public double Phase;        // mean anomaly at t = 0
        public double MeanMotion;   // rad/s
        public double Height;       // vertical offset within the ring plane (sheet thickness), metres
        public float Radius;        // metres
        public Vector3D<float> Color;
        public float ShapeSeed;
        public Vector3D<float> SpinAxis;
        public float SpinRate;      // rad per simulation second
        public float SpinPhase;
    }

    private readonly Rock[] _rocks;
    // Ring-plane-local (x, y, z) -> planet-relative orientation. Built from the SAME matrix product the
    // smooth annulus uses (tilt about X, then node azimuth about Y), in double, so the rocks lie
    // exactly in the drawn band.
    private readonly Matrix4X4<double> _toPlane;

    public int Count => _rocks.Length;

    private PlanetRing(Rock[] rocks, Matrix4X4<double> toPlane)
    {
        _rocks = rocks;
        _toPlane = toPlane;
    }

    /// <summary>Populate a ringed planet's annulus with particles, deterministic in the planet seed
    /// (independent RNG, so the main generation stream is untouched).</summary>
    public static PlanetRing Generate(Planet p)
    {
        var rng = new DeterministicRng(Hashing.Combine(p.Seed, 0x71467135UL));
        double inner = p.RingInnerRadius, outer = p.RingOuterRadius;

        Matrix4X4<double> toPlane =
            Matrix4X4.CreateRotationX((double)p.RingTilt) * Matrix4X4.CreateRotationY((double)p.RingTiltAzimuth);

        int count = rng.RangeInt(2500, 6500);
        var rocks = new Rock[count];
        for (int i = 0; i < count; i++)
        {
            double s = (rng.NextDouble() + rng.NextDouble()) * 0.5; // mild concentration to mid-ring
            double a = inner + s * (outer - inner);
            double n = Math.Sqrt(MathUtil.GravitationalConstant * p.MassKg / (a * a * a));
            rocks[i] = new Rock
            {
                A = a,
                Phase = rng.Range(0, 2 * Math.PI),
                MeanMotion = n,
                Height = a * rng.Range(-0.004, 0.004), // a thin sheet
                Radius = AsteroidTraits.Radius(ref rng, bigness: 2.0), // small ring particles
                Color = Tint(ref rng, p.RingColor),
                ShapeSeed = (float)rng.Range(0.0, 100.0),
                SpinAxis = AsteroidTraits.SpinAxis(ref rng),
                SpinRate = (float)rng.Range(-5e-6, 5e-6),
                SpinPhase = (float)rng.Range(0, 2 * Math.PI),
            };
        }
        return new PlanetRing(rocks, toPlane);
    }

    /// <summary>Shade the ring's overall tint per rock so particles read as the same icy/dusty material.</summary>
    private static Vector3D<float> Tint(ref DeterministicRng rng, Vector3D<float> ringColor)
    {
        float shade = (float)rng.Range(0.6, 1.15);
        return new Vector3D<float>(
            Math.Min(1f, ringColor.X * shade),
            Math.Min(1f, ringColor.Y * shade),
            Math.Min(1f, ringColor.Z * shade));
    }

    /// <summary>Append every particle within <paramref name="maxDist"/> of the camera as a
    /// camera-relative instance, orbiting <paramref name="planet"/> at simulation time <paramref name="t"/>.</summary>
    public void Collect(double t, in UniversePosition planet, in UniversePosition camera,
        double maxDist, List<AsteroidInstance> output)
    {
        double maxSq = maxDist * maxDist;
        for (int i = 0; i < _rocks.Length; i++)
        {
            ref Rock r = ref _rocks[i];
            double theta = r.Phase + r.MeanMotion * t;
            var local = new Vector3D<double>(r.A * Math.Cos(theta), r.Height, r.A * Math.Sin(theta));
            Vector3D<double> offset = Vector3D.Transform(local, _toPlane);
            UniversePosition pos = planet.Translated(offset);
            if (pos.DistanceSquaredTo(camera) > maxSq) continue;

            output.Add(new AsteroidInstance
            {
                RelPos = pos.ToCameraRelative(camera),
                Radius = r.Radius,
                Color = r.Color,
                ShapeSeed = r.ShapeSeed,
                SpinAxis = r.SpinAxis,
                SpinAngle = r.SpinPhase + r.SpinRate * (float)t,
            });
        }
    }
}
