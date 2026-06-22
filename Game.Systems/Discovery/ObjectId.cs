using Game.Universe;

namespace Game.Systems.Discovery;

/// <summary>
/// The string identity scheme shared with the server (see docs/DISCOVERY_PLAN.md). Because the
/// universe is a pure function of the world seed, every player generates the same stars, planets
/// and moons in the same order — so these ids are stable across clients without the server knowing
/// any geometry.
///
///   star   = decimal <see cref="Star.Id"/>            e.g. "12407198355"
///   planet = "{starId}-{PP}"  (PP = index in Planets) e.g. "12407198355-02"
///   moon   = "{starId}-{PP}-{MM}" (MM = index in Moons) e.g. "12407198355-02-03"
///
/// Indices are 0-based generation order, zero-padded to two digits.
/// </summary>
public static class ObjectId
{
    public static string Star(in Star s) => s.Id.ToString();
    public static string Planet(in Star s, int planetIndex) => $"{s.Id}-{planetIndex:D2}";
    public static string Moon(in Star s, int planetIndex, int moonIndex) => $"{s.Id}-{planetIndex:D2}-{moonIndex:D2}";

    /// <summary>
    /// Resolve a body in the active system to its (id, kind). Matches by reference (the instances
    /// the system manager hands out), falling back to <see cref="CelestialBody.Seed"/>. Returns
    /// false if the body isn't part of this system.
    /// </summary>
    public static bool TryFor(SolarSystem sys, CelestialBody body, out string id, out string kind)
    {
        for (int pi = 0; pi < sys.Planets.Length; pi++)
        {
            Planet p = sys.Planets[pi];
            if (ReferenceEquals(p, body) || p.Seed == body.Seed)
            {
                id = Planet(sys.Sun, pi);
                kind = "planet";
                return true;
            }
            for (int mi = 0; mi < p.Moons.Length; mi++)
            {
                Moon m = p.Moons[mi];
                if (ReferenceEquals(m, body) || m.Seed == body.Seed)
                {
                    id = Moon(sys.Sun, pi, mi);
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
