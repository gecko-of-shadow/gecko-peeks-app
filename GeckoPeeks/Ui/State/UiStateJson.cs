using System.Globalization;
using System.Text;
using System.Text.Json;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Ui.State;

/// <summary>
/// Writes the Contract 2 host-to-web messages. Field names and shapes follow the contract
/// exactly. Numbers that are not finite become null.
/// </summary>
public static class UiStateJson
{
    private static readonly JsonWriterOptions Options = new() { Indented = false };

    /// <param name="historyJson">
    /// The history array already written by <see cref="History"/>. The host caches it and
    /// rebuilds it only when the log changes. Null writes <see cref="UiState.History"/>.
    /// </param>
    /// <param name="includeHistory">
    /// False leaves "history" out, and the page keeps the history it has. The host sends the
    /// history only when it changed, because it can be several megabytes.
    /// </param>
    /// <param name="historyTotal">
    /// How many tests the log holds. More than the rows sent means the oldest ones were left out
    /// (<see cref="UiMapping.MaxHistoryRows"/>), and the page must not call its view "all tests".
    /// </param>
    public static string Build(UiState state, string? historyJson = null, bool includeHistory = true, int? historyTotal = null)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("type", "state");

            w.WriteStartObject("app");
            w.WriteString("version", state.App.Version);
            w.WriteString("profile", state.App.Profile);
            w.WriteBoolean("demo", state.App.Demo);
            w.WriteEndObject();

            WriteRegion(w, state.Region ?? UiMapping.DefaultRegion(RefData.Current));
            WriteRegions(w, state.Regions ?? UiMapping.Regions(RefData.Current));
            WriteProbe(w, state.Probe);
            WriteLog(w, state.Log);

            if (includeHistory)
            {
                w.WritePropertyName("history");
                if (historyJson != null)
                    w.WriteRawValue(historyJson, skipInputValidation: true);
                else
                    WriteHistory(w, state.History);
                if (historyTotal is { } total && total >= 0)
                    w.WriteNumber("historyTotal", total);
            }

            WriteCloud(w, state.Cloud);
            WriteUpdate(w, state.Update);

            w.WriteStartObject("settings");
            w.WriteBoolean("startWithWindows", state.Settings.StartWithWindows);
            w.WriteBoolean("autoInstallUpdates", state.Settings.AutoInstallUpdates);
            w.WriteBoolean("cloudEnabled", state.Settings.CloudEnabled);
            w.WriteString("region", SafeRegionSetting(state.Settings.Region));
            w.WriteEndObject();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The history array alone, oldest first.</summary>
    public static string History(IReadOnlyList<HistoryRow> rows)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
            WriteHistory(w, rows);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Toast(string text, string tone)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("type", "toast");
            w.WriteString("text", text ?? "");
            w.WriteString("tone", UiMapping.SafeTone(tone));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>ISO 8601 UTC with a Z, for nextDueUtc and lastCheckUtc.</summary>
    public static string IsoUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string SafeRegionSetting(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v.Length is > 0 and <= 8 && v.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ? v : "auto";
    }

    private static void WriteRegion(Utf8JsonWriter w, RegionView r)
    {
        w.WriteStartObject("region");
        w.WriteString("id", r.Id ?? "");
        w.WriteString("shortName", r.ShortName ?? "");
        w.WriteString("name", r.Name ?? "");
        w.WriteBoolean("auto", r.Auto);
        w.WriteString("basis", r.Basis ?? "");
        w.WriteString("serverCity", r.ServerCity ?? "");
        w.WriteBoolean("cityConfirmed", r.CityConfirmed);
        Number(w, "lat", r.Lat);
        Number(w, "lon", r.Lon);
        w.WriteString("lolStatusId", r.LolStatusId ?? "");
        w.WriteString("valorantShard", r.ValorantShard ?? "");
        w.WriteStartObject("target");
        w.WriteString("ip", r.Target.Ip ?? "");
        w.WriteString("label", r.Target.Label ?? "");
        w.WriteString("kind", r.Target.Kind == "dns" ? "dns" : "anycast");
        w.WriteBoolean("inRegion", r.Target.InRegion);
        w.WriteEndObject();
        w.WriteString("lastTestTarget", r.LastTestTarget ?? "");
        w.WriteEndObject();
    }

