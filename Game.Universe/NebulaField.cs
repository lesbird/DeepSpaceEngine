using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>One procedurally-placed nebula: a large, soft, emissive gas cloud somewhere in the galactic
/// disk. Deliberately huge (tens of light-years) so it reads as a glowing landmark from far away and
/// envelops the many catalog stars that fall inside its volume.</summary>
public readonly struct Nebula
{
    public readonly UniversePosition Position; // centre, world
    public readonly double RadiusMeters;       // visual radius
    public readonly Vector3D<float> Color;     // emission tint
    public readonly float Seed;                // per-nebula noise offset (look variety)
    public readonly string Name;

    public Nebula(UniversePosition position, double radiusMeters, Vector3D<float> color, float seed, string name)
    {
        Position = position;
        RadiusMeters = radiusMeters;
        Color = color;
        Seed = seed;
        Name = name;
    }
}

/// <summary>
/// Deterministic set of nebulae scattered through one galaxy's inner disk, seeded from that galaxy so
/// every galaxy has its own nebulae (not just the home one). Few and enormous, so they are held as a
/// flat list (not paged) and tested each frame. Rebuilt when you enter a different galaxy; an empty
/// field is used in intergalactic space.
/// </summary>
public sealed class NebulaField
{
    // Emission palette: H-alpha reds/magentas, reflection blues, O-III teals, dusty golds. One is picked and
    // jittered per nebula so the field has variety without looking random-confetti.
    private static readonly Vector3D<float>[] Palette =
    {
        new(1.00f, 0.32f, 0.42f), // hydrogen red / rose
        new(0.95f, 0.40f, 0.85f), // magenta
        new(0.40f, 0.55f, 1.00f), // reflection blue
        new(0.35f, 0.85f, 0.80f), // teal / O-III
        new(1.00f, 0.70f, 0.35f), // dusty gold
        new(0.65f, 0.45f, 0.95f), // violet
    };

    private readonly Nebula[] _nebulae;
    public IReadOnlyList<Nebula> Nebulae => _nebulae;

    /// <summary>An empty field — intergalactic space has no nebulae.</summary>
    public NebulaField() => _nebulae = Array.Empty<Nebula>();

    /// <summary>Place nebulae through <paramref name="galaxy"/>'s inner disk, in its own frame (centre +
    /// disk orientation), seeded from the galaxy so each galaxy's nebulae are distinct.</summary>
    public NebulaField(Galaxy galaxy, int count = -1)
    {
        if (count < 0) count = Math.Max(0, NebulaTuning.Count);
        // Tolerate sliders set in either order; never let the RNG range invert.
        double minR = Math.Max(1.0, Math.Min(NebulaTuning.MinRadiusLy, NebulaTuning.MaxRadiusLy));
        double maxR = Math.Max(minR, Math.Max(NebulaTuning.MinRadiusLy, NebulaTuning.MaxRadiusLy));
        var rng = new DeterministicRng(Hashing.Combine(galaxy.Seed, 0xEB01AUL));
        double ly = MathUtil.LightYear;

        // Scale the placement lens to the galaxy: nebulae sit in the inner disk (~12% of the radius) with
        // a thin vertical spread. For a 50,000 ly galaxy this reproduces the original 6000/1800 ly bounds.
        double diskRadiusLy = galaxy.RadiusLy * 0.12;
        double diskHeightLy = galaxy.RadiusLy * 0.036;
        Basis(galaxy.DiskNormal, out Vector3D<double> u, out Vector3D<double> v, out Vector3D<double> n);

        _nebulae = new Nebula[count];
        for (int i = 0; i < count; i++)
        {
            double ang = rng.Range(0.0, 2.0 * Math.PI);
            double rad = diskRadiusLy * Math.Sqrt(rng.Range(0.0, 1.0)); // sqrt → uniform over the disk area
            // Vertical spread: a signed sample with a mild power bias toward the plane.
            double s = rng.Range(-1.0, 1.0);
            double height = Math.Sign(s) * Math.Pow(Math.Abs(s), 1.5) * diskHeightLy;
            Vector3D<double> offset =
                (u * (Math.Cos(ang) * rad) + v * (Math.Sin(ang) * rad) + n * height) * ly;
            UniversePosition pos = galaxy.Center.Translated(offset);

            double radius = rng.Range(minR, maxR) * ly;

            Vector3D<float> baseCol = Palette[rng.RangeInt(0, Palette.Length)];
            float jitter = (float)rng.Range(0.85, 1.15);
            var color = new Vector3D<float>(
                Math.Clamp(baseCol.X * jitter, 0.05f, 1f),
                Math.Clamp(baseCol.Y * jitter, 0.05f, 1f),
                Math.Clamp(baseCol.Z * jitter, 0.05f, 1f));

            _nebulae[i] = new Nebula(pos, radius, color, (float)rng.Range(0.0, 1000.0), $"Nebula N-{i + 1:000}");
        }
    }

    /// <summary>Orthonormal basis (u, v in the disk plane, n the normal) for a galaxy's disk normal.</summary>
    private static void Basis(Vector3D<float> normal, out Vector3D<double> u, out Vector3D<double> v, out Vector3D<double> n)
    {
        var nn = new Vector3D<double>(normal.X, normal.Y, normal.Z);
        nn = nn.LengthSquared < 1e-12 ? new Vector3D<double>(0, 1, 0) : Vector3D.Normalize(nn);
        Vector3D<double> a = Math.Abs(nn.Y) < 0.99 ? new Vector3D<double>(0, 1, 0) : new Vector3D<double>(1, 0, 0);
        u = Vector3D.Normalize(Vector3D.Cross(a, nn));
        v = Vector3D.Cross(nn, u);
        n = nn;
    }
}
