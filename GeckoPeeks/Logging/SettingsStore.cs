using System.Text.Json;

namespace GeckoPeeks.Logging;

public sealed class SavedHop
{
    public int Number { get; set; }
    public string Ip { get; set; } = "";
    public string Hostname { get; set; } = "";
    public long? RttMs { get; set; }
    public bool Responded { get; set; }
    public bool ReachedTarget { get; set; }
    public long? Asn { get; set; }
}

public sealed class SavedResult
{
    public string Timestamp { get; set; } = "";
    public string Upstream { get; set; } = "";
    public string HandoffNetwork { get; set; } = "";
    public string HandoffCity { get; set; } = "";
    public string HandoffRttMs { get; set; } = "";
    public string RiotHopAvg { get; set; } = "";
    public string LastHopAvg { get; set; } = "";
    public string FullPath { get; set; } = "";

    // v2.1. Older settings files have none of these, and still load.
    public string TargetIp { get; set; } = "";
    public string UpstreamSource { get; set; } = "";
    public string HandoffSource { get; set; } = "";
    public long? UpstreamAsn { get; set; }
}

public sealed class AppSettings
{
    public bool Paused { get; set; }
    public SavedResult? Last { get; set; }
    public List<SavedHop> Hops { get; set; } = new();
}

/// <summary>
/// Paused flag and last result, written beside the CSV by temp file plus move.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object SaveGate = new();
    private static readonly Dictionary<string, long> WrittenRevision = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <param name="revision">
    /// Snapshot order from the caller. A write older than one already stored for
    /// this path is ignored, so a copy taken before pause cannot clear it.
    /// Callers that omit it (revision 0) still replace one another.
    /// </param>
    public static void Save(string path, AppSettings settings, long revision = 0)
    {
        // One shared path+".tmp" with two callers (pause on the UI thread, a
        // finished test on the pool) lets the second Move throw. The lock makes
        // each write finish before the next one starts. The revision check drops
        // a snapshot that was copied earlier than the one already on disk.
        lock (SaveGate)
        {
            var key = Path.GetFullPath(path);
            if (WrittenRevision.TryGetValue(key, out var written) && revision < written)
                return;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            var json = JsonSerializer.Serialize(settings, Json);
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            WrittenRevision[key] = revision;
        }
    }
}
