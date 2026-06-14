namespace Game.Universe;

/// <summary>
/// A moon orbiting a <see cref="Planet"/>. Same orbital/surface model as a planet (see
/// <see cref="CelestialBody"/>), but its position is relative to the parent planet rather than
/// the star, so it gets procedural terrain and — for the larger ones — a thin atmosphere too.
/// </summary>
public sealed class Moon : CelestialBody
{
}
