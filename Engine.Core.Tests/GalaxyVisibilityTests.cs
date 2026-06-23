using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

/// <summary>
/// Guards the galaxy point-sprite visibility: that galaxies are actually resident just outside the
/// Milky Way, and that the brightness-cue calibration yields clearly visible sprites (the bug that
/// made every galaxy collapse to an invisible floor dot). Mirrors the cue math in
/// <c>GalaxyRenderer</c> — keep them in sync.
/// </summary>
public class GalaxyVisibilityTests
{
    private const ulong Seed = 0xA11CE5EEDUL;

    // Must match GalaxyRenderer.
    private const double RefStarCount = 2.0e11;
    private const double RefDistLy = 1.0e6;
    private const float MinSizePx = 4.0f, MaxSizePx = 28.0f, SizeScale = 16.0f;

    private static float SizePx(double starCount, double distLy)
    {
        float cue = (float)(System.Math.Sqrt(starCount / RefStarCount) * (RefDistLy / distLy));
        return System.Math.Clamp(MinSizePx + SizeScale * cue, MinSizePx, MaxSizePx);
    }

    [Fact]
    public void JustOutsideTheMilkyWay_GalaxiesAreResidentAndDrawable()
    {
        // 150,000 ly above the disk — clear of the 50,000 ly Milky Way (so it's no longer "inside" and
        // gets drawn), still deep in the resident galaxy region.
        var pager = new GalaxyCatalogPager(new GalaxyField(Seed));
        var pos = UniversePosition.FromMeters(0, 1.5e5 * MathUtil.LightYear, 0);
        pager.Update(pos);

        Assert.False(pager.IsInside);                 // so the renderer does NOT exclude the Milky Way
        Assert.True(pager.LoadedGalaxyCount > 1);      // Milky Way + neighbours are resident to draw
    }

    [Fact]
    public void MilkyWaySprite_FromJustOutside_IsLargeAndVisible()
    {
        // The galaxy you just left should be an obvious bright blob, not a floor dot.
        float size = SizePx(starCount: 2.0e11, distLy: 1.5e5);
        Assert.Equal(MaxSizePx, size); // clamps to max — unmistakable
    }

    [Fact]
    public void DistantGalaxySprite_StaysAboveTheFloor_NotInvisible()
    {
        // A Milky-Way-class galaxy 20 Mly out: faint, but still a real dot above the 4px floor — the
        // pre-fix calibration produced ~2px at minimum brightness (effectively invisible).
        float size = SizePx(starCount: 2.0e11, distLy: 2.0e7);
        Assert.True(size > MinSizePx);
        Assert.True(size < 6.0f); // and genuinely small/faint, as a distant galaxy should be
    }

    [Fact]
    public void Sprite_GrowsAsYouApproach()
    {
        float far = SizePx(2.0e11, 5.0e6);
        float near = SizePx(2.0e11, 5.0e5);
        Assert.True(near > far); // closing distance ⇒ bigger sprite (approach feedback)
    }
}
