using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Global live-tuning knobs for surface colouring, read by <see cref="PlanetTerrain.ColorAt"/>.
/// Defaults reproduce the original hardcoded ramp. Because vertex colours are baked into each
/// patch mesh at generation time, changing any of these requires a terrain rebuild (Program
/// watches for changes and triggers it), exactly like the relief knobs in <see cref="TerrainTuning"/>.
/// </summary>
public static class BiomeTuning
{
    /// <summary>Elevation (0..1) where the rock band gives way to snow caps.</summary>
    public static float SnowLine = 0.55f;

    /// <summary>Slope midpoint (1 = flat, 0 = vertical) at which faces become bare cliff rock.</summary>
    public static float CliffThreshold = 0.685f;

    /// <summary>How strongly cliffs override the elevation colour (0 = off, 1 = full rock).</summary>
    public static float CliffStrength = 0.85f;

    /// <summary>Multiplies each planet's seeded lowland colour (white = unchanged).</summary>
    public static Vector3D<float> LowlandTint = new(1f, 1f, 1f);

    public static Vector3D<float> RockColor = new(0.42f, 0.38f, 0.33f);
    public static Vector3D<float> SnowColor = new(0.92f, 0.94f, 0.98f);
    public static Vector3D<float> CliffColor = new(0.30f, 0.27f, 0.24f);
}
