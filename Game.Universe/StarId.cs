using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Packs a star's global catalog id from its <b>(block coordinate, local index)</b> and unpacks it
/// back. The star field is an infinite lattice of <see cref="StarCatalog"/> blocks; a star is
/// addressed by which block it lives in and its index within that block. Packing both into one 64-bit
/// id keeps <see cref="Star.Id"/> globally unique and deterministic (so system seeding stays stable)
/// while remaining invertible — the "find #N" box unpacks an id, loads its block and flies there.
///
/// Layout (low → high bits): local index | block z | block y | block x. The home block (0,0,0) puts
/// its bias-zero coordinate fields at zero, so its stars keep ids 0..count-1 exactly as before tiling.
/// </summary>
public static class StarId
{
    public const int LocalBits = 21;                 // up to 2,097,152 stars per block
    public const int AxisBits = 14;                  // ±8192 blocks per axis (far beyond the galaxy)
    private const long AxisBias = 1L << (AxisBits - 1); // maps signed coord → unsigned field
    private const ulong LocalMask = (1UL << LocalBits) - 1;
    private const ulong AxisMask = (1UL << AxisBits) - 1;

    /// <summary>Largest local index a block may hold (a generation-time sanity bound).</summary>
    public const int MaxLocal = 1 << LocalBits;

    public static ulong Pack(Vector3D<long> block, int local) =>
        (Field(block.X) << (LocalBits + 2 * AxisBits)) |
        (Field(block.Y) << (LocalBits + AxisBits)) |
        (Field(block.Z) << LocalBits) |
        ((ulong)local & LocalMask);

    public static void Unpack(ulong id, out Vector3D<long> block, out int local)
    {
        local = (int)(id & LocalMask);
        long z = Coord(id >> LocalBits);
        long y = Coord(id >> (LocalBits + AxisBits));
        long x = Coord(id >> (LocalBits + 2 * AxisBits));
        block = new Vector3D<long>(x, y, z);
    }

    private static ulong Field(long coord) => (ulong)(coord + AxisBias) & AxisMask;
    private static long Coord(ulong field) => (long)(field & AxisMask) - AxisBias;
}
