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
/// Deterministic set of nebulae scattered through the galactic disk, generated once from the world seed.
/// They are few and enormous, so unlike the star catalog they are held as a flat list (not paged): a few
/// hundred entries is trivial to keep resident and to test against each frame.
/// </summary>
public sealed class NebulaField
{
    // Galactic placement bounds (light-years). Nebulae scatter within DiskRadius of the centre, with a
    // generous vertical spread so they fill a thick lens above and below the plane rather than sitting on it.
    private const double DiskRadiusLy = 6000.0;
    private const double DiskHeightLy = 1800.0;

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

    public NebulaField(ulong worldSeed, int count = 140)
    {
        var rng = new DeterministicRng(Hashing.Combine(worldSeed, 0xEB01AUL));
        double ly = MathUtil.LightYear;
        _nebulae = new Nebula[count];
        for (int i = 0; i < count; i++)
        {
            double ang = rng.Range(0.0, 2.0 * Math.PI);
            double rad = DiskRadiusLy * Math.Sqrt(rng.Range(0.0, 1.0)); // sqrt → uniform over the disk area
            // Vertical spread: a single signed sample with a mild power bias toward the plane, so nebulae
            // populate the full ±DiskHeight extent up and down instead of clustering on it.
            double u = rng.Range(-1.0, 1.0);
            double height = Math.Sign(u) * Math.Pow(Math.Abs(u), 1.5) * DiskHeightLy;
            var pos = UniversePosition.FromMeters(
                Math.Cos(ang) * rad * ly, height * ly, Math.Sin(ang) * rad * ly);

            double radius = rng.Range(30.0, 140.0) * ly;

            Vector3D<float> baseCol = Palette[rng.RangeInt(0, Palette.Length)];
            float jitter = (float)rng.Range(0.85, 1.15);
            var color = new Vector3D<float>(
                Math.Clamp(baseCol.X * jitter, 0.05f, 1f),
                Math.Clamp(baseCol.Y * jitter, 0.05f, 1f),
                Math.Clamp(baseCol.Z * jitter, 0.05f, 1f));

            _nebulae[i] = new Nebula(pos, radius, color, (float)rng.Range(0.0, 1000.0), $"Nebula N-{i + 1:000}");
        }
    }
}
