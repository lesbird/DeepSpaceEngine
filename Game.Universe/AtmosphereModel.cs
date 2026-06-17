using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Turns a body's <b>scanned composition</b> (plus its temperature, gravity and surface pressure)
/// into the optical parameters the <c>AtmosphereRenderer</c> draws, so the sky you see reflects the
/// chemistry the scanner reports rather than a per-type colour constant. Three physical links:
///
/// <list type="bullet">
/// <item><b>Thickness</b> — the scale height <c>H = R·T / (M̄·g)</c> (mean molar mass M̄ from the
///   mix). Heavy gases (CO₂) sit lower and tighter; light H₂ puffs up.</item>
/// <item><b>Sky colour</b> — Rayleigh scattering is λ⁻⁴ for any clear gas (so a clear sky is blue
///   whatever the gas); composition sets the overall <i>strength</i> (∝ refractivity², CO₂ ≈ 2.4×
///   air) and an <i>absorption tint</i> from coloured gases (methane eats red → cyan skies).</item>
/// <item><b>Aerosols</b> — haze gases (sulphur dioxide) and suspended surface dust (iron-oxide red,
///   silica tan) add a Mie term whose colour is where the tan/yellow non-blue skies come from.</item>
/// </list>
/// </summary>
public static class AtmosphereModel
{
    private const double GasConstant = 8.314;      // J/(mol·K)
    private const double AirRefractivity = 292.0;  // (n−1)×10⁶ for dry air — the reference strength
    // Real scale height as a fraction of radius is tiny (~0.001 on Earth); exaggerate it so the limb
    // haze actually reads from space, while preserving the relative differences between worlds.
    private const double ShellExaggeration = 26.0;
    private const float MinShell = 0.012f, MaxShell = 0.10f;

    private static readonly Vector3D<float> White = new(1f, 1f, 1f);

    /// <summary>Per-gas optical properties. Absorption is a per-channel coefficient (0 = clear);
    /// the visible tint is exp(−Σ fraction·coeff). Aerosol is the gas's haze contribution.</summary>
    private readonly record struct Gas(double MolarMass, double Refractivity, Vector3D<float> Absorb, double Aerosol);

    private static Gas GasFor(string name) => name switch
    {
        "Nitrogen"        => new(28.0, 298, default, 0.0),
        "Oxygen"          => new(32.0, 271, default, 0.0),
        "Argon"           => new(40.0, 281, default, 0.0),
        "Carbon dioxide"  => new(44.0, 449, default, 0.0),                         // clear, but a strong scatterer
        "Water vapour"    => new(18.0, 256, new(0f, 0f, 0.04f), 0.15),             // faint blue absorb + light haze
        "Methane"         => new(16.0, 444, new(2.2f, 0.6f, 0.05f), 0.0),          // eats red → cyan (Neptune)
        "Ammonia"         => new(17.0, 375, new(0.4f, 0.15f, 0.6f), 0.25),
        "Sulphur dioxide" => new(64.0, 686, new(0f, 0.15f, 0.7f), 0.9),            // eats blue → yellow + haze
        "Hydrogen"        => new(2.0, 136, default, 0.0),
        "Helium"          => new(4.0, 35, default, 0.0),
        _                 => new(29.0, 292, default, 0.0),
    };

    /// <summary>How much each surface material lofts as dust, and the colour of that dust. Drives the
    /// Mie haze that turns a clear blue sky tan/ochre (the Martian look comes from dust, not CO₂).</summary>
    private static (float Dustiness, Vector3D<float> Color) SurfaceDust(string name) => name switch
    {
        "Iron oxide"    => (1.0f, new(0.62f, 0.34f, 0.22f)),  // rusty red
        "Silica sand"   => (0.8f, new(0.80f, 0.72f, 0.55f)),  // tan
        "Sand"          => (0.8f, new(0.82f, 0.74f, 0.56f)),
        "Gypsum"        => (0.5f, new(0.88f, 0.86f, 0.80f)),  // pale
        "Sulphur"       => (0.7f, new(0.85f, 0.78f, 0.35f)),  // yellow
        "Regolith"      => (0.6f, new(0.60f, 0.57f, 0.52f)),  // grey dust
        "Clay"          => (0.4f, new(0.70f, 0.58f, 0.45f)),
        "Basalt"        => (0.2f, new(0.45f, 0.43f, 0.42f)),
        "Obsidian"      => (0.15f, new(0.35f, 0.34f, 0.36f)),
        _               => (0.0f, White),
    };

