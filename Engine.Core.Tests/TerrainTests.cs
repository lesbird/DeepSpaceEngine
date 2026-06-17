using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class TerrainTests
{
    private static Planet RockyPlanet()
    {
        var field = new StarField(new GalaxyModel(31337));
        field.Update(UniversePosition.Origin, 8);
        SolarSystem sys = SystemGenerator.Generate(field.Nearest);
        foreach (Planet p in sys.Planets)
            if (p.Type is not (PlanetType.GasGiant or PlanetType.IceGiant))
                return p;
        return sys.Planets[0];
    }

    [Fact]
    public void Noise_IsDeterministicAndBounded()
    {
        var a = new Noise(123);
        var b = new Noise(123);
        var p = new Vector3D<double>(0.3, -0.7, 1.1);
        double va = a.Fbm(p, 8, 2.0, 2.0, 0.5);
        double vb = b.Fbm(p, 8, 2.0, 2.0, 0.5);
        Assert.Equal(va, vb);
        Assert.InRange(va, -1.0, 1.0);
    }

    [Fact]
    public void ValueD_GradientMatchesFiniteDifference()
    {
        var n = new Noise(7);
        double x = 0.31, y = -0.42, z = 1.17, e = 1e-5;
        (double v, Vector3D<double> g) = n.ValueD(x, y, z);

        Assert.InRange(v, -1.0, 1.0);
        double gx = (n.Value(x + e, y, z) - n.Value(x - e, y, z)) / (2 * e);
        double gy = (n.Value(x, y + e, z) - n.Value(x, y - e, z)) / (2 * e);
        double gz = (n.Value(x, y, z + e) - n.Value(x, y, z - e)) / (2 * e);
        Assert.True(Math.Abs(gx - g.X) < 1e-3, $"∂x mismatch {gx} vs {g.X}");
        Assert.True(Math.Abs(gy - g.Y) < 1e-3, $"∂y mismatch {gy} vs {g.Y}");
        Assert.True(Math.Abs(gz - g.Z) < 1e-3, $"∂z mismatch {gz} vs {g.Z}");
    }

    [Fact]
    public void ErodedFbm_IsBoundedAndDeterministic()
    {
        var a = new Noise(99);
        var b = new Noise(99);
        foreach (var p in new[]
        {
            new Vector3D<double>(0.2, 0.9, -0.3),
            new Vector3D<double>(-1.1, 0.4, 2.3),
            new Vector3D<double>(5.0, -3.2, 0.7),
        })
        {
            double va = a.ErodedFbm(p, 8.0, 3.0, 2.0, 0.5);
            double vb = b.ErodedFbm(p, 8.0, 3.0, 2.0, 0.5);
            Assert.Equal(va, vb);                 // deterministic
            Assert.InRange(va, -1.0, 1.0);        // normalised → preserves amplitude bounds
            // Fractional octave count stays bounded too (LOD fade).
            Assert.InRange(a.ErodedFbm(p, 5.4, 3.0, 2.0, 0.5), -1.0, 1.0);
        }
    }

    [Fact]
    public void Terrain_HeightIsDeterministicAndWithinAmplitude()
    {
        Planet planet = RockyPlanet();
        var t1 = new PlanetTerrain(planet);
        var t2 = new PlanetTerrain(planet);

        var dir = Vector3D.Normalize(new Vector3D<double>(0.2, 0.9, -0.3));
        double h1 = t1.HeightAt(dir);
        double h2 = t2.HeightAt(dir);

        Assert.Equal(h1, h2);                       // deterministic
        Assert.True(Math.Abs(h1) <= t1.Amplitude + 1e-6); // bounded by amplitude
    }

    [Fact]
    public void Height_IsContinuousAcrossLodSpacing()
    {
        // The whole point of fractional-octave band-limiting: as a patch subdivides, its sample
        // spacing halves and detail must fade in SMOOTHLY. Sweeping the spacing across the octave
        // boundaries must never produce a sudden jump in height (which is what made the surface
        // visibly "rebuild" itself with the old integer octave count).
        Planet planet = RockyPlanet();
        var terrain = new PlanetTerrain(planet);
        var dir = Vector3D.Normalize(new Vector3D<double>(0.2, 0.9, -0.3));

        // Geometric sweep from coarse (km-scale spacing) down to fine (sub-metre), like flying in.
        double prev = terrain.HeightAt(dir, 5000.0);
        double maxStep = 0.0;
        double s = 5000.0;
        for (int i = 0; i < 400; i++)
        {
            s *= 0.97; // ~0.5 m by the end
            double h = terrain.HeightAt(dir, s);
            maxStep = Math.Max(maxStep, Math.Abs(h - prev));
            prev = h;
        }

        // No single fine step should move the surface by more than a tiny fraction of the relief.
        // (The old whole-octave version would jump by several percent at each octave boundary.)
        Assert.True(maxStep < 0.01 * terrain.Amplitude,
            $"max height step {maxStep:0.###} exceeded 1% of amplitude {terrain.Amplitude:0.###}");
    }

    [Fact]
    public void AllStyles_StayBoundedAndContinuous()
    {
        // Sweep every surfaced body across many systems so the new terrain styles — including the
        // crater fields — are all exercised, and confirm the two invariants the renderer relies on:
        // heights stay within Amplitude (horizon culling / skirts) and never pop across an LOD step.
        var dirs = new[]
        {
            Vector3D.Normalize(new Vector3D<double>(0.2, 0.9, -0.3)),
            Vector3D.Normalize(new Vector3D<double>(-0.7, 0.1, 0.6)),
            Vector3D.Normalize(new Vector3D<double>(0.5, -0.5, 0.5)),
        };

        int bodies = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, 8);
            if (!field.HasNearest) continue;

            foreach (CelestialBody b in SystemGenerator.Generate(field.Nearest).AllBodies())
            {
                if (!b.HasSurface) continue;
                bodies++;
                var t = new PlanetTerrain(b);
                foreach (var dir in dirs)
                {
                    Assert.True(Math.Abs(t.HeightAt(dir)) <= t.Amplitude + 1e-6, "height exceeded amplitude");

                    double prev = t.HeightAt(dir, 5000.0), s = 5000.0;
                    for (int i = 0; i < 400; i++)
                    {
                        s *= 0.97;
                        double h = t.HeightAt(dir, s);
                        Assert.True(Math.Abs(h - prev) < 0.01 * t.Amplitude, "LOD height step popped");
                        prev = h;
                    }
                }
            }
        }
        Assert.True(bodies > 50, "expected to sweep many surfaced bodies");
    }

    [Fact]
    public void HeightAt2_MatchesTwoSeparateHeightCalls()
    {
        // The bake path samples each grid point once with HeightAt2 (fine + parent-coarse in one pass,
        // sharing the lattice work). That optimisation is only valid if it reproduces what two separate
        // HeightAt calls would have produced. Sweep every surfaced body across many systems — so the
        // crater/erosion/ridge layers are all exercised — at the real coarse = 2×fine spacing the baker
        // uses, and confirm both outputs match (to a sub-millimetre tolerance that swamps the only
        // difference: float associativity in the ridged layer's single fractional top octave).
        var dirs = new[]
        {
            Vector3D.Normalize(new Vector3D<double>(0.2, 0.9, -0.3)),
            Vector3D.Normalize(new Vector3D<double>(-0.7, 0.1, 0.6)),
            Vector3D.Normalize(new Vector3D<double>(0.5, -0.5, 0.5)),
        };
        double[] fineSpacings = { 4000.0, 250.0, 12.0, 0.5 };

        int bodies = 0, cratered = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, 8);
            if (!field.HasNearest) continue;

            foreach (CelestialBody b in SystemGenerator.Generate(field.Nearest).AllBodies())
            {
                if (!b.HasSurface) continue;
                bodies++;
                var t = new PlanetTerrain(b);
                if (t.IsCratered) cratered++;
                // Tight relative tolerance: the only legitimate difference is float associativity in the
                // ridged layer's single fractional top octave (~1e-15 relative), so 1e-9·amplitude has
                // enormous margin while still catching a real divergence of even a few centimetres.
                double tol = 1e-9 * t.Amplitude + 1e-6;

                foreach (var dir in dirs)
                foreach (double fine in fineSpacings)
                {
                    double coarse = fine * 2.0;
                    t.HeightAt2(dir, fine, coarse, out double hF, out double hC, out double craterF);

                    Assert.True(Math.Abs(hF - t.HeightAt(dir, fine)) <= tol, "HeightAt2 fine diverged");
                    Assert.True(Math.Abs(hC - t.HeightAt(dir, coarse)) <= tol, "HeightAt2 coarse diverged");

                    // The reused crater value must drive exactly the same albedo as recomputing it.
                    Vector3D<float> reused = t.ColorAt(dir, hF, 1.0, fine, craterF);
                    Vector3D<float> fresh = t.ColorAt(dir, hF, 1.0, fine);
                    Assert.True((reused - fresh).Length < 1e-5f, "crater reuse changed the colour");
                }
            }
        }
        Assert.True(bodies > 50, "expected to sweep many surfaced bodies");
        Assert.True(cratered > 0, "expected at least one cratered world in the sweep");
    }

    [Fact]
    public void Terrain_StylesProduceDiverseRelief()
    {
        // Guard against the "every world is the same hills" regression: across many worlds the
        // overall relief (Amplitude) should span a wide range, reflecting the different styles.
        double min = double.MaxValue, max = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, 8);
            if (!field.HasNearest) continue;

            foreach (CelestialBody b in SystemGenerator.Generate(field.Nearest).AllBodies())
            {
                if (!b.HasSurface) continue;
                double reliefFrac = new PlanetTerrain(b).Amplitude / b.RadiusMeters;
                min = Math.Min(min, reliefFrac);
                max = Math.Max(max, reliefFrac);
            }
        }
        // Flattest worlds should be markedly flatter than the most rugged ones (≥ 2× spread).
        Assert.True(max > min * 2.0, $"relief spread too narrow: {min:0.0000}..{max:0.0000}");
    }

    [Fact]
    public void GasGiants_HaveNoSurface()
    {
        var field = new StarField(new GalaxyModel(555));
        field.Update(UniversePosition.Origin, 8);
        SolarSystem sys = SystemGenerator.Generate(field.Nearest);

        foreach (Planet p in sys.Planets)
        {
            var terrain = new PlanetTerrain(p);
            bool isGiant = p.Type is PlanetType.GasGiant or PlanetType.IceGiant;
            Assert.Equal(!isGiant, terrain.HasSurface);
            if (isGiant)
                Assert.Equal(0.0, terrain.HeightAt(Vector3D<double>.UnitY));
        }
    }
}
