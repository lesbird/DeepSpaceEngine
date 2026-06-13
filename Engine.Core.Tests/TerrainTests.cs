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
