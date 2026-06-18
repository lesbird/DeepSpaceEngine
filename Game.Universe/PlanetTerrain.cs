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
/// On top of that, each body picks a seeded <b>terrain style</b> (tectonic, mountainous, plains
/// or cratered) so worlds differ from one another, and a low-frequency <b>regional ruggedness
/// mask</b> modulates the mountains and detail so a <i>single</i> world has both flat plains and
/// rugged highlands instead of uniform hills. Airless worlds also get a Worley-based <b>crater
/// field</b> (bowl + raised rim); worlds with weather erode their craters away.
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
    public readonly ulong Seed; // body seed — also salts the GPU generator's hash so worlds differ

    private readonly Noise _noise;
    private readonly double _baseAmplitude;    // seeded max relief (metres) before ReliefScale
    private readonly double _continentFreq;
    private readonly double _mountainFreq;
    private readonly double _detailFreq;
    private readonly double _microFreq;
    private readonly double _warpFreq;
    private readonly double _warpStrength;
    private readonly double _mountainWeight;    // seeded mountain weight (scaled by MountainScale)

    // Per-body style: a regional ruggedness mask (flat plains vs rugged highlands) and an optional
    // impact-crater field. See ChooseStyle / StyleParamsFor.
    private readonly double _ruggedFreq;        // low frequency of the regional ruggedness field
    private readonly double _ruggedLo, _ruggedHi; // smoothstep edges turning that field into [0,1]
    private readonly double _detailFloor;       // min high-frequency roughness in the flattest regions
    private readonly double _craterWeight;      // 0 = no craters; else crater layer weight in the blend
    private readonly double _craterDensity;     // fraction of cells [0,1] that actually bear a crater
    private readonly double _craterFreqA, _craterFreqB; // large basins / smaller craters

    private readonly bool _hasOcean;
    private readonly double _seaLevelFrac;      // sea level as a fraction of Amplitude
    private readonly Vector3D<float> _baseColor;

    // Climate inputs for the Whittaker biome colouring (temperature × moisture).
    private readonly float _surfaceTempK;       // representative planet temperature
    private readonly bool _hasLife;             // can support vegetation: liquid water + atmosphere
    private readonly double _moistureFreq;      // moisture-field frequency
    private readonly double _moistureBias;      // per-type wet/dry shift

    // Sedimentary stratification: a terraced term on a low-frequency, LOD-stable shape (so it never
    // pops as patches subdivide). 0 weight on worlds that don't get strata (ice/ocean).
    private readonly double _strataWeight;
    private readonly double _strataFreq;
    private readonly int _strataSteps;
    private readonly double _strataSharp;

    // Three additive layers: broad continents, ridged mountains on the highlands, and
    // high-frequency detail roughness. Octave counts are clamped per-patch to the resolution
    // it can actually show (see OctavesFor), so strong fine detail appears close up without
    // aliasing into noise when the whole planet is a few coarse patches seen from orbit.
    private const int WarpOctaves = 3;
    private const int MaxContinentOctaves = 9;
    private const int MaxMountainOctaves = 20;
    private const int MaxDetailOctaves = 18;
    // A separate fine micro-relief layer, octaves above the detail layer and LOD-gated so it appears
    // only when a patch resolves it (no aliasing from orbit). Small weight: it adds crisp close-up
    // roughness without changing the planet's silhouette. Folded into `shape` like the other layers,
    // so the analytic Amplitude bound (weights sum to the relief budget) still holds exactly.
    private const int MaxMicroOctaves = 8;
    private const double MicroGain = 0.5;
    private const double MicroWeight = 0.0006;
    // Continents are the lowest-frequency layer, so their amplitude shows up as the planet's
    // large-scale silhouette. Keeping it modest stops continental swells from looming over the
    // limb as "mountains" that turn out to be gentle slopes when you actually fly there; the
    // ruggedness you see up close comes mostly from the mountain + detail layers instead.
    private const double ContinentWeight = 0.30;
    private const double DetailWeight = 0.45;
    private const double ContinentGain = 0.50;
    private const double MountainGain = 0.58; // > 0.5 keeps amplitude in the high octaves → rugged
    private const double DetailGain = 0.55;
    private const double CraterRimFrac = 0.28; // raised rim height as a fraction of crater depth
    // Impact craters are a multi-scale cascade, not two classes: many size decades from large basins
    // down to small pits, each LOD-gated so the right scales appear at the right distance (the look of
    // a real airless body, where craters pepper every scale). Finer octaves are shallower (depth ∝
    // diameter), and the weighted-average normalisation keeps the field in [-1, rim] so the analytic
    // Amplitude bound is unchanged.
    private const int CraterOctaves = 10;
    private const double CraterLacunarity = 1.9;
    private const double CraterDepthFalloff = 0.62; // per-octave weight: smaller craters are shallower

    // Analytic upper bound on |height|: the three layers are bounded by their weights, and the
    // mountain term is ≥ 0. Resolved per type so an enabled override wins over the global knobs.
    public double Amplitude =>
        _baseAmplitude * PlanetTuning.EffectiveRelief(Type) *
        (ContinentWeight + _mountainWeight * PlanetTuning.EffectiveMountain(Type) + DetailWeight
         + MicroWeight * Math.Max(0.0, TerrainTuning.MicroDetailScale)
         + _craterWeight * PlanetTuning.EffectiveCraterScale(Type)
         + _strataWeight);

    public PlanetTerrain(CelestialBody body)
    {
        Radius = body.RadiusMeters;
        Type = body.Type;
        HasSurface = body.HasSurface;
        Seed = body.Seed;
        _baseColor = body.Color;

        var rng = new DeterministicRng(Hashing.Combine(body.Seed, 0x7E44A1u));
        _noise = new Noise(body.Seed);

        // Per-type silhouette: relief height and how mountainous (ridge weight) the world is.
        (double relief, double ridge) = body.Type switch
        {
            PlanetType.Lava => (0.022, 0.70),   // jagged, fractured crust
            PlanetType.Rocky => (0.020, 0.65),  // tall ranges, deep valleys
            PlanetType.Desert => (0.015, 0.50), // mesas and broad dunes
            PlanetType.Ocean => (0.014, 0.55),  // continents with coastal ranges
            PlanetType.Ice => (0.012, 0.40),    // smoother, cracked plains
            _ => (0.016, 0.55),
        };

        // A seeded style (biased by type and whether the world has weather) makes worlds differ from
        // one another: some are mountainous, some flat plains, airless ones are crater-scarred.
        StyleParams sp = StyleParamsFor(ChooseStyle(ref rng, body));

        _baseAmplitude = Radius * relief * sp.Relief * rng.Range(0.85, 1.25);
        _mountainWeight = ridge * sp.MountainProminence * rng.Range(0.9, 1.1);

        _continentFreq = rng.Range(1.1, 2.2);
        _mountainFreq = _continentFreq * rng.Range(2.5, 4.0);
        _detailFreq = rng.Range(28.0, 60.0);   // fine roughness, layered above the mountains
        _microFreq = _detailFreq * 10.0;       // micro-relief, an order finer (fixed multiple → no rng shift)
        _warpFreq = _continentFreq * rng.Range(0.8, 1.6);
        _warpStrength = rng.Range(0.10, 0.22);

        _ruggedFreq = _continentFreq * rng.Range(0.4, 0.8); // regional, below continent scale
        _ruggedLo = sp.RuggedLo;
        _ruggedHi = sp.RuggedHi;
        _detailFloor = sp.DetailFloor;
        _craterWeight = sp.CraterWeight;
        _craterDensity = sp.CraterDensity;
        _craterFreqA = rng.Range(28.0, 55.0);              // large basins, visible from orbit
        _craterFreqB = _craterFreqA * rng.Range(3.0, 4.5); // smaller craters, fade in up close

        _hasOcean = body.Type == PlanetType.Ocean;
        _seaLevelFrac = _hasOcean ? rng.Range(-0.15, 0.05) : double.NegativeInfinity;

        _surfaceTempK = body.SurfaceTempK;
        _hasLife = body.HasLiquidWater && body.HasAtmosphere; // vegetation needs both
        _moistureFreq = _continentFreq * rng.Range(0.7, 1.3);
        _moistureBias = body.Type switch
        {
            PlanetType.Ocean => 0.15,   // generally humid
            PlanetType.Desert => -0.32, // arid
            PlanetType.Ice => -0.05,
            _ => 0.0,
        };

        // Sedimentary terracing, strongest on arid/rocky/volcanic crust (mesas, banded canyons).
        double strataBase = body.Type switch
        {
            PlanetType.Desert => 0.10,
            PlanetType.Rocky => 0.07,
            PlanetType.Lava => 0.05,
            _ => 0.0,
        };
        _strataWeight = strataBase * rng.Range(0.6, 1.3);
        _strataFreq = _continentFreq * rng.Range(2.0, 4.0);
        _strataSteps = rng.RangeInt(5, 11);
        _strataSharp = rng.Range(2.0, 3.5);
    }

    /// <summary>Terrain height (metres, signed) at a unit direction — full detail (tests/colour).</summary>
    public double HeightAt(Vector3D<double> unitDir) => HeightAt(unitDir, 0.0);

    /// <summary>
    /// Base frequency (noise cells over the unit sphere) of the mountain layer — the scale the
    /// orbital macro-relief shader starts from. The shader adds octaves finer than this; coarser
    /// (continent-scale) relief is already carried by the baked silhouette/colour.
    /// </summary>
    public double MacroReliefFrequency => _mountainFreq * PlanetTuning.EffectiveFrequency(Type);

    /// <summary>
    /// Finest surface wavelength (metres) the baked geometry can carry. The detail layer's octave
    /// budget (MaxDetailOctaves) saturates here, and since DetailGain &gt; 0.5 keeps the lit slope in
    /// the high octaves, the mesh holds crisp relief right down to this scale and then goes smooth — no
    /// matter how finely a near patch is tessellated below it. The detail layer outreaches the micro
    /// layer (far more octaves), so it sets the floor. The renderer hands its procedural surface detail
    /// off to the geometry no finer than this, so deeply-subdivided near patches (vertex spacing well
    /// below this) keep their procedural texture instead of going flat.
    /// </summary>
    public double FinestGeometryWavelength
    {
        get
        {
            double fs = PlanetTuning.EffectiveFrequency(Type);
            double finestFreq = _detailFreq * fs * Math.Pow(2.0, MaxDetailOctaves - 1);
            return 2.0 * Math.PI * Radius / finestFreq;
        }
    }

    /// <summary>True if this world is genuinely crater-dominated (airless "cratered" style), as opposed
    /// to a weathered world carrying only a faint residual crater weight. Gates the crater albedo/maria
    /// and the orbital crater shading — so an Ocean/plains world isn't painted (or charged) for craters.</summary>
    public bool IsCratered => _craterWeight > 0.25;

    /// <summary>Base frequency (cells over the unit sphere) of the largest craters — the scale the
    /// orbital crater shader starts from (matching <see cref="CraterField"/>'s coarsest octave).</summary>
    public double CraterOrbitalFrequency => _craterFreqA * 0.5 * PlanetTuning.EffectiveFrequency(Type);

    /// <summary>
    /// Where rugged mountain relief actually belongs, in [0,1]: 0 over ocean basins and flat
    /// plains, → 1 on rugged highlands. It is the product of the regional ruggedness field and the
    /// same highland mask the mountain layer uses, so it tracks the real terrain. Both inputs are
    /// LOW-frequency, so this interpolates cleanly across even a coarse (orbital) patch grid — the
    /// renderer bakes it per vertex and the shader uses it to keep fake relief off the seas/plains.
    /// </summary>
    public double MacroReliefMask(Vector3D<double> unitDir)
    {
        if (!HasSurface) return 0.0;
        double fs = PlanetTuning.EffectiveFrequency(Type);
        double rugged = Ruggedness(unitDir, fs);                                           // [0,1]
        double continents = _noise.Fbm(unitDir, MaxContinentOctaves, _continentFreq * fs, 2.0, ContinentGain);
        double mask = Smoothstep(-0.2, 0.4, continents);                                   // [0,1] highlands
        return Math.Clamp(rugged * mask, 0.0, 1.0);
    }

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

        // Regional ruggedness: a low-frequency mask saying how rough THIS area is, so the same world
        // has calm plains in some regions and rugged highlands in others rather than uniform hills.
        double rugged = Ruggedness(unitDir, fs); // [0,1]

        // Ridged mountains, domain-warped, rising on the highlands — only where the region is rugged.
        double mask = Smoothstep(-0.2, 0.4, continents); // [0,1]
        Vector3D<double> warped = unitDir + DomainWarp(unitDir, fs);
        double mountains = _noise.Ridged(warped, mtnOct, _mountainFreq * fs, 2.0, MountainGain); // [0,1]

        // High-frequency roughness via EROSIVE fBm: detail is suppressed on steep accumulated slopes,
        // carving smooth valley floors with detail riding the shoulders/ridges (a cheap erosion model).
        // Also damped in the flat regions (but never fully — _detailFloor keeps a little texture on
        // plains so they don't read as glass).
        double detail = _noise.ErodedFbm(unitDir, detOct, _detailFreq * fs, 2.0, DetailGain); // [-1,1]
        double detailGate = _detailFloor + (1.0 - _detailFloor) * rugged; // [_detailFloor, 1]

        // Fine micro-relief: high-frequency roughness, faded in only once a patch can resolve it
        // (LayerGate) and damped in the flat regions (detailGate), so it crisps up close without
        // aliasing from afar. Bounded by its weight, so |micro term| ≤ microW (Amplitude includes it).
        double microW = MicroWeight * Math.Max(0.0, TerrainTuning.MicroDetailScale);
        double micro = 0.0;
        if (microW > 0.0)
        {
            double microGate = LayerGate(_microFreq * fs, sampleSpacing);
            if (microGate > 0.0)
            {
                double microOct = OctavesFor(_microFreq * fs, sampleSpacing, MaxMicroOctaves);
                micro = _noise.Fbm(unitDir, microOct, _microFreq * fs, 2.0, MicroGain) * microGate * detailGate;
            }
        }

        // Impact craters (airless worlds): a depressed bowl with a raised rim, in [-1, CraterRimFrac].
        // Depth and density are live-tunable (TerrainTuning.Crater*), scaling the seeded per-world amount.
        double craterW = _craterWeight * PlanetTuning.EffectiveCraterScale(Type);
        double crater = craterW > 0.0 ? CraterField(unitDir, fs, sampleSpacing) : 0.0;

        // Sedimentary strata: a terrace applied to a FIXED-octave (LOD-independent) low-frequency
        // field, so the steps are identical at every sample spacing and never pop across an LOD swap.
        double strata = 0.0;
        if (_strataWeight > 0.0)
        {
            var sp2 = unitDir + new Vector3D<double>(8.2, 71.5, 3.6); // decorrelate from other layers
            double low = _noise.Fbm(sp2, 4, _strataFreq * fs, 2.0, 0.5); // [-1,1], spacing-independent
            strata = Terrace(low, _strataSteps, _strataSharp);          // [-1,1]
        }

        // Each term is bounded by its weight (rugged/mask/gate ∈ [0,1], crater ∈ [-1, rim],
        // strata ∈ [-1,1]), so |shape| ≤ sum of weights, matching Amplitude.
        double shape = ContinentWeight * continents
                     + mWeight * mountains * mask * rugged
                     + DetailWeight * detail * detailGate
                     + microW * micro
                     + craterW * crater
                     + _strataWeight * strata;
        // The solid surface keeps its full relief even below the waterline — that *is* the sea
        // floor. A separate translucent water surface (PlanetTerrainRenderer) sits at SeaLevel,
        // and coastlines emerge naturally where the rugged land crosses it.
        return _baseAmplitude * PlanetTuning.EffectiveRelief(Type) * shape;
    }

    /// <summary>
    /// Terrain height at <paramref name="unitDir"/> for BOTH a fine and a coarse band-limit in a single
    /// pass — what a patch needs for geomorphing (its own surface and its parent's). Because the coarse
    /// band is the fine octave series truncated by one octave, every layer's expensive lattice samples
    /// are shared between the two cutoffs (see <see cref="Noise.Fbm2"/> etc.), roughly halving the cost
    /// versus two <see cref="HeightAt(Vector3D{double}, double)"/> calls — with bit-identical results.
    /// Also returns the fine crater value so the caller can feed it to <see cref="ColorAt"/> instead of
    /// re-evaluating the (expensive) crater field a second time for albedo.
    /// </summary>
    public void HeightAt2(Vector3D<double> unitDir, double fineSpacing, double coarseSpacing,
        out double fine, out double coarse, out double craterFine)
    {
        craterFine = 0.0;
        if (!HasSurface) { fine = 0; coarse = 0; return; }

        double fs = PlanetTuning.EffectiveFrequency(Type);
        double mWeight = _mountainWeight * PlanetTuning.EffectiveMountain(Type);

        // Spacing-independent terms (fixed octave counts) — computed once, shared by both cutoffs.
        double rugged = Ruggedness(unitDir, fs);                      // [0,1]
        Vector3D<double> warped = unitDir + DomainWarp(unitDir, fs);  // fixed WarpOctaves
        double detailGate = _detailFloor + (1.0 - _detailFloor) * rugged;

        // Continents (sets the highland mask, which differs per cutoff since the continents do).
        (double contF, double contC) = _noise.Fbm2(unitDir,
            OctavesFor(_continentFreq * fs, fineSpacing, MaxContinentOctaves),
            OctavesFor(_continentFreq * fs, coarseSpacing, MaxContinentOctaves),
            _continentFreq * fs, 2.0, ContinentGain);
        double maskF = Smoothstep(-0.2, 0.4, contF), maskC = Smoothstep(-0.2, 0.4, contC);

        // Ridged mountains on the warped highlands.
        (double mtnF, double mtnC) = _noise.Ridged2(warped,
            OctavesFor(_mountainFreq * fs, fineSpacing, MaxMountainOctaves),
            OctavesFor(_mountainFreq * fs, coarseSpacing, MaxMountainOctaves),
            _mountainFreq * fs, 2.0, MountainGain);

        // High-frequency erosive detail.
        (double detF, double detC) = _noise.ErodedFbm2(unitDir,
            OctavesFor(_detailFreq * fs, fineSpacing, MaxDetailOctaves),
            OctavesFor(_detailFreq * fs, coarseSpacing, MaxDetailOctaves),
            _detailFreq * fs, 2.0, DetailGain);

        // Fine micro-relief (skip the layer entirely when no cutoff resolves it — #6 per-patch gate).
        double microW = MicroWeight * Math.Max(0.0, TerrainTuning.MicroDetailScale);
        double microF = 0.0, microC = 0.0;
        if (microW > 0.0)
        {
            double mgF = LayerGate(_microFreq * fs, fineSpacing), mgC = LayerGate(_microFreq * fs, coarseSpacing);
            if (mgF > 0.0 || mgC > 0.0)
            {
                (double mfF, double mfC) = _noise.Fbm2(unitDir,
                    OctavesFor(_microFreq * fs, fineSpacing, MaxMicroOctaves),
                    OctavesFor(_microFreq * fs, coarseSpacing, MaxMicroOctaves),
                    _microFreq * fs, 2.0, MicroGain);
                microF = mfF * mgF * detailGate;
                microC = mfC * mgC * detailGate;
            }
        }

        // Impact craters (dual gate, shared 3×3×3 work). craterFine is reused for albedo by the caller.
        double craterW = _craterWeight * PlanetTuning.EffectiveCraterScale(Type);
        double craterC = 0.0;
        if (craterW > 0.0)
        {
            (craterFine, craterC) = CraterField2(unitDir, fs, fineSpacing, coarseSpacing);
        }

        // Sedimentary strata: fixed-octave, LOD-independent — identical for both cutoffs.
        double strata = 0.0;
        if (_strataWeight > 0.0)
        {
            var sp2 = unitDir + new Vector3D<double>(8.2, 71.5, 3.6);
            double low = _noise.Fbm(sp2, 4, _strataFreq * fs, 2.0, 0.5);
            strata = Terrace(low, _strataSteps, _strataSharp);
        }

        double scale = _baseAmplitude * PlanetTuning.EffectiveRelief(Type);
        fine = scale * (ContinentWeight * contF + mWeight * mtnF * maskF * rugged
                        + DetailWeight * detF * detailGate + microW * microF
                        + craterW * craterFine + _strataWeight * strata);
        coarse = scale * (ContinentWeight * contC + mWeight * mtnC * maskC * rugged
                        + DetailWeight * detC * detailGate + microW * microC
                        + craterW * craterC + _strataWeight * strata);
    }

    /// <summary>
    /// The deterministic parameters the GPU tile generator needs to reproduce this world's terrain in a
    /// shader (the live <see cref="TerrainTuning"/> multipliers already folded in). The GPU path uses its
    /// own GLSL hash, so the result has the same <i>character</i> as the CPU terrain rather than matching
    /// it bit-for-bit. Frequencies are in noise-cells over the unit sphere; <c>Scale</c> is metres of
    /// relief (so a shader height = Scale × shape). Layer weights/gains mirror <see cref="HeightAt"/>.
    /// </summary>
    public struct GpuTerrainParams
    {
        public double Radius;
        public ulong Seed;
        public double ContinentFreq, MountainFreq, DetailFreq;
        public double ContinentWeight, MountainWeight, DetailWeight;
        public double ContinentGain, MountainGain, DetailGain;
        public double WarpFreq, WarpStrength, Scale;
        public double RuggedFreq, RuggedLo, RuggedHi, DetailFloor; // regional ruggedness mask
        public int MaxContinentOctaves, MaxMountainOctaves, MaxDetailOctaves;

        // Impact craters (airless worlds): a cellular bowl+rim cascade baked into the height tile, plus
        // crater-floor/rim and maria albedo. CraterFreq is the coarsest (basin) frequency; the cascade
        // climbs from there. Weight 0 → the world has no craters (the GPU path skips the whole field).
        public double CraterWeight, CraterDensity, CraterFreq;
        public float IsCratered;             // 1 = crater albedo + maria apply (genuinely cratered world)

        // Biome / colour (for the per-pixel albedo in the render shader, mirroring ColorAt/LandBand).
        public Vector3D<float> BaseColor, Rock, Snow, Cliff, Lowland;
        public float SnowLine, CliffThreshold, CliffStrength, SurfaceTempK;
        public float HasLife;               // 1 = supports vegetation (liquid water + atmosphere)
        public double MoistureFreq, MoistureBias, Amplitude;
    }

    /// <summary>Snapshot the generation parameters for the GPU tile path (see <see cref="GpuTerrainParams"/>).</summary>
    public GpuTerrainParams GpuParams()
    {
        double fs = PlanetTuning.EffectiveFrequency(Type);
        return new GpuTerrainParams
        {
            Radius = Radius, Seed = Seed,
            ContinentFreq = _continentFreq * fs, MountainFreq = _mountainFreq * fs, DetailFreq = _detailFreq * fs,
            ContinentWeight = ContinentWeight, MountainWeight = _mountainWeight * PlanetTuning.EffectiveMountain(Type),
            DetailWeight = DetailWeight,
            ContinentGain = ContinentGain, MountainGain = MountainGain, DetailGain = DetailGain,
            WarpFreq = _warpFreq * fs, WarpStrength = _warpStrength,
            RuggedFreq = _ruggedFreq * fs, RuggedLo = _ruggedLo, RuggedHi = _ruggedHi, DetailFloor = _detailFloor,
            Scale = _baseAmplitude * PlanetTuning.EffectiveRelief(Type),
            MaxContinentOctaves = MaxContinentOctaves, MaxMountainOctaves = MaxMountainOctaves,
            MaxDetailOctaves = MaxDetailOctaves,
            CraterWeight = _craterWeight * PlanetTuning.EffectiveCraterScale(Type),
            CraterDensity = Math.Clamp(_craterDensity * PlanetTuning.EffectiveCraterDensity(Type), 0.0, 1.0),
            CraterFreq = _craterFreqA * 0.5 * fs,
            IsCratered = IsCratered ? 1f : 0f,
            BaseColor = _baseColor, Rock = PlanetTuning.EffectiveRock(Type), Snow = PlanetTuning.EffectiveSnow(Type),
            Cliff = PlanetTuning.EffectiveCliff(Type), Lowland = PlanetTuning.EffectiveLowland(Type),
            SnowLine = PlanetTuning.EffectiveSnowLine(Type),
            CliffThreshold = PlanetTuning.EffectiveCliffThreshold(Type),
            CliffStrength = PlanetTuning.EffectiveCliffStrength(Type),
            SurfaceTempK = _surfaceTempK, HasLife = _hasLife ? 1f : 0f,
            MoistureFreq = _moistureFreq, MoistureBias = _moistureBias, Amplitude = Amplitude,
        };
    }

    /// <summary>Fractional fBm octave count a patch with the given vertex spacing can show without
    /// aliasing (public so the GPU generator clamps octaves identically to the CPU path). Spacing ≤ 0
    /// returns the full budget.</summary>
    public double OctavesForSpacing(double baseFreq, double sampleSpacing, int max)
        => OctavesFor(baseFreq, sampleSpacing, max);

    /// <summary>Fractional crater-octave count the GPU generator should bake at this vertex spacing: the
    /// sum of each crater size class's smooth LOD gate (coarse basins open first, finer pits fade in on
    /// approach), capped at <see cref="CraterOctaves"/>. The shader loops 10 octaves and fades the top
    /// one by the fraction, so coarse tiles carry shallow basins and deep tiles the full cascade — the
    /// crater geomorph. Mirrors <see cref="CraterField"/>'s per-octave <see cref="LayerGate"/>.</summary>
    public double CraterOctavesForSpacing(double sampleSpacing)
    {
        double freq = _craterFreqA * 0.5 * PlanetTuning.EffectiveFrequency(Type);
        double oct = 0.0;
        for (int o = 0; o < CraterOctaves; o++)
        {
            oct += LayerGate(freq, sampleSpacing);
            freq *= CraterLacunarity;
        }
        return oct;
    }

    // --- GPU-path height mirror -----------------------------------------------------------------
    // A CPU re-implementation of the GPU tile generator's height (TerrainTileGenerator's GLSL), so the
    // camera near/far fit and the rover can know the actual GPU surface height (the GPU uses a different
    // hash than HeightAt, so HeightAt does NOT describe the GPU terrain). Mirrors the same hash/noise/
    // octave-clamp; double here vs float on the GPU, so it matches to a fraction of a noise cell —
    // ample for the near plane and good enough for vehicle placement.

    private const double GpuFloatSafeFreq = 700_000.0; // mirror of TerrainTileGenerator.FloatSafeFreq

    /// <summary>Height (metres, signed) of the GPU-generated terrain at a unit direction, band-limited to
    /// <paramref name="sampleSpacing"/> (0 = full). Mirrors the generation shader; see the region note.</summary>
    public double GpuHeightAt(Vector3D<double> unitDir, double sampleSpacing)
    {
        if (!HasSurface) return 0.0;
        GpuTerrainParams p = GpuParams();
        var dir = new Vector3D<float>((float)unitDir.X, (float)unitDir.Y, (float)unitDir.Z);
        Vector3D<float> seed = GpuSeedOffset(p.Seed);
        float oc = (float)GpuOctClamp(p.ContinentFreq, sampleSpacing, p.MaxContinentOctaves);
        float om = (float)GpuOctClamp(p.MountainFreq, sampleSpacing, p.MaxMountainOctaves);
        float od = (float)GpuOctClamp(p.DetailFreq, sampleSpacing, p.MaxDetailOctaves);

        float cont = GpuFbm(dir, (float)p.ContinentFreq, oc, (float)p.ContinentGain, seed);
        float rugged = SmoothstepF((float)p.RuggedLo, (float)p.RuggedHi,
            0.5f + 0.5f * GpuFbm(dir + new Vector3D<float>(53.1f, 12.7f, 91.3f), (float)p.RuggedFreq, 4f, 0.5f, seed));
        float mask = SmoothstepF(-0.2f, 0.4f, cont);
        Vector3D<float> warped = dir + GpuDomainWarp(dir, p, seed);
        float mtn = GpuRidged(warped, (float)p.MountainFreq, om, (float)p.MountainGain, seed);
        float det = GpuErodedFbm(dir, (float)p.DetailFreq, od, (float)p.DetailGain, seed);
        float detailGate = (float)p.DetailFloor + (1f - (float)p.DetailFloor) * rugged;
        float shape = (float)p.ContinentWeight * cont + (float)p.MountainWeight * mtn * mask * rugged
                    + (float)p.DetailWeight * det * detailGate;
        if (p.CraterWeight > 0.0)
        {
            float crater = GpuCraterField(dir, (float)p.CraterFreq,
                (float)CraterOctavesForSpacing(sampleSpacing), (float)p.CraterDensity, seed);
            shape += (float)p.CraterWeight * crater;
        }
        return p.Scale * shape;
    }

    /// <summary>Float mirror of the GPU generator's GLSL crater cascade (see <c>TerrainTileGenerator</c>):
    /// a 3×3×3 cellular bowl+rim field over 10 size classes, the top one faded by <paramref name="octCount"/>'s
    /// fraction, normalised by the full-cascade weight sum. In ≈[-1, rim]. Lets the rover/near-plane know the
    /// real baked crater surface.</summary>
    private static float GpuCraterField(Vector3D<float> dir, float baseFreq, float octCount, float density, Vector3D<float> seed)
    {
        if (octCount <= 0f) return 0f;
        const float wnorm = 2.6094f; // Σ_{o=0..9} 0.62^o — the full cascade weight, so coarse tiles read shallow
        float sum = 0f, freq = baseFreq, weight = 1f;
        for (int o = 0; o < 10; o++)
        {
            float ofade = Math.Clamp(octCount - o, 0f, 1f);
            if (ofade > 0f)
            {
                Vector3D<float> p = dir * freq;
                var ip = new Vector3D<float>(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
                float salt = o * 17f;
                float minBowl = 0f, maxRim = 0f;
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = ip + new Vector3D<float>(dx, dy, dz);
                    float ex = GpuHash13(c + new Vector3D<float>(salt, salt, salt), seed);
                    if (ex > density) continue;
                    var jit = new Vector3D<float>(
                        GpuHash13(c + new Vector3D<float>(salt + 1.7f, salt + 1.7f, salt + 1.7f), seed),
                        GpuHash13(c + new Vector3D<float>(salt + 9.1f, salt + 9.1f, salt + 9.1f), seed),
                        GpuHash13(c + new Vector3D<float>(salt + 4.3f, salt + 4.3f, salt + 4.3f), seed));
                    float radius = 0.22f + 0.28f * GpuFractF(ex * 7.3f + 0.19f);
                    Vector3D<float> e = p - (c + jit);
                    float t = MathF.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z) / radius;
                    if (t >= 1.5f) continue;
                    float bowl = -(1f - SmoothstepF(0f, 0.85f, MathF.Min(t, 1f)));
                    float er = (t - 0.95f) / 0.12f;
                    float rim = 0.28f * MathF.Exp(-0.5f * er * er);
                    minBowl = MathF.Min(minBowl, bowl);
                    maxRim = MathF.Max(maxRim, rim);
                }
                sum += weight * ofade * (minBowl + maxRim);
            }
            freq *= 1.9f; weight *= 0.62f;
        }
        return sum / wnorm;
    }

    private double GpuOctClamp(double baseFreq, double spacing, int max)
    {
        double lod = OctavesFor(baseFreq, spacing, max); // same LOD band-limit as the GPU generator
        double safe = Math.Floor(Math.Log2(GpuFloatSafeFreq / Math.Max(1.0, baseFreq))) + 1.0;
        return Math.Max(0.0, Math.Min(lod, safe));
    }

    // Everything below mirrors the GLSL generator in float (not double): the noise hash floors at the
    // float precision edge, so only matching float reproduces the GPU surface — a double mirror diverged
    // by up to kilometres on a coarse leaf's top octave.
    private static float SmoothstepF(float lo, float hi, float x) { float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f); return t * t * (3f - 2f * t); }
    private static Vector3D<float> GpuSeedOffset(ulong seed) => new(
        (float)(seed & 1023) / 1024f, (float)((seed >> 10) & 1023) / 1024f, (float)((seed >> 20) & 1023) / 1024f);
    private static float GpuFractF(float x) => x - MathF.Floor(x);
    private static float ModF(float a, float b) => a - b * MathF.Floor(a / b);
    private static float LerpF(float a, float b, float t) => a + (b - a) * t;

    private static float GpuHash13(Vector3D<float> p, Vector3D<float> seed)
    {
        p = new Vector3D<float>(ModF(p.X, 8192f), ModF(p.Y, 8192f), ModF(p.Z, 8192f)) + seed;
        p = new Vector3D<float>(GpuFractF(p.X * 0.1031f), GpuFractF(p.Y * 0.1031f), GpuFractF(p.Z * 0.1031f));
        float d = p.X * (p.Y + 33.33f) + p.Y * (p.Z + 33.33f) + p.Z * (p.X + 33.33f); // dot(p, p.yzx + 33.33)
        p += new Vector3D<float>(d, d, d);
        return GpuFractF((p.X + p.Y) * p.Z);
    }

    private static float GpuVNoise(Vector3D<float> q, Vector3D<float> seed)
    {
        var c = new Vector3D<float>(MathF.Floor(q.X), MathF.Floor(q.Y), MathF.Floor(q.Z));
        var f = q - c;
        f = new Vector3D<float>(f.X * f.X * (3f - 2f * f.X), f.Y * f.Y * (3f - 2f * f.Y), f.Z * f.Z * (3f - 2f * f.Z));
        float n000 = GpuHash13(c, seed), n100 = GpuHash13(c + new Vector3D<float>(1, 0, 0), seed);
        float n010 = GpuHash13(c + new Vector3D<float>(0, 1, 0), seed), n110 = GpuHash13(c + new Vector3D<float>(1, 1, 0), seed);
        float n001 = GpuHash13(c + new Vector3D<float>(0, 0, 1), seed), n101 = GpuHash13(c + new Vector3D<float>(1, 0, 1), seed);
        float n011 = GpuHash13(c + new Vector3D<float>(0, 1, 1), seed), n111 = GpuHash13(c + new Vector3D<float>(1, 1, 1), seed);
        float x00 = LerpF(n000, n100, f.X), x10 = LerpF(n010, n110, f.X);
        float x01 = LerpF(n001, n101, f.X), x11 = LerpF(n011, n111, f.X);
        return LerpF(LerpF(x00, x10, f.Y), LerpF(x01, x11, f.Y), f.Z);
    }

    private static float GpuFbm(Vector3D<float> dir, float freq, float oct, float gain, Vector3D<float> seed)
    {
        if (oct <= 0f) return 0f;
        int full = (int)MathF.Floor(oct);
        float frac = oct - full, sum = 0, amp = 1, f = freq, norm = 0;
        for (int i = 0; i < 32 && i < full; i++) { sum += amp * (GpuVNoise(dir * f, seed) * 2f - 1f); norm += amp; amp *= gain; f *= 2f; }
        if (frac > 0f) { sum += amp * frac * (GpuVNoise(dir * f, seed) * 2f - 1f); norm += amp * frac; }
        return norm > 0f ? sum / norm : 0f;
    }

    /// <summary>Value noise in [-1,1] plus its analytic gradient (mirrors the gen shader's <c>vnoiseD</c>):
    /// trilinear of the 8 corner hashes, gradient via the smoothstep derivative. Value + gradient both
    /// scaled to the [-1,1] range (factor 2).</summary>
    private static (float val, Vector3D<float> grad) GpuVNoiseD(Vector3D<float> q, Vector3D<float> seed)
    {
        var c = new Vector3D<float>(MathF.Floor(q.X), MathF.Floor(q.Y), MathF.Floor(q.Z));
        var ff = q - c;
        var u = new Vector3D<float>(ff.X * ff.X * (3f - 2f * ff.X), ff.Y * ff.Y * (3f - 2f * ff.Y), ff.Z * ff.Z * (3f - 2f * ff.Z));
        var du = new Vector3D<float>(6f * ff.X * (1f - ff.X), 6f * ff.Y * (1f - ff.Y), 6f * ff.Z * (1f - ff.Z));
        float n000 = GpuHash13(c, seed), n100 = GpuHash13(c + new Vector3D<float>(1, 0, 0), seed);
        float n010 = GpuHash13(c + new Vector3D<float>(0, 1, 0), seed), n110 = GpuHash13(c + new Vector3D<float>(1, 1, 0), seed);
        float n001 = GpuHash13(c + new Vector3D<float>(0, 0, 1), seed), n101 = GpuHash13(c + new Vector3D<float>(1, 0, 1), seed);
        float n011 = GpuHash13(c + new Vector3D<float>(0, 1, 1), seed), n111 = GpuHash13(c + new Vector3D<float>(1, 1, 1), seed);
        float x00 = LerpF(n000, n100, u.X), x10 = LerpF(n010, n110, u.X);
        float x01 = LerpF(n001, n101, u.X), x11 = LerpF(n011, n111, u.X);
        float y0 = LerpF(x00, x10, u.Y), y1 = LerpF(x01, x11, u.Y);
        float val = LerpF(y0, y1, u.Z);
        float dfu = LerpF(LerpF(n100 - n000, n110 - n010, u.Y), LerpF(n101 - n001, n111 - n011, u.Y), u.Z);
        float dfv = LerpF(x10 - x00, x11 - x01, u.Z);
        float dfw = y1 - y0;
        return (val * 2f - 1f, new Vector3D<float>(2f * dfu * du.X, 2f * dfv * du.Y, 2f * dfw * du.Z));
    }

    /// <summary>Erosive fBm in ~[-1,1], each octave damped by 1/(1+k·|Σgrad|²) so detail fades on steep
    /// slopes — carved valley floors, roughness on ridges. Mirrors the gen shader's <c>erodedFbm</c> and
    /// PlanetTerrain Noise.ErodedFbm (k = 1.4).</summary>
    private static float GpuErodedFbm(Vector3D<float> dir, float freq, float oct, float gain, Vector3D<float> seed)
    {
        if (oct <= 0f) return 0f;
        const float k = 1.4f;
        int full = (int)MathF.Floor(oct);
        float frac = oct - full, sum = 0, amp = 1, f = freq, norm = 0;
        var gradSum = new Vector3D<float>(0, 0, 0);
        for (int i = 0; i < 32 && i < full; i++)
        {
            (float n, Vector3D<float> g) = GpuVNoiseD(dir * f, seed);
            gradSum += g;
            float damp = 1f / (1f + k * Vector3D.Dot(gradSum, gradSum));
            sum += amp * n * damp; norm += amp; amp *= gain; f *= 2f;
        }
        if (frac > 0f)
        {
            (float n, Vector3D<float> g) = GpuVNoiseD(dir * f, seed);
            gradSum += g * frac;
            float damp = 1f / (1f + k * Vector3D.Dot(gradSum, gradSum));
            sum += amp * frac * n * damp; norm += amp * frac;
        }
        return norm > 0f ? sum / norm : 0f;
    }

    private static float GpuRidged(Vector3D<float> dir, float freq, float oct, float gain, Vector3D<float> seed)
    {
        if (oct <= 0f) return 0f;
        int full = (int)MathF.Floor(oct);
        float frac = oct - full, sum = 0, amp = 0.5f, f = freq, prev = 1f, norm = 0;
        for (int i = 0; i < 32 && i < full; i++)
        {
            float n = 1f - MathF.Abs(GpuVNoise(dir * f, seed) * 2f - 1f); n *= n; n *= prev;
            sum += n * amp; norm += amp; prev = n; amp *= gain; f *= 2f;
        }
        if (frac > 0f) { float n = 1f - MathF.Abs(GpuVNoise(dir * f, seed) * 2f - 1f); n *= n; n *= prev; sum += n * amp * frac; norm += amp * frac; }
        return norm > 0f ? Math.Clamp(sum / norm, 0f, 1f) : 0f;
    }

    private static Vector3D<float> GpuDomainWarp(Vector3D<float> dir, GpuTerrainParams p, Vector3D<float> seed)
    {
        float wx = GpuFbm(dir, (float)p.WarpFreq, 3f, 0.5f, seed);
        float wy = GpuFbm(dir + new Vector3D<float>(31.4f, 11.7f, 5.2f), (float)p.WarpFreq, 3f, 0.5f, seed);
        float wz = GpuFbm(dir + new Vector3D<float>(-7.1f, 23.9f, 17.3f), (float)p.WarpFreq, 3f, 0.5f, seed);
        return new Vector3D<float>(wx, wy, wz) * (float)p.WarpStrength;
    }

    // --- terrain style ---

    private enum TerrainStyle { Tectonic, Mountainous, Plains, Cratered }

    private readonly record struct StyleParams(
        double Relief, double MountainProminence, double RuggedLo, double RuggedHi,
        double DetailFloor, double CraterWeight, double CraterDensity);

    /// <summary>Pick a style from the seed, biased by world class and weathering. Airless worlds keep
    /// their impact scars (cratered); worlds with an atmosphere erode them into plains/hills/ranges.</summary>
    private static TerrainStyle ChooseStyle(ref DeterministicRng rng, CelestialBody body)
    {
        double u = rng.NextDouble();
        if (body.Type == PlanetType.Lava)
            return u < 0.65 ? TerrainStyle.Mountainous : body.HasAtmosphere ? TerrainStyle.Tectonic : TerrainStyle.Cratered;

        if (!body.HasAtmosphere) // no weather → craters survive
            return u < 0.60 ? TerrainStyle.Cratered : u < 0.80 ? TerrainStyle.Plains : TerrainStyle.Mountainous;

        return u < 0.40 ? TerrainStyle.Plains : u < 0.75 ? TerrainStyle.Tectonic : TerrainStyle.Mountainous;
    }

    private static StyleParams StyleParamsFor(TerrainStyle s) => s switch
    {
        //                              relief  mtn   rLo   rHi  detFloor craterW craterDensity
        TerrainStyle.Mountainous => new(1.30, 1.55, 0.10, 0.55, 0.50, 0.00, 0.00),
        TerrainStyle.Plains      => new(0.85, 0.70, 0.50, 0.82, 0.18, 0.06, 0.45),
        TerrainStyle.Cratered    => new(1.00, 0.40, 0.45, 0.85, 0.22, 0.50, 0.75),
        _ /* Tectonic */         => new(1.00, 1.00, 0.25, 0.62, 0.45, 0.00, 0.00),
    };

    /// <summary>Low-frequency regional roughness in [0,1]: 0 = flat plains here, 1 = rugged highlands.</summary>
    private double Ruggedness(Vector3D<double> unitDir, double freqScale)
    {
        // Offset so the regional field is decorrelated from the continents it modulates.
        var p = unitDir + new Vector3D<double>(53.1, 12.7, 91.3);
        double r = _noise.Fbm(p, 4, _ruggedFreq * freqScale, 2.0, 0.5); // [-1,1]
        return Smoothstep(_ruggedLo, _ruggedHi, 0.5 + 0.5 * r);
    }

    // --- impact craters ---

    /// <summary>A cascade of crater size classes (large basins → small pits), normalised to [-1, rim].
    /// Each octave is finer and shallower than the last and LOD-gated, so coarse basins read from
    /// orbit and progressively smaller craters fade in as the camera descends — no aliasing, no pop.</summary>
    private double CraterField(Vector3D<double> unitDir, double freqScale, double sampleSpacing)
    {
        double density = Math.Clamp(_craterDensity * PlanetTuning.EffectiveCraterDensity(Type), 0.0, 1.0);
        // Start half an octave below the seeded basin frequency so the largest craters are genuine
        // basins, then climb CraterOctaves decades finer.
        double freq = _craterFreqA * 0.5;
        double weight = 1.0, sum = 0.0, wsum = 0.0;
        for (int o = 0; o < CraterOctaves; o++)
        {
            sum += weight * CraterLayer(unitDir, freq * freqScale, CraterSalt(o), sampleSpacing, density);
            wsum += weight;
            freq *= CraterLacunarity;
            weight *= CraterDepthFalloff;
        }
        return sum / wsum; // weighted average stays in [-1, rim]
    }

    /// <summary>Per-octave hash salt so each crater size class lays down an independent pattern.</summary>
    private static ulong CraterSalt(int octave) => 0x1111u + (ulong)octave * 0x9E3779B97F4A7C15u;

    /// <summary>
    /// One crater size class. Accumulates every crater in the surrounding 3×3×3 cells (one per cell)
    /// rather than only the nearest, combining bowls by minimum (deepest wins) and rims by maximum.
    /// Each crater's footprint falls smoothly to zero at its edge, so the field is continuous across
    /// cell borders — no vertical "zipper" where the dominant crater switches. Faded in by LOD so far
    /// patches don't alias sub-pixel craters. Returns [-1, CraterRimFrac].
    /// </summary>
    private double CraterLayer(Vector3D<double> unitDir, double freq, ulong salt, double sampleSpacing, double density)
    {
        double gate = LayerGate(freq, sampleSpacing);
        if (gate <= 0.0) return 0.0;

        Vector3D<double> p = unitDir * freq;
        long xi = (long)Math.Floor(p.X), yi = (long)Math.Floor(p.Y), zi = (long)Math.Floor(p.Z);

        double minBowl = 0.0, maxRim = 0.0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            (double fx, double fy, double fz, double rand) = _noise.CellFeature(xi + dx, yi + dy, zi + dz, salt);

            // Only some cells bear a crater; decorrelate the radius from the existence test. Radius is
            // kept ≤ 0.5 cell so a crater's full footprint stays within the 3×3×3 search window.
            if (rand > density) continue;
            double radius = 0.22 + 0.28 * Frac(rand * 7.3 + 0.19); // [0.22, 0.50] in cell units

            double ex = p.X - fx, ey = p.Y - fy, ez = p.Z - fz;
            double t = Math.Sqrt(ex * ex + ey * ey + ez * ez) / radius;
            if (t >= 1.5) continue;

            // Depressed bowl rising to the rim, plus a gaussian rim ring just outside it.
            double bowl = -(1.0 - Smoothstep(0.0, 0.85, Math.Min(t, 1.0))); // [-1, 0]
            double e = (t - 0.95) / 0.12;
            double rim = CraterRimFrac * Math.Exp(-0.5 * e * e);            // [0, CraterRimFrac]

            if (bowl < minBowl) minBowl = bowl;
            if (rim > maxRim) maxRim = rim;
        }
        return (minBowl + maxRim) * gate;
    }

    /// <summary>
    /// <see cref="CraterField"/> at a fine and a coarse band-limit in one pass. The 3×3×3 cellular
    /// accumulation (the expensive part) is spacing-independent, so it's evaluated once per octave and
    /// only the LOD gate differs between the two cutoffs. The coarsest octave resolves first, so if even
    /// it is gated out at the coarse spacing the whole field is skipped (#6 per-patch early-out).
    /// </summary>
    private (double fine, double coarse) CraterField2(Vector3D<double> unitDir, double freqScale,
        double fineSpacing, double coarseSpacing)
    {
        double freq0 = _craterFreqA * 0.5;
        // Coarsest octave opens earliest; if it's dark for the coarser spacing, nothing finer survives.
        if (LayerGate(freq0 * freqScale, coarseSpacing) <= 0.0 && LayerGate(freq0 * freqScale, fineSpacing) <= 0.0)
            return (0.0, 0.0);

        double density = Math.Clamp(_craterDensity * PlanetTuning.EffectiveCraterDensity(Type), 0.0, 1.0);
        double freq = freq0, weight = 1.0, sumF = 0.0, sumC = 0.0, wsum = 0.0;
        for (int o = 0; o < CraterOctaves; o++)
        {
            (double lf, double lc) = CraterLayer2(unitDir, freq * freqScale, CraterSalt(o), fineSpacing, coarseSpacing, density);
            sumF += weight * lf; sumC += weight * lc; wsum += weight;
            freq *= CraterLacunarity; weight *= CraterDepthFalloff;
        }
        return (sumF / wsum, sumC / wsum);
    }

    /// <summary>One crater size class for two band-limits: the bowl/rim accumulation is computed once
    /// and scaled by each cutoff's LOD gate (see <see cref="CraterLayer"/>).</summary>
    private (double fine, double coarse) CraterLayer2(Vector3D<double> unitDir, double freq, ulong salt,
        double fineSpacing, double coarseSpacing, double density)
    {
        double gateF = LayerGate(freq, fineSpacing), gateC = LayerGate(freq, coarseSpacing);
        if (gateF <= 0.0 && gateC <= 0.0) return (0.0, 0.0);

        Vector3D<double> p = unitDir * freq;
        long xi = (long)Math.Floor(p.X), yi = (long)Math.Floor(p.Y), zi = (long)Math.Floor(p.Z);

        double minBowl = 0.0, maxRim = 0.0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            (double fx, double fy, double fz, double rand) = _noise.CellFeature(xi + dx, yi + dy, zi + dz, salt);
            if (rand > density) continue;
            double radius = 0.22 + 0.28 * Frac(rand * 7.3 + 0.19);

            double ex = p.X - fx, ey = p.Y - fy, ez = p.Z - fz;
            double t = Math.Sqrt(ex * ex + ey * ey + ez * ez) / radius;
            if (t >= 1.5) continue;

            double bowl = -(1.0 - Smoothstep(0.0, 0.85, Math.Min(t, 1.0)));
            double e = (t - 0.95) / 0.12;
            double rim = CraterRimFrac * Math.Exp(-0.5 * e * e);

            if (bowl < minBowl) minBowl = bowl;
            if (rim > maxRim) maxRim = rim;
        }
        double raw = minBowl + maxRim;
        return (raw * gateF, raw * gateC);
    }

    /// <summary>Smoothly fade a feature class in as the patch resolves it: 0 until a feature spans a
    /// few samples, 1 once it spans many. Continuous in spacing, so LOD never pops the craters in.</summary>
    private double LayerGate(double freq, double sampleSpacing)
    {
        if (sampleSpacing <= 0.0) return 1.0;
        double wavelength = 2.0 * Math.PI * Radius / freq; // size of one cellular feature (m)
        double samples = wavelength / sampleSpacing;       // samples across that feature
        return Smoothstep(4.0, 64.0, samples);
    }

    private static double Frac(double x) => x - Math.Floor(x);

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
    /// Mean surface albedo over the whole globe — the flat colour the body shows from far away.
    /// It is computed from the <em>same</em> per-direction logic the surface map bakes (water blue
    /// below the waterline, <see cref="ColorAt"/> otherwise), averaged over a Fibonacci-sphere
    /// sample. Feeding this to the distant sphere instead of the raw seeded tint keeps the far view
    /// matching the surface, so a cold ice/regolith moon no longer pops from brown to white when the
    /// terrain or baked map loads. Returns the seeded base colour for surfaceless gas/ice giants.
    /// Sampled at an orbital band-limit (no per-metre detail), matching what the far view resolves.
    /// </summary>
    public Vector3D<float> AverageAlbedo(int samples = 256)
    {
        if (!HasSurface) return _baseColor;

        double spacing = 2.0 * Math.PI * Radius / 1024.0; // ~map texel scale: average the orbital look
        double seaLevel = SeaLevel;
        // Water tones must mirror PlanetSurfaceMap.Bake so the gen-time mean matches the baked map.
        var shallow = new Vector3D<float>(0.20f, 0.55f, 0.62f);
        var deep = new Vector3D<float>(0.02f, 0.10f, 0.26f);

        var sum = new Vector3D<float>(0f, 0f, 0f);
        double ga = Math.PI * (3.0 - Math.Sqrt(5.0)); // golden angle → an even spiral over the sphere
        for (int i = 0; i < samples; i++)
        {
            double y = 1.0 - (i + 0.5) / samples * 2.0;        // +1 (north) → -1 (south)
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            double th = ga * i;
            var dir = new Vector3D<double>(Math.Cos(th) * r, y, Math.Sin(th) * r);

            double h = HeightAt(dir, spacing);
            Vector3D<float> c;
            if (_hasOcean && h < seaLevel)
            {
                float f = (float)Math.Clamp((seaLevel - h) / (Amplitude * 0.12 + 1.0), 0, 1);
                c = shallow + (deep - shallow) * f;
            }
            else
            {
                c = ColorAt(dir, h, 1.0, spacing); // slope = 1 (gentle ground dominates the visible area)
            }
            sum += c;
        }
        return sum * (1f / samples);
    }

    /// <summary>
    /// Surface colour from <b>climate</b> (temperature × moisture → biome), elevation and slope.
    /// <paramref name="dir"/> is the unit surface direction (drives latitude/temperature and the
    /// moisture field); <paramref name="slope"/> is cos(angle-from-vertical) — 1 on flats, → 0 on
    /// cliffs — so steep faces read as bare rock and snow settles on gentle ground / cold latitudes.
    /// </summary>
    public Vector3D<float> ColorAt(Vector3D<double> dir, double height, double slope, double sampleSpacing)
        => ColorCore(dir, height, slope, sampleSpacing, 0.0, craterKnown: false);

    /// <summary>As <see cref="ColorAt(Vector3D{double}, double, double, double)"/> but reusing a crater
    /// value already evaluated by <see cref="HeightAt2"/>, so the bake path samples the (expensive)
    /// crater field once per vertex for both geometry and albedo instead of twice.</summary>
    public Vector3D<float> ColorAt(Vector3D<double> dir, double height, double slope, double sampleSpacing, double craterValue)
        => ColorCore(dir, height, slope, sampleSpacing, craterValue, craterKnown: true);

    private Vector3D<float> ColorCore(Vector3D<double> dir, double height, double slope, double sampleSpacing,
        double craterValue, bool craterKnown)
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
                            LandBand(dir, height, amplitude, slope), Smoothstepf(0f, 1f, above));
        }

        Vector3D<float> land = LandBand(dir, height, amplitude, slope);
        if (!IsCratered) return land;
        // Airless cratered worlds get crater-floor/rim albedo and maria provinces over the bare land.
        double cr = craterKnown ? craterValue : CraterField(dir, PlanetTuning.EffectiveFrequency(Type), sampleSpacing);
        return RegolithAlbedo(land, dir, cr);
    }

    /// <summary>
    /// Airless-body albedo over the bare substrate: <b>dark crater floors</b> and <b>bright rims/
    /// ejecta</b> tracking the crater cascade, plus low-frequency <b>maria</b> provinces (darker
    /// basaltic plains). This is what makes a cratered moon read as cratered from orbit to the
    /// surface — relief alone goes flat on coarse far patches, but the maria term is low-frequency
    /// enough to survive there. Strengths are live (TerrainTuning); a colour change needs a rebuild.
    /// </summary>
    private Vector3D<float> RegolithAlbedo(Vector3D<float> col, Vector3D<double> dir, double crater)
    {
        double fs = PlanetTuning.EffectiveFrequency(Type);

        float ca = Math.Max(0f, TerrainTuning.CraterAlbedo);
        if (ca > 0f)
        {
            double cr = crater;                                            // [-1, rim] (precomputed by the caller)
            float dark = (float)Math.Max(0.0, -cr);                        // 0 at rim → 1 deep floor
            float bright = (float)(Math.Max(0.0, cr) / CraterRimFrac);     // 0 → 1 at the rim crest
            col *= 1f - 0.45f * ca * dark;                                 // darker, dust-pooled floors
            col *= 1f + 0.30f * ca * bright;                               // brighter rims/ejecta
        }

        float ms = Math.Max(0f, TerrainTuning.MariaStrength);
        if (ms > 0f)
        {
            var p = dir + new Vector3D<double>(23.7, 88.1, 4.3);           // decorrelate from other fields
            double m = _noise.Fbm(p, 4, _continentFreq * fs * 0.6, 2.0, 0.5); // low freq → reads from orbit
            float maria = (float)Smoothstep(0.08, 0.5, m);
            Vector3D<float> mare = new(col.X * 0.55f, col.Y * 0.56f, col.Z * 0.60f); // darker, faintly cooler
            col = Lerp(col, mare, maria * ms);
        }
        return col;
    }

    /// <summary>Climate + elevation + slope colour for dry land: a Whittaker biome ground colour,
    /// rising through rock to snow (by altitude or cold latitude), with bare cliff faces.</summary>
    private Vector3D<float> LandBand(Vector3D<double> dir, double height, double amplitude, double slope)
    {
        double temp = Temperature01(dir, height, amplitude);
        double moist = Moisture01(dir);
        Vector3D<float> ground = BiomeGround(temp, moist);

        Vector3D<float> rock = PlanetTuning.EffectiveRock(Type);
        Vector3D<float> snow = PlanetTuning.EffectiveSnow(Type);
        float snowLine = Math.Clamp(PlanetTuning.EffectiveSnowLine(Type), 0.02f, 0.98f);

        // Ground → rock as elevation climbs toward the snow line.
        float t = (float)Math.Clamp((height / amplitude + 0.3) / 1.3, 0f, 1f);
        Vector3D<float> band = Lerp(ground, rock, Smoothstepf(0f, snowLine, t));

        // Snow from EITHER high elevation OR cold latitude — so polar regions get caps at sea level
        // while the tropics only whiten on peaks.
        float coldSnow = 1f - Smoothstepf(0.06f, 0.24f, (float)temp);
        float elevSnow = Smoothstepf(snowLine, Math.Min(1f, snowLine + 0.22f), t);
        band = Lerp(band, snow, Math.Clamp(Math.Max(coldSnow, elevSnow), 0f, 1f));

        float c = Math.Clamp(PlanetTuning.EffectiveCliffThreshold(Type), 0.2f, 0.97f);

        // Scree / talus: loose gravel gathering on moderate slopes just gentler than bare cliffs —
        // a band peaking a little above the cliff threshold (in slope = cos-of-steepness terms).
        float scree = Smoothstepf(c - 0.04f, c + 0.10f, (float)slope) * (1f - Smoothstepf(c + 0.16f, c + 0.32f, (float)slope));
        Vector3D<float> screeColor = (PlanetTuning.EffectiveRock(Type) + PlanetTuning.EffectiveCliff(Type)) * 0.5f
                                     + new Vector3D<float>(0.05f, 0.045f, 0.04f);
        band = Lerp(band, screeColor, scree * 0.5f);

        // Steep faces are bare cliff rock; this is the main driver of close-up detail.
        float steep = 1f - Smoothstepf(c - 0.135f, c + 0.135f, (float)slope);
        return Lerp(band, PlanetTuning.EffectiveCliff(Type), steep * PlanetTuning.EffectiveCliffStrength(Type));
    }

    /// <summary>Point temperature in [0,1]: the planet's base warmth, cooled toward the poles
    /// (latitude from the surface direction) and with altitude.</summary>
    private double Temperature01(Vector3D<double> dir, double height, double amplitude)
    {
        double tBase = Math.Clamp((_surfaceTempK - 215.0) / (320.0 - 215.0), 0.0, 1.0);
        double lat = Math.Abs(dir.Y);                          // 0 at equator, 1 at the poles
        double elevAbove = Math.Max(0.0, height / amplitude);  // higher ground is colder
        return Math.Clamp(tBase - 0.55 * Math.Pow(lat, 1.3) - 0.55 * elevAbove, 0.0, 1.0);
    }

    /// <summary>Point moisture in [0,1] from a low-frequency field, biased wet/dry per world class.</summary>
    private double Moisture01(Vector3D<double> dir)
    {
        var p = dir + new Vector3D<double>(17.3, 5.9, 42.1); // decorrelate from continents/ruggedness
        double m = 0.5 + 0.5 * _noise.Fbm(p, 4, _moistureFreq, 2.0, 0.5);
        return Math.Clamp(m + _moistureBias, 0.0, 1.0);
    }

    /// <summary>Whittaker-style ground colour from temperature × moisture. The abiotic substrate
    /// (frozen → temperate rock/tint → hot sand) always applies; vegetation overlays it only where
    /// the world can support it (liquid water + atmosphere), and only in the warm, moist middle.</summary>
    private Vector3D<float> BiomeGround(double temp, double moist)
    {
        Vector3D<float> tint = PlanetTuning.EffectiveLowland(Type);
        Vector3D<float> temperate = new(_baseColor.X * tint.X, _baseColor.Y * tint.Y, _baseColor.Z * tint.Z);
        var frozen = new Vector3D<float>(0.78f, 0.82f, 0.86f); // bare cold ground
        var hot = new Vector3D<float>(0.80f, 0.62f, 0.40f);    // desert sand
        Vector3D<float> substrate = temp < 0.5
            ? Lerp(frozen, temperate, (float)(temp / 0.5))
            : Lerp(temperate, hot, (float)((temp - 0.5) / 0.5));

        if (!_hasLife) return substrate;

        var tundra = new Vector3D<float>(0.45f, 0.46f, 0.34f);
        var grass = new Vector3D<float>(0.48f, 0.56f, 0.28f);
        var forest = new Vector3D<float>(0.20f, 0.42f, 0.18f);
        var jungle = new Vector3D<float>(0.12f, 0.40f, 0.15f);
        Vector3D<float> veg;
        if (temp < 0.35) veg = Lerp(tundra, forest, (float)Smoothstep(0.15, 0.35, temp));
        else if (temp < 0.7) veg = Lerp(grass, forest, (float)moist);
        else veg = Lerp(substrate, jungle, (float)moist); // hot+dry stays desert, hot+wet turns jungle

        // Lushness needs warmth and moisture; frozen wastes and parched deserts stay bare substrate.
        double lush = Smoothstep(0.12, 0.35, temp) * Smoothstep(0.25, 0.6, moist);
        return Lerp(substrate, veg, (float)lush);
    }

    /// <summary>Backward-compatible overloads (full detail; treat the point as on the equator / flat ground).</summary>
    public Vector3D<float> ColorAt(Vector3D<double> dir, double height, double slope)
        => ColorAt(dir, height, slope, 0.0);
    public Vector3D<float> ColorAt(double height, double slope)
        => ColorAt(new Vector3D<double>(1.0, 0.0, 0.0), height, slope, 0.0);
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

    /// <summary>Quantise <paramref name="v"/>∈[-1,1] into <paramref name="steps"/> terraces: long flats
    /// with smoothly-ramped risers (a soft step, so normals stay finite). Stays in [-1,1].</summary>
    private static double Terrace(double v, int steps, double sharp)
    {
        double s = v * steps;
        double fl = Math.Floor(s);
        double frac = Math.Pow(s - fl, sharp);          // bias toward the flat tread
        double riser = frac * frac * (3.0 - 2.0 * frac); // smoothstep the rise → no vertical cliff
        return (fl + riser) / steps;
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