    private static void WriteRegions(Utf8JsonWriter w, IReadOnlyList<RegionListItem> regions)
    {
        w.WriteStartArray("regions");
        foreach (var r in regions)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id ?? "");
            w.WriteString("shortName", r.ShortName ?? "");
            w.WriteString("name", r.Name ?? "");
            w.WriteString("serverCity", r.ServerCity ?? "");
            w.WriteBoolean("cityConfirmed", r.CityConfirmed);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteProbe(Utf8JsonWriter w, ProbeView p)
    {
        w.WriteStartObject("probe");
        w.WriteBoolean("paused", p.Paused);
        w.WriteBoolean("leagueRunning", p.LeagueRunning);
        w.WriteBoolean("testRunning", p.TestRunning);
        w.WriteString("phase", p.Phase ?? "");
        w.WriteString("mode", p.Mode ?? "");
        if (p.NextDueUtc is { } due)
            w.WriteString("nextDueUtc", IsoUtc(due));
        else
            w.WriteNull("nextDueUtc");
        w.WriteString("lastError", p.LastError ?? "");
        w.WriteString("activityLine", p.ActivityLine ?? "");
        w.WriteString("noMoreTests", p.NoMoreTests ?? "");
        w.WriteBoolean("hasRoute", p.HasRoute);
        w.WriteString("routeLine", p.RouteLine ?? "");
        w.WriteString("upstream", p.Upstream ?? "");
        w.WriteString("handoffNetwork", p.HandoffNetwork ?? "");
        w.WriteString("handoffCity", p.HandoffCity ?? "");
        w.WriteString("handoffCityName", p.HandoffCityName ?? "");
        w.WriteString("upstreamSource", Source(p.UpstreamSource));
        w.WriteString("handoffSource", Source(p.HandoffSource));
        if (p.UpstreamAsn is { } upAsn)
            w.WriteNumber("upstreamAsn", upAsn);
        else
            w.WriteNull("upstreamAsn");
        w.WriteStartArray("upstreamAsns");
        foreach (var asn in p.UpstreamAsns ?? Array.Empty<long>())
            w.WriteNumberValue(asn);
        w.WriteEndArray();
        Number(w, "handoffLat", p.HandoffLat);
        Number(w, "handoffLon", p.HandoffLon);
        WriteFigure(w, "lastHop", p.LastHop);
        WriteFigure(w, "riot", p.Riot);
        w.WriteString("contextLine", p.ContextLine ?? "");
        w.WriteString("changeLine", p.ChangeLine ?? "");
        w.WriteStartArray("changedHops");
        foreach (var hop in p.ChangedHops)
            w.WriteNumberValue(hop);
        w.WriteEndArray();
        w.WriteString("testTimestamp", p.TestTimestamp ?? "");
        w.WriteString("adapter", p.Adapter ?? "");
        w.WriteString("wifi", p.Wifi ?? "");
        w.WriteString("gateway", p.Gateway ?? "");
        w.WriteBoolean("vpn", p.Vpn);
        w.WriteBoolean("league", p.League);
        w.WriteStartArray("hops");
        foreach (var hop in p.Hops)
        {
            w.WriteStartObject();
            w.WriteNumber("n", hop.N);
            w.WriteString("ip", hop.Ip ?? "");
            w.WriteString("host", hop.Host ?? "");
            Number(w, "rttMs", hop.RttMs);
            w.WriteBoolean("responded", hop.Responded);
            w.WriteBoolean("reachedTarget", hop.ReachedTarget);
            w.WriteBoolean("changed", hop.Changed);
            w.WriteBoolean("isPrivate", hop.IsPrivate);
            if (hop.Asn is { } asn)
                w.WriteNumber("asn", asn);
            else
                w.WriteNull("asn");
            if (hop.AsName != null)
                w.WriteString("asName", hop.AsName);
            else
                w.WriteNull("asName");
            w.WriteString("network", hop.Network ?? "");
            w.WriteString("nameSource", Source(hop.NameSource));
            w.WriteString("cityName", hop.CityName ?? "");
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteBoolean("hopsAddressesOnly", p.HopsAddressesOnly);
        w.WriteString("tooltip", p.Tooltip ?? "");
        w.WriteEndObject();
    }

    private static void WriteFigure(Utf8JsonWriter w, string name, LatencyFigure figure)
    {
        w.WriteStartObject(name);
        Number(w, "avgMs", figure.AvgMs);
        Number(w, "lossPct", figure.LossPct);
        w.WriteString("figure", figure.Figure ?? "");
        w.WriteEndObject();
    }

    private static void WriteLog(Utf8JsonWriter w, LogView log)
    {
        w.WriteStartObject("log");
        w.WriteString("path", log.Path ?? "");
        w.WriteString("folder", log.Folder ?? "");
        w.WriteString("fileName", log.FileName ?? "");
        w.WriteString("health", log.Health ?? "");
        w.WriteString("chip", log.Chip ?? "");
        w.WriteString("chipTone", UiMapping.SafeTone(log.ChipTone));
        w.WriteString("status", log.Status ?? "");
        w.WriteString("statusTone", UiMapping.SafeTone(log.StatusTone));
        w.WriteNumber("unsavedRows", log.UnsavedRows);
        w.WriteEndObject();
    }

    private static void WriteHistory(Utf8JsonWriter w, IReadOnlyList<HistoryRow> rows)
    {
        w.WriteStartArray();
        foreach (var row in rows)
        {
            w.WriteStartObject();
            w.WriteString("t", row.T);
            w.WriteString("upstream", row.Upstream ?? "");
            w.WriteString("handoffNetwork", row.HandoffNetwork ?? "");
            w.WriteString("handoffCity", row.HandoffCity ?? "");
            Number(w, "lastHopAvg", row.LastHopAvg);
            Number(w, "lastHopLoss", row.LastHopLoss);
            Number(w, "riotAvg", row.RiotAvg);
            Number(w, "riotLoss", row.RiotLoss);
            w.WriteBoolean("league", row.League);
            w.WriteBoolean("vpn", row.Vpn);
            w.WriteString("adapter", row.Adapter ?? "");
            w.WriteString("handoffCityName", string.IsNullOrEmpty(row.HandoffCityName) ? row.HandoffCity ?? "" : row.HandoffCityName);
            if (row.NamedLater)
                w.WriteBoolean("namedLater", true);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteCloud(Utf8JsonWriter w, CloudView cloud)
    {
        w.WriteStartObject("cloud");
        w.WriteBoolean("enabled", cloud.Enabled);
        w.WriteString("status", cloud.Status ?? "off");
        w.WriteString("error", cloud.Error ?? "");
        Number(w, "edgeAgeSec", cloud.EdgeAgeSec);
        Number(w, "pulseAgeSec", cloud.PulseAgeSec);
        RawObject(w, "edge", cloud.EdgeJson);
        RawObject(w, "pulse", cloud.PulseJson);
        RawObject(w, "riotStatus", cloud.RiotStatusJson);
        Number(w, "riotStatusAgeSec", cloud.RiotStatusAgeSec);
        if (cloud.RefData is { } rd)
        {
            w.WriteStartObject("refdata");
            w.WriteString("version", rd.Version ?? "");
            w.WriteString("source", rd.Source ?? "builtin");
            Number(w, "ageSec", rd.AgeSec);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("refdata");
        }

        w.WriteEndObject();
    }

    /// <summary>"hostname", "asn" or "".</summary>
    private static string Source(string? value) => value is "hostname" or "asn" ? value : "";

    private static void WriteUpdate(Utf8JsonWriter w, UpdateView u)
    {
        w.WriteStartObject("update");
        w.WriteString("state", u.State);
        w.WriteString("current", u.Current);
        if (u.Available != null)
            w.WriteString("available", u.Available);
        else
            w.WriteNull("available");
        Number(w, "progress", u.Progress);
        w.WriteString("notes", u.Notes ?? "");
        w.WriteString("releaseUrl", u.ReleaseUrl ?? "");
        w.WriteString("error", u.Error ?? "");
        if (u.LastCheckUtc is { } last)
            w.WriteString("lastCheckUtc", IsoUtc(last));
        else
            w.WriteNull("lastCheckUtc");
        w.WriteBoolean("autoInstall", u.AutoInstall);
        w.WriteEndObject();
    }

    /// <summary>
    /// Worker JSON goes in as it came, but only when it parses as one object. Anything else is
    /// null, so a broken body can never break the state message.
    /// </summary>
    private static void RawObject(Utf8JsonWriter w, string name, string? json)
    {
        w.WritePropertyName(name);
        if (string.IsNullOrWhiteSpace(json) || !IsObject(json))
        {
            w.WriteNullValue();
            return;
        }

        w.WriteRawValue(json, skipInputValidation: false);
    }

    private static bool IsObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Number(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
            w.WriteNumber(name, v);
        else
            w.WriteNull(name);
    }
}
