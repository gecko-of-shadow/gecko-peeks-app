using System.Text.Json;
using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Text;
using GeckoPeeks.Ui.State;
using Xunit;

namespace GeckoPeeks.Tests;

public class UiStateTests
{
    private static UiState SampleState(CloudView? cloud = null) => new(
        new AppInfo("2.0.0", "Production", false),
        new ProbeView
        {
            Phase = "Idle",
            Mode = "Every 15 min",
            NextDueUtc = new DateTime(2026, 11, 20, 21, 15, 0, DateTimeKind.Utc),
            HasRoute = true,
            RouteLine = "Zayo \u2192 Telia sea1",
            Upstream = "Zayo",
            LastHop = new LatencyFigure(48, 0, "48 ms"),
            Riot = new LatencyFigure(null, 100, "no reply"),
            ChangedHops = new[] { 6 },
            Hops = new[]
            {
                new HopView(1, "192.168.1.1", "router", 1, true, false, false, true, null, null),
                new HopView(2, "*", "", null, false, false, false, false, null, null),
                new HopView(6, "62.115.49.194", "sea-b2", 16.5, true, false, true, false, 1299, "TWELVE99"),
            },
        },
        UiMapping.Log(false, LogHealth.Saved, @"C:\logs", "riot-route-log.csv", "Saved row at 20:46:25.", "", "", 0),
        new[] { new HistoryRow("2026-11-20T20:46:25", "Zayo", "Telia", "sea1", 48, 0, double.NaN, 100, true, false, "Ethernet") },
        cloud ?? CloudView.Off,
        new UpdateView("ready", "2.0.0", "2.0.1", 1, "notes", "https://github.com/gecko-of-shadow/gecko-peeks-app/releases", "", null, true),
        new SettingsView(true, true, false));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void State_has_every_contract_section_and_type()
    {
        var root = Parse(UiStateJson.Build(SampleState()));

        Assert.Equal("state", root.GetProperty("type").GetString());
        foreach (var name in new[] { "app", "probe", "log", "history", "cloud", "update", "settings" })
            Assert.True(root.TryGetProperty(name, out _), name);
        Assert.Equal("2.0.0", root.GetProperty("app").GetProperty("version").GetString());
        Assert.Equal("Production", root.GetProperty("app").GetProperty("profile").GetString());
        Assert.False(root.GetProperty("app").GetProperty("demo").GetBoolean());
    }

