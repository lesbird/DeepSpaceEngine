using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game.Systems.Discovery;

/// <summary>
/// Thin async REST transport for the discovery API (see server/). One <see cref="HttpClient"/>;
/// every call is fully try/caught and returns null on any failure (network down, bad URL, non-2xx,
/// malformed JSON) so callers never have to guard — the game degrades to "offline", it never throws.
/// <see cref="ServerUrl"/> and <see cref="ApiKey"/> are mutable so the config UI can update them live.
/// </summary>
public sealed class DiscoveryClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // ObjectId ↔ objectId, etc.
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // omit Meta when absent
    };

    private readonly HttpClient _http;

    public string ServerUrl { get; set; }
    public string ApiKey { get; set; }

    public DiscoveryClient(string serverUrl, string apiKey)
    {
        ServerUrl = serverUrl ?? "";
        ApiKey = apiKey ?? "";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    private string Endpoint(string file) => $"{ServerUrl.TrimEnd('/')}/{file}";

    /// <summary>GET all discoveries (the launch sync). Null on failure.</summary>
    public async Task<IReadOnlyList<DiscoveryRecord>?> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage resp = await _http.GetAsync(Endpoint("discoveries.php"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            ListDto? dto = await resp.Content.ReadFromJsonAsync<ListDto>(Json, ct).ConfigureAwait(false);
            if (dto?.Discoveries == null) return null;
            var list = new List<DiscoveryRecord>(dto.Discoveries.Count);
            foreach (RecordDto r in dto.Discoveries) list.Add(r.ToRecord());
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>POST one discovery. Returns the authoritative record + isNew, or null on failure.</summary>
    public async Task<DiscoveryResult?> ReportAsync(ReportRequest req, CancellationToken ct = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint("discover.php"));
            msg.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
            msg.Content = JsonContent.Create(req, options: Json);
            using HttpResponseMessage resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            RecordDto? dto = await resp.Content.ReadFromJsonAsync<RecordDto>(Json, ct).ConfigureAwait(false);
            if (dto == null) return null;
            return new DiscoveryResult(dto.ToRecord(), dto.IsNew);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    // --- Wire DTOs (camelCase via Json options) ---
    private sealed class ListDto
    {
        public List<RecordDto> Discoveries { get; set; } = new();
    }

    private sealed class RecordDto
    {
        public string ObjectId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string StarId { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Discoverer { get; set; } = "";
        public DateTimeOffset DiscoveredAt { get; set; }
        public bool IsNew { get; set; }

        public DiscoveryRecord ToRecord() => new()
        {
            ObjectId = ObjectId,
            Kind = Kind,
            StarId = StarId,
            Designation = Designation,
            Discoverer = Discoverer,
            DiscoveredAtUtc = DiscoveredAt.UtcDateTime,
        };
    }
}
