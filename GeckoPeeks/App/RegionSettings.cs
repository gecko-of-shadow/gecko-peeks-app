using System.Text.Json;
using System.Text.Json.Nodes;
using GeckoPeeks.Cloud;
using GeckoPeeks.Routing;

namespace GeckoPeeks.App;

/// <param name="Basis">"manual", "country", "continent", "saved" or "default".</param>
public sealed record RegionSelection(RegionInfo Region, bool Auto, string Basis);

/// <summary>
/// The region choice: automatic (from the Cloudflare edge country) or one region picked by hand.
/// File: {"mode":"auto","manualId":null,"lastAutoId":"na1"}. A null path keeps it in memory only.
/// </summary>
public sealed class RegionSettings
{
    public const string FileName = "region-settings.json";
    public const string AutoMode = "auto";
    public const string ManualMode = "manual";

    private readonly string? _path;
    private readonly object _lock = new();
    private string _mode = AutoMode;
    private string? _manualId;
    private string? _lastAutoId;

    /// <param name="path">Null = memory only.</param>
    public RegionSettings(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        Load();
    }

    /// <summary>Production %LOCALAPPDATA%\GeckoPeeks\region-settings.json, Dev &lt;devRoot&gt;\region-settings.json, else null.</summary>
    public static string? PathFor(RunProfile profile, string? devRoot) => StorePaths.For(profile, devRoot, FileName);

    /// <summary>"auto" (default) or "manual".</summary>
    public string Mode
    {
        get
        {
            lock (_lock)
                return _mode;
        }
        set
        {
            lock (_lock)
                _mode = value == ManualMode ? ManualMode : AutoMode;
        }
    }

    public string? ManualId
    {
        get
        {
            lock (_lock)
                return _manualId;
        }
        set
        {
            lock (_lock)
                _manualId = CleanId(value);
        }
    }

    public string? LastAutoId
    {
        get
        {
            lock (_lock)
                return _lastAutoId;
        }
        set
        {
            lock (_lock)
                _lastAutoId = CleanId(value);
        }
    }

    /// <summary>Temp file plus move. Errors are ignored.</summary>
    public void Save()
    {
        if (_path == null)
            return;
        string json;
        lock (_lock)
        {
            json = new JsonObject
            {
                ["mode"] = _mode,
                ["manualId"] = _manualId,
                ["lastAutoId"] = _lastAutoId,
            }.ToJsonString();
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
            Mode = Text(root, "mode") ?? AutoMode;
            ManualId = Text(root, "manualId");
            LastAutoId = Text(root, "lastAutoId");
        }
        catch
        {
            _mode = AutoMode;
            _manualId = null;
            _lastAutoId = null;
        }
    }

    private static string? CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var text = id.Trim().ToLowerInvariant();
        return text.Length <= 8 && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ? text : null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Picks the region. Automatic: the edge country, else the edge continent, else the last
/// automatic pick, else the default region. Manual: the saved id. An unknown saved id falls
/// back to automatic.
/// </summary>
public sealed class RegionService
{
    private readonly RegionSettings _settings;
    private readonly Func<RefData> _refData;
    private readonly object _lock = new();
    private string? _country;
    private string? _continent;
    private bool _hasEdge;
    private RegionSelection _current;

    public RegionService(RegionSettings settings, Func<RefData> refData)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _refData = refData ?? (() => RefData.Current);
        _current = Compute(out _);
    }

    public RegionSelection Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>Raised only when Region.Id, Region.TargetIp or Auto changed. Any thread.</summary>
    public event Action<RegionSelection>? Changed;

    public bool SetAuto()
    {
        _settings.Mode = RegionSettings.AutoMode;
        _settings.Save();
        Recompute();
        return true;
    }

    /// <summary>False when the id is unknown. Aliases are allowed.</summary>
    public bool SetManual(string id)
    {
        var region = Data().FindRegion(id);
        if (region == null)
            return false;
        _settings.Mode = RegionSettings.ManualMode;
        _settings.ManualId = region.Id;
        _settings.Save();
        Recompute();
        return true;
    }

    /// <summary>Reads "country" and "continent" from the Worker /v1/edge object.</summary>
    public void OnEdgeJson(string? edgeJson)
    {
        if (string.IsNullOrWhiteSpace(edgeJson))
            return;
        string? country = null;
        string? continent = null;
        try
        {
            using var doc = JsonDocument.Parse(edgeJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            country = root.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            continent = root.TryGetProperty("continent", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }

        lock (_lock)
        {
            _country = country;
            _continent = continent;
            _hasEdge = true;
        }

        Recompute();
    }

    public void OnRefDataChanged() => Recompute();

    private RefData Data()
    {
        try
        {
            return _refData() ?? RefData.Current;
        }
        catch
        {
            return RefData.Current;
        }
    }

    private void Recompute()
    {
        RegionSelection? raised = null;
        bool saveAuto;
        lock (_lock)
        {
            var next = Compute(out saveAuto);
            var old = _current;
            _current = next;
            if (old.Region.Id != next.Region.Id || old.Region.TargetIp != next.Region.TargetIp || old.Auto != next.Auto)
                raised = next;
        }

        if (saveAuto)
            _settings.Save();

        if (raised != null)
        {
            try
            {
                Changed?.Invoke(raised);
            }
            catch
            {
                // A listener problem must not break region picking.
            }
        }
    }

    /// <summary>Caller holds the lock (or is the constructor).</summary>
    private RegionSelection Compute(out bool saveAuto)
    {
        saveAuto = false;
        var data = Data();
        if (_settings.Mode == RegionSettings.ManualMode && data.FindRegion(_settings.ManualId) is { } manual)
            return new RegionSelection(manual, false, "manual");

        if (_hasEdge)
        {
            var region = data.RegionForLocation(_country, _continent, out var basis);
            // An edge with no usable country or continent (XX, T1, no continent) gives "default".
            // Then the saved automatic pick still wins over the default region.
            if (basis != "default")
            {
                if (!string.Equals(_settings.LastAutoId, region.Id, StringComparison.Ordinal))
                {
                    _settings.LastAutoId = region.Id;
                    saveAuto = true;
                }

                return new RegionSelection(region, true, basis);
            }
        }

        if (data.FindRegion(_settings.LastAutoId) is { } saved)
            return new RegionSelection(saved, true, "saved");

        return new RegionSelection(data.DefaultRegion, true, "default");
    }
}
