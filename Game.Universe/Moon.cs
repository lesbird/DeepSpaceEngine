using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A moon orbiting a <see cref="Planet"/>. Same circular-orbit model as a planet, but its
/// position is relative to the parent planet rather than the star.
/// </summary>
public sealed class Moon
{
    public ulong Seed;
    public string Designation = ""; // "<star>-<planet>-<moon>"

    public double SemiMajorAxis;   // metres, around the parent planet
    public double Inclination;     // radians
    public double AscendingNode;   // radians
    public double Phase;           // radians at t = 0
    public double MeanMotion;      // radians / second

    public double RadiusMeters;
    public Vector3D<float> Color;

    public UniversePosition CurrentPosition;

    public Vector3D<double> OrbitalOffset(double t)
    {
        double theta = Phase + MeanMotion * t;
        double x = SemiMajorAxis * Math.Cos(theta);
        double z = SemiMajorAxis * Math.Sin(theta);
        double y = z * Math.Sin(Inclination);
        double zi = z * Math.Cos(Inclination);
        double cosN = Math.Cos(AscendingNode), sinN = Math.Sin(AscendingNode);
        return new Vector3D<double>(x * cosN - zi * sinN, y, x * sinN + zi * cosN);
    }
}
