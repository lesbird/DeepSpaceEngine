using Engine.Core;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

public class UniversePositionTests
{
    private const double Au = UniversePosition.SectorSize;

    [Fact]
    public void Normalize_KeepsLocalWithinSector()
    {
        var p = new UniversePosition(default, new Vector3D<double>(2.5 * Au, -0.5 * Au, 3.0 * Au + 10));
        p.Normalize();

        Assert.True(p.Local.X >= 0 && p.Local.X < Au);
        Assert.True(p.Local.Y >= 0 && p.Local.Y < Au);
        Assert.True(p.Local.Z >= 0 && p.Local.Z < Au);
    }

    [Fact]
    public void Normalize_CarriesIntoSector()
    {
        // 2.5 AU on X should become sector 2 + 0.5 AU; -0.5 AU on Y -> sector -1 + 0.5 AU.
        var p = new UniversePosition(default, new Vector3D<double>(2.5 * Au, -0.5 * Au, 0));
        p.Normalize();

        Assert.Equal(2, p.Sector.X);
        Assert.Equal(-1, p.Sector.Y);
        Assert.Equal(0, p.Sector.Z);
        Assert.Equal(0.5 * Au, p.Local.X, 3);
        Assert.Equal(0.5 * Au, p.Local.Y, 3);
    }

    [Fact]
    public void Normalize_DoesNotChangeAbsolutePosition()
    {
        var raw = new Vector3D<double>(7.3 * Au, -12.9 * Au, 0.1 * Au);
        var a = new UniversePosition(default, raw);   // un-normalized
        var b = a;
        b.Normalize();

        // Both describe the same absolute point, so their delta is ~zero.
        var delta = a.DeltaMeters(b);
        Assert.True(delta.Length < 1e-3, $"delta was {delta.Length} m");
    }

    [Fact]
    public void ToCameraRelative_NearCamera_IsTinyAndPrecise()
    {
        // Two points 1 meter apart, but ~1000 light-years from the origin.
        // NOTE: we must build the offset via Translated, NOT `far + 1.0` — at ~1e19
        // the raw double `far + 1.0 == far` (the +1 falls below the ULP). That silent
        // drop is precisely the failure mode UniversePosition is designed to avoid.
        const double far = 1000.0 * 9.4607e15; // 1000 ly in meters (~1e19)
        var cam = UniversePosition.FromMeters(far, far, far);
        var obj = cam.Translated(new Vector3D<double>(1.0, 0, 0));

        var rel = obj.ToCameraRelative(cam);

        // The relative vector must reproduce the 1 m offset to sub-millimeter accuracy,
        // even though the absolute coordinates are ~1e19 m (where a raw double/float
        // would have tens-of-meters error).
        Assert.Equal(1.0, (double)rel.X, 3);
        Assert.Equal(0.0, (double)rel.Y, 3);
        Assert.Equal(0.0, (double)rel.Z, 3);
    }

    [Fact]
    public void Precision_HoldsAtGalacticScale()
    {
        // ~100,000 ly from origin (galactic edge), then nudge by 1 mm.
        const double galactic = 100_000.0 * 9.4607e15; // ~1e21 m
        var a = UniversePosition.FromMeters(galactic, 0, 0);
        var b = a.Translated(new Vector3D<double>(0.001, 0, 0)); // +1 mm

        double d = a.DistanceTo(b);
        // At the galactic edge (~1e21 m) double ULP within a 1 AU sector is ~30 microns,
        // so 1 mm resolves to ~4 decimal places. (Far better than the hundreds-of-km
        // error a single absolute double would give at this magnitude.)
        Assert.Equal(0.001, d, 4);
    }

    [Fact]
    public void Translate_AccumulatesWithoutDrift()
    {
        var p = UniversePosition.Origin;
        // Take a million 1 cm steps = 10 km total.
        for (int i = 0; i < 1_000_000; i++)
            p.Translate(new Vector3D<double>(0.01, 0, 0));

        double dist = p.DistanceTo(UniversePosition.Origin);
        Assert.Equal(10_000.0, dist, 2); // within 1 cm of 10 km after a million steps
    }

    [Fact]
    public void RoundTrip_FromMeters_BackToDelta()
    {
        var meters = new Vector3D<double>(123.456 * Au, -987.654 * Au, 42.0 * Au);
        var p = UniversePosition.FromMeters(meters);
        var back = p.DeltaMeters(UniversePosition.Origin);

        Assert.Equal(meters.X, back.X, 3);
        Assert.Equal(meters.Y, back.Y, 3);
        Assert.Equal(meters.Z, back.Z, 3);
    }
}
