using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Game.Universe;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>
/// Serializable snapshot of every live-tuning value (atmosphere + terrain relief + biome
/// colours). Lets the user save the look they've dialled in to <c>tuning.json</c> and have it
/// auto-load on the next launch, so the sliders effectively become the new defaults.
/// </summary>
public sealed class TuningConfig
{
    // Galaxy backdrop.
    public bool RenderBackdrop { get; set; } = true;
    public float BandBrightness { get; set; } = 0.6f;
    public float BackdropStarBrightness { get; set; } = 1.0f;

    // Atmosphere.
    public bool RenderAtmosphere { get; set; } = true;
    public float SunIntensity { get; set; } = 20f;
    public float Exposure { get; set; } = 1.2f;
    public float RayleighStrength { get; set; } = 0.45f;
    public float MieStrength { get; set; } = 0.16f;
    public float MieG { get; set; } = 0.76f;
    public float ShellHeight { get; set; } = 1.0f;

    // Terrain relief.
    public float ReliefScale { get; set; } = 1.0f;
    public float MountainScale { get; set; } = 1.0f;
    public float FrequencyScale { get; set; } = 1.0f;
    public float CraterScale { get; set; } = 1.0f;
    public float CraterDensity { get; set; } = 1.0f;

    // Biome / colour.
    public float SnowLine { get; set; } = 0.55f;
    public float CliffThreshold { get; set; } = 0.685f;
    public float CliffStrength { get; set; } = 0.85f;
    public float[] LowlandTint { get; set; } = { 1f, 1f, 1f };
    public float[] RockColor { get; set; } = { 0.42f, 0.38f, 0.33f };
    public float[] SnowColor { get; set; } = { 0.92f, 0.94f, 0.98f };
    public float[] CliffColor { get; set; } = { 0.30f, 0.27f, 0.24f };

    /// <summary>Enabled per-planet-type overrides (absent types fall back to the globals above).</summary>
    public List<ProfileDto> Overrides { get; set; } = new();

    /// <summary>One per-type override profile (terrain relief + biome colour).</summary>
    public sealed class ProfileDto
    {
        public string Type { get; set; } = "";
        public float ReliefScale { get; set; } = 1f;
        public float MountainScale { get; set; } = 1f;
        public float FrequencyScale { get; set; } = 1f;
        public float CraterScale { get; set; } = 1f;
        public float CraterDensity { get; set; } = 1f;
        public float SnowLine { get; set; } = 0.55f;
        public float CliffThreshold { get; set; } = 0.685f;
        public float CliffStrength { get; set; } = 0.85f;
        public float[] LowlandTint { get; set; } = { 1f, 1f, 1f };
        public float[] RockColor { get; set; } = { 0.42f, 0.38f, 0.33f };
        public float[] SnowColor { get; set; } = { 0.92f, 0.94f, 0.98f };
        public float[] CliffColor { get; set; } = { 0.30f, 0.27f, 0.24f };
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static Vector3D<float> ToVec(float[] a) =>
        a is { Length: >= 3 } ? new Vector3D<float>(a[0], a[1], a[2]) : new Vector3D<float>(1f, 1f, 1f);

    private static float[] ToArr(Vector3D<float> v) => new[] { v.X, v.Y, v.Z };

    /// <summary>Read the current live values into a config snapshot.</summary>
    public static TuningConfig Capture(AtmosphereRenderer a, GalaxyBackdrop b) => new()
    {
        RenderBackdrop = b.Enabled,
        BandBrightness = b.BandBrightness,
        BackdropStarBrightness = b.StarBrightness,
        RenderAtmosphere = a.Enabled,
        SunIntensity = a.SunIntensity,
        Exposure = a.Exposure,
        RayleighStrength = a.RayleighStrength,
        MieStrength = a.MieStrength,
        MieG = a.MieG,
        ShellHeight = a.HeightScale,
        ReliefScale = TerrainTuning.ReliefScale,
        MountainScale = TerrainTuning.MountainScale,
        FrequencyScale = TerrainTuning.FrequencyScale,
        CraterScale = TerrainTuning.CraterScale,
        CraterDensity = TerrainTuning.CraterDensity,
        SnowLine = BiomeTuning.SnowLine,
        CliffThreshold = BiomeTuning.CliffThreshold,
        CliffStrength = BiomeTuning.CliffStrength,
        LowlandTint = ToArr(BiomeTuning.LowlandTint),
        RockColor = ToArr(BiomeTuning.RockColor),
        SnowColor = ToArr(BiomeTuning.SnowColor),
        CliffColor = ToArr(BiomeTuning.CliffColor),
        Overrides = CaptureOverrides(),
    };

