using System.IO.Compression;
using System.Text.Json;
using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;
using GeckoPeeks.Ui.State;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>v2.1.5: every diagnostic record is kept, and Export makes one zip. Made-up sample data only.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", "diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    private DiagnosticsStore Store() => new(Path.Combine(_folder, "Diagnostics"), "9.9.9");

    private static RouteRow SampleRow(string timestamp, string upstream = "Hurricane Electric", string city = "Chicago",
        string riotAvg = "", string lastAvg = "41") => new()
    {
        Timestamp = timestamp,
        LeagueRunning = "Yes",
        Upstream = upstream,
        HandoffNetwork = "Riot Direct",
        HandoffIP = "104.160.0.1",
        HandoffCity = city,
        LastHopIP = "104.160.0.9",
        LastHopMin = "39",
        LastHopAvg = lastAvg,
        LastHopMax = "47",
        LastHopLossPct = "0",
        RiotHopAvg = riotAvg,
        RiotHopLossPct = riotAvg.Length == 0 ? "100" : "0",
        FullPath = "184.105.0.1 > 104.160.0.1 > 104.160.0.9",
        TargetIp = "192.207.0.5",
        Wifi = "DemoWifi",
        Hops = new List<HopObservation>
        {
            new(1, "184.105.0.1", "core1.example.he.net", 12, true, false),
            new(2, "*", "", null, false, false),
            new(3, "104.160.0.9", "", 41, true, false),
        },
        HopAsns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["184.105.0.1"] = 6939, ["104.160.0.9"] = 6507 },
    };

    [Fact]
    public void A_test_line_keeps_every_hop_its_asn_the_outcome_and_league_ping()
    {
        var store = Store();
        var at = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        var ping = new GamePing(58, 1.5, 3, 58, at.AddSeconds(-4), 120000, false);

        store.RecordTest(new TestRecord(at, "saved", "", SampleRow("2026-11-21 16:00:00"), "na1", true,
            new MatchServer("192.207.0.5", 7363), ping, new Dictionary<int, string> { [1] = "Hurricane Electric" }));

        var file = Path.Combine(store.Folder!, "tests-2026-11.jsonl");
        var line = Assert.Single(File.ReadAllLines(file));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("9.9.9", root.GetProperty("app").GetString());
        Assert.Equal("2026-11-21T20:00:00.000Z", root.GetProperty("at").GetString());
        Assert.Equal("saved", root.GetProperty("outcome").GetString());
        Assert.Equal("192.207.0.5:7363", root.GetProperty("matchServer").GetString());
        Assert.Equal(3, root.GetProperty("hops").GetArrayLength());
        Assert.Equal(6939, root.GetProperty("hops")[0].GetProperty("asn").GetInt64());
        Assert.Equal("Hurricane Electric", root.GetProperty("hops")[0].GetProperty("network").GetString());
        Assert.False(root.GetProperty("hops")[1].GetProperty("responded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("riotIcmp").GetProperty("avgMs").ValueKind); // no reply, not 0
        Assert.Equal(41, root.GetProperty("lastHop").GetProperty("avgMs").GetDouble());
        Assert.Equal(58, root.GetProperty("leaguePing").GetProperty("pingMs").GetInt32());
    }

    [Fact]
    public void A_cancelled_test_without_a_row_is_kept_with_its_reason()
    {
        var store = Store();
        var at = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

        store.RecordTest(new TestRecord(at, "stopped", "Test stopped: boom", null, "na1", false, null, null));

        using var doc = JsonDocument.Parse(Assert.Single(store.ReadLines(DiagnosticsStore.TestsPrefix, null, at)));
        Assert.Equal("stopped", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Test stopped: boom", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void No_folder_keeps_nothing_and_never_throws()
    {
        var store = new DiagnosticsStore(null, "1.0.0");

        store.RecordTest(new TestRecord(DateTime.UtcNow, "saved", "", SampleRow("2026-11-21 16:00:00"), "na1", false, null, null));
        Assert.False(store.RecordCloud("edge", "{\"a\":1}", DateTime.UtcNow));
        Assert.Equal(0, store.RecordGamePings(new[] { new GamePing(50, 0, 1, 50, DateTime.UtcNow, 1, false) }));
        Assert.Null(store.SizeBytes());
        Assert.Empty(store.ReadLines(DiagnosticsStore.TestsPrefix, null, DateTime.UtcNow));
    }

    [Fact]
    public void Cloud_data_is_kept_only_when_it_changed_ignoring_fetch_times()
    {
        var store = Store();
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

        Assert.True(store.RecordCloud("pulse", "{\"time\":\"a\",\"riot\":{\"asn\":6507}}", t));
        Assert.False(store.RecordCloud("pulse", "{\"time\":\"b\",\"riot\":{\"asn\":6507}}", t.AddMinutes(5)));
        Assert.True(store.RecordCloud("riotStatus", "{\"fetchedAt\":\"x\",\"lol\":{\"incidents\":[]}}", t));
        Assert.False(store.RecordCloud("riotStatus", "{\"fetchedAt\":\"y\",\"lol\":{\"incidents\":[]}}", t));
        Assert.True(store.RecordCloud("pulse", "{\"time\":\"c\",\"riot\":{\"asn\":6508}}", t.AddMinutes(10)));
        Assert.False(store.RecordCloud("pulse", "not json", t));

        Assert.Equal(3, store.ReadLines(DiagnosticsStore.CloudPrefix, null, t.AddHours(1)).Count());
    }

    [Fact]
    public void League_rows_are_kept_once_and_a_new_game_starts_over()
    {
        var store = Store();
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        GamePing Row(long game, int ping) => new(ping, 0, 1, ping, t.AddMilliseconds(game), game, false);

        Assert.Equal(2, store.RecordGamePings(new[] { Row(10000, 50), Row(20000, 51) }));
        Assert.Equal(1, store.RecordGamePings(new[] { Row(10000, 50), Row(20000, 51), Row(30000, 52) }));
        Assert.Equal(0, store.RecordGamePings(new[] { Row(20000, 51), Row(30000, 52) }));
        // A later read whose game clock went back: a new game.
        Assert.Equal(1, store.RecordGamePings(new[] { new GamePing(70, 0, 1, 70, t.AddMinutes(5), 5000, false) }));
        store.EndGame();
        // A late copy of the finished game's log saves nothing again.
        Assert.Equal(0, store.RecordGamePings(new[] { new GamePing(70, 0, 1, 70, t.AddMinutes(5), 5000, false) }));
        // The next game, ten minutes later.
        Assert.Equal(1, store.RecordGamePings(new[] { new GamePing(80, 0, 1, 80, t.AddMinutes(10), 1000, false) }));

        var lines = store.ReadLines(DiagnosticsStore.LeaguePrefix, null, t.AddHours(1)).ToList();
        Assert.Equal(5, lines.Count);
        Assert.DoesNotContain("X.X.X.X", string.Join("\n", lines));
    }

    [Fact]
    public void Reading_a_window_spans_months_and_skips_torn_lines()
    {
        var store = Store();
        var oct = new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc);
        var nov = new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);
        store.RecordTest(new TestRecord(oct.AddDays(-5), "saved", "", null, "na1", false, null, null));
        store.RecordTest(new TestRecord(oct, "saved", "", null, "na1", false, null, null));
        store.RecordTest(new TestRecord(nov, "cancelled", "", null, "na1", false, null, null));
        File.AppendAllText(Path.Combine(store.Folder!, "tests-2026-11.jsonl"), "{\"v\":1,\"at\":\"2026-11-01T0");

        var window = store.ReadLines(DiagnosticsStore.TestsPrefix, oct.AddHours(-1), nov.AddHours(1)).ToList();

        Assert.Equal(2, window.Count);
        Assert.Equal(3, store.ReadLines(DiagnosticsStore.TestsPrefix, null, nov.AddHours(1)).Count());
        Assert.True(store.SizeBytes() > 0);
    }

    [Fact]
    public void Activity_lines_past_the_cap_move_to_monthly_archives_and_none_are_lost()
    {
        var path = Path.Combine(_folder, "activity.log");
        var old = Enumerable.Range(0, 300).Select(i => $"2026-10-15 10:{i / 60:00}:{i % 60:00}  October line {i} " + new string('x', 200));
        var newer = Enumerable.Range(0, 300).Select(i => $"2026-11-15 10:{i / 60:00}:{i % 60:00}  November line {i} " + new string('x', 200));
        File.WriteAllLines(path, old.Concat(newer));
        var log = new ActivityLog(path);

        log.Write("one more");

        var all = ActivityLog.ReadAll(_folder).ToList();
        Assert.Equal(601, all.Count);
        Assert.Equal(ActivityLog.MaxLines, File.ReadAllLines(path).Length);
        // 601 lines, 400 kept: the 201 oldest (all October) are archived under their own month.
        Assert.Equal(201, File.ReadAllLines(Path.Combine(_folder, "activity-2026-10.log")).Length);
        Assert.False(File.Exists(Path.Combine(_folder, "activity-2026-11.log")));
        Assert.EndsWith("one more", all[^1]);
    }

    [Fact]
    public void Export_writes_a_zip_with_a_plain_summary_and_the_window_slices()
    {
        var store = Store();
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddHours(-24);
        string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var rows = new List<RouteRow>
        {
            SampleRow(Local(toUtc.AddDays(-3)), upstream: "Old Upstream"), // outside the window
            SampleRow(Local(toUtc.AddHours(-3))),
            SampleRow(Local(toUtc.AddHours(-2)), upstream: "Cogent", city: "Dallas"),
            SampleRow(Local(toUtc.AddHours(-1)), upstream: "Cogent", city: "Dallas", riotAvg: "44"),
        };
        store.RecordTest(new TestRecord(toUtc.AddHours(-1), "saved", "", rows[3], "na1", true, new MatchServer("192.207.0.5", 7363), null));
        store.RecordGamePings(new[] { new GamePing(61, 2.5, 4, 61, toUtc.AddMinutes(-30), 5000, true) });
        store.RecordCloud("riotStatus",
            "{\"lol\":{\"incidents\":[{\"id\":7,\"type\":\"incident\",\"title\":\"Demo: login issues\",\"createdAt\":\"2026-11-21T10:00:00Z\"}],\"maintenances\":[]}}",
            toUtc.AddHours(-2));
        var activity = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(activity);
        File.WriteAllLines(Path.Combine(activity, "activity.log"), new[]
        {
            toUtc.AddDays(-3).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  too old",
            toUtc.AddHours(-1).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  in the window",
        });
        var zipPath = Path.Combine(_folder, "out.zip");

        var result = DiagnosticsExport.Write(new ExportRequest(zipPath, fromUtc, toUtc, "Last 24 hours", "9.9.9", store,
            rows, new[] { activity, activity }, "NA (North America), automatic", "192.207.0.5"));

        Assert.Equal(3, result.CsvRows);
        Assert.Equal(1, result.Tests);
        Assert.Equal(1, result.ActivityLines);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "summary.txt", "route-log.csv", "tests.jsonl", "activity.log", "cloud.jsonl", "league.jsonl" },
            zip.Entries.Select(e => e.FullName));
        var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open()).ReadToEnd();
        Assert.Contains("3 tests", summary);
        Assert.Contains("Route changes: 1", summary);
        Assert.Contains("Hurricane Electric to Riot Direct in Chicago -> Cogent to Riot Direct in Dallas", summary);
        Assert.Contains("no reply in 2 tests", summary);
        Assert.Contains("answered in 1 of 3 tests that pinged it", summary);
        Assert.Contains("192.207.0.5:7363", summary);
        Assert.Contains("ping lowest 61 ms", summary);
        Assert.Contains("reconnect flag set: 1", summary);
        Assert.Contains("League incident: Demo: login issues", summary);
        Assert.Contains("folder paths", summary);
        Assert.Contains("Not measured", summary);
        Assert.DoesNotContain("Old Upstream", summary);
        var csv = new StreamReader(zip.GetEntry("route-log.csv")!.Open()).ReadToEnd();
        Assert.Equal(3, CsvFormat.Parse(csv).Count);
    }

    [Fact]
    public void An_empty_window_says_not_recorded_instead_of_zero()
    {
        var store = Store();
        var summary = DiagnosticsExport.Summary(
            new ExportRequest("x.zip", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, "Last hour", "9.9.9", store,
                Array.Empty<RouteRow>(), Array.Empty<string>(), "", ""),
            Array.Empty<RouteRow>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0);

        Assert.Contains("No saved tests in this window.", summary);
        Assert.Contains("No test details recorded in this window", summary);
        Assert.Contains("No League match ping recorded", summary);
        Assert.Contains("No Riot status recorded for this window", summary);
        Assert.DoesNotContain("0 ms", summary);
    }

    [Theory]
    [InlineData("{\"cmd\":\"exportDiagnostics\",\"range\":\"24h\"}", "24h")]
    [InlineData("{\"cmd\":\"exportDiagnostics\",\"range\":\"all\"}", "all")]
    [InlineData("{\"cmd\":\"exportDiagnostics\",\"range\":\"2y\"}", null)]
    [InlineData("{\"cmd\":\"exportDiagnostics\"}", null)]
    [InlineData("{\"cmd\":\"exportDiagnostics\",\"range\":5}", null)]
    public void The_page_can_ask_for_an_export_only_with_a_known_range(string json, string? range)
    {
        var message = BridgeMessage.Parse(json);
        if (range == null)
        {
            Assert.Null(message);
            return;
        }

        Assert.Equal(BridgeCommand.ExportDiagnostics, message!.Command);
        Assert.Equal(range, message.Range);
    }

    [Fact]
    public void Dev_diagnostics_live_in_the_dev_root_only()
    {
        Assert.Null(DiagnosticsStore.PathFor(RunProfile.Dev, null));
        Assert.Equal(Path.Combine(Path.GetFullPath(_folder), "Diagnostics"), DiagnosticsStore.PathFor(RunProfile.Dev, _folder));
    }

    // ---------- Review fixes ----------

    [Fact]
    public void After_an_app_restart_a_running_match_is_not_saved_twice()
    {
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        GamePing Row(DateTime start, long game, int ping) => new(ping, 0, 1, ping, start.AddMilliseconds(game), game, false);

        Assert.Equal(2, Store().RecordGamePings(new[] { Row(t, 10000, 50), Row(t, 20000, 51) }));

        // A new app run, same match: only the row after the saved ones is new.
        Assert.Equal(1, Store().RecordGamePings(new[] { Row(t, 10000, 50), Row(t, 20000, 51), Row(t, 30000, 52) }));

        // A new app run and a new, longer match (it started an hour later): all its rows are new.
        var later = t.AddHours(1);
        Assert.Equal(2, Store().RecordGamePings(new[] { Row(later, 25000, 60), Row(later, 35000, 61) }));
    }

    [Fact]
    public void Lines_that_could_not_be_written_are_kept_and_written_later()
    {
        var store = Store();
        File.WriteAllText(store.Folder!, "a file where the folder should be");
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

        store.RecordTest(new TestRecord(t, "saved", "", null, "na1", false, null, null));
        Assert.Equal(1, store.UnsavedLines);
        Assert.NotEqual("", store.LastProblem);

        File.Delete(store.Folder!);
        store.RecordTest(new TestRecord(t.AddMinutes(10), "saved", "", null, "na1", false, null, null));

        Assert.Equal(0, store.UnsavedLines);
        Assert.Equal("", store.LastProblem);
        Assert.Equal(2, store.ReadLines(DiagnosticsStore.TestsPrefix, null, t.AddHours(1)).Count());
    }

    [Fact]
    public void If_the_archive_cannot_be_written_activity_log_is_not_trimmed()
    {
        var path = Path.Combine(_folder, "activity.log");
        File.WriteAllLines(path, Enumerable.Range(0, 600).Select(i => $"2026-10-15 10:{i / 60:00}:{i % 60:00}  line {i} " + new string('x', 200)));
        Directory.CreateDirectory(Path.Combine(_folder, "activity-2026-10.log")); // blocks the archive file

        new ActivityLog(path).Write("one more");

        Assert.Equal(601, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void The_summary_sorts_rows_counts_loss_only_on_answered_tests_and_ignores_empty_status()
    {
        var store = Store();
        var toUtc = DateTime.UtcNow;
        string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var answered = SampleRow(Local(toUtc.AddHours(-1)), upstream: "Cogent", city: "Dallas", riotAvg: "44");
        answered.RiotHopLossPct = "0";
        var noReply = SampleRow(Local(toUtc.AddHours(-2)));
        noReply.RiotHopLossPct = "100";
        // Pending rows come last in the log, out of time order.
        var rows = new List<RouteRow> { answered, noReply };
        store.RecordCloud("riotStatus", "{\"lol\":null,\"valorant\":null}", toUtc.AddMinutes(-30));
        var zipPath = Path.Combine(_folder, "sorted.zip");

        DiagnosticsExport.Write(new ExportRequest(zipPath, toUtc.AddHours(-24), toUtc, "Last 24 hours", "9.9.9", store,
            rows, Array.Empty<string>(), "", ""));

        using var zip = ZipFile.OpenRead(zipPath);
        var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open()).ReadToEnd();
        Assert.Contains("Latest route: Cogent to Riot Direct in Dallas", summary);
        Assert.Contains("Hurricane Electric to Riot Direct in Chicago -> Cogent", summary);
        Assert.Contains("average loss 0 %", summary);
        Assert.Contains("No Riot status recorded for this window", summary);
        Assert.False(File.Exists(zipPath + ".partial"));
    }

    [Fact]
    public void Export_keeps_untimed_lines_and_real_repeats_but_not_copies_between_folders()
    {
        var toUtc = DateTime.UtcNow;
        var stamp = toUtc.AddMinutes(-5).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var lines = new[] { stamp + "  Test stopped: boom", "   at Some.Stack.Frame()", stamp + "  same", stamp + "  same" };
        var a = Path.Combine(_folder, "a");
        var b = Path.Combine(_folder, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllLines(Path.Combine(a, "activity.log"), lines);
        File.WriteAllLines(Path.Combine(b, "activity.log"), lines); // the copy made when the log folder moved

        var result = DiagnosticsExport.Write(new ExportRequest(Path.Combine(_folder, "act.zip"), toUtc.AddHours(-1), toUtc,
            "Last hour", "9.9.9", Store(), Array.Empty<RouteRow>(), new[] { a, b }, "", ""));

        Assert.Equal(4, result.ActivityLines);
    }

    // ---------- Second review fixes ----------

    [Fact]
    public void An_older_copy_of_the_netlog_from_another_caller_is_not_saved_again()
    {
        var store = Store();
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        GamePing Row(long game) => new(50, 0, 1, 50, t.AddMilliseconds(game), game, false);

        Assert.Equal(3, store.RecordGamePings(new[] { Row(10000), Row(20000), Row(30000) }));
        // The other task read the log a moment earlier and writes second.
        Assert.Equal(0, store.RecordGamePings(new[] { Row(10000), Row(20000) }));
        Assert.Equal(3, store.ReadLines(DiagnosticsStore.LeaguePrefix, null, t.AddHours(1)).Count());
    }

    [Fact]
    public void A_restart_seeds_from_the_highest_game_time_of_the_last_game()
    {
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        var folder = Path.Combine(_folder, "Diagnostics");
        Directory.CreateDirectory(folder);
        // Saved out of order by an older app version.
        File.WriteAllLines(Path.Combine(folder, "league-2026-11.jsonl"), new[]
        {
            $"{{\"v\":1,\"at\":\"{DiagnosticsStore.Iso(t.AddMilliseconds(30000))}\",\"gameTimeMs\":30000,\"pingMs\":50}}",
            $"{{\"v\":1,\"at\":\"{DiagnosticsStore.Iso(t.AddMilliseconds(20000))}\",\"gameTimeMs\":20000,\"pingMs\":50}}",
        });

        var added = Store().RecordGamePings(new[] { new GamePing(50, 0, 1, 50, t.AddMilliseconds(30000), 30000, false), new GamePing(51, 0, 1, 51, t.AddMilliseconds(40000), 40000, false) });

        Assert.Equal(1, added);
    }

    [Fact]
    public void A_retry_after_a_cut_off_line_starts_on_a_new_line()
    {
        var store = Store();
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(store.Folder!);
        var file = Path.Combine(store.Folder!, "tests-2026-11.jsonl");
        File.WriteAllText(file, "{\"v\":1,\"at\":\"2026-11-2"); // cut off by a full disk
        var blocker = Path.Combine(store.Folder!, "tests-2026-12.jsonl");
        Directory.CreateDirectory(blocker); // the next write fails
        store.RecordTest(new TestRecord(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), "saved", "", null, "na1", false, null, null));
        Directory.Delete(blocker);

        store.RecordTest(new TestRecord(t, "saved", "", null, "na1", false, null, null));

        Assert.Single(store.ReadLines(DiagnosticsStore.TestsPrefix, null, t.AddHours(1)));
    }

    [Fact]
    public void A_failed_rewrite_does_not_archive_the_same_lines_twice()
    {
        var path = Path.Combine(_folder, "activity.log");
        var lines = Enumerable.Range(0, 600).Select(i => $"2026-10-15 10:{i / 60:00}:{i % 60:00}  line {i} " + new string('x', 200)).ToList();
        File.WriteAllLines(path, lines);
        // As if an earlier trim archived the oldest lines and then could not rewrite activity.log.
        File.WriteAllLines(Path.Combine(_folder, "activity-2026-10.log"), lines.Take(200));

        new ActivityLog(path).Write("one more");

        Assert.Equal(601, ActivityLog.ReadAll(_folder).Count());
    }

    [Fact]
    public void Riot_ping_not_run_is_not_measured_and_status_names_its_region_and_the_start_state()
    {
        var store = Store();
        var toUtc = DateTime.UtcNow;
        string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var stopped = SampleRow(Local(toUtc.AddHours(-1)));
        stopped.RiotHopLossPct = "";
        store.RecordCloud("riotStatus",
            "{\"region\":\"euw1\",\"lol\":{\"incidents\":[{\"id\":9,\"type\":\"incident\",\"title\":\"Demo: queue delays\"}],\"maintenances\":[]}}",
            toUtc.AddDays(-2)); // saved once, long before the window, and unchanged since

        var summary = DiagnosticsExport.Summary(
            new ExportRequest("x.zip", toUtc.AddHours(-24), toUtc, "Last 24 hours", "9.9.9", store, new[] { stopped }, Array.Empty<string>(), "", ""),
            new[] { stopped }, Array.Empty<string>(), store.LastCloudBefore(toUtc.AddHours(-24)), Array.Empty<string>(), 0);

        Assert.Contains("not measured in 1 tests", summary);
        Assert.DoesNotContain("no reply in", summary);
        Assert.Contains("League (euw1) incident: Demo: queue delays", summary);
    }

    // ---------- Third review fixes ----------

    [Fact]
    public void A_paused_game_whose_start_drifts_is_still_the_same_game()
    {
        var store = Store();
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        GamePing At(DateTime wall, long game) => new(50, 0, 1, 50, wall, game, false);
        // 20 minutes into a match.
        Assert.Equal(2, store.RecordGamePings(new[] { At(t.AddMinutes(20), 1_200_000), At(t.AddMinutes(20).AddSeconds(10), 1_210_000) }));

        // A 5-minute pause: wall time moved on, game time did not. The log read again holds the old rows too.
        var later = t.AddMinutes(25).AddSeconds(20);
        var added = store.RecordGamePings(new[] { At(later.AddSeconds(-20), 1_200_000), At(later.AddSeconds(-10), 1_210_000), At(later, 1_220_000) });

        Assert.Equal(1, added);
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, new[] { "b", "c" }, 1)]
    [InlineData(new[] { "x", "a", "b" }, new[] { "a", "b", "c" }, 2)]
    [InlineData(new[] { "a" }, new[] { "b", "a", "c" }, 0)]   // the last line appears later in the batch: not an overlap
    [InlineData(new[] { "c" }, new[] { "a", "b" }, 0)]
    public void Only_an_archive_tail_that_equals_the_batch_head_counts_as_archived(string[] archive, string[] batch, int expected)
    {
        var path = Path.Combine(_folder, "activity-2026-10.log");
        File.WriteAllLines(path, archive);

        Assert.Equal(expected, ActivityLog.AlreadyArchived(path, batch.ToList()));
    }

    [Fact]
    public void A_line_cut_off_by_a_crash_is_ended_before_the_first_write_of_a_new_run()
    {
        var store = Store();
        Directory.CreateDirectory(store.Folder!);
        var t = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(store.Folder!, "tests-2026-11.jsonl"), "{\"v\":1,\"at\":\"2026-11-2");

        store.RecordTest(new TestRecord(t, "saved", "", null, "na1", false, null, null));

        Assert.Single(store.ReadLines(DiagnosticsStore.TestsPrefix, null, t.AddHours(1)));
    }

    [Fact]
    public void Status_from_before_the_window_carries_its_saved_time()
    {
        var store = Store();
        var toUtc = DateTime.UtcNow;
        var savedAt = toUtc.AddDays(-21);
        store.RecordCloud("riotStatus", "{\"region\":\"na1\",\"lol\":{\"incidents\":[{\"id\":1,\"type\":\"incident\",\"title\":\"Demo: old issue\"}],\"maintenances\":[]}}", savedAt);
        var from = toUtc.AddHours(-1);

        var summary = DiagnosticsExport.Summary(
            new ExportRequest("x.zip", from, toUtc, "Last hour", "9.9.9", store, Array.Empty<RouteRow>(), Array.Empty<string>(), "", ""),
            Array.Empty<RouteRow>(), Array.Empty<string>(), store.LastCloudBefore(from), Array.Empty<string>(), 0);

        Assert.Contains("League (na1), latest answer saved " + savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ", before this window", summary);
    }

    [Fact]
    public void A_feed_Riot_did_not_answer_keeps_that_games_last_answer()
    {
        var store = Store();
        var toUtc = DateTime.UtcNow;
        store.RecordCloud("riotStatus", "{\"region\":\"na1\",\"lol\":{\"incidents\":[{\"id\":3,\"type\":\"incident\",\"title\":\"Demo: login issues\"}],\"maintenances\":[]},\"valorant\":{\"incidents\":[],\"maintenances\":[]}}", toUtc.AddMinutes(-20));
        store.RecordCloud("riotStatus", "{\"region\":\"na1\",\"lol\":null,\"valorant\":{\"incidents\":[],\"maintenances\":[]}}", toUtc.AddMinutes(-10));
        var lines = store.ReadLines(DiagnosticsStore.CloudPrefix, null, toUtc).ToList();

        var summary = DiagnosticsExport.Summary(
            new ExportRequest("x.zip", toUtc.AddHours(-1), toUtc, "Last hour", "9.9.9", store, Array.Empty<RouteRow>(), Array.Empty<string>(), "", ""),
            Array.Empty<RouteRow>(), Array.Empty<string>(), lines, Array.Empty<string>(), 0);

        var league = summary.Substring(summary.IndexOf("League (na1), latest answer", StringComparison.Ordinal));
        Assert.StartsWith("League (na1), latest answer saved " + toUtc.AddMinutes(-20).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), league);
        Assert.Contains("League (na1) incident: Demo: login issues", summary);
        Assert.DoesNotContain("not in the latest", summary);
    }
}
