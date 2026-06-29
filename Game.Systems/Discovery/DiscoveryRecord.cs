using Game.Universe;

namespace Game.Systems.Discovery;

/// <summary>One discovered object as the game sees it (server's authoritative view once synced).</summary>
public sealed class DiscoveryRecord
{
    public string ObjectId { get; init; } = "";
    public string Kind { get; init; } = "";        // "star" | "planet" | "moon"
    public string StarId { get; init; } = "";
    public string Designation { get; init; } = "";
    public string Discoverer { get; init; } = "";
    public DateTime DiscoveredAtUtc { get; init; }

    /// <summary>A human-readable name derived from the ids: the star's proper name
    /// (<see cref="Naming.StarName"/>) with an exoplanet-style suffix — a lowercase letter for
    /// planets ("Helix Prime b", "Helix Prime c") and a roman numeral for moons ("Helix Prime b I").
    /// Falls back to <see cref="Designation"/> (then <see cref="ObjectId"/>) if the ids don't parse.</summary>
    public string Name
    {
        get
        {
            // ObjectId = "{galaxy}-{star}[-PP[-MM]]" (see ObjectId): seg[1] is the star's numeric id.
            string[] seg = ObjectId.Split('-');
            if (seg.Length < 2 || !ulong.TryParse(seg[1], out ulong starId))
                return string.IsNullOrEmpty(Designation) ? ObjectId : Designation;

            string name = Naming.StarName(starId);
            // Planet index → exoplanet letter ('b' is the first planet, the star itself being 'a').
            if (seg.Length >= 3 && int.TryParse(seg[2], out int pi) && pi is >= 0 and < 25)
            {
                name += $" {(char)('b' + pi)}";
                if (seg.Length >= 4 && int.TryParse(seg[3], out int mi) && mi >= 0)
                    name += $" {Naming.Roman(mi + 1)}";
            }
            return name;
        }
    }
}

/// <summary>A discovery to POST to the server. <see cref="Meta"/> is free-form display data
/// (class/type/temp…) stored as JSON; omit it (null) when there's nothing useful.</summary>
public sealed class ReportRequest
{
    public string ObjectId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string StarId { get; init; } = "";
    public string Designation { get; init; } = "";
    public string Discoverer { get; init; } = "";
    public IReadOnlyDictionary<string, object?>? Meta { get; init; }
}

/// <summary>The server's reply to a report: the authoritative record plus whether *this* request
/// created it (i.e. the caller is the first finder).</summary>
public readonly record struct DiscoveryResult(DiscoveryRecord Record, bool IsNew);
