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
    /// <summary>
    /// Selects the <b>GPU tile-generation</b> terrain path (procedural height/normal/albedo baked into
    /// texture tiles on the GPU and displaced via vertex texture fetch). <b>On by default</b> — it carries
    /// continents, mountains, eroded detail, craters/maria, water and the matching orbital relief. The CPU
    /// worker-pool bake remains as a fallback (uncheck the HUD toggle to A/B-compare). Toggling rebuilds the
    /// active terrain.
    /// </summary>
    public static bool GpuTerrain = true;

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

    /// <summary>Airless-body albedo from the crater cascade: dark floors + bright rims/ejecta; 0 = off.
    /// Baked into the surface colour, so a change needs a terrain rebuild.</summary>
    public static float CraterAlbedo = 1.0f;

    /// <summary>Strength of low-frequency maria provinces (darker basaltic plains) on airless worlds;
    /// 0 = off. Baked into the surface colour, so a change needs a terrain rebuild.</summary>
    public static float MariaStrength = 0.6f;

    /// <summary>Emissive brightness of glowing lava (fissures + volcano vents) on lava worlds; 0 = cold
    /// crust. Pushed above 1 so the bloom pass haloes it. Live (per-pixel on the GPU path; no rebuild).</summary>
    public static float LavaGlow = 2.5f;

    /// <summary>Brightness of night-side city lights on inhabited (life-bearing) worlds; 0 = uninhabited
    /// dark. Clustered on temperate coastal lowlands, visible from orbit, blooming. Live (no rebuild).</summary>
    public static float CityGlow = 1.5f;

    /// <summary>Scales the fine, LOD-gated micro-relief height layer that only appears up close;
    /// 0 removes it. Changing it requires a terrain rebuild (it's geometry).</summary>
    public static float MicroDetailScale = 1.0f;

    /// <summary>Strength of the fragment-shader detail-normal bump (perceived close-up roughness);
    /// 0 = off. Read live by the terrain shader — no rebuild needed.</summary>
    public static float DetailNormalStrength = 0.4f;

    /// <summary>Scales the detail-normal frequency; &gt;1 = finer bumps. Live (no rebuild).</summary>
    public static float DetailNormalScale = 1.0f;

    /// <summary>How far from the camera the surface detail stays visible before smoothing out:
    /// higher keeps coarser octaves on so the ground reads as textured toward the horizon instead
    /// of going flat in the mid-distance. 1 = close-up only. Live (no rebuild).</summary>
    public static float SurfaceDetailRange = 4.0f;

    /// <summary>How aggressively the terrain quadtree subdivides on approach: a patch splits once the
    /// camera is closer than this multiple of the patch's size. Higher = finer geometry (so the real,
    /// baked craters/relief resolve) shows from a higher altitude, closing the mid-altitude detail gap
    /// — at the cost of more patches/triangles and more background baking. Live (no rebuild): it only
    /// changes the per-frame split/merge decision. 2.5 was the old fixed value.</summary>
    public static float LodDistanceFactor = 4.0f;

    /// <summary>How much the detail noise breaks up the surface albedo (0 = none). Live (no rebuild).</summary>
    public static float DetailAlbedo = 0.12f;

    /// <summary>Procedural material breakup up close: dark cracks/cavity on rock + a faint mineral
    /// tint, so the ground reads as material rather than flat paint; 0 = off. Live (no rebuild).</summary>
    public static float MaterialDetail = 0.5f;

    /// <summary>Close-up specular highlight strength — makes the detail normals catch the sun;
    /// 0 = matte. Live (no rebuild).</summary>
    public static float SurfaceSpecular = 0.2f;

    /// <summary>Hemispheric ambient fill so shadowed slopes keep their form instead of going black;
    /// the old flat term was 0.06. Live (no rebuild).</summary>
    public static float SurfaceAmbient = 0.10f;

    // --- Orbital macro-relief (the fragment-shader layer that makes mountain ranges read from space) ---
    // Geometry is correctly band-limited to a smooth silhouette at orbital distance, so this relief
    // lives entirely in the lighting normal and albedo. On the GPU tile path it evaluates the SAME warped
    // ridged-mountain field the tiles bake, over just the octaves between the coarse mesh's vertex spacing
    // and the pixel footprint — so the mountains seen from orbit are the real ones and the band fades to
    // zero as the mesh resolves them (seamless handoff, no separate noise field). All three are read live
    // by the terrain shader — no rebuild needed. (The CPU path still uses its own approximate noise field.)

    /// <summary>Strength of the orbital macro-relief normal shading (mountain ranges catching the sun
    /// from space); 0 = off.</summary>
    public static float OrbitalReliefStrength = 1.0f;

    /// <summary>How much the macro relief darkens valleys / lightens ridges in albedo (0 = none).</summary>
    public static float OrbitalReliefAlbedo = 0.2f;

    /// <summary>GPU path: scales the pixel-footprint octave reach (&gt;1 reveals the relief a touch finer/
    /// earlier on approach; the match still holds because it only widens the band, capped at the mountain
    /// octave budget). CPU path: scales the approximate relief's base frequency. Live (no rebuild).</summary>
    public static float OrbitalReliefScale = 1.0f;
}
