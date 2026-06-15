using System.Collections.Generic;
using Engine.Core;
using Game.Systems;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class AsteroidTests
{
    /// <summary>Find a system that rolled an asteroid belt, sweeping seeds until one appears.</summary>
    private static SolarSystem BeltedSystem(out Star star)
    {
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            foreach (Star s in field.Visible)
            {
                SolarSystem sys = SystemGenerator.Generate(s);
                if (sys.Belt != null) { star = s; return sys; }
            }
        }
        throw new Xunit.Sdk.XunitException("no belted system found in the sample");
    }

    private static List<AsteroidInstance> Collect(AsteroidBelt belt, UniversePosition star, double t, double maxDist)
    {
        var outp = new List<AsteroidInstance>();
        belt.Collect(t, star, star, maxDist, outp); // camera == star, so RelPos == orbital offset
        return outp;
    }

    [Fact]
    public void Belt_RocksLieWithinTheAnnulus()
    {
        SolarSystem sys = BeltedSystem(out _);
        AsteroidBelt belt = sys.Belt!;
        Assert.True(belt.Count > 0);
        Assert.True(belt.OuterRadius > belt.InnerRadius && belt.InnerRadius > 0);

        List<AsteroidInstance> rocks = Collect(belt, sys.Sun.Position, t: 0, maxDist: belt.OuterRadius * 4);
        Assert.Equal(belt.Count, rocks.Count); // nothing culled at this range

        foreach (AsteroidInstance a in rocks)
        {
            double r = a.RelPos.Length; // distance from the star (camera sat on the star)
            Assert.InRange(r, belt.InnerRadius * 0.999, belt.OuterRadius * 1.001);
            Assert.True(a.Radius > 0, "every rock has a positive radius");
            Assert.True(a.SpinAxis.Length is > 0.99f and < 1.01f, "spin axis is unit length");
        }
    }

    [Fact]
    public void Belt_OrbitPreservesRadiusOverTime()
    {
        SolarSystem sys = BeltedSystem(out _);
        AsteroidBelt belt = sys.Belt!;

        List<AsteroidInstance> t0 = Collect(belt, sys.Sun.Position, t: 0, maxDist: belt.OuterRadius * 4);
        List<AsteroidInstance> t1 = Collect(belt, sys.Sun.Position, t: 3.0e8, maxDist: belt.OuterRadius * 4);

        bool anyMoved = false;
        for (int i = 0; i < t0.Count; i++)
        {
            // Circular orbits: the distance from the star is invariant as the belt churns.
            // (Compared with a relative tolerance — these are ~1e11 m values held in floats.)
            double r0 = t0[i].RelPos.Length, r1 = t1[i].RelPos.Length;
            Assert.True(Math.Abs(r0 - r1) <= r0 * 1e-4, $"orbit radius drifted: {r0} vs {r1}");
            if ((t0[i].RelPos - t1[i].RelPos).Length > belt.InnerRadius * 1e-3) anyMoved = true;
        }
        Assert.True(anyMoved, "the belt should actually rotate over time");
    }

    [Fact]
    public void Belt_IsDeterministic()
    {
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            if (!field.HasNearest) continue;
            Star s = field.Nearest;

            AsteroidBelt? a = SystemGenerator.Generate(s).Belt;
            AsteroidBelt? b = SystemGenerator.Generate(s).Belt;
            Assert.Equal(a == null, b == null);
            if (a == null) continue;

            Assert.Equal(a.Count, b!.Count);
            Assert.Equal(a.InnerRadius, b.InnerRadius);
            List<AsteroidInstance> ra = Collect(a, s.Position, 1.0e7, a.OuterRadius * 4);
            List<AsteroidInstance> rb = Collect(b, s.Position, 1.0e7, b.OuterRadius * 4);
            Assert.Equal(ra.Count, rb.Count);
            for (int i = 0; i < ra.Count; i++)
            {
                Assert.Equal(ra[i].RelPos.X, rb[i].RelPos.X);
                Assert.Equal(ra[i].Radius, rb[i].Radius);
                Assert.Equal(ra[i].ShapeSeed, rb[i].ShapeSeed);
            }
            return; // one belted system is enough
        }
        throw new Xunit.Sdk.XunitException("no belted system found");
    }

    private static Planet RingedPlanet()
    {
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            foreach (Star s in field.Visible)
                foreach (Planet p in SystemGenerator.Generate(s).Planets)
                    if (p.HasRings)
                    {
                        Assert.NotNull(p.RingRocks); // every ringed planet gets particles
                        return p;
                    }
        }
        throw new Xunit.Sdk.XunitException("no ringed planet found in the sample");
    }

    [Fact]
    public void RingRocks_LieWithinTheAnnulusAndOrbit()
    {
        Planet p = RingedPlanet();
        PlanetRing ring = p.RingRocks!;
        Assert.True(ring.Count > 0);

        var rocks = new List<AsteroidInstance>();
        // Camera == planet centre, so RelPos is the particle's offset from the planet.
        ring.Collect(0, p.CurrentPosition, p.CurrentPosition, p.RingOuterRadius * 4, rocks);
        Assert.Equal(ring.Count, rocks.Count);
        foreach (AsteroidInstance a in rocks)
        {
            double r = a.RelPos.Length; // ~orbital radius (vertical thickness is tiny)
            Assert.InRange(r, p.RingInnerRadius * 0.97, p.RingOuterRadius * 1.03);
        }

        // The ring churns over time, but particles stay within the annulus.
        var later = new List<AsteroidInstance>();
        ring.Collect(2.0e6, p.CurrentPosition, p.CurrentPosition, p.RingOuterRadius * 4, later);
        bool moved = false;
        for (int i = 0; i < rocks.Count; i++)
            if ((rocks[i].RelPos - later[i].RelPos).Length > p.RingInnerRadius * 1e-3) { moved = true; break; }
        Assert.True(moved, "ring particles should orbit over time");
    }

    [Fact]
    public void RingRocks_AreDeterministic()
    {
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            if (!field.HasNearest) continue;
            Star s = field.Nearest;

            foreach (var (pa, pb) in Zip(SystemGenerator.Generate(s).Planets, SystemGenerator.Generate(s).Planets))
            {
                Assert.Equal(pa.RingRocks == null, pb.RingRocks == null);
                if (pa.RingRocks == null) continue;

                var ra = new List<AsteroidInstance>();
                var rb = new List<AsteroidInstance>();
                pa.RingRocks.Collect(1.0e6, pa.CurrentPosition, pa.CurrentPosition, pa.RingOuterRadius * 4, ra);
                pb.RingRocks!.Collect(1.0e6, pb.CurrentPosition, pb.CurrentPosition, pb.RingOuterRadius * 4, rb);
                Assert.Equal(ra.Count, rb.Count);
                for (int i = 0; i < ra.Count; i++)
                {
                    Assert.Equal(ra[i].RelPos.X, rb[i].RelPos.X);
                    Assert.Equal(ra[i].Radius, rb[i].Radius);
                }
                return; // one ringed planet is enough
            }
        }
        throw new Xunit.Sdk.XunitException("no ringed planet found");
    }

    private static IEnumerable<(Planet, Planet)> Zip(Planet[] a, Planet[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++) yield return (a[i], b[i]);
    }

    [Fact]
    public void Cluster_RocksStayWithinBound()
    {
        var field = AsteroidField.Generate(UniversePosition.Origin, seed: 0xABCDEF);
        Assert.True(field.Count > 0 && field.BoundRadius > 0);

        var rocks = new List<AsteroidInstance>();
        field.Collect(t: 0, UniversePosition.Origin, field.BoundRadius * 4, rocks);
        Assert.Equal(field.Count, rocks.Count);
        foreach (AsteroidInstance a in rocks)
            Assert.True(a.RelPos.Length <= field.BoundRadius * 1.001, "rock stays inside the cluster bound");
    }

    [Fact]
    public void Cluster_DistanceCullDropsFarRocks()
    {
        var field = AsteroidField.Generate(UniversePosition.Origin, seed: 7);
        var all = new List<AsteroidInstance>();
        field.Collect(0, UniversePosition.Origin, field.BoundRadius * 4, all);
        var near = new List<AsteroidInstance>();
        field.Collect(0, UniversePosition.Origin, field.BoundRadius * 0.25, near);
        Assert.True(near.Count < all.Count, "a tighter cull radius keeps fewer rocks");
    }

    [Fact]
    public void Belt_RollsPredicateAgreesWithGeneration()
    {
        // The navigator's cheap Rolls() test must match whether Generate() actually yields a belt,
        // otherwise "jump to belt" could frame a beltless system.
        int belted = 0, checkd = 0;
        for (ulong seed = 1; seed <= 30 && checkd < 200; seed++)
        {
            var field = new StarField(new GalaxyModel(seed));
            field.Update(UniversePosition.Origin, radiusCells: 8);
            foreach (Star s in field.Visible)
            {
                if (checkd >= 200) break;
                checkd++;
                bool rolls = AsteroidBelt.Rolls(s);
                bool generated = SystemGenerator.Generate(s).Belt != null;
                Assert.Equal(rolls, generated);
                if (generated) belted++;
            }
        }
        Assert.True(belted > 0, "expected some belted systems in the sample");
    }

    [Fact]
    public void FieldManager_TryFindNearest_LandsOnACluster()
    {
        var mgr = new AsteroidFieldManager(0xA11CE5EEDUL);
        Assert.True(mgr.TryFindNearest(UniversePosition.Origin, searchCells: 8, out AsteroidField f));
        Assert.True(f.Count > 0 && f.BoundRadius > 0);

        // The found cluster's rocks resolve from its centre (i.e. it's a real, populated field).
        var rocks = new List<AsteroidInstance>();
        f.Collect(0, f.Center, f.BoundRadius * 2, rocks);
        Assert.Equal(f.Count, rocks.Count);
    }

    [Fact]
    public void FieldManager_IsDeterministicAndFindsClusters()
    {
        const ulong worldSeed = 0xA11CE5EEDUL;
        var a = new AsteroidFieldManager(worldSeed);
        var b = new AsteroidFieldManager(worldSeed);

        int totalClusters = 0;
        // Sweep a few well-separated cells so the 20%-per-cell roll surfaces some clusters.
        long step = AsteroidFieldManager.CellSizeSectors * 5;
        for (long k = 0; k < 6; k++)
        {
            var camera = new UniversePosition(new Vector3D<long>(k * step, 0, 0), default);
            a.Update(camera, radiusCells: 2);
            b.Update(camera, radiusCells: 2);

            Assert.Equal(a.VisibleClusterCount, b.VisibleClusterCount);
            for (int i = 0; i < a.Visible.Count; i++)
                Assert.Equal(a.Visible[i].Center, b.Visible[i].Center);
            totalClusters += a.VisibleClusterCount;
        }
        Assert.True(totalClusters > 0, "expected at least one deep-space cluster across the sweep");
    }
}
