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
