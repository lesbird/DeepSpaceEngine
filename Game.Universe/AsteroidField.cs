using System.Collections.Generic;
using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A free-floating deep-space asteroid cluster: a flattened cloud of rocks at fixed offsets from a
/// <see cref="Center"/> anywhere in interstellar space (no central mass, so they don't orbit — they
/// just tumble slowly). Streamed into existence near the camera by <see cref="AsteroidFieldManager"/>.
/// Pure data; <see cref="Collect"/> projects rocks to camera-relative instances in double precision.
/// </summary>
public sealed class AsteroidField
{
    private struct Rock
    {
        public Vector3D<double> Offset; // metres from the cluster centre
        public float Radius;            // metres
        public Vector3D<float> Color;
        public float ShapeSeed;
        public Vector3D<float> SpinAxis;
        public float SpinRate;          // rad per simulation second
        public float SpinPhase;
    }

    private readonly Rock[] _rocks;

    /// <summary>Cluster centre in the universe.</summary>
    public UniversePosition Center { get; }
    /// <summary>Bounding radius (metres) — nothing extends past this from the centre.</summary>
    public double BoundRadius { get; }
    public int Count => _rocks.Length;

    private AsteroidField(UniversePosition center, double boundRadius, Rock[] rocks)
    {
        Center = center;
        BoundRadius = boundRadius;
        _rocks = rocks;
    }

    /// <summary>Build a cluster centred at <paramref name="center"/>, deterministic in <paramref name="seed"/>.</summary>
    public static AsteroidField Generate(UniversePosition center, ulong seed)
    {
        var rng = new DeterministicRng(Hashing.Combine(seed, 0xF1E1D00DUL));

        // A LOCAL dense pocket (thousands of km across), not a light-year-scale void: this is the
        // fly-through "asteroid field" — rocks close enough together to actually see around you.
        double radius = rng.Range(5.0e6, 1.8e7);   // metres (~5,000–18,000 km)
        double flatten = rng.Range(0.25, 0.6);     // squash on Y so clusters read as drifting sheets
        int count = rng.RangeInt(1800, 4800);

        var rocks = new Rock[count];
        for (int i = 0; i < count; i++)
        {
            // Uniform-in-volume sample (cube-root radius), then squash vertically.
            Vector3D<float> dir = AsteroidTraits.SpinAxis(ref rng); // reuse: a random unit vector
            double rr = radius * Math.Pow(rng.NextDouble(), 1.0 / 3.0);
            var offset = new Vector3D<double>(dir.X * rr, dir.Y * rr * flatten, dir.Z * rr);

            rocks[i] = new Rock
            {
                Offset = offset,
                Radius = AsteroidTraits.Radius(ref rng, bigness: 3.5), // ~80 m – 4 km, sized for fly-through
                Color = AsteroidTraits.Color(ref rng),
                ShapeSeed = (float)rng.Range(0.0, 100.0),
                SpinAxis = AsteroidTraits.SpinAxis(ref rng),
                SpinRate = (float)rng.Range(-3e-6, 3e-6),
                SpinPhase = (float)rng.Range(0, 2 * Math.PI),
            };
        }
        return new AsteroidField(center, radius, rocks);
    }

    /// <summary>Append rocks within <paramref name="maxDist"/> of the camera as camera-relative instances.</summary>
    public void Collect(double t, in UniversePosition camera, double maxDist, List<AsteroidInstance> output)
    {
        // Whole-cluster reject: if even the nearest possible rock is beyond the cull range, skip.
        double centerDist = camera.DistanceTo(Center);
        if (centerDist - BoundRadius > maxDist) return;

        double maxSq = maxDist * maxDist;
        for (int i = 0; i < _rocks.Length; i++)
        {
            ref Rock r = ref _rocks[i];
            UniversePosition pos = Center.Translated(r.Offset);
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
