namespace Game.Universe;

/// <summary>
/// Live-tuning parameters for the procedurally-placed <see cref="NebulaField"/> (the fly-to gas
/// clouds, distinct from the painted backdrop nebulosity). Unlike the terrain/biome knobs these
/// are <b>generation</b> inputs: changing any of them requires the field to be rebuilt (Program
/// constructs a fresh <see cref="NebulaField"/> from the world seed). Held here so they can be
/// driven by HUD sliders and persisted to <c>tuning.json</c> alongside the rest of the look.
/// </summary>
public static class NebulaTuning
{
    /// <summary>How many nebulae are scattered through the disk. Few and large reads better than
    /// many and small.</summary>
    public static int Count = 40;

    /// <summary>Smallest visual radius, light-years.</summary>
    public static float MinRadiusLy = 120f;

    /// <summary>Largest visual radius, light-years.</summary>
    public static float MaxRadiusLy = 400f;
}
