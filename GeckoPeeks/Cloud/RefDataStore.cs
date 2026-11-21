using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeckoPeeks.App;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Cloud;

/// <param name="Source">"builtin", "cache" or "worker".</param>
public sealed record RefDataStatus(string Version, string Source, double? AgeSec);

/// <summary>
/// Holds the reference data in use: the built-in copy, a newer cached copy from the Worker, or
/// a fresh Worker copy. The cache file is {"etag":"...","fetchedAt":"ISO","data":{...}}.
/// Every change calls <see cref="RefData.SetCurrent"/>.
/// </summary>
public sealed class RefDataStore
{
    public const string FileName = "refdata-cache.json";

    private readonly string? _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private RefData _current;
    private string _source = "builtin";
    private string? _etag;
    private DateTime? _fetchedAt;
    private string? _json;

    public RefDataStore(string? cachePath, Func<DateTime>? utcNow = null)
    {
        _path = string.IsNullOrWhiteSpace(cachePath) ? null : cachePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _current = RefData.BuiltIn;
        LoadCache();
        RefData.SetCurrent(_current);
    }

    public static string? PathFor(RunProfile profile, string? devRoot) => StorePaths.For(profile, devRoot, FileName);

    /// <summary>Raised after <see cref="Apply"/> changed the data. Any thread.</summary>
    public event Action? Changed;

    public RefData Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public RefDataStatus Status
    {
        get
        {
            lock (_lock)
            {
                double? age = _fetchedAt is { } at ? Math.Max(0, Math.Round((_utcNow() - at).TotalSeconds)) : null;
                return new RefDataStatus(_current.Version, _source, _source == "builtin" ? null : age);
            }
        }
    }

    public string? ETag
    {
        get
        {
            lock (_lock)
                return _etag;
        }
    }

    /// <summary>
    /// Validates, saves the cache, sets the current data and raises Changed. False when the JSON is not valid.
    /// Data older than the built-in copy or the data in use is ignored (true, nothing changes):
    /// the Worker is deployed apart from the app and can serve older tables than a new build.
    /// </summary>
    public bool Apply(string json, string? etag)
    {
        if (!RefData.TryParse(json, out var data, out _) || data == null)
            return false;

        if (CompareVersions(data.Version, RefData.BuiltIn.Version) < 0)
            return true;

        lock (_lock)
        {
            if (CompareVersions(data.Version, _current.Version) < 0)
                return true;
            _current = data;
            _source = "worker";
            _etag = string.IsNullOrWhiteSpace(etag) ? null : etag.Trim();
            _fetchedAt = _utcNow();
            _json = json;
            SaveLocked();
        }

        RefData.SetCurrent(data);
        Raise();
        return true;
    }

    /// <summary>A 304 from the Worker: the data is current, so its age starts again.</summary>
    public void MarkNotModified()
    {
        lock (_lock)
        {
            _fetchedAt = _utcNow();
            if (_source == "cache")
                _source = "worker";
            SaveLocked();
        }
    }

    private void LoadCache()
    {
        if (_path == null)
            return;
        try
        {
            if (!File.Exists(_path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                return;
            var json = dataEl.GetRawText();
            if (!RefData.TryParse(json, out var data, out _) || data == null)
                return;
            // An app update can ship newer data than an old cache.
            if (CompareVersions(data.Version, RefData.BuiltIn.Version) < 0)
                return;

            _current = data;
            _source = "cache";
            _json = json;
            _etag = root.TryGetProperty("etag", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (root.TryGetProperty("fetchedAt", out var f) && f.ValueKind == JsonValueKind.String
                && DateTime.TryParse(f.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                _fetchedAt = at;
        }
        catch
        {
            // A broken cache is ignored; the built-in copy stays.
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void SaveLocked()
    {
        if (_path == null || _json == null)
            return;
        try
        {
            var root = new JsonObject
            {
                ["etag"] = _etag,
                ["fetchedAt"] = (_fetchedAt ?? _utcNow()).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["data"] = JsonNode.Parse(_json),
            };
            StorePaths.WriteAtomic(_path, root.ToJsonString());
        }
        catch
        {
            // The cache is a convenience.
        }
    }

    /// <summary>"YYYY.MM.DD-N" compared by number; anything else compares as oldest.</summary>
    internal static int CompareVersions(string? a, string? b) => Key(a).CompareTo(Key(b));

    private static (int, int, int, int) Key(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return (0, 0, 0, 0);
        var parts = version.Split('.', '-');
        if (parts.Length != 4)
            return (0, 0, 0, 0);
        var n = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                return (0, 0, 0, 0);
        }

        return (n[0], n[1], n[2], n[3]);
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A listener problem must not break the store.
        }
    }
}
