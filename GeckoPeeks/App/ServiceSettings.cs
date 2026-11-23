using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeckoPeeks.Cloud;

namespace GeckoPeeks.App;

/// <summary>
/// Which other services are on (all are off until the user turns one on), and the
/// "Playing on console" switch. File: {"on":["valve-sdr"],"consolePlaying":false}.
/// A null path keeps it in memory only.
/// </summary>
public sealed class ServiceSettings
{
    public const string FileName = "service-settings.json";
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);

    private readonly string? _path;
    private readonly object _lock = new();
    private readonly SortedSet<string> _on = new(StringComparer.Ordinal);
    private bool _consolePlaying;

    public ServiceSettings(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        Load();
    }

    public static string? PathFor(RunProfile profile, string? devRoot) => StorePaths.For(profile, devRoot, FileName);

    /// <summary>Raised after a change, on the caller's thread.</summary>
    public event Action? Changed;

    public bool IsOn(string id)
    {
        lock (_lock)
            return _on.Contains(id);
    }

    public IReadOnlyList<string> On
    {
        get
        {
            lock (_lock)
                return _on.ToArray();
        }
    }

    public bool ConsolePlaying
    {
        get
        {
            lock (_lock)
                return _consolePlaying;
        }
    }

    public void Set(string id, bool on)
    {
        if (!IdPattern.IsMatch(id ?? ""))
            return;
        bool changed;
        lock (_lock)
            changed = on ? _on.Add(id!) : _on.Remove(id!);
        if (changed)
            SaveAndRaise();
    }

    public void SetConsolePlaying(bool playing)
    {
        lock (_lock)
        {
            if (_consolePlaying == playing)
                return;
            _consolePlaying = playing;
        }

        SaveAndRaise();
    }

    private void SaveAndRaise()
    {
        Save();
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A listener problem must not undo the setting.
        }
    }

    private void Save()
    {
        if (_path == null)
            return;
        string json;
        lock (_lock)
        {
            var on = new JsonArray();
            foreach (var id in _on)
                on.Add(id);
            json = new JsonObject { ["on"] = on, ["consolePlaying"] = _consolePlaying }.ToJsonString();
        }

        try
        {
            StorePaths.WriteAtomic(_path, json);
        }
        catch
        {
            // The choice still holds for this session.
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
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (root.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in on.EnumerateArray())
                {
                    if (x.ValueKind == JsonValueKind.String && x.GetString() is { } id && IdPattern.IsMatch(id))
                        _on.Add(id);
                }
            }

            _consolePlaying = root.TryGetProperty("consolePlaying", out var cp) && cp.ValueKind == JsonValueKind.True;
        }
        catch
        {
            _on.Clear();
            _consolePlaying = false;
        }
    }
}
