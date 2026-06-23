using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Packs a galaxy's global id from its <b>(lattice block, local index)</b> and unpacks it back —
/// the universe-level mirror of <see cref="StarId"/>. The universe is a sparse lattice of
/// <see cref="GalaxyCatalog"/> blocks; a galaxy is addressed by which block it lives in and its index
/// within that block. Packing both into one 64-bit id keeps the id globally unique and deterministic
/// (so a galaxy's internal generation stays stable) while remaining invertible — a future "jump to
/// galaxy" can unpack an id, load its block and fly there.
///
/// Layout (low → high bits): local index | block z | block y | block x. Block coordinates are
/// bias-encoded so negative coords pack cleanly; the id is therefore an opaque-but-stable 64-bit value
/// (not a small running index). The Milky Way is simply block (0,0,0) local 0 — its id is whatever that
/// packs to, fixed forever.
/// </summary>
public static class GalaxyId
{
    public const int LocalBits = 16;                 // up to 65,536 galaxies per block
    public const int AxisBits = 16;                  // ±32,768 blocks per axis (≫ the observable universe)
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
