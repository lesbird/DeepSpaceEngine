using Game.Universe;

namespace Game.Systems.Discovery;

/// <summary>
/// The string identity scheme shared with the server (see docs/DISCOVERY_PLAN.md). Because the
/// universe is a pure function of the world seed, every player generates the same stars, planets
/// and moons in the same order — so these ids are stable across clients without the server knowing
/// any geometry.
///
///   star   = "{galaxyId}-{starId}"               e.g. "12345-56789"
///   planet = "{galaxyId}-{starId}-{PP}"          e.g. "12345-56789-00"
///   moon   = "{galaxyId}-{starId}-{PP}-{MM}"     e.g. "12345-56789-00-03"
///
/// The galaxy id prefixes the star id because <see cref="Star.Id"/> is only unique <i>within</i> a
/// galaxy: the star-lattice block field (<see cref="StarId"/>) wraps at a few Mly, so two stars in
/// different galaxies can share an id — but a galaxy id is globally unique, so the pair is too.
/// Indices are 0-based generation order, zero-padded to two digits.
/// </summary>
public static class ObjectId
{
    public static string Star(ulong galaxyId, in Star s) => $"{galaxyId}-{s.Id}";
    public static string Planet(ulong galaxyId, in Star s, int planetIndex) => $"{galaxyId}-{s.Id}-{planetIndex:D2}";
    public static string Moon(ulong galaxyId, in Star s, int planetIndex, int moonIndex) =>
        $"{galaxyId}-{s.Id}-{planetIndex:D2}-{moonIndex:D2}";

    /// <summary>
    /// Resolve a body in the active system to its (id, kind), prefixed by <paramref name="galaxyId"/>.
    /// Matches by reference (the instances the system manager hands out), falling back to
    /// <see cref="CelestialBody.Seed"/>. Returns false if the body isn't part of this system.
    /// </summary>
    public static bool TryFor(ulong galaxyId, SolarSystem sys, CelestialBody body, out string id, out string kind)
    {
        for (int pi = 0; pi < sys.Planets.Length; pi++)
        {
            Planet p = sys.Planets[pi];
            if (ReferenceEquals(p, body) || p.Seed == body.Seed)
            {
                id = Planet(galaxyId, sys.Sun, pi);
                kind = "planet";
                return true;
            }
            for (int mi = 0; mi < p.Moons.Length; mi++)
            {
                Moon m = p.Moons[mi];
                if (ReferenceEquals(m, body) || m.Seed == body.Seed)
                {
                    id = Moon(galaxyId, sys.Sun, pi, mi);
                    kind = "moon";
                    return true;
                }
            }
        }
        id = "";
        kind = "";
        return false;
    }
}
