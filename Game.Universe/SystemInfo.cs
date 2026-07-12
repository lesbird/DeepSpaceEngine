using Engine.Core;

namespace Game.Universe;

/// <summary>
/// A cheap, read-only snapshot of a star system produced by <see cref="SystemGenerator.Describe"/> —
/// enough to describe every planet and moon (type, orbit, size, ring/atmosphere flags) without paying
/// for the terrain, ring particles and composition that spawning a real <see cref="SolarSystem"/> costs.
/// The numbers match exactly what would spawn, since Describe walks the same RNG stream as Generate.
/// </summary>
public sealed class SystemInfo
{
    public readonly ulong StarId;
    public readonly string StarDesignation;
    public readonly PlanetInfo[] Planets;

    /// <summary>Gas giants only (<see cref="PlanetType.GasGiant"/>).</summary>
    public readonly int GasGiantCount;
    /// <summary>Ice giants only (<see cref="PlanetType.IceGiant"/>).</summary>
    public readonly int IceGiantCount;
    /// <summary>Total moons across every planet.</summary>
    public readonly int MoonCount;
    /// <summary>Planets carrying a ring system (giants and the occasional large rocky world).</summary>
    public readonly int RingedCount;

    public SystemInfo(ulong starId, string starDesignation, PlanetInfo[] planets,
        int gasGiantCount, int iceGiantCount, int moonCount, int ringedCount)
    {
        StarId = starId;
        StarDesignation = starDesignation;
        Planets = planets;
        GasGiantCount = gasGiantCount;
        IceGiantCount = iceGiantCount;
        MoonCount = moonCount;
        RingedCount = ringedCount;
    }

    public int PlanetCount => Planets.Length;
    /// <summary>Gas + ice giants.</summary>
    public int GiantCount => GasGiantCount + IceGiantCount;
}

/// <summary>One planet within a <see cref="SystemInfo"/> snapshot, plus its moons.</summary>
public sealed class PlanetInfo
{
    /// <summary>Stable per-planet id (equals the spawned <c>Planet.Seed</c>). Sequential within a star,
    /// so <see cref="SystemGenerator.TryGetPlanet"/> resolves it by walking the described system.</summary>
    public readonly ulong Id;
    public readonly int Index;               // 0-based position out from the star
    public readonly string Designation;
    public readonly PlanetType Type;
    public readonly double SemiMajorAxis;     // metres from the star
    public readonly double RadiusMeters;
    public readonly double MassKg;
    public readonly double MeanMotion;        // radians / second
    public readonly double Inclination, AscendingNode, Phase, AxialTilt; // radians
    public readonly bool HasRings;
    public readonly float SurfaceTempK;
    public readonly bool HasAtmosphere;
    public readonly float SurfacePressureBar;
    public readonly MoonInfo[] Moons;

    public PlanetInfo(ulong id, int index, string designation, PlanetType type, double semiMajorAxis,
        double radiusMeters, double massKg, double meanMotion, double inclination, double ascendingNode,
        double phase, double axialTilt, bool hasRings, float surfaceTempK, bool hasAtmosphere,
        float surfacePressureBar, MoonInfo[] moons)
    {
        Id = id;
        Index = index;
        Designation = designation;
        Type = type;
        SemiMajorAxis = semiMajorAxis;
        RadiusMeters = radiusMeters;
        MassKg = massKg;
        MeanMotion = meanMotion;
        Inclination = inclination;
        AscendingNode = ascendingNode;
        Phase = phase;
        AxialTilt = axialTilt;
        HasRings = hasRings;
        SurfaceTempK = surfaceTempK;
        HasAtmosphere = hasAtmosphere;
        SurfacePressureBar = surfacePressureBar;
        Moons = moons;
    }

    public bool IsGiant => Type is PlanetType.GasGiant or PlanetType.IceGiant;
    public int MoonCount => Moons.Length;
    public double SemiMajorAxisAu => SemiMajorAxis / MathUtil.AstronomicalUnit;

    internal static PlanetInfo From(Planet p, int index)
    {
        var moons = new MoonInfo[p.Moons.Length];
        for (int j = 0; j < moons.Length; j++) moons[j] = MoonInfo.From(p.Moons[j]);
        return new PlanetInfo(p.Seed, index, p.Designation, p.Type, p.SemiMajorAxis, p.RadiusMeters,
            p.MassKg, p.MeanMotion, p.Inclination, p.AscendingNode, p.Phase, p.AxialTilt, p.HasRings,
            p.SurfaceTempK, p.HasAtmosphere, p.SurfacePressureBar, moons);
    }
}

/// <summary>One moon within a <see cref="PlanetInfo"/> snapshot.</summary>
public sealed class MoonInfo
{
    public readonly ulong Id;                 // equals the spawned Moon.Seed
    public readonly string Designation;
    public readonly PlanetType Type;
    public readonly double SemiMajorAxis;     // metres from the parent planet
    public readonly double RadiusMeters;
    public readonly double MassKg;
    public readonly double MeanMotion;        // radians / second
    public readonly double Inclination, AscendingNode, Phase; // radians
    public readonly float SurfaceTempK;
    public readonly bool HasAtmosphere;
    public readonly float SurfacePressureBar;

    public MoonInfo(ulong id, string designation, PlanetType type, double semiMajorAxis,
        double radiusMeters, double massKg, double meanMotion, double inclination, double ascendingNode,
        double phase, float surfaceTempK, bool hasAtmosphere, float surfacePressureBar)
    {
        Id = id;
        Designation = designation;
        Type = type;
        SemiMajorAxis = semiMajorAxis;
        RadiusMeters = radiusMeters;
        MassKg = massKg;
        MeanMotion = meanMotion;
        Inclination = inclination;
        AscendingNode = ascendingNode;
        Phase = phase;
        SurfaceTempK = surfaceTempK;
        HasAtmosphere = hasAtmosphere;
        SurfacePressureBar = surfacePressureBar;
    }

    internal static MoonInfo From(Moon m) => new(m.Seed, m.Designation, m.Type, m.SemiMajorAxis,
        m.RadiusMeters, m.MassKg, m.MeanMotion, m.Inclination, m.AscendingNode, m.Phase,
        m.SurfaceTempK, m.HasAtmosphere, m.SurfacePressureBar);
}
