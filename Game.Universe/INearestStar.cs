namespace Game.Universe;

/// <summary>
/// A source that can report the star nearest the camera. Implemented by both the legacy
/// infinite <see cref="StarField"/> and the finite <see cref="StarCatalog"/>, so the
/// solar-system spawner and the proximity speed limit work against either.
/// </summary>
public interface INearestStar
{
    bool HasNearest { get; }
    Star Nearest { get; }
    double NearestDistanceMeters { get; }
}
