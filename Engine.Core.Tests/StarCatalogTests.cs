using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

/// <summary>
/// Phase-1 behaviour: star generation is confined to galaxy interiors. A block inside the Milky Way is
/// densely populated; a block far from any galaxy is empty; generation is deterministic and ids stay
/// in range.
/// </summary>
public class StarCatalogTests
{
    private const ulong Seed = 0xA11CE5EEDUL;

    /// <summary>The Milky Way descriptor as the home block produces it (block (0,0,0) galaxy 0).</summary>
    private static Galaxy MilkyWay() =>
        new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(0, 0, 0)).Galaxies[0];

    [Fact]
    public void BlockAtGalacticCentre_IsDenselyPopulated()
    {
        // Block (0,0,0) is centred on the Milky Way's core, so density is near peak.
        var cat = new StarCatalog(MilkyWay(), new Vector3D<long>(0, 0, 0));
        Assert.InRange(cat.Count, 100_000, StarId.MaxLocal); // ~quarter-million at neighbourhood density
    }

    [Fact]
    public void BlockFarOutsideTheDisk_IsEmpty()
    {
        // ~70,000 ly above the disk plane (block Y index well past the 50,000 ly radius). The galaxy's
        // density — and therefore the star count — has collapsed to zero out here.
        long by = (long)Math.Round(7.0e4 / StarCatalog.BlockSideLy);
        var cat = new StarCatalog(MilkyWay(), new Vector3D<long>(0, by, 0));
        Assert.Equal(0, cat.Count);
    }

    [Fact]
    public void EmptyBlockConstructor_ProducesNoStars()
    {
        var cat = new StarCatalog(new Vector3D<long>(5, 5, 5));
        Assert.Equal(0, cat.Count);
        // Querying a huge radius over an empty block is safe and finds nothing.
        var visible = new List<Star>();
        double bestSq = double.MaxValue;
        Star nearest = default;
        bool has = false;
        cat.Collect(cat.Origin, 1.0e6 * MathUtil.LightYear, visible, ref bestSq, ref nearest, ref has);
        Assert.Empty(visible);
        Assert.False(has);
    }

    [Fact]
    public void Generation_IsDeterministicAcrossInstances()
    {
        Galaxy mw = MilkyWay();
        var coord = new Vector3D<long>(2, 0, -1);
        StarCatalog a = new(mw, coord);
        StarCatalog b = new(mw, coord);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Stars[i].Id, b.Stars[i].Id);
            Assert.Equal(a.Stars[i].Position.Sector, b.Stars[i].Position.Sector);
            Assert.Equal(a.Stars[i].Temperature, b.Stars[i].Temperature);
        }
    }

    [Fact]
    public void KeptStars_HaveSequentialLocalIds_WithinTheBlock()
    {
        Galaxy mw = MilkyWay();
        var coord = new Vector3D<long>(1, 0, 0);
        var cat = new StarCatalog(mw, coord);

        for (int i = 0; i < Math.Min(cat.Count, 1000); i++)
        {
            StarId.Unpack(cat.Stars[i].Id, out Vector3D<long> block, out int local);
            Assert.Equal(coord, block);
            Assert.Equal(i, local);
        }
    }

    [Fact]
    public void Pager_AtOrigin_StreamsTheMilkyWaysStars()
    {
        var galaxies = new GalaxyCatalogPager(new GalaxyField(Seed));
        galaxies.Update(UniversePosition.Origin);
        var stars = new StarCatalogPager(galaxies);

        stars.Update(UniversePosition.Origin, 25.0 * MathUtil.LightYear);

        Assert.True(stars.LoadedStarCount > 0);
        Assert.True(stars.HasNearest);
    }

    [Fact]
    public void Pager_InIntergalacticVoid_StreamsNothing()
    {
        // 3 million ly out along +Y — clear of the Milky Way and (deterministically, for this seed)
        // not inside any other galaxy. The block resolves to empty, so no stars stream in.
        var galaxies = new GalaxyCatalogPager(new GalaxyField(Seed));
        var pos = UniversePosition.FromMeters(0, 3.0e6 * MathUtil.LightYear, 0);
        galaxies.Update(pos);
        Assume.NotInside(galaxies); // guard: skip if the random field happens to enclose this point

        var stars = new StarCatalogPager(galaxies);
        stars.Update(pos, 25.0 * MathUtil.LightYear);

        Assert.Equal(0, stars.LoadedStarCount);
        Assert.False(stars.HasNearest);
    }

    [Fact]
    public void GlobularClusterStars_AreInjectedAsRealCatalogStars()
    {
        Galaxy mw = new GalaxyCatalog(new GalaxyField(Seed), new Vector3D<long>(0, 0, 0)).Galaxies[0];
        GlobularCluster c = GlobularClusters.ForGalaxy(mw)[0];

        // The lattice block containing the cluster centre (blocks are centred on lattice points).
        double side = StarCatalog.BlockSideLy * MathUtil.LightYear;
        Vector3D<double> m = c.Center.DeltaMeters(UniversePosition.Origin);
        var coord = new Vector3D<long>(
            (long)Math.Round(m.X / side), (long)Math.Round(m.Y / side), (long)Math.Round(m.Z / side));

        var block = new StarCatalog(mw, coord);

        // The halo's galaxy density is ~0, so essentially all of these stars are the injected cluster.
        Assert.True(block.Count > 100, $"expected the cluster's stars in the block, got {block.Count}");
        bool anyInCluster = false;
        foreach (Star s in block.Stars)
            if (s.Position.DistanceTo(c.Center) <= c.RadiusMeters) { anyInCluster = true; break; }
        Assert.True(anyInCluster, "no catalog star landed inside the cluster radius");

        // Deterministic: regenerating the same block yields the same injected stars (stable ids).
        var again = new StarCatalog(mw, coord);
        Assert.Equal(block.Count, again.Count);
        Assert.Equal(block.Stars[0].Id, again.Stars[0].Id);
    }

    /// <summary>Tiny guard helper: only meaningful to assert "empty void" when the point really is
    /// outside every resident galaxy (the random field could, in principle, place one here).</summary>
    private static class Assume
    {
        public static void NotInside(GalaxyCatalogPager g) => Assert.False(g.IsInside,
            "test point unexpectedly fell inside a galaxy for this seed");
    }
}
