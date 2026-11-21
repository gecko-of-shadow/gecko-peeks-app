using System.Globalization;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using RegionInfo = GeckoPeeks.Routing.RegionInfo;

namespace GeckoPeeks.Ui.State;

/// <summary>Small conversions from the core types to the Contract 2 records.</summary>
public static class UiMapping
{
    /// <summary>
    /// The web page gets at most this many history rows, the newest ones. At one test every
    /// 15 minutes this is about 7 months.
    /// </summary>
    public const int MaxHistoryRows = 20000;

    public static string Tone(StatusTone tone) => tone switch
    {
        StatusTone.Good => "good",
        StatusTone.Warn => "warn",
        StatusTone.Bad => "bad",
        _ => "normal",
    };

    public static string SafeTone(string? tone) => tone switch
    {
        "good" or "warn" or "bad" => tone,
        _ => "normal",
    };

    /// <summary>A CSV number ("38", "38.5", "38 ms") or null when it is blank or not a number.</summary>
    public static double? Number(string? text) =>
        LatencyLabels.TryParseMs(text, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>The CSV writes "Y"/"N". Older rows and hand edits may say Yes or true.</summary>
    public static bool IsYes(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            || t.Equals("true", StringComparison.OrdinalIgnoreCase)
            || t == "1";
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss" to "yyyy-MM-ddTHH:mm:ss". Null when the text is not that format.</summary>
    public static string? HistoryTime(string? csvTimestamp)
    {
        if (!DateTime.TryParseExact(
                csvTimestamp?.Trim(),
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
            return null;

        return local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The newest <paramref name="max"/> rows with a readable time, oldest first. Rows whose
    /// time does not parse are left out, because the page cannot place them on the chart.
    /// </summary>
    public static IReadOnlyList<HistoryRow> HistoryRows(IReadOnlyList<RouteRow> rows, int max = MaxHistoryRows) =>
        HistoryRows(rows, null, max);

    /// <summary>
    /// Same, and a row saved as Unknown takes the name <paramref name="nameLater"/> gives it from its
    /// saved hops (see <see cref="HistoryRelabel"/>). Such a row is marked NamedLater.
    /// </summary>
    public static IReadOnlyList<HistoryRow> HistoryRows(
        IReadOnlyList<RouteRow> rows,
        Func<RouteRow, RouteClassifier.Features?>? nameLater,
        int max = MaxHistoryRows)
    {
        var picked = new List<HistoryRow>(Math.Min(rows.Count, max));
        for (var i = rows.Count - 1; i >= 0 && picked.Count < max; i--)
        {
            var row = rows[i];
            var t = HistoryTime(row.Timestamp);
            if (t == null)
                continue;

            var later = nameLater != null && HistoryRelabel.NeedsName(row) ? nameLater(row) : null;
            var upstream = later?.Upstream ?? row.Upstream ?? "";
            var handoffNetwork = later != null ? later.HandoffNetwork : row.HandoffNetwork ?? "";
            var handoffCity = later != null ? later.HandoffCity : row.HandoffCity ?? "";

            picked.Add(new HistoryRow(
                t,
                upstream,
                handoffNetwork,
                handoffCity,
                Number(row.LastHopAvg),
                Number(row.LastHopLossPct),
                Number(row.RiotHopAvg),
                Number(row.RiotHopLossPct),
                IsYes(row.LeagueRunning),
                IsYes(row.Vpn),
                row.Adapter ?? "",
                CityNames.Display(handoffCity),
                later != null));
        }

        picked.Reverse();
        return picked;
    }

    /// <summary>The region block. The target label comes from the region kind, never "game server".</summary>
    public static RegionView Region(RegionInfo region, bool auto, string basis, string? lastTestTarget) => new(
        region.Id,
        region.ShortName,
        region.Name,
        auto,
        auto ? (string.IsNullOrEmpty(basis) ? "default" : basis) : "manual",
        region.ServerCity ?? "",
        region.CityConfirmed,
        Finite(region.Lat),
        Finite(region.Lon),
        region.LolStatusId ?? "",
        region.ValorantShard ?? "",
        new TargetView(region.TargetIp ?? "", region.TargetLabel, region.TargetKind == "dns" ? "dns" : "anycast", region.TargetInRegion),
        lastTestTarget ?? "");

    public static RegionView Region(RegionSelection selection, string? lastTestTarget) =>
        Region(selection.Region, selection.Auto, selection.Basis, lastTestTarget);

    /// <summary>Before any choice: the reference data default, automatic, basis "default".</summary>
    public static RegionView DefaultRegion(RefData data) => Region(data.DefaultRegion, true, "default", "");

    /// <summary>The picker list, in reference data order. No targets, no city table.</summary>
    public static IReadOnlyList<RegionListItem> Regions(RefData data) =>
        data.Regions.Select(r => new RegionListItem(r.Id, r.ShortName, r.Name, r.ServerCity ?? "", r.CityConfirmed)).ToArray();

    /// <summary>Every ASN of the provider with this name (for example "Lumen": 3356, 3549, 209). Empty when unknown.</summary>
    public static IReadOnlyList<long> ProviderAsns(string? providerName, RefData data)
    {
        if (string.IsNullOrWhiteSpace(providerName) || data == null)
            return Array.Empty<long>();
        var provider = data.Providers.FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.Ordinal));
        return provider?.Asns.ToArray() ?? Array.Empty<long>();
    }

    /// <summary>"auto", or the manual region id.</summary>
    public static string RegionSetting(RegionSelection selection) => selection.Auto ? "auto" : selection.Region.Id;

    public static RefDataView? RefDataInfo(RefDataStatus? status) =>
        status == null ? null : new RefDataView(status.Version ?? "", SafeSource(status.Source), Finite(status.AgeSec));

    private static string SafeSource(string? source) => source switch
    {
        "cache" or "worker" => source,
        _ => "builtin",
    };

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    public static LogView Log(
        bool paused,
        LogHealth health,
        string folder,
        string fileName,
        string? writeStatus,
        string? readProblem,
        string? location,
        int unsavedRows)
    {
        var (chip, chipTone) = LogStatusText.LoggingChip(paused, health);
        var (status, statusTone) = LogStatusText.StripLine(health, writeStatus, readProblem, location, unsavedRows);
        var path = string.IsNullOrWhiteSpace(folder) ? "" : System.IO.Path.Combine(folder, fileName);
        return new LogView(path, folder ?? "", fileName ?? "", health.ToString(), chip, Tone(chipTone), status, Tone(statusTone), unsavedRows);
    }

    public static string UpdateStateName(UpdateState state) => state switch
    {
        UpdateState.Idle => "idle",
        UpdateState.Checking => "checking",
        UpdateState.Downloading => "downloading",
        UpdateState.Ready => "ready",
        UpdateState.Installing => "installing",
        UpdateState.UpToDate => "upToDate",
        UpdateState.Error => "error",
        _ => "idle",
    };

    public static UpdateView Update(UpdateStatus status) => new(
        UpdateStateName(status.State),
        AppUpdate.FormatVersion(status.Current),
        status.Available == null ? null : AppUpdate.FormatVersion(status.Available),
        status.Progress is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null,
        status.Notes ?? "",
        status.ReleaseUrl ?? "",
        status.Error ?? "",
        status.LastCheckUtc,
        status.AutoInstall);
}
