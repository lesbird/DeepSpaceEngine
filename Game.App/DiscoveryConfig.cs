using System.Text.Json;

namespace Game.App;

/// <summary>
/// Player-side discovery settings, persisted to <c>discovery.json</c> next to <c>tuning.json</c>.
/// Holds the player name, the server URL + API key, and an enable flag. Gitignored because it
/// carries the API key; a committed <c>discovery.sample.json</c> documents the shape. Mirrors the
/// load/save style of <see cref="TuningConfig"/>.
/// </summary>
public sealed class DiscoveryConfig
{
    public bool Enabled { get; set; }                       // opt-in: nothing is sent until enabled
    public string PlayerName { get; set; } = "";
    public string ServerUrl { get; set; } = "http://localhost:8080";
    public string ApiKey { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static DiscoveryConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                DiscoveryConfig? c = JsonSerializer.Deserialize<DiscoveryConfig>(File.ReadAllText(path));
                if (c != null) return c;
            }
        }
        catch
        {
            // Corrupt/unreadable file → fall back to defaults rather than failing launch.
        }
        return new DiscoveryConfig();
    }

    public bool Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