    [Fact]
    public void Probe_fields_use_contract_names_and_types()
    {
        var probe = Parse(UiStateJson.Build(SampleState())).GetProperty("probe");

        Assert.Equal("2026-11-20T21:15:00Z", probe.GetProperty("nextDueUtc").GetString());
        Assert.Equal(48, probe.GetProperty("lastHop").GetProperty("avgMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, probe.GetProperty("riot").GetProperty("avgMs").ValueKind);
        Assert.Equal("no reply", probe.GetProperty("riot").GetProperty("figure").GetString());
        Assert.Equal(6, probe.GetProperty("changedHops")[0].GetInt32());
        foreach (var name in new[]
                 {
                     "paused", "leagueRunning", "testRunning", "phase", "mode", "lastError", "activityLine", "hasRoute",
                     "routeLine", "upstream", "handoffNetwork", "handoffCity", "handoffCityName", "contextLine",
                     "changeLine", "testTimestamp", "adapter", "wifi", "gateway", "vpn", "league", "hops",
                     "hopsAddressesOnly", "tooltip",
                 })
            Assert.True(probe.TryGetProperty(name, out _), name);

        var hops = probe.GetProperty("hops");
        Assert.Equal(3, hops.GetArrayLength());
        Assert.True(hops[0].GetProperty("isPrivate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hops[1].GetProperty("rttMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, hops[1].GetProperty("asn").ValueKind);
        Assert.Equal(1299, hops[2].GetProperty("asn").GetInt64());
        Assert.Equal("TWELVE99", hops[2].GetProperty("asName").GetString());
        Assert.Equal(16.5, hops[2].GetProperty("rttMs").GetDouble());
        Assert.True(hops[2].GetProperty("changed").GetBoolean());
    }

    [Fact]
    public void Not_finite_numbers_become_null()
    {
        var row = Parse(UiStateJson.Build(SampleState())).GetProperty("history")[0];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("riotAvg").ValueKind);
        Assert.Equal(100, row.GetProperty("riotLoss").GetDouble());
        Assert.Equal("2026-11-20T20:46:25", row.GetProperty("t").GetString());
        Assert.True(row.GetProperty("league").GetBoolean());
    }

    [Fact]
    public void Log_update_and_settings_follow_the_contract()
    {
        var root = Parse(UiStateJson.Build(SampleState()));
        var log = root.GetProperty("log");
        Assert.Equal(Path.Combine(@"C:\logs", "riot-route-log.csv"), log.GetProperty("path").GetString());
        Assert.Equal("Saved", log.GetProperty("health").GetString());
        Assert.Equal("good", log.GetProperty("statusTone").GetString());
        Assert.Equal(0, log.GetProperty("unsavedRows").GetInt32());

        var update = root.GetProperty("update");
        Assert.Equal("ready", update.GetProperty("state").GetString());
        Assert.Equal("2.0.1", update.GetProperty("available").GetString());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("lastCheckUtc").ValueKind);
        Assert.True(update.GetProperty("autoInstall").GetBoolean());

        var settings = root.GetProperty("settings");
        Assert.True(settings.GetProperty("startWithWindows").GetBoolean());
        Assert.False(settings.GetProperty("cloudEnabled").GetBoolean());
    }

    [Fact]
    public void Cloud_off_writes_null_data()
    {
        var cloud = Parse(UiStateJson.Build(SampleState())).GetProperty("cloud");
        Assert.False(cloud.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", cloud.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("edge").ValueKind);
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("pulse").ValueKind);
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("edgeAgeSec").ValueKind);
    }

    [Fact]
    public void Cloud_json_goes_in_as_an_object_and_broken_json_becomes_null()
    {
        var good = new CloudView(true, "ok", "", 12, 30, "{\"colo\":\"SEA\",\"asn\":64500}", "not json");
        var cloud = Parse(UiStateJson.Build(SampleState(good))).GetProperty("cloud");

        Assert.Equal("SEA", cloud.GetProperty("edge").GetProperty("colo").GetString());
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("pulse").ValueKind);
        Assert.Equal(12, cloud.GetProperty("edgeAgeSec").GetDouble());

