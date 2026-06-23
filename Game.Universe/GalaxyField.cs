using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// The large-scale structure of the universe: how many galaxies per unit volume sit at a given
/// position. The universe-level analogue of <see cref="GalaxyModel"/> (which describes one galaxy's
/// internal stellar density). For now the field is uniform; the cosmic web — filaments, clusters and
/// voids — is a later refinement, so the <see cref="DensityAt"/> API is in place now and callers won't
/// change when that structure lands.
/// </summary>
public sealed class GalaxyField
{
    public ulong WorldSeed { get; }

    /// <summary>
    /// Mean number of luminous galaxies per cubic light-year. The default ~5.8e-22 gives a mean
    /// separation of ~12 million ly — i.e. galaxies sit millions of light-years apart (true scale).
    /// </summary>
    public double Density = 5.8e-22;

    public GalaxyField(ulong worldSeed) => WorldSeed = worldSeed;

    /// <summary>Galaxies per cubic light-year at the given absolute position (metres). Uniform for now.</summary>
    public double DensityAt(Vector3D<double> meters) => Density;
}