    /// <summary>
    /// Compute and store the atmosphere's optical look on <paramref name="body"/> from its already-set
    /// composition, temperature, gravity and <see cref="CelestialBody.SurfacePressureBar"/>. No-op for
    /// airless bodies. Gas/ice giants (no solid surface) keep a generous artistic shell since their
    /// "atmosphere thickness" isn't a thin layer over a surface.
    /// </summary>
    public static void Derive(CelestialBody body)
    {
        if (!body.HasAtmosphere || body.AtmosphereComposition.Length == 0) return;

        double tempK = Math.Max(30.0, body.SurfaceTempK);
        double g = Math.Clamp(body.SurfaceGravity, 0.3, 40.0);
        double pressure = Math.Max(1e-4, body.SurfacePressureBar);

        // --- mean molar mass → scale height → visual shell thickness ---
        double meanMolar = 0.0, rayleighRel = 0.0, aerosolGas = 0.0;
        double absR = 0.0, absG = 0.0, absB = 0.0;
        Vector3D<float> hazeColor = default; double hazeWeight = 0.0;
        foreach (Constituent c in body.AtmosphereComposition)
        {
            Gas gas = GasFor(c.Name);
            double f = c.Fraction;
            meanMolar += f * gas.MolarMass;
            double rr = gas.Refractivity / AirRefractivity;
            rayleighRel += f * rr * rr;                       // scattering strength ∝ refractivity²
            absR += f * gas.Absorb.X; absG += f * gas.Absorb.Y; absB += f * gas.Absorb.Z;
            if (gas.Aerosol > 0.0)
            {
                aerosolGas += f * gas.Aerosol;
                // Sulphurous haze reads yellow; generic haze (water) stays near-white.
                Vector3D<float> hc = c.Name == "Sulphur dioxide" ? new(0.92f, 0.86f, 0.45f) : new(0.9f, 0.92f, 0.95f);
                hazeColor += hc * (float)(f * gas.Aerosol); hazeWeight += f * gas.Aerosol;
            }
        }
        if (meanMolar <= 0) meanMolar = 29.0;

        if (body.HasSurface)
        {
            double scaleHeight = GasConstant * tempK / ((meanMolar / 1000.0) * g);   // metres
            float frac = (float)(scaleHeight / body.RadiusMeters * ShellExaggeration);
            body.AtmosphereHeight = Math.Clamp(frac, MinShell, MaxShell);
        }
        else
        {
            // All-atmosphere giants: render a generous limb, nudged by molar mass (H₂ puffs up).
            body.AtmosphereHeight = Math.Clamp((float)(0.055 * (29.0 / meanMolar) * 0.5 + 0.04), 0.04f, 0.10f);
        }

        // --- Rayleigh strength (optical depth) and absorption tint ---
        // Thicker air (pressure) and stronger scatterers (refractivity²) → denser blue.
        body.AtmosphereDensity = (float)Math.Clamp(Math.Pow(pressure, 0.35) * rayleighRel, 0.12, 3.0);
        body.AtmosphereColor = new Vector3D<float>(
            (float)Math.Exp(-absR), (float)Math.Exp(-absG), (float)Math.Exp(-absB)); // ≈white clear, cyan methane

        // --- aerosols: haze gases + suspended surface dust ---
        double dustFrac = 0.0; Vector3D<float> dustColor = default;
        foreach (Constituent c in body.SurfaceComposition)
        {
            (float dustiness, Vector3D<float> col) = SurfaceDust(c.Name);
            double d = c.Fraction * dustiness;
            dustFrac += d; dustColor += col * (float)d;
        }

        double mie = aerosolGas + dustFrac * 1.2;
        body.MieDensity = (float)Math.Clamp(mie * Math.Pow(pressure, 0.2), 0.0, 2.0);

        Vector3D<float> aerosolColor = White * 0.0001f; double w = 0.0001;
        if (hazeWeight > 0) { aerosolColor += hazeColor; w += hazeWeight; }
        if (dustFrac > 0) { aerosolColor += dustColor; w += dustFrac; }
        aerosolColor *= (float)(1.0 / w);
        body.MieColor = w > 0.001 ? aerosolColor : White;
    }
}
