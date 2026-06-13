using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Per-<see cref="PlanetType"/> override layer for the terrain/biome tuning. Each type has a
/// <see cref="Profile"/>; while its <see cref="Profile.Enabled"/> flag is off the type falls
/// back to the global <see cref="TerrainTuning"/> / <see cref="BiomeTuning"/> defaults, so
/// nothing changes until an override is deliberately switched on. This lets e.g. lava worlds
/// carry a jagged red palette while ice worlds stay smooth and pale, all saved together.
///
/// <see cref="PlanetTerrain"/> reads the <c>Effective*</c> accessors (override-or-global) per
/// sample — they only do a flag check and field read, no allocation.
/// </summary>
public static class PlanetTuning
{
    public sealed class Profile
    {
        public bool Enabled;
        public float ReliefScale = 1f;
        public float MountainScale = 1f;
        public float FrequencyScale = 1f;
        public float SnowLine = 0.55f;
        public float CliffThreshold = 0.685f;
        public float CliffStrength = 0.85f;
        public Vector3D<float> LowlandTint = new(1f, 1f, 1f);
        public Vector3D<float> RockColor = new(0.42f, 0.38f, 0.33f);
        public Vector3D<float> SnowColor = new(0.92f, 0.94f, 0.98f);
        public Vector3D<float> CliffColor = new(0.30f, 0.27f, 0.24f);
    }

    private static readonly int Count = Enum.GetValues(typeof(PlanetType)).Length;
    private static readonly Profile[] Profiles = NewSet();

    private static Profile[] NewSet()
    {
        var arr = new Profile[Enum.GetValues(typeof(PlanetType)).Length];
        for (int i = 0; i < arr.Length; i++) arr[i] = new Profile();
        return arr;
    }

    /// <summary>The (always-present) override profile for a type; check <see cref="Profile.Enabled"/>.</summary>
    public static Profile For(PlanetType t) => Profiles[(int)t];

    /// <summary>Disable and reset every override (used when loading a saved config).</summary>
    public static void ResetAll()
    {
        for (int i = 0; i < Count; i++) Profiles[i] = new Profile();
    }

    // Effective values: the type's override when enabled, otherwise the global default.
    public static float EffectiveRelief(PlanetType t)    { var p = Profiles[(int)t]; return p.Enabled ? p.ReliefScale : TerrainTuning.ReliefScale; }
    public static float EffectiveMountain(PlanetType t)  { var p = Profiles[(int)t]; return p.Enabled ? p.MountainScale : TerrainTuning.MountainScale; }
    public static float EffectiveFrequency(PlanetType t) { var p = Profiles[(int)t]; return p.Enabled ? p.FrequencyScale : TerrainTuning.FrequencyScale; }
    public static float EffectiveSnowLine(PlanetType t)  { var p = Profiles[(int)t]; return p.Enabled ? p.SnowLine : BiomeTuning.SnowLine; }
    public static float EffectiveCliffThreshold(PlanetType t) { var p = Profiles[(int)t]; return p.Enabled ? p.CliffThreshold : BiomeTuning.CliffThreshold; }
    public static float EffectiveCliffStrength(PlanetType t)  { var p = Profiles[(int)t]; return p.Enabled ? p.CliffStrength : BiomeTuning.CliffStrength; }
    public static Vector3D<float> EffectiveLowland(PlanetType t) { var p = Profiles[(int)t]; return p.Enabled ? p.LowlandTint : BiomeTuning.LowlandTint; }
    public static Vector3D<float> EffectiveRock(PlanetType t)    { var p = Profiles[(int)t]; return p.Enabled ? p.RockColor : BiomeTuning.RockColor; }
    public static Vector3D<float> EffectiveSnow(PlanetType t)    { var p = Profiles[(int)t]; return p.Enabled ? p.SnowColor : BiomeTuning.SnowColor; }
    public static Vector3D<float> EffectiveCliff(PlanetType t)   { var p = Profiles[(int)t]; return p.Enabled ? p.CliffColor : BiomeTuning.CliffColor; }
}