        var array = new CloudView(true, "ok", "", null, null, "[1,2]", null);
        Assert.Equal(JsonValueKind.Null, Parse(UiStateJson.Build(SampleState(array))).GetProperty("cloud").GetProperty("edge").ValueKind);
    }

    [Fact]
    public void Cached_history_json_is_used_as_is()
    {
        var json = UiStateJson.Build(SampleState(), "[{\"t\":\"2026-01-01T00:00:00\"}]");
        var history = Parse(json).GetProperty("history");
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal("2026-01-01T00:00:00", history[0].GetProperty("t").GetString());
    }

    [Fact]
    public void Text_with_quotes_and_markup_stays_inside_json_strings()
    {
        var state = SampleState() with { Probe = new ProbeView { RouteLine = "\"</script><b>x\u2028" } };
        var probe = Parse(UiStateJson.Build(state)).GetProperty("probe");
        Assert.Equal("\"</script><b>x\u2028", probe.GetProperty("routeLine").GetString());
    }

    [Fact]
    public void Toast_message_has_type_text_and_a_known_tone()
    {
        var toast = Parse(UiStateJson.Toast("Copied.", "good"));
        Assert.Equal("toast", toast.GetProperty("type").GetString());
        Assert.Equal("Copied.", toast.GetProperty("text").GetString());
        Assert.Equal("good", toast.GetProperty("tone").GetString());
        Assert.Equal("normal", Parse(UiStateJson.Toast("x", "purple")).GetProperty("tone").GetString());
    }

    [Fact]
    public void History_rows_are_the_newest_20000_oldest_first()
    {
        Assert.Equal(20000, UiMapping.MaxHistoryRows);
        var rows = new List<RouteRow>();
        var start = new DateTime(2026, 1, 1, 0, 0, 0);
        for (var i = 0; i < 20100; i++)
            rows.Add(new RouteRow { Timestamp = CsvFormat.FormatTimestamp(start.AddMinutes(15 * i)), LastHopAvg = "40" });

        var mapped = UiMapping.HistoryRows(rows);

        Assert.Equal(20000, mapped.Count);
        Assert.Equal(start.AddMinutes(15 * 100).ToString("yyyy-MM-dd'T'HH:mm:ss"), mapped[0].T);
        Assert.Equal(start.AddMinutes(15 * 20099).ToString("yyyy-MM-dd'T'HH:mm:ss"), mapped[^1].T);
    }

    [Fact]
    public void A_full_42_day_log_is_sent_whole()
    {
        // 42 days at one test per 15 min is 4032 rows: v2.1.1 cut this to the newest 3000.
        var rows = new List<RouteRow>();
        var start = new DateTime(2026, 10, 10, 23, 0, 0);
        for (var i = 0; i < 4032; i++)
            rows.Add(new RouteRow { Timestamp = CsvFormat.FormatTimestamp(start.AddMinutes(15 * i)), LastHopAvg = "40" });

        var mapped = UiMapping.HistoryRows(rows);

        Assert.Equal(4032, mapped.Count);
        Assert.Equal("2026-10-10T23:00:00", mapped[0].T);
    }

    [Fact]
    public void History_maps_csv_text_to_numbers_and_flags()
    {
        var rows = new[]
        {
            new RouteRow
            {
                Timestamp = "2026-11-20 20:46:25", Upstream = "Zayo", HandoffNetwork = "Telia", HandoffCity = "sea1",
                LastHopAvg = "48", LastHopLossPct = "0", RiotHopAvg = "", RiotHopLossPct = "100",
                LeagueRunning = NetworkContext.Yes, Vpn = NetworkContext.No, Adapter = "Ethernet",
            },
            new RouteRow { Timestamp = "not a time", LastHopAvg = "1" },
        };

        var mapped = UiMapping.HistoryRows(rows);

        var row = Assert.Single(mapped);
        Assert.Equal("2026-11-20T20:46:25", row.T);
        Assert.Equal(48, row.LastHopAvg);
        Assert.Null(row.RiotAvg);
        Assert.Equal(100, row.RiotLoss);
        Assert.True(row.League);
        Assert.False(row.Vpn);
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData("Yes", true)]
    [InlineData("true", true)]
    [InlineData("N", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsYes_reads_csv_flags(string? text, bool expected) => Assert.Equal(expected, UiMapping.IsYes(text));

    [Theory]
    [InlineData("38", 38.0)]
    [InlineData("38.5 ms", 38.5)]
    [InlineData("\u2014", null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Number_parses_invariant_milliseconds(string text, double? expected) => Assert.Equal(expected, UiMapping.Number(text));

    [Fact]
    public void Update_states_use_camel_case_names()
    {
        Assert.Equal("upToDate", UiMapping.UpdateStateName(UpdateState.UpToDate));
        Assert.Equal("downloading", UiMapping.UpdateStateName(UpdateState.Downloading));
        var view = UiMapping.Update(new UpdateStatus(UpdateState.Downloading, new Version(2, 0, 0), new Version(2, 0, 1), 1.7,
            "n", "https://github.com/gecko-of-shadow/gecko-peeks-app/releases", "", new DateTime(2026, 11, 20, 1, 2, 3, DateTimeKind.Utc), false));
        Assert.Equal("2.0.0", view.Current);
        Assert.Equal("2.0.1", view.Available);
        Assert.Equal(1, view.Progress);
        var json = Parse(UiStateJson.Build(SampleState() with { Update = view })).GetProperty("update");
        Assert.Equal("2026-11-20T01:02:03Z", json.GetProperty("lastCheckUtc").GetString());
    }

    [Fact]
    public void Log_view_tones_come_from_the_log_status_text()
    {
        var blocked = UiMapping.Log(false, LogHealth.Blocked, "C:\\x", "riot-route-log.csv", "", "", "", 3);
        Assert.Equal("bad", blocked.StatusTone);
        Assert.Equal("Blocked", blocked.Health);
        Assert.Equal(3, blocked.UnsavedRows);
        var (chip, tone) = LogStatusText.LoggingChip(true, LogHealth.Saved);
        var paused = UiMapping.Log(true, LogHealth.Saved, "", "riot-route-log.csv", "", "", "", 0);
        Assert.Equal(chip, paused.Chip);
        Assert.Equal(UiMapping.Tone(tone), paused.ChipTone);
        Assert.Equal("", paused.Path);
    }

    // ---- Bridge messages (web -> host) ----

    [Theory]
    [InlineData("{\"cmd\":\"ready\"}", BridgeCommand.Ready)]
    [InlineData("{\"cmd\":\"runNow\"}", BridgeCommand.RunNow)]
    [InlineData("{\"cmd\":\"togglePause\"}", BridgeCommand.TogglePause)]
    [InlineData("{\"cmd\":\"copyDiagnosis\"}", BridgeCommand.CopyDiagnosis)]
    [InlineData("{\"cmd\":\"openLog\"}", BridgeCommand.OpenLog)]
    [InlineData("{\"cmd\":\"openFolder\"}", BridgeCommand.OpenFolder)]
    [InlineData("{\"cmd\":\"copyPath\"}", BridgeCommand.CopyPath)]
    [InlineData("{\"cmd\":\"checkUpdate\"}", BridgeCommand.CheckUpdate)]
    [InlineData("{\"cmd\":\"installUpdate\"}", BridgeCommand.InstallUpdate)]
    [InlineData("{\"cmd\":\"refreshCloud\"}", BridgeCommand.RefreshCloud)]
    public void Every_simple_command_parses(string json, BridgeCommand expected) =>
        Assert.Equal(expected, BridgeMessage.Parse(json)?.Command);

    [Fact]
    public void OpenUrl_and_setSetting_carry_their_values()
    {
        var open = BridgeMessage.Parse("{\"cmd\":\"openUrl\",\"url\":\"https://radar.cloudflare.com/as6507\"}");
        Assert.Equal(BridgeCommand.OpenUrl, open?.Command);
        Assert.Equal("https://radar.cloudflare.com/as6507", open?.Url);

        var set = BridgeMessage.Parse("{\"cmd\":\"setSetting\",\"key\":\"cloudEnabled\",\"value\":false}");
        Assert.Equal(BridgeCommand.SetSetting, set?.Command);
        Assert.Equal(BridgeSetting.CloudEnabled, set?.Setting);
        Assert.False(set?.Value);
        Assert.Equal(BridgeSetting.StartWithWindows,
            BridgeMessage.Parse("{\"cmd\":\"setSetting\",\"key\":\"startWithWindows\",\"value\":true}")?.Setting);
        Assert.Equal(BridgeSetting.AutoInstallUpdates,
            BridgeMessage.Parse("{\"cmd\":\"setSetting\",\"key\":\"autoInstallUpdates\",\"value\":true}")?.Setting);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"ready\"")]
    [InlineData("{\"cmd\":\"format C:\"}")]
    [InlineData("{\"cmd\":1}")]
    [InlineData("{\"cmd\":\"openUrl\"}")]
    [InlineData("{\"cmd\":\"openUrl\",\"url\":5}")]
    [InlineData("{\"cmd\":\"setSetting\",\"key\":\"theme\",\"value\":true}")]
    [InlineData("{\"cmd\":\"setSetting\",\"key\":\"cloudEnabled\",\"value\":\"true\"}")]
    [InlineData("{\"cmd\":\"setSetting\",\"key\":\"cloudEnabled\"}")]
    public void Bad_messages_are_ignored(string? json) => Assert.Null(BridgeMessage.Parse(json));

    [Fact]
    public void Very_long_messages_are_ignored() =>
        Assert.Null(BridgeMessage.Parse("{\"cmd\":\"openUrl\",\"url\":\"https://radar.cloudflare.com/" + new string('a', 9000) + "\"}"));

    // ---- openUrl allow-list ----

    [Theory]
    [InlineData("https://radar.cloudflare.com/")]
    [InlineData("https://radar.cloudflare.com/as6507")]
    [InlineData("https://radar.cloudflare.com/routing/as852?dateRange=7d")]
    [InlineData("https://RADAR.cloudflare.com/outage-center")]
    [InlineData("https://github.com/gecko-of-shadow/gecko-peeks-app")]
    [InlineData("https://github.com/gecko-of-shadow/gecko-peeks-app/releases/tag/v2.1.2")]
    public void Allowed_urls_open(string url)
    {
        Assert.True(UrlPolicy.TryAllow(url, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://radar.cloudflare.com/as6507")]
    [InlineData("https://radar.cloudflare.com.evil.example/")]
    [InlineData("https://evil.example/radar.cloudflare.com")]
    [InlineData("https://user@radar.cloudflare.com/")]
    [InlineData("https://radar.cloudflare.com:8443/")]
    [InlineData("https://cloudflare.com/")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/gecko-of-shadow/gecko-peeks-app-evil")]
    [InlineData("https://github.com/gecko-of-shadow/gecko-peeks")]           // the old private repo
    [InlineData("https://github.com/someone/gecko-peeks-app")]
    [InlineData("https://gist.github.com/gecko-of-shadow/gecko-peeks-app")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:")]
    [InlineData("/as6507")]
    public void Other_urls_are_refused(string? url) => Assert.False(UrlPolicy.TryAllow(url, out _));

    // ---- settings file ----

    [Fact]
    public void Cloud_setting_defaults_on_and_persists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, HostSettings.FileName);
        try
        {
            var settings = new HostSettings(path);
            Assert.True(settings.CloudEnabled);
            settings.CloudEnabled = false;
            Assert.True(File.Exists(path));
            Assert.False(new HostSettings(path).CloudEnabled);

            File.WriteAllText(path, "{ broken");
            Assert.True(new HostSettings(path).CloudEnabled);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Settings_path_is_null_in_dev_without_a_root_and_never_in_production_folders_for_dev()
    {
        Assert.Null(HostSettings.PathFor(RunProfile.Dev, null));
        var dev = HostSettings.PathFor(RunProfile.Dev, @"C:\scratch\dev");
        Assert.Equal(Path.Combine(@"C:\scratch\dev", HostSettings.FileName), dev);
        var prod = HostSettings.PathFor(RunProfile.Production, @"C:\scratch\dev");
        Assert.NotNull(prod);
        Assert.EndsWith(Path.Combine("GeckoPeeks", HostSettings.FileName), prod);
    }

    [Fact]
    public void Memory_only_settings_never_write()
    {
        var settings = new HostSettings(null);
        settings.CloudEnabled = false;
        Assert.False(settings.CloudEnabled);
        Assert.Null(settings.Path);
    }

    // ---- demo data ----

    [Fact]
    public void Demo_is_only_on_for_dev_with_the_variable()
    {
        Assert.True(DemoData.IsOn(RunProfile.Dev, "1"));
        Assert.False(DemoData.IsOn(RunProfile.Production, "1"));
        Assert.False(DemoData.IsOn(RunProfile.Dev, null));
        Assert.False(DemoData.IsOn(RunProfile.Dev, "0"));
    }

    [Fact]
    public void Demo_state_is_complete_and_uses_the_asn_lookup()
    {
        var now = new DateTime(2026, 11, 20, 20, 52, 0, DateTimeKind.Local);
        var probe = DemoData.Probe(now, ip => ip == "192.207.0.5" ? (6507L, "RIOT-NA1") : null);
        Assert.True(probe.HasRoute);
        Assert.Equal(DemoData.Hops.Count, probe.Hops.Count);
        Assert.True(probe.Hops[0].IsPrivate);
        Assert.True(probe.Hops[1].IsPrivate);
        Assert.False(probe.Hops[2].Responded);
        Assert.Equal(6507, probe.Hops[^1].Asn);
        Assert.True(probe.Hops[^1].ReachedTarget);

        var history = DemoData.History(now);
        Assert.InRange(history.Count, 7 * 96, 7 * 96 + 1);
        Assert.Equal(history, DemoData.History(now));
        var json = Parse(UiStateJson.Build(SampleState() with { Probe = probe, History = history }));
        Assert.Equal(history.Count, json.GetProperty("history").GetArrayLength());
    }
}
