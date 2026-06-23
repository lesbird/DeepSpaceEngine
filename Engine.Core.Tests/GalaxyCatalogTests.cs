using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class GalaxyCatalogTests
{
    private static GalaxyField NewField(ulong seed = 0xA11CE5EEDUL) => new(seed);

    [Fact]
    public void HomeBlock_HostsMilkyWayAtOrigin_AsGalaxyZero()
    {
        var cat = new GalaxyCatalog(NewField(), new Vector3D<long>(0, 0, 0));

        Assert.True(cat.Count >= 1);
        Galaxy mw = cat.Galaxies[0];
        Assert.Equal("Milky Way", mw.Name);
        Assert.Equal(GalaxyType.Spiral, mw.Type);
        Assert.Equal(GalaxyId.Pack(new Vector3D<long>(0, 0, 0), 0), mw.Id); // block (0,0,0), local 0
        Assert.Equal(UniversePosition.Origin, mw.Center);          // sits exactly at the universe origin
        Assert.Equal(5.0e4, mw.RadiusLy, 0);                       // ~50,000 ly disk radius
    }

    [Fact]
    public void NonHomeBlock_DoesNotContainTheMilkyWay()
    {
        var cat = new GalaxyCatalog(NewField(), new Vector3D<long>(3, -1, 2));
        Assert.DoesNotContain(cat.Galaxies, g => g.Name == "Milky Way");
    }

    [Fact]
    public void Generation_IsDeterministicAcrossInstances()
    {
        var coord = new Vector3D<long>(5, 2, -3);
        GalaxyCatalog a = new(NewField(), coord);
        GalaxyCatalog b = new(NewField(), coord);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Galaxies[i].Id, b.Galaxies[i].Id);
            Assert.Equal(a.Galaxies[i].Type, b.Galaxies[i].Type);
            Assert.Equal(a.Galaxies[i].Center.Sector, b.Galaxies[i].Center.Sector);
            Assert.Equal(a.Galaxies[i].Center.Local, b.Galaxies[i].Center.Local);
            Assert.Equal(a.Galaxies[i].RadiusMeters, b.Galaxies[i].RadiusMeters);
        }
    }

    [Fact]
    public void DifferentWorldSeed_ProducesDifferentGalaxies()
    {
        var coord = new Vector3D<long>(1, 1, 1);
        GalaxyCatalog a = new(NewField(1), coord);
        GalaxyCatalog b = new(NewField(2), coord);

        bool different = a.Count != b.Count ||
                         (a.Count > 0 && a.Galaxies[0].Center.Sector != b.Galaxies[0].Center.Sector);
        Assert.True(different);
    }

    [Fact]
    public void GeneratedGalaxies_FallWithinTheirBlock()
    {
        var coord = new Vector3D<long>(2, -4, 7);
        var cat = new GalaxyCatalog(NewField(), coord);
        double side = cat.SideMeters;

        foreach (Galaxy g in cat.Galaxies)
        {
            Vector3D<double> rel = g.Center.DeltaMeters(cat.Origin);
            Assert.InRange(rel.X, 0.0, side);
            Assert.InRange(rel.Y, 0.0, side);
            Assert.InRange(rel.Z, 0.0, side);
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 0, 7)]
    [InlineData(5, -3, 12, 42)]
    [InlineData(-100, 200, -300, 1234)]
    public void GalaxyId_RoundTrips(long bx, long by, long bz, int local)
    {
        var block = new Vector3D<long>(bx, by, bz);
        ulong id = GalaxyId.Pack(block, local);
        GalaxyId.Unpack(id, out Vector3D<long> outBlock, out int outLocal);
        Assert.Equal(block, outBlock);
        Assert.Equal(local, outLocal);
    }

    [Fact]
    public void GalaxyId_HomeBlockLocalZero_UnpacksToOrigin()
    {
        GalaxyId.Unpack(GalaxyId.Pack(new Vector3D<long>(0, 0, 0), 0), out Vector3D<long> block, out int local);
        Assert.Equal(new Vector3D<long>(0, 0, 0), block);
        Assert.Equal(0, local);
    }

    [Fact]
    public void Pager_AtOrigin_IsInsideTheMilkyWay()
    {
        var pager = new GalaxyCatalogPager(NewField());
        pager.Update(UniversePosition.Origin);

        Assert.True(pager.IsInside);
        Assert.Equal("Milky Way", pager.Containing.Name);
        Assert.True(pager.HasNearest);
        Assert.Equal("Milky Way", pager.Nearest.Name);
        Assert.Equal(0.0, pager.NearestDistanceMeters, 0);
    }

    [Fact]
    public void Pager_JustOutsideTheMilkyWayDisk_IsNotInsideIt()
    {
        // 200,000 ly out — well beyond the Milky Way's ~50,000 ly radius. (A stray random galaxy
        // enclosing this exact point is astronomically unlikely at the field density.)
        var pager = new GalaxyCatalogPager(NewField());
        var pos = UniversePosition.FromMeters(2.0e5 * MathUtil.LightYear, 0, 0);
        pager.Update(pos);

        Assert.False(pager.IsInside && pager.Containing.Name == "Milky Way");
    }
}
