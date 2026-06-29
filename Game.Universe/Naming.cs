using Engine.Core;

namespace Game.Universe;

/// <summary>
/// Deterministic proper names for stars and galaxies, derived purely from an object's 64-bit id — so a
/// name is never stored, only regenerated (the same id always yields the same name, on every machine).
///
/// <para><b>Stars</b> get evocative two-word names ("Helix Prime", "Crimson Vega", "Lyra Tau") drawn
/// from a handful of word lists and patterns. The combinatorial space is large (tens of thousands of
/// forms) but deliberately <i>not</i> unique — with billions of stars, names repeat. That's by design:
/// the goal is flavour and variety across a large portion of the catalog, while the numeric
/// <see cref="Star.Id"/> remains the unique, searchable handle.</para>
///
/// <para><b>Galaxies</b> get catalog-style designations ("M 31", "NGC 224", "UGC 12158") with the
/// prefix weighted toward the larger real catalogs, so the field reads like an astronomical survey.</para>
/// </summary>
public static class Naming
{
    // Proper-noun cores — the recognisable body of a star name.
    private static readonly string[] Roots =
    {
        "Helix", "Halo", "Vega", "Orion", "Lyra", "Nova", "Pulsar", "Quasar", "Hyperion", "Tycho",
        "Kepler", "Atlas", "Cygnus", "Draco", "Phoenix", "Aether", "Solace", "Mirage", "Cinder", "Ember",
        "Onyx", "Cobalt", "Vesper", "Zephyr", "Lumen", "Aurora", "Cradle", "Tempest", "Specter", "Sable",
        "Calliope", "Icarus", "Daedalus", "Perseus", "Andros", "Cassia", "Lyric", "Halcyon", "Maris", "Corvus",
        "Sirius", "Rigel", "Altair", "Antares", "Polaris", "Mizar", "Castor", "Pollux", "Talos", "Erebus",
        "Nyx", "Selene", "Helios", "Astra", "Nimbus", "Cirrus", "Boreas", "Caldera", "Meridian", "Tiber",
        "Galen", "Wren", "Caspian", "Soren",
    };

    // Modifiers — set the mood ("Crimson Vega", "Silent Reach").
    private static readonly string[] Adjectives =
    {
        "Crimson", "Azure", "Golden", "Silent", "Burning", "Frozen", "Hollow", "Radiant", "Distant", "Ancient",
        "Shattered", "Emerald", "Obsidian", "Pale", "Verdant", "Scarlet", "Amber", "Dusken", "Gilded", "Wandering",
        "Forsaken", "Eternal", "Drifting", "Veiled", "Iron", "Hidden", "Starlit", "Twilight", "Ashen", "Umbral",
        "Lucent", "Lone", "Vivid", "Quiet", "Velvet", "Cardinal", "Argent", "Solar", "Lunar", "Astral",
        "Nether", "Pallid", "Sunder", "Glass", "Bright", "Dim", "Hallowed", "Stormborn",
    };

    // Place-like suffixes — give a star the feel of a settled waypoint ("Halo Reach", "Atlas Gate").
    private static readonly string[] Designators =
    {
        "Prime", "Reach", "Gate", "Station", "Major", "Minor", "Nexus", "Spire", "Veil", "Crown",
        "Throne", "Haven", "Expanse", "Drift", "Hollow", "Watch", "Bastion", "Refuge", "Verge", "Hold",
        "Crossing", "Beacon", "Anchor", "Terminus", "Junction", "Outpost", "Frontier", "Threshold", "Sanctum", "Vault",
        "Rest", "Light", "Fall", "Rise", "Edge", "Deep", "Span", "Wake", "Mark", "Gateway",
    };

    private static readonly string[] Greek =
    {
        "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa",
        "Lambda", "Sigma", "Tau", "Omega", "Phi", "Psi", "Chi", "Omicron",
    };

    /// <summary>A flavourful, deterministic name for a star (not guaranteed unique — see class summary).</summary>
    public static string StarName(ulong id)
    {
        // Three independent hash streams: pattern choice, first word, second word.
        ulong h0 = Hashing.Mix64(id ^ 0x57A2_C0DEUL);
        ulong h1 = Hashing.Mix64(h0);
        ulong h2 = Hashing.Mix64(h1);

        return (h0 % 6) switch
        {
            0 => $"{Pick(Roots, h1)} {Pick(Designators, h2)}",     // Halo Reach
            1 => $"{Pick(Adjectives, h1)} {Pick(Roots, h2)}",      // Crimson Vega
            2 => $"{Pick(Roots, h1)} {Roman(1 + (int)(h2 % 30))}", // Vega VII
            3 => $"{Pick(Roots, h1)} {Pick(Greek, h2)}",           // Lyra Tau
            4 => $"{Pick(Adjectives, h1)} {Pick(Designators, h2)}",// Silent Reach
            _ => $"{Pick(Roots, h1)} {Pick(Designators, h2)}",     // Helix Prime
        };
    }

    /// <summary>A catalog-style designation for a galaxy ("M 31", "NGC 224", "UGC 12158").</summary>
    public static string GalaxyName(ulong id)
    {
        ulong h = Hashing.Mix64(id ^ 0xA17E_6A1AUL);
        ulong n = Hashing.Mix64(h);   // the running catalog number
        return (h % 100) switch
        {
            < 5  => $"M {1 + (int)(n % 110)}",      // Messier — small, famous list
            < 30 => $"NGC {1 + (int)(n % 7840)}",   // New General Catalogue
            < 50 => $"IC {1 + (int)(n % 5386)}",    // Index Catalogue
            < 78 => $"UGC {1 + (int)(n % 12921)}",  // Uppsala General Catalogue
            < 92 => $"PGC {1 + (int)(n % 900000)}", // Principal Galaxies Catalogue
            _    => $"ESO {1 + (int)(n % 600)}-{1 + (int)(Hashing.Mix64(n) % 60)}", // ESO field-number form
        };
    }

    private static string Pick(string[] words, ulong h) => words[(int)(h % (ulong)words.Length)];

    /// <summary>Roman numeral for a small positive integer (the values used in star names, 1–39).</summary>
    public static string Roman(int n)
    {
        // Only the symbols needed for 1..39.
        string r = "";
        while (n >= 10) { r += "X"; n -= 10; }
        if (n == 9) { r += "IX"; n = 0; }
        if (n >= 5) { r += "V"; n -= 5; }
        if (n == 4) { r += "IV"; n = 0; }
        while (n >= 1) { r += "I"; n -= 1; }
        return r;
    }
}
