using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Approximate blackbody (star) colour from temperature in Kelvin. Based on the
/// well-known Tanner Helland approximation, returning a normalized linear-ish RGB.
/// </summary>
public static class Blackbody
{
    public static Vector3D<float> ColorOf(float kelvin)
    {
        double temp = Math.Clamp(kelvin, 1000f, 40000f) / 100.0;
        double r, g, b;

        // Red
        if (temp <= 66) r = 255;
        else r = 329.698727446 * Math.Pow(temp - 60, -0.1332047592);

        // Green
        if (temp <= 66) g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
        else g = 288.1221695283 * Math.Pow(temp - 60, -0.0755148492);

        // Blue
        if (temp >= 66) b = 255;
        else if (temp <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;

        return new Vector3D<float>(
            (float)(Math.Clamp(r, 0, 255) / 255.0),
            (float)(Math.Clamp(g, 0, 255) / 255.0),
            (float)(Math.Clamp(b, 0, 255) / 255.0));
    }
}
