namespace GeckoPeeks.Ui.State;

// Plain records for the Contract 2 state message. The host fills them from the probe, the log,
// the cloud poller, the updater and the settings. UiStateJson writes them out. Nothing here
// touches WinForms, so the net8.0 test project compiles these files as they are.

public sealed record AppInfo(string Version, string Profile, bool Demo);

public sealed record LatencyFigure(double? AvgMs, double? LossPct, string Figure);

public sealed record HopView(
    int N,
    string Ip,
    string Host,
    double? RttMs,
    bool Responded,
    bool ReachedTarget,
    bool Changed,
    bool IsPrivate,
    long? Asn,
    string? AsName,
    string Network = "",
    string NameSource = "",
    string CityName = "");

/// <param name="Kind">"anycast" or "dns".</param>
public sealed record TargetView(string Ip, string Label, string Kind, bool InRegion);

/// <summary>The region block of the state message (contract 6.1).</summary>
/// <param name="Basis">"manual", "country", "continent", "saved" or "default".</param>
public sealed record RegionView(
    string Id,
    string ShortName,
    string Name,
    bool Auto,
    string Basis,
    string ServerCity,
    bool CityConfirmed,
    double? Lat,
    double? Lon,
    string LolStatusId,
    string ValorantShard,
    TargetView Target,
    string LastTestTarget);

/// <summary>One row of the region picker list.</summary>
public sealed record RegionListItem(string Id, string ShortName, string Name, string ServerCity, bool CityConfirmed);

/// <param name="Source">"builtin", "cache" or "worker".</param>
public sealed record RefDataView(string Version, string Source, double? AgeSec);

public sealed record ProbeView
{
    public bool Paused { get; init; }
    public bool LeagueRunning { get; init; }

    /// <summary>"ip:port" the running League match connected to (League's own match log), else empty.</summary>
    public string MatchServer { get; init; } = "";

    /// <summary>True when that server is the address the tests trace anyway.</summary>
    public bool MatchSameAsTarget { get; init; }
    public bool TestRunning { get; init; }
    public string Phase { get; init; } = "Idle";
    public string Mode { get; init; } = "";
    public DateTime? NextDueUtc { get; init; }
    public string LastError { get; init; } = "";
    public string ActivityLine { get; init; } = "";

    /// <summary>Why no more tests run (the one Dev probe is spent), else empty. Then there is no next-due time.</summary>
    public string NoMoreTests { get; init; } = "";
    public bool HasRoute { get; init; }
    public string RouteLine { get; init; } = "";
    public string Upstream { get; init; } = "";
    public string HandoffNetwork { get; init; } = "";
    public string HandoffCity { get; init; } = "";
    public string HandoffCityName { get; init; } = "";

    /// <summary>"hostname", "asn" or "".</summary>
    public string UpstreamSource { get; init; } = "";

    /// <summary>"hostname", "asn" or "".</summary>
    public string HandoffSource { get; init; } = "";
    public long? UpstreamAsn { get; init; }

    /// <summary>Every ASN of the upstream provider (refdata), so "in use now" matches any of them.</summary>
    public IReadOnlyList<long> UpstreamAsns { get; init; } = Array.Empty<long>();
    public double? HandoffLat { get; init; }
    public double? HandoffLon { get; init; }
    public LatencyFigure LastHop { get; init; } = new(null, null, "—");
    public LatencyFigure Riot { get; init; } = new(null, null, "—");
    public string ContextLine { get; init; } = "";
    public string ChangeLine { get; init; } = "";
    public IReadOnlyList<int> ChangedHops { get; init; } = Array.Empty<int>();
    public string TestTimestamp { get; init; } = "";
    public string Adapter { get; init; } = "";
    public string Wifi { get; init; } = "";
    public string Gateway { get; init; } = "";
    public bool Vpn { get; init; }
    public bool League { get; init; }
    public IReadOnlyList<HopView> Hops { get; init; } = Array.Empty<HopView>();
    public bool HopsAddressesOnly { get; init; }
    public string Tooltip { get; init; } = "";
}

public sealed record LogView(
    string Path,
    string Folder,
    string FileName,
    string Health,
    string Chip,
    string ChipTone,
    string Status,
    string StatusTone,
    int UnsavedRows);

/// <param name="T">Local CSV time as "yyyy-MM-ddTHH:mm:ss", no zone.</param>
public sealed record HistoryRow(
    string T,
    string Upstream,
    string HandoffNetwork,
    string HandoffCity,
    double? LastHopAvg,
    double? LastHopLoss,
    double? RiotAvg,
    double? RiotLoss,
    bool League,
    bool Vpn,
    string Adapter,
    string HandoffCityName = "",
    bool NamedLater = false);

/// <param name="EdgeJson">The Worker /v1/edge object as raw JSON, or null.</param>
/// <param name="PulseJson">The Worker /v1/pulse object as raw JSON, or null.</param>
/// <param name="RiotStatusJson">The Worker /v1/riot-status object as raw JSON, or null.</param>
public sealed record CloudView(
    bool Enabled,
    string Status,
    string Error,
    double? EdgeAgeSec,
    double? PulseAgeSec,
    string? EdgeJson,
    string? PulseJson,
    string? RiotStatusJson = null,
    double? RiotStatusAgeSec = null,
    RefDataView? RefData = null)
{
    public static CloudView Off { get; } = new(false, "off", "", null, null, null, null);
}

public sealed record UpdateView(
    string State,
    string Current,
    string? Available,
    double? Progress,
    string Notes,
    string ReleaseUrl,
    string Error,
    DateTime? LastCheckUtc,
    bool AutoInstall);

/// <param name="Region">"auto" or the manual region id.</param>
public sealed record SettingsView(bool StartWithWindows, bool AutoInstallUpdates, bool CloudEnabled, string Region = "auto");

/// <param name="Region">Null writes the built-in default region (na1).</param>
/// <param name="Regions">Null writes an empty list.</param>
public sealed record UiState(
    AppInfo App,
    ProbeView Probe,
    LogView Log,
    IReadOnlyList<HistoryRow> History,
    CloudView Cloud,
    UpdateView Update,
    SettingsView Settings,
    RegionView? Region = null,
    IReadOnlyList<RegionListItem>? Regions = null);
