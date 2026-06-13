using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// The terrain model for one planet: a height field over the unit sphere plus an
/// elevation/slope/biome colour ramp, both seeded from the planet. Gas/ice giants have no
/// solid surface (<see cref="HasSurface"/> is false) and are never landed on.
///
/// The height field layers <b>fBm continents</b> (broad land/ocean basins) with
/// <b>domain-warped ridged-multifractal mountains</b> masked onto the highlands, so planets
/// read as dramatic ranges and canyons rather than gentle rolling noise. Per-type tuning
/// gives each world class a distinct silhouette (jagged lava, big rocky ranges, smoother ice).
///
/// Every term is a FIXED-parameter pure function of direction, so neighbouring LOD patches
/// sampling a shared edge get identical heights — no vertical cracks (the renderer adds
/// skirts for the remaining T-junction gaps). <see cref="Amplitude"/> stays a true upper
/// bound on |height| because the blend weights sum to ≤ 1 and the mountain term is ≥ 0.
/// </summary>
public sealed class PlanetTerrain
{
    public readonly double Radius;
    public readonly bool HasSurface;
    public readonly PlanetType Type;

    private readonly Noise _noise;
    private readonly double _baseAmplitude;    // seeded max relief (metres) before ReliefScale
    private readonly double _continentFreq;
    private readonly double _mountainFreq;
    private readonly double _detailFreq;
    private readonly double _warpFreq;
    private readonly double _warpStrength;
    private readonly double _mountainWeight;    // seeded mountain weight (scaled by MountainScale)
    private readonly bool _hasOcean;
    private readonly double _seaLevelFrac;      // sea level as a fraction of Amplitude
    private readonly Vector3D<float> _baseColor;

    // Three additive layers: broad continents, ridged mountains on the highlands, and
    // high-frequency detail roughness. Octave counts are clamped per-patch to the resolution
    // it can actually show (see OctavesFor), so strong fine detail appears close up without
    // aliasing into noise when the whole planet is a few coarse patches seen from orbit.
    private const int WarpOctaves = 3;
    private const int MaxContinentOctaves = 9;
    private const int MaxMountainOctaves = 16;
    private const int MaxDetailOctaves = 14;
    // Continents are the lowest-frequency layer, so their amplitude shows up as the planet's
    // large-scale silhouette. Keeping it modest stops continental swells from looming over the
    // limb as "mountains" that turn out to be gentle slopes when you actually fly there; the
    // ruggedness you see up close comes mostly from the mountain + detail layers instead.
    private const double ContinentWeight = 0.30;
    private const double DetailWeight = 0.45;
    private const double ContinentGain = 0.50;
    private const double MountainGain = 0.58; // > 0.5 keeps amplitude in the high octaves → rugged
    private const double DetailGain = 0.55;

    // Analytic upper bound on |height|: the three layers are bounded by their weights, and the
    // mountain term is ≥ 0. Resolved per type so an enabled override wins over the global knobs.
    public double Amplitude =>
        _baseAmplitude * PlanetTuning.EffectiveRelief(Type) *
        (ContinentWeight + _mountainWeight * PlanetTuning.EffectiveMountain(Type) + DetailWeight);

    public PlanetTerrain(Planet planet)
    {
        Radius = planet.RadiusMeters;
        Type = planet.Type;
        HasSurface = planet.Type is not (PlanetType.GasGiant or PlanetType.IceGiant);
        _baseColor = planet.Color;

        var rng = new DeterministicRng(Hashing.Combine(planet.Seed, 0x7E44A1u));
        _noise = new Noise(planet.Seed);

        // Per-type silhouette: relief height and how mountainous (ridge weight) the world is.
        (double relief, double ridge) = planet.Type switch
        {
            PlanetType.Lava => (0.022, 0.70),   // jagged, fractured crust
            PlanetType.Rocky => (0.020, 0.65),  // tall ranges, deep valleys
            PlanetType.Desert => (0.015, 0.50), // mesas and broad dunes
            PlanetType.Ocean => (0.014, 0.55),  // continents with coastal ranges
            PlanetType.Ice => (0.012, 0.40),    // smoother, cracked plains
            _ => (0.016, 0.55),
        };

        _baseAmplitude = Radius * relief * rng.Range(0.85, 1.25);
        _mountainWeight = ridge * rng.Range(0.9, 1.1);

        _continentFreq = rng.Range(1.1, 2.2);
        _mountainFreq = _continentFreq * rng.Range(2.5, 4.0);
        _detailFreq = rng.Range(28.0, 60.0);   // fine roughness, layered above the mountains
        _warpFreq = _continentFreq * rng.Range(0.8, 1.6);
        _warpStrength = rng.Range(0.10, 0.22);

        _hasOcean = planet.Type == PlanetType.Ocean;
        _seaLevelFrac = _hasOcean ? rng.Range(-0.15, 0.05) : double.NegativeInfinity;
    }

