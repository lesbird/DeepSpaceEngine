using System.Collections.Concurrent;
using Game.Universe;

namespace Game.Systems.Discovery;

/// <summary>
/// Game-facing discovery layer over <see cref="DiscoveryClient"/>: a thread-safe cache of known
/// discoveries plus the report policy. The render/update thread only reads the cache and fires async
/// sends; HttpClient continuations mutate the (concurrent) cache — nothing ever blocks the frame, and
/// failures degrade to "offline" rather than throwing.
///
/// Report policy is first-finder-wins with optimistic local credit: reaching an unknown object adds a
/// local record (you, now) immediately so the HUD updates, then POSTs; the server's authoritative
/// reply overwrites it (so if someone beat you, their name shows). Failed sends go to a retry queue
/// flushed from <see cref="Update"/>.
/// </summary>
public sealed class DiscoveryService
{
    public enum SyncState { Idle, Loading, Ready, Offline }

    private const double FlushIntervalSec = 10.0; // how often the retry queue is drained

    private readonly DiscoveryClient _client;
    private readonly ConcurrentDictionary<string, DiscoveryRecord> _byId = new();
    private readonly ConcurrentQueue<ReportRequest> _pending = new();
    private double _flushTimer;

    /// <summary>When false, nothing is sent (and the launch sync is skipped). Set from config.</summary>
    public bool Enabled { get; set; }

    /// <summary>Credited as the discoverer on new finds. Reporting is skipped while blank.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>The galaxy currently containing the camera (0 in intergalactic space). Set each frame by
    /// the host; it prefixes every star/body object id, since a star id is only unique within its galaxy
    /// (see <see cref="ObjectId"/>). All stars the player can report or credit are in this galaxy.</summary>
    public ulong CurrentGalaxyId { get; set; }

    public SyncState State { get; private set; } = SyncState.Idle;
    public int Count => _byId.Count;

    public DiscoveryService(DiscoveryClient client) => _client = client;

    /// <summary>Pull the full discovery list from the server and merge it into the cache. Called once
    /// at launch (and when reporting is enabled, or via a manual re-sync).</summary>
    public async Task InitializeAsync()
    {
        State = SyncState.Loading;
        IReadOnlyList<DiscoveryRecord>? all = await _client.GetAllAsync().ConfigureAwait(false);
        if (all == null)
        {
            State = SyncState.Offline;
            return;
        }
        foreach (DiscoveryRecord r in all) _byId[r.ObjectId] = r; // server truth wins over local optimism
        State = SyncState.Ready;
    }

    /// <summary>Report a star (system entry). No-op if already known, disabled, or no player name.</summary>
    public void ReportStar(in Star sun, IReadOnlyDictionary<string, object?>? meta = null)
    {
        string id = ObjectId.Star(CurrentGalaxyId, sun);
        Report(id, "star", id, sun.Designation, meta);
    }

    /// <summary>Report a planet or moon (environment entry). Resolves the id/kind from the system.</summary>
    public void ReportBody(SolarSystem sys, CelestialBody body, IReadOnlyDictionary<string, object?>? meta = null)
    {
        if (!ObjectId.TryFor(CurrentGalaxyId, sys, body, out string id, out string kind)) return;
        Report(id, kind, ObjectId.Star(CurrentGalaxyId, sys.Sun), body.Designation, meta);
    }

    private void Report(string id, string kind, string starId, string designation,
        IReadOnlyDictionary<string, object?>? meta)
    {
        if (!Enabled) return;
        string name = PlayerName.Trim();
        if (name.Length == 0) return;

        // Optimistic local credit. TryAdd doubles as the de-dupe guard: if the object is already
        // known (ours or the server's), there's nothing to send.
        var optimistic = new DiscoveryRecord
        {
            ObjectId = id, Kind = kind, StarId = starId,
            Designation = designation, Discoverer = name, DiscoveredAtUtc = DateTime.UtcNow,
        };
        if (!_byId.TryAdd(id, optimistic)) return;

        Send(new ReportRequest
        {
            ObjectId = id, Kind = kind, StarId = starId,
            Designation = designation, Discoverer = name, Meta = meta,
        });
    }

    private void Send(ReportRequest req)
    {
        _ = Task.Run(async () =>
        {
            DiscoveryResult? res = await _client.ReportAsync(req).ConfigureAwait(false);
            if (res != null) _byId[req.ObjectId] = res.Value.Record; // reconcile to authoritative
            else _pending.Enqueue(req);                              // server unreachable → retry later
        });
    }

    /// <summary>Drive the retry queue. Call once per frame; cheap (a timer gate + at most the current
    /// backlog every <see cref="FlushIntervalSec"/> seconds).</summary>
    public void Update(double dt)
    {
        if (!Enabled || _pending.IsEmpty) return;
        _flushTimer += dt;
        if (_flushTimer < FlushIntervalSec) return;
        _flushTimer = 0;

        // Snapshot the current backlog: each Send re-enqueues on failure, so without the count cap a
        // persistently-down server would spin this loop.
        int n = _pending.Count;
        for (int i = 0; i < n && _pending.TryDequeue(out ReportRequest? req); i++) Send(req);
    }

    /// <summary>All known discoveries (local + server-synced), newest first — for the library panel.
    /// Snapshots the concurrent cache into a fresh list, so the caller can iterate without locking.</summary>
    public IReadOnlyList<DiscoveryRecord> Snapshot()
    {
        var list = new List<DiscoveryRecord>(_byId.Values);
        list.Sort((a, b) => b.DiscoveredAtUtc.CompareTo(a.DiscoveredAtUtc));
        return list;
    }

    public bool TryGet(string id, out DiscoveryRecord record) => _byId.TryGetValue(id, out record!);

    public bool TryGetStar(in Star s, out DiscoveryRecord record) => TryGet(ObjectId.Star(CurrentGalaxyId, s), out record);

    public bool TryGetBody(SolarSystem sys, CelestialBody body, out DiscoveryRecord record)
    {
        if (ObjectId.TryFor(CurrentGalaxyId, sys, body, out string id, out _)) return TryGet(id, out record);
        record = null!;
        return false;
    }
}
