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

    /// <summary>Scales impact-crater depth/strength on worlds that have craters; 0 removes them.</summary>
    public static float CraterScale = 1.0f;

    /// <summary>Scales how many craters appear (the fraction of cells that bear one); 0 = none.</summary>
    public static float CraterDensity = 1.0f;

    /// <summary>Scales the fine, LOD-gated micro-relief height layer that only appears up close;
    /// 0 removes it. Changing it requires a terrain rebuild (it's geometry).</summary>
    public static float MicroDetailScale = 1.0f;

    /// <summary>Strength of the fragment-shader detail-normal bump (perceived close-up roughness);
    /// 0 = off. Read live by the terrain shader — no rebuild needed.</summary>
    public static float DetailNormalStrength = 0.4f;

    /// <summary>Scales the detail-normal frequency; &gt;1 = finer bumps. Live (no rebuild).</summary>
    public static float DetailNormalScale = 1.0f;

    /// <summary>How much the detail noise breaks up the surface albedo (0 = none). Live (no rebuild).</summary>
    public static float DetailAlbedo = 0.12f;
}
