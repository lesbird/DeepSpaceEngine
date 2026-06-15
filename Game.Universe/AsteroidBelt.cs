using System.Collections.Generic;
using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A ring of asteroids on circular, slightly-inclined Keplerian orbits around a system's star —
/// generated near the snow line, like the real main belt. Each rock advances with simulation time
/// exactly like a planet (so the belt slowly churns), and <see cref="Collect"/> projects the rocks
/// into camera-relative <see cref="AsteroidInstance"/>s in full double precision (no float jitter on
/// the kilometre-scale bodies). Pure data — holds no GPU resources.
/// </summary>
public sealed class AsteroidBelt
{
    private struct Rock
    {
        public double A;             // semi-major axis, metres
        public double Phase;         // mean anomaly at t = 0
        public double MeanMotion;    // rad/s
        public double Inclination;   // rad
        public double AscendingNode; // rad
        public float Radius;         // metres
        public Vector3D<float> Color;
        public float ShapeSeed;
        public Vector3D<float> SpinAxis;
        public float SpinRate;       // rad per simulation second
        public float SpinPhase;
    }

    private readonly Rock[] _rocks;

    public int Count => _rocks.Length;
    public double InnerRadius { get; }
    public double OuterRadius { get; }

    private AsteroidBelt(Rock[] rocks, double inner, double outer)
    {
        _rocks = rocks;
        InnerRadius = inner;
        OuterRadius = outer;
    }

    /// <summary>
    /// Generate a belt for <paramref name="star"/> from an independent, star-seeded RNG (so the main
    /// planet/moon generation stream is left untouched). ~75% of systems get one. The belt is centred
    /// near the snow line and given a randomized width; rocks are spread across it with a mild
    /// concentration toward the middle. Returns null when the system rolls no belt.
    /// </summary>
    /// <summary>Seed used for a star's belt RNG — shared by <see cref="Generate"/> and <see cref="Rolls"/>.</summary>
    private static ulong BeltSeed(Star star) => Hashing.Combine(star.Id, 0xA57E801DUL);

    /// <summary>Cheap test of whether a star's system would carry a belt, without building the rocks
    /// (the first draw of the belt RNG). Lets the navigator find a belted system to jump to.</summary>
    public static bool Rolls(Star star) => new DeterministicRng(BeltSeed(star)).NextDouble() >= 0.25;

    public static AsteroidBelt? Generate(Star star, double snowLineAu, double starMassKg)
    {
        var rng = new DeterministicRng(BeltSeed(star));
        if (rng.NextDouble() < 0.25) return null; // a quarter of systems have no belt

        double au = MathUtil.AstronomicalUnit;
        double centerAu = Math.Max(0.4, snowLineAu * rng.Range(0.7, 1.15));
        double widthAu = centerAu * rng.Range(0.25, 0.5);
        double innerAu = Math.Max(0.2, centerAu - widthAu * 0.5);
        double outerAu = centerAu + widthAu * 0.5;

        // Many rocks: from afar they overlap along the ring into a visible dust band (most are far
        // too small to resolve individually — like the Milky Way reading as a band of unseen stars).
        int count = rng.RangeInt(6000, 14000);
        var rocks = new Rock[count];
        for (int i = 0; i < count; i++)
        {
            // Bias the semi-major axis toward the belt centre: average two uniforms (triangular).
            double s = (rng.NextDouble() + rng.NextDouble()) * 0.5;
            double aAu = innerAu + s * (outerAu - innerAu);
            double a = aAu * au;
            double n = Math.Sqrt(MathUtil.GravitationalConstant * starMassKg / (a * a * a));

            rocks[i] = new Rock
            {
                A = a,
                Phase = rng.Range(0, 2 * Math.PI),
                MeanMotion = n,
                Inclination = rng.Range(-0.04, 0.04),
                AscendingNode = rng.Range(0, 2 * Math.PI),
                Radius = AsteroidTraits.Radius(ref rng, bigness: 95.0), // up to ~tens of km
                Color = AsteroidTraits.Color(ref rng),
                ShapeSeed = (float)rng.Range(0.0, 100.0),
                SpinAxis = AsteroidTraits.SpinAxis(ref rng),
                SpinRate = (float)rng.Range(-3e-6, 3e-6),
                SpinPhase = (float)rng.Range(0, 2 * Math.PI),
            };
        }
        return new AsteroidBelt(rocks, innerAu * au, outerAu * au);
    }

    /// <summary>
    /// Append every rock within <paramref name="maxDist"/> metres of the camera to
    /// <paramref name="output"/> as a camera-relative instance, at simulation time <paramref name="t"/>.
    /// </summary>
    public void Collect(double t, in UniversePosition star, in UniversePosition camera,
        double maxDist, List<AsteroidInstance> output)
    {
        double maxSq = maxDist * maxDist;
        for (int i = 0; i < _rocks.Length; i++)
        {
            ref Rock r = ref _rocks[i];
            UniversePosition pos = star.Translated(Offset(in r, t));
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

    /// <summary>Circular orbit offset from the star, inclined then rotated by the node (matches
    /// <see cref="CelestialBody.OrbitalOffset"/>).</summary>
    private static Vector3D<double> Offset(in Rock r, double t)
    {
        double theta = r.Phase + r.MeanMotion * t;
        double x = r.A * Math.Cos(theta);
        double z = r.A * Math.Sin(theta);
        double y = z * Math.Sin(r.Inclination);
        double zi = z * Math.Cos(r.Inclination);
        double cosN = Math.Cos(r.AscendingNode), sinN = Math.Sin(r.AscendingNode);
        return new Vector3D<double>(x * cosN - zi * sinN, y, x * sinN + zi * cosN);
    }
}
