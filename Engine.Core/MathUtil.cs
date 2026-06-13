namespace Engine.Core;

public static class MathUtil
{
    /// <summary>Light-year in metres.</summary>
    public const double LightYear = 9.4607e15;

    /// <summary>Speed of light in metres per second.</summary>
    public const double SpeedOfLight = 299_792_458.0;

    /// <summary>Astronomical unit in metres (equals one sector edge).</summary>
    public const double AstronomicalUnit = UniversePosition.SectorSize;

    /// <summary>Newtonian gravitational constant (m^3 kg^-1 s^-2).</summary>
    public const double GravitationalConstant = 6.674e-11;

    /// <summary>Solar mass in kilograms.</summary>
    public const double SolarMassKg = 1.989e30;

    /// <summary>Solar radius in metres.</summary>
    public const double SolarRadiusM = 6.957e8;

    /// <summary>Earth radius in metres.</summary>
    public const double EarthRadiusM = 6.371e6;

    /// <summary>Floor division for longs (rounds toward negative infinity, unlike '/').</summary>
    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
