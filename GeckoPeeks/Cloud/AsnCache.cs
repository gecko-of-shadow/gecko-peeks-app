using System.Text.Json;
using System.Text.Json.Nodes;
using GeckoPeeks.App;

namespace GeckoPeeks.Cloud;

/// <summary>
/// ASN rows for public router addresses, kept for 30 days. A null path keeps them in memory
/// only. The file is {"schema":1,"entries":{"&lt;ip&gt;":{"asn":6939,"name":"...","country":"US","t":1763700000}}}.
/// </summary>
public sealed class AsnCache
{
    public const string FileName = "asn-cache.json";
    public const int MaxEntries = 4096;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly string? _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private sealed record Entry(long Asn, string? Name, string? Country, long T);

    /// <param name="path">Null = memory only.</param>
    public AsnCache(string? path, Func<DateTime>? utcNow = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Production %LOCALAPPDATA%\GeckoPeeks\asn-cache.json, Dev &lt;devRoot&gt;\asn-cache.json, else null.</summary>
    public static string? PathFor(RunProfile profile, string? devRoot) => StorePaths.For(profile, devRoot, FileName);

    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>Null when missing, expired (30 days) or no ASN.</summary>
    public AsnResult? Get(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return null;
        var key = ip.Trim();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return null;
            if (Now() - entry.T > (long)MaxAge.TotalSeconds)
                return null;
            return new AsnResult(key, entry.Asn, entry.Name, entry.Country);
        }
    }

    /// <summary>Ignores rows with a null Asn or a non-public Ip.</summary>
    public void Put(AsnResult row)
    {
        if (row?.Asn is not { } asn || asn < 1 || asn > 4294967295 || !PublicIp.IsPublic(row.Ip))
            return;
        var key = row.Ip.Trim();
        lock (_lock)
        {
            _entries.TryGetValue(key, out var old);
            _entries[key] = new Entry(asn, row.Name ?? old?.Name, row.Country ?? old?.Country, Now());
            Trim();
            _dirty = true;
        }
    }

    /// <summary>Writes when dirty, by temp file and move. Errors are ignored.</summary>
    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty || _path == null)
                return;
            var entries = new JsonObject();
            foreach (var (ip, e) in _entries)
            {
                entries[ip] = new JsonObject
                {
                    ["asn"] = e.Asn,
                    ["name"] = e.Name,
                    ["country"] = e.Country,
                    ["t"] = e.T,
                };
            }

            json = new JsonObject { ["schema"] = 1, ["entries"] = entries }.ToJsonString();
            _dirty = false;
        }

        try
        {
            StorePaths.WriteAtomic(_path, json);
        }
        catch
        {
            lock (_lock)
                _dirty = true;
        }
    }

    private void Load()
    {
        if (_path == null)
            return;
        try
        {
            if (!File.Exists(_path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1
                || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in entries.EnumerateObject())
            {
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object || !PublicIp.IsPublic(prop.Name))
                    continue;
                if (!e.TryGetProperty("asn", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out var asn)
                    || asn < 1 || asn > 4294967295)
                    continue;
                if (!e.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var time))
                    continue;
                _entries[prop.Name.Trim()] = new Entry(asn, Text(e, "name"), Text(e, "country"), time);
            }

            Trim();
        }
        catch
        {
            _entries.Clear(); // a broken file loads as empty
        }
    }

    /// <summary>Caller holds the lock. Drops the oldest rows over the cap.</summary>
    private void Trim()
    {
        var over = _entries.Count - MaxEntries;
        if (over <= 0)
            return;
        foreach (var key in _entries.OrderBy(kv => kv.Value.T).Take(over).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }

    private long Now() => new DateTimeOffset(DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Where the small app stores live, and a safe write.</summary>
internal static class StorePaths
{
    /// <summary>Production %LOCALAPPDATA%\GeckoPeeks\&lt;file&gt;, Dev &lt;devRoot&gt;\&lt;file&gt;, else null.</summary>
    public static string? For(RunProfile profile, string? devRoot, string fileName)
    {
        switch (profile)
        {
            case RunProfile.Production:
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "GeckoPeeks", fileName);
            case RunProfile.Dev:
                return string.IsNullOrWhiteSpace(devRoot) ? null : Path.Combine(Path.GetFullPath(devRoot.Trim()), fileName);
            default:
                return null;
        }
    }

    private static readonly object WriteGate = new();

    /// <summary>Temp file plus move. Throws on failure; callers ignore it.</summary>
    public static void WriteAtomic(string path, string text)
    {
        lock (WriteGate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
    }
}
