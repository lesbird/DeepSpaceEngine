using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Describes the large-scale stellar density of the galaxy as a function of position.
/// The disk lies in the XZ plane; Y is vertical thickness. Density falls off
/// exponentially with galactic radius and height, with a mild spiral-arm modulation.
///
/// At the ~tens-of-light-years scale of the live render bubble this is effectively
/// constant; the structure becomes visible only across thousands of light-years (and
/// will drive a distant-star backdrop in a later milestone). It is plumbed in now so
/// star counts are physically motivated and the universe has real shape.
/// </summary>
public sealed class GalaxyModel
{
    public ulong WorldSeed { get; }

    // Tunables (light-years).
    public double BaseDensity = 0.006;   // stars per ly^3 near the solar neighborhood
    public double ScaleLength = 3500.0;  // radial exponential scale
    public double ScaleHeight = 320.0;   // vertical exponential scale
    public int ArmCount = 2;
    public double ArmStrength = 0.4;
    public double ArmWind = 0.25;

    public GalaxyModel(ulong worldSeed) => WorldSeed = worldSeed;

    /// <summary>Stars per cubic light-year at the given absolute position (meters from origin).</summary>
    public double DensityAt(Vector3D<double> meters)
    {
        double x = meters.X / MathUtil.LightYear;
        double y = meters.Y / MathUtil.LightYear;
        double z = meters.Z / MathUtil.LightYear;

        double r = Math.Sqrt(x * x + z * z);
        double disk = Math.Exp(-r / ScaleLength) * Math.Exp(-Math.Abs(y) / ScaleHeight);

        // Spiral arm modulation (logarithmic spiral); ~1 near the origin where r→0.
        double theta = Math.Atan2(z, x);
        double arm = 1.0 + ArmStrength * Math.Cos(ArmCount * theta - ArmWind * Math.Log(r + 1.0));

        return BaseDensity * disk * Math.Max(arm, 0.1);
    }
}
