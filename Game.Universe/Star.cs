using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>Spectral class buckets, ordered cool → hot.</summary>
public enum SpectralClass { M, K, G, F, A, B, O }

/// <summary>
/// A procedurally generated star. Every field is derived deterministically from the
/// star's <see cref="Id"/> (and its cell), so a star is never stored — only regenerated.
/// </summary>
public readonly struct Star
{
    public readonly ulong Id;
    public readonly UniversePosition Position;
    public readonly float Temperature;        // Kelvin
    public readonly float Luminosity;         // relative to the Sun (visual approximation)
    public readonly float MassSolar;          // relative to the Sun
    public readonly double RadiusMeters;      // physical radius
    public readonly SpectralClass Class;
    public readonly Vector3D<float> Color;

    public Star(ulong id, UniversePosition position, float temperature, float luminosity,
        float massSolar, double radiusMeters, SpectralClass cls)
    {
        Id = id;
        Position = position;
        Temperature = temperature;
        Luminosity = luminosity;
        MassSolar = massSolar;
        RadiusMeters = radiusMeters;
        Class = cls;
        Color = Blackbody.ColorOf(temperature);
    }

    /// <summary>A radius cue for point-sprite sizing (grows slowly with luminosity).</summary>
    public float SizeCue => MathF.Sqrt(MathF.Max(Luminosity, 0.0005f));

    public char ClassLetter => "MKGFABO"[(int)Class];

    /// <summary>Catalog designation: the star's catalog index in decimal (the number the player
    /// searches for). Planets/moons extend this as star-planet[-moon].</summary>
    public string Designation => Id.ToString();

    /// <summary>A flavourful proper name derived deterministically from <see cref="Id"/> (e.g. "Helix
    /// Prime", "Crimson Vega"). Not guaranteed unique across the catalog — the <see cref="Id"/> is the
    /// unique handle; the name is for display. See <see cref="Naming.StarName"/>.</summary>
    public string Name => Naming.StarName(Id);
}