    /// <summary>Terrain height (metres, signed) at a unit direction — full detail (tests/colour).</summary>
    public double HeightAt(Vector3D<double> unitDir) => HeightAt(unitDir, 0.0);

    /// <summary>
    /// Terrain height (metres, signed) at a unit direction, band-limited to a patch whose
    /// vertices are <paramref name="sampleSpacing"/> metres apart (0 = full detail). Clamping
    /// the octave count to what the patch can resolve keeps coarse, far-away patches smooth and
    /// only spends fine octaves where they are actually visible.
    /// </summary>
    public double HeightAt(Vector3D<double> unitDir, double sampleSpacing)
    {
        if (!HasSurface) return 0;

        double fs = PlanetTuning.EffectiveFrequency(Type);
        double mWeight = _mountainWeight * PlanetTuning.EffectiveMountain(Type);

        double contOct = OctavesFor(_continentFreq * fs, sampleSpacing, MaxContinentOctaves);
        double mtnOct = OctavesFor(_mountainFreq * fs, sampleSpacing, MaxMountainOctaves);
        double detOct = OctavesFor(_detailFreq * fs, sampleSpacing, MaxDetailOctaves);

        // Broad continents / ocean basins.
        double continents = _noise.Fbm(unitDir, contOct, _continentFreq * fs, 2.0, ContinentGain); // [-1,1]

        // Ridged mountains, domain-warped, rising on the highlands. Added on top of continents
        // (not blended) so dialling up "Mountains" makes ranges taller without flattening land.
        double mask = Smoothstep(-0.2, 0.4, continents); // [0,1]
        Vector3D<double> warped = unitDir + DomainWarp(unitDir, fs);
        double mountains = _noise.Ridged(warped, mtnOct, _mountainFreq * fs, 2.0, MountainGain); // [0,1]

        // High-frequency roughness everywhere — this is what makes a patch you stand on look
        // rugged rather than a flat tilted plane.
        double detail = _noise.Fbm(unitDir, detOct, _detailFreq * fs, 2.0, DetailGain); // [-1,1]

        // |shape| ≤ ContinentWeight + mWeight + DetailWeight, matching Amplitude → bound holds.
        double shape = ContinentWeight * continents + mWeight * mountains * mask + DetailWeight * detail;
        // The solid surface keeps its full relief even below the waterline — that *is* the sea
        // floor. A separate translucent water surface (PlanetTerrainRenderer) sits at SeaLevel,
        // and coastlines emerge naturally where the rugged land crosses it.
        return _baseAmplitude * PlanetTuning.EffectiveRelief(Type) * shape;
    }

    /// <summary>
    /// How many fBm octaves a patch with the given vertex spacing can show without aliasing:
    /// include octaves whose wavelength (≈ 2πR / freq) stays above the Nyquist limit (2·spacing).
    /// Returns a <b>fractional</b> count so the finest octave fades in continuously as spacing
    /// shrinks — that's what lets a patch and its subdivided children differ only by a sliver of
    /// detail instead of a whole octave, so the surface doesn't visibly rebuild itself across an LOD
    /// step. Spacing ≤ 0 returns the full octave budget (used for colouring and the determinism tests).
    /// </summary>
    private double OctavesFor(double baseFreq, double sampleSpacing, int max)
    {
        if (sampleSpacing <= 0.0) return max;
        double maxFreq = Math.PI * Radius / sampleSpacing; // wavelength ≥ 2·spacing
        if (maxFreq <= baseFreq) return 1.0;
        double o = Math.Log2(maxFreq / baseFreq) + 1.0;
        return Math.Clamp(o, 1.0, max);
    }

    private double SeaLevel => _hasOcean ? Amplitude * _seaLevelFrac : double.NegativeInfinity;

