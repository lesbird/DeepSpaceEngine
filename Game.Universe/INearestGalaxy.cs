namespace Game.Universe;

/// <summary>
/// A source that reports the galaxy nearest the camera and whether the camera is currently inside a
/// galaxy's volume — the universe-level analogue of <see cref="INearestStar"/>. Star streaming, the
/// galaxy renderer, and (later) per-galaxy effects all query this.
/// </summary>
public interface INearestGalaxy
{
    bool HasNearest { get; }
    Galaxy Nearest { get; }
    double NearestDistanceMeters { get; }

    /// <summary>True when the camera lies within <see cref="Containing"/>'s radius.</summary>
    bool IsInside { get; }
    /// <summary>The galaxy whose volume currently contains the camera (valid only when <see cref="IsInside"/>).</summary>
    Galaxy Containing { get; }
}
