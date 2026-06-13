namespace Engine.Core;

/// <summary>
/// Fast, deterministic, allocation-free integer hashing for procedural generation.
/// Pure functions: identical inputs always yield identical outputs, on every machine
/// and every run. This is how billions of stars exist without being stored — each is
/// regenerated on demand from its integer seed.
/// </summary>
public static class Hashing
{
    /// <summary>
    /// SplitMix64 finalizer — mixes a 64-bit value into a well-distributed 64-bit hash.
    /// </summary>
    public static ulong Mix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Combine an existing hash with another value (order matters).</summary>
    public static ulong Combine(ulong seed, ulong value) => Mix64(seed ^ Mix64(value));

    /// <summary>Hash a 3D integer cell coordinate plus a world seed into a 64-bit value.</summary>
    public static ulong HashCell(long x, long y, long z, ulong worldSeed)
    {
        ulong h = worldSeed;
        h = Combine(h, unchecked((ulong)x));
        h = Combine(h, unchecked((ulong)y));
        h = Combine(h, unchecked((ulong)z));
        return h;
    }

    /// <summary>Map a 64-bit hash to a double in [0, 1).</summary>
    public static double ToUnitDouble(ulong h) => (h >> 11) * (1.0 / 9007199254740992.0); // 2^53

    /// <summary>Map a 64-bit hash to a float in [0, 1).</summary>
    public static float ToUnitFloat(ulong h) => (h >> 40) * (1.0f / 16777216.0f); // 2^24

    /// <summary>Map a 64-bit hash to a double in [min, max).</summary>
    public static double Range(ulong h, double min, double max) => min + ToUnitDouble(h) * (max - min);
}

/// <summary>
/// A tiny deterministic PRNG (SplitMix64) seeded from a single 64-bit value.
/// Use one per generated object so that generation order never affects results.
/// Value type — copy it to fork a stream.
/// </summary>
public struct DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed) => _state = seed;

    public ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        return Hashing.Mix64(_state);
    }

    /// <summary>Next double in [0, 1).</summary>
    public double NextDouble() => Hashing.ToUnitDouble(NextULong());

    /// <summary>Next float in [0, 1).</summary>
    public float NextFloat() => Hashing.ToUnitFloat(NextULong());

    /// <summary>Next double in [min, max).</summary>
    public double Range(double min, double max) => min + NextDouble() * (max - min);

    /// <summary>Next int in [min, max).</summary>
    public int RangeInt(int min, int max) => min + (int)(NextDouble() * (max - min));

    /// <summary>Next long in [0, maxExclusive). (Negligible modulo bias for our small ranges.)</summary>
    public long RangeLong(long maxExclusive) => (long)(NextULong() % (ulong)maxExclusive);

    /// <summary>
    /// Sample a Poisson-distributed count with mean <paramref name="lambda"/> (Knuth's method;
    /// intended for the small means we use for stars-per-cell).
    /// </summary>
    public int Poisson(double lambda)
    {
        double l = Math.Exp(-lambda);
        int k = 0;
        double p = 1.0;
        do { k++; p *= NextDouble(); } while (p > l);
        return k - 1;
    }
}
