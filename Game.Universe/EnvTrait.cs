using System;

namespace Game.Universe;

/// <summary>
/// Coarse environmental traits of a world, derived from its physical state. Used to gate which
/// surface-object spawners are eligible on a given body — e.g. vegetation only where there's life,
/// so trees never appear on an airless moon. See <see cref="CelestialBody.Traits"/>.
/// </summary>
[Flags]
public enum EnvTrait
{
    None       = 0,
    Surface    = 1 << 0, // a landable solid surface (excludes gas/ice giants)
    Atmosphere = 1 << 1, // has an atmosphere
    Life       = 1 << 2, // vegetation-capable: liquid water + breathable air + temperate (Habitable)
    Water      = 1 << 3, // surface liquid water
    Molten     = 1 << 4, // lava world
    Frozen     = 1 << 5, // ice world
}
