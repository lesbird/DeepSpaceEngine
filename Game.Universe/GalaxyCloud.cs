using Engine.Core;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// A coarse volumetric point-cloud representation of a galaxy — the LOD tier between the flat impostor
/// disk and the fully-resolved star catalog. ~10⁵ sample "stars" are distributed by the galaxy's
/// density model (exponential disk + spiral arms + a spheroidal central bulge), giving the galaxy real
/// 3D structure with parallax as you fly in. Points are baked once as offsets from the galaxy centre
/// (metres) plus a colour and a point size; the renderer positions them camera-relative each frame.
/// Deterministic from the galaxy seed.
/// </summary>
public static class GalaxyCloud
{
    public const int FloatsPerPoint = 7; // offset(3) + colour(3) + size(1)

    /// <summary>Generate <paramref name="count"/> interleaved cloud points for <paramref name="g"/>.</summary>
    public static float[] Generate(Galaxy g, int count)
    {
        GalaxyModel m = GalaxyModel.For(g);
        Basis(g.DiskNormal, out Vector3D<double> u, out Vector3D<double> v, out Vector3D<double> n);

        double ly = MathUtil.LightYear;
        double rMax = g.RadiusLy * ly;
        double scaleLen = Math.Max(m.ScaleLength, 1.0) * ly;
        double scaleHt = Math.Max(m.ScaleHeight, 1.0) * ly;
        double truncR = 1.0 - Math.Exp(-rMax / scaleLen); // truncate the exponential disk at the radius
        double bulgeR = 0.18 * rMax;

        bool spiral = m.ArmCount > 0 && m.ArmStrength > 0;
        double armPeak = 1.0 + m.ArmStrength;

        var rng = new DeterministicRng(Hashing.Combine(g.Seed, 0xC10D5UL)); // "CLOUDS"
        var data = new float[count * FloatsPerPoint];

        for (int i = 0; i < count; i++)
        {
            Vector3D<double> offset;
            bool bulge = rng.NextDouble() < 0.16;
            if (bulge)
            {
                // Spheroidal central bulge: isotropic direction, radius concentrated toward the centre.
                double br = bulgeR * Math.Sqrt(rng.NextDouble());
                double z = rng.Range(-1, 1), ph = rng.Range(0, 2 * Math.PI);
                double rp = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                offset = (u * (rp * Math.Cos(ph)) + v * (rp * Math.Sin(ph)) + n * z) * br;
            }
            else
            {
                // Exponential disk radius + exponential height, angle biased toward the spiral arms.
                double rr = -scaleLen * Math.Log(1.0 - rng.NextDouble() * truncR);
                double hh = -scaleHt * Math.Log(1.0 - rng.NextDouble());
                if (rng.NextDouble() < 0.5) hh = -hh;
                double phi = SampleAngle(ref rng, m, rr / ly, spiral, armPeak);
                offset = u * (rr * Math.Cos(phi)) + v * (rr * Math.Sin(phi)) + n * hh;
            }

            // Colour: the galaxy tint, with a warmer/brighter bulge and per-point brightness jitter.
            Vector3D<float> col = g.Color;
            if (bulge) col = new Vector3D<float>(Math.Min(1f, col.X * 1.15f), col.Y * 1.02f, col.Z * 0.9f);
            float jitter = 0.7f + 0.6f * rng.NextFloat();

            int o = i * FloatsPerPoint;
            data[o + 0] = (float)offset.X; data[o + 1] = (float)offset.Y; data[o + 2] = (float)offset.Z;
            data[o + 3] = col.X * jitter; data[o + 4] = col.Y * jitter; data[o + 5] = col.Z * jitter;
            data[o + 6] = bulge ? 2.0f : 1.4f;
        }
        return data;
    }

    /// <summary>Sample a disk angle, 1-D-rejection-biased toward the spiral arm crests (or uniform if armless).</summary>
    private static double SampleAngle(ref DeterministicRng rng, GalaxyModel m, double rLy, bool spiral, double armPeak)
    {
        if (!spiral) return rng.Range(0, 2 * Math.PI);
        for (int t = 0; t < 8; t++) // bounded; falls back to uniform if it never accepts
        {
            double phi = rng.Range(0, 2 * Math.PI);
            double arm = 1.0 + m.ArmStrength * Math.Cos(m.ArmCount * phi - m.ArmWind * Math.Log(rLy + 1.0));
            if (rng.NextDouble() * armPeak <= arm) return phi;
        }
        return rng.Range(0, 2 * Math.PI);
    }

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
