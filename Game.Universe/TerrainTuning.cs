namespace Game.Universe;

/// <summary>
/// Global live-tuning multipliers applied on top of each planet's deterministic terrain
/// parameters (see <see cref="PlanetTerrain"/>). Driven by the HUD sliders. Defaults of 1.0
/// reproduce the per-planet seeded values exactly, so generation stays deterministic until
/// the user deliberately dials a knob. Changing any of these requires the terrain meshes to
/// be regenerated (Program watches for changes and calls the renderer's rebuild).
///
/// These are intentionally multipliers, not absolutes, so the per-type / per-seed variety
/// between worlds is preserved while the overall look is nudged.
/// </summary>
public static class TerrainTuning
{
    /// <summary>Scales overall relief height (and <see cref="PlanetTerrain.Amplitude"/> with it).</summary>
    public static float ReliefScale = 1.0f;

    /// <summary>Biases the continent↔mountain blend; &gt;1 = more ridged mountains.</summary>
    public static float MountainScale = 1.0f;

    /// <summary>Scales feature frequency; &gt;1 = smaller, busier features.</summary>
    public static float FrequencyScale = 1.0f;
}
