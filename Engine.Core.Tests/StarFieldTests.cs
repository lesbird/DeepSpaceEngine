using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class StarFieldTests
{
    private static StarField NewField(ulong seed = 12345) => new(new GalaxyModel(seed));

    [Fact]
    public void GenerateCell_IsDeterministicAcrossInstances()
    {
        var cell = new Vector3D<long>(3, -2, 7);
        Star[] a = NewField().GetOrGenerate(cell);
        Star[] b = NewField().GetOrGenerate(cell);

        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Id, b[i].Id);
            Assert.Equal(a[i].Temperature, b[i].Temperature);
            Assert.Equal(a[i].Position.Sector, b[i].Position.Sector);
            Assert.Equal(a[i].Position.Local, b[i].Position.Local);
        }
    }

    [Fact]
    public void DifferentWorldSeed_ProducesDifferentStars()
    {
        var cell = new Vector3D<long>(0, 0, 0);
        Star[] a = NewField(1).GetOrGenerate(cell);
        Star[] b = NewField(2).GetOrGenerate(cell);

        // Overwhelmingly likely to differ in count or first-star id.
        bool different = a.Length != b.Length || (a.Length > 0 && a[0].Id != b[0].Id);
        Assert.True(different);
    }

    [Fact]
    public void GeneratedStars_FallWithinTheirCell()
    {
        var cell = new Vector3D<long>(5, 5, 5);
        long lo = cell.X * StarField.CellSizeSectors;
        long hi = lo + StarField.CellSizeSectors;

        foreach (Star s in NewField().GetOrGenerate(cell))
        {
            Assert.InRange(s.Position.Sector.X, lo, hi);
            Assert.InRange(s.Position.Sector.Y, cell.Y * StarField.CellSizeSectors, (cell.Y + 1) * StarField.CellSizeSectors);
            Assert.InRange(s.Position.Sector.Z, cell.Z * StarField.CellSizeSectors, (cell.Z + 1) * StarField.CellSizeSectors);
        }
    }

    [Fact]
    public void CellOf_RoundTripsGeneratedStars()
    {
        // Every star generated for a cell must report that same cell.
        var cell = new Vector3D<long>(-4, 9, 2);
        foreach (Star s in NewField().GetOrGenerate(cell))
            Assert.Equal(cell, StarField.CellOf(s.Position));
    }

    [Fact]
    public void Update_FindsNearestStarInBubble()
    {
        var field = NewField();
        field.Update(UniversePosition.Origin, radiusCells: 6);

        Assert.True(field.Visible.Count > 0);
        Assert.True(field.HasNearest);

        // The reported nearest must actually be the minimum over the visible set.
        double min = double.MaxValue;
        foreach (Star s in field.Visible)
            min = Math.Min(min, s.Position.DistanceTo(UniversePosition.Origin));
        Assert.Equal(min, field.NearestDistanceMeters, 0);
    }
}
