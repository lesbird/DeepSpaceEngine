using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public class HashingTests
{
    [Fact]
    public void HashCell_IsDeterministic()
    {
        ulong a = Hashing.HashCell(12, -7, 99, worldSeed: 0xCAFE);
        ulong b = Hashing.HashCell(12, -7, 99, worldSeed: 0xCAFE);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashCell_DiffersByCoordinate()
    {
        ulong a = Hashing.HashCell(12, -7, 99, worldSeed: 0xCAFE);
        ulong b = Hashing.HashCell(12, -7, 100, worldSeed: 0xCAFE);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashCell_DiffersByWorldSeed()
    {
        ulong a = Hashing.HashCell(1, 2, 3, worldSeed: 1);
        ulong b = Hashing.HashCell(1, 2, 3, worldSeed: 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToUnitDouble_StaysInRange()
    {
        var rng = new DeterministicRng(0xDEADBEEF);
        for (int i = 0; i < 10_000; i++)
        {
            double d = rng.NextDouble();
            Assert.InRange(d, 0.0, 0.9999999999);
        }
    }

    [Fact]
    public void DeterministicRng_SameSeedSameSequence()
    {
        var r1 = new DeterministicRng(42);
        var r2 = new DeterministicRng(42);
        for (int i = 0; i < 100; i++)
            Assert.Equal(r1.NextULong(), r2.NextULong());
    }

    [Fact]
    public void DeterministicRng_DistributionIsRoughlyUniform()
    {
        var rng = new DeterministicRng(7);
        int[] buckets = new int[10];
        const int n = 100_000;
        for (int i = 0; i < n; i++)
            buckets[(int)(rng.NextDouble() * 10)]++;

        // Each bucket should hold ~10% of samples; allow generous slack.
        foreach (int count in buckets)
            Assert.InRange(count, n / 10 - n / 50, n / 10 + n / 50);
    }
}
