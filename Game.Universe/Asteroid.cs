using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// One asteroid ready for rendering, expressed in <b>camera-relative</b> metres (the floating-origin
/// step already applied) plus the data the hybrid-LOD renderer needs to draw it either as a lumpy
/// lit 3D rock (near) or a soft point sprite (far). Produced each frame by <see cref="AsteroidBelt"/>
/// and <see cref="AsteroidField"/>; consumed by the renderer in <c>Game.App</c>.
/// </summary>
public struct AsteroidInstance
{
    public Vector3D<float> RelPos;   // camera-relative position, metres
    public float Radius;             // metres
    public Vector3D<float> Color;
    public float ShapeSeed;          // drives the vertex-shader noise displacement (unique shape)
    public Vector3D<float> SpinAxis; // unit tumble axis
    public float SpinAngle;          // current tumble angle, radians
}

/// <summary>
/// Shared deterministic helpers for giving a single asteroid its physical look — size drawn from a
/// steep power law (countless small, a rare few large), a dusty grey/brown tint, and a random tumble
/// axis + rate. Used by both the in-system belt and the deep-space clusters so rocks look consistent
/// wherever they appear.
/// </summary>
internal static class AsteroidTraits
{
    /// <summary>Radius in metres from a steep power law: most rocks are sub-kilometre, a rare few
    /// reach tens of km. <paramref name="bigness"/> scales the heavy tail (belts get the largest).</summary>
    public static float Radius(ref DeterministicRng rng, double bigness)
    {
        double km = 0.4 + bigness * Math.Pow(rng.NextDouble(), 5.0);
        return (float)(km * 1000.0);
    }

    /// <summary>A dusty rock colour: mostly carbonaceous grey, some reddish-brown, the odd pale icy one.</summary>
    public static Vector3D<float> Color(ref DeterministicRng rng)
    {
        double u = rng.NextDouble();
        Vector3D<float> baseCol = u < 0.6
            ? new Vector3D<float>(0.34f, 0.31f, 0.28f)   // carbonaceous grey-brown
            : u < 0.9
                ? new Vector3D<float>(0.46f, 0.34f, 0.25f) // rusty/silicate
                : new Vector3D<float>(0.55f, 0.55f, 0.58f); // pale metallic/icy
        float shade = (float)rng.Range(0.7, 1.15);
        return new Vector3D<float>(
            Math.Min(1f, baseCol.X * shade),
            Math.Min(1f, baseCol.Y * shade),
            Math.Min(1f, baseCol.Z * shade));
    }

    /// <summary>A random unit tumble axis.</summary>
    public static Vector3D<float> SpinAxis(ref DeterministicRng rng)
    {
        double z = rng.Range(-1.0, 1.0);
        double t = rng.Range(0.0, 2.0 * Math.PI);
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
        return new Vector3D<float>((float)(r * Math.Cos(t)), (float)(r * Math.Sin(t)), (float)z);
    }
}