    /// <summary>True if this world has a liquid ocean (renderer draws a water surface).</summary>
    public bool HasOcean => _hasOcean;

    /// <summary>Ocean surface height (metres, signed) relative to the base radius; sea floor lies below.</summary>
    public double SeaLevelMeters => SeaLevel;

    /// <summary>
    /// Surface colour from elevation, slope and biome. <paramref name="slope"/> is
    /// cos(angle-from-vertical) — 1 on flats, → 0 on cliffs — so steep faces read as bare
    /// rock and snow only settles on gentle high ground.
    /// </summary>
    public Vector3D<float> ColorAt(double height, double slope)
    {
        double amplitude = Amplitude;

        if (_hasOcean)
        {
            double seaLevel = SeaLevel;
            if (height < seaLevel)
            {
                // Sea floor: pale sand on the shelf near the coast → dark sediment in the deeps.
                // (The blue of the water itself comes from the translucent surface above.)
                float d = (float)Math.Clamp((seaLevel - height) / (amplitude * 0.20 + 1.0), 0f, 1f);
                return Lerp(new Vector3D<float>(0.66f, 0.60f, 0.45f),
                            new Vector3D<float>(0.16f, 0.18f, 0.22f), d);
            }
            // A sandy beach band just above the waterline, fading up into normal land.
            float above = (float)((height - seaLevel) / (amplitude * 0.025 + 1.0));
            if (above < 1f)
                return Lerp(new Vector3D<float>(0.80f, 0.74f, 0.55f),
                            LandBand(height, amplitude, slope), Smoothstepf(0f, 1f, above));
        }

        return LandBand(height, amplitude, slope);
    }

    /// <summary>Elevation/slope colour ramp for dry land (lowland → rock → snow, cliffs in rock).</summary>
    private Vector3D<float> LandBand(double height, double amplitude, double slope)
    {
        Vector3D<float> tint = PlanetTuning.EffectiveLowland(Type);
        float t = (float)Math.Clamp((height / amplitude + 0.3) / 1.3, 0f, 1f);
        Vector3D<float> lowland = new(
            _baseColor.X * tint.X,
            _baseColor.Y * tint.Y,
            _baseColor.Z * tint.Z);
        Vector3D<float> rock = PlanetTuning.EffectiveRock(Type);
        Vector3D<float> snow = PlanetTuning.EffectiveSnow(Type);

        float snowLine = Math.Clamp(PlanetTuning.EffectiveSnowLine(Type), 0.02f, 0.98f);
        Vector3D<float> band = t < snowLine
            ? Lerp(lowland, rock, t / snowLine)
            : Lerp(rock, snow, (t - snowLine) / (1f - snowLine));

        // Steep faces are bare cliff rock; this is the main driver of close-up detail.
        float c = Math.Clamp(PlanetTuning.EffectiveCliffThreshold(Type), 0.2f, 0.97f);
        float steep = 1f - Smoothstepf(c - 0.135f, c + 0.135f, (float)slope);
        return Lerp(band, PlanetTuning.EffectiveCliff(Type), steep * PlanetTuning.EffectiveCliffStrength(Type));
    }

    /// <summary>Backward-compatible overload (treats the point as flat ground).</summary>
    public Vector3D<float> ColorAt(double height) => ColorAt(height, 1.0);

    /// <summary>A small decorrelated noise offset that bends mountain ranges organically.</summary>
    private Vector3D<double> DomainWarp(Vector3D<double> dir, double freqScale)
    {
        double f = _warpFreq * freqScale;
        double wx = _noise.Fbm(dir, WarpOctaves, f, 2.0, 0.5);
        double wy = _noise.Fbm(dir + new Vector3D<double>(31.4, 11.7, 5.2), WarpOctaves, f, 2.0, 0.5);
        double wz = _noise.Fbm(dir + new Vector3D<double>(-7.1, 23.9, 17.3), WarpOctaves, f, 2.0, 0.5);
        return new Vector3D<double>(wx, wy, wz) * _warpStrength;
    }

    private static double Smoothstep(double lo, double hi, double x)
    {
        double t = Math.Clamp((x - lo) / (hi - lo), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static float Smoothstepf(float lo, float hi, float x)
    {
        float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3D<float> Lerp(Vector3D<float> a, Vector3D<float> b, float t)
        => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