    private static List<ProfileDto> CaptureOverrides()
    {
        var list = new List<ProfileDto>();
        foreach (PlanetType t in Enum.GetValues<PlanetType>())
        {
            PlanetTuning.Profile p = PlanetTuning.For(t);
            if (!p.Enabled) continue; // only persist the ones the user turned on
            list.Add(new ProfileDto
            {
                Type = t.ToString(),
                ReliefScale = p.ReliefScale,
                MountainScale = p.MountainScale,
                FrequencyScale = p.FrequencyScale,
                CraterScale = p.CraterScale,
                CraterDensity = p.CraterDensity,
                SnowLine = p.SnowLine,
                CliffThreshold = p.CliffThreshold,
                CliffStrength = p.CliffStrength,
                LowlandTint = ToArr(p.LowlandTint),
                RockColor = ToArr(p.RockColor),
                SnowColor = ToArr(p.SnowColor),
                CliffColor = ToArr(p.CliffColor),
            });
        }
        return list;
    }

    /// <summary>Push this snapshot back onto the live backdrop / atmosphere / terrain / biome state.</summary>
    public void Apply(AtmosphereRenderer a, GalaxyBackdrop b)
    {
        b.Enabled = RenderBackdrop;
        b.BandBrightness = BandBrightness;
        b.StarBrightness = BackdropStarBrightness;
        a.Enabled = RenderAtmosphere;
        a.SunIntensity = SunIntensity;
        a.Exposure = Exposure;
        a.RayleighStrength = RayleighStrength;
        a.MieStrength = MieStrength;
        a.MieG = MieG;
        a.HeightScale = ShellHeight;
        TerrainTuning.ReliefScale = ReliefScale;
        TerrainTuning.MountainScale = MountainScale;
        TerrainTuning.FrequencyScale = FrequencyScale;
        TerrainTuning.CraterScale = CraterScale;
        TerrainTuning.CraterDensity = CraterDensity;
        BiomeTuning.SnowLine = SnowLine;
        BiomeTuning.CliffThreshold = CliffThreshold;
        BiomeTuning.CliffStrength = CliffStrength;
        BiomeTuning.LowlandTint = ToVec(LowlandTint);
        BiomeTuning.RockColor = ToVec(RockColor);
        BiomeTuning.SnowColor = ToVec(SnowColor);
        BiomeTuning.CliffColor = ToVec(CliffColor);

        // Replace the override set wholesale so a loaded file is authoritative.
        PlanetTuning.ResetAll();
        foreach (ProfileDto d in Overrides)
        {
            if (!Enum.TryParse(d.Type, out PlanetType t)) continue;
            PlanetTuning.Profile p = PlanetTuning.For(t);
            p.Enabled = true;
            p.ReliefScale = d.ReliefScale;
            p.MountainScale = d.MountainScale;
            p.FrequencyScale = d.FrequencyScale;
            p.CraterScale = d.CraterScale;
            p.CraterDensity = d.CraterDensity;
            p.SnowLine = d.SnowLine;
            p.CliffThreshold = d.CliffThreshold;
            p.CliffStrength = d.CliffStrength;
            p.LowlandTint = ToVec(d.LowlandTint);
            p.RockColor = ToVec(d.RockColor);
            p.SnowColor = ToVec(d.SnowColor);
            p.CliffColor = ToVec(d.CliffColor);
        }
    }

    /// <summary>Serialize the current live values to <paramref name="path"/>. Returns false on IO error.</summary>
    public static bool Save(AtmosphereRenderer a, GalaxyBackdrop b, string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Capture(a, b), Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Load and apply values from <paramref name="path"/> if it exists. Returns false if absent/invalid.</summary>
    public static bool Load(AtmosphereRenderer a, GalaxyBackdrop b, string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            TuningConfig? c = JsonSerializer.Deserialize<TuningConfig>(File.ReadAllText(path));
            if (c == null) return false;
            c.Apply(a, b);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
