using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GeckoPeeks.Logging;

/// <summary>What to put in one diagnostics zip.</summary>
/// <param name="FromUtc">Start of the window; null = everything saved.</param>
/// <param name="WindowText">For example "Last 24 hours".</param>
/// <param name="CsvRows">The route log rows (the caller reads them).</param>
/// <param name="ActivityFolders">Folders that hold activity.log and its monthly archives.</param>
/// <param name="RegionNow">For example "NA (North America), automatic".</param>
/// <param name="TargetNow">For example "192.207.0.5".</param>
public sealed record ExportRequest(
    string ZipPath,
    DateTime? FromUtc,
    DateTime ToUtc,
    string WindowText,
    string AppVersion,
    DiagnosticsStore Store,
    IReadOnlyList<RouteRow> CsvRows,
    IReadOnlyList<string> ActivityFolders,
    string RegionNow,
    string TargetNow);

public sealed record ExportResult(int Tests, int CsvRows, int ActivityLines, int CloudLines, int LeagueLines, int ServiceLines = 0);

/// <summary>
/// Builds a diagnostics zip on this PC: summary.txt (plain facts a support person can read in a
/// minute) plus the raw records of the window. Nothing is uploaded.
/// </summary>
public static class DiagnosticsExport
{
    /// <summary>The ranges the window offers, by id.</summary>
    public static readonly IReadOnlyDictionary<string, (string Text, TimeSpan? Span)> Ranges =
        new Dictionary<string, (string, TimeSpan?)>(StringComparer.Ordinal)
        {
            ["1h"] = ("Last hour", TimeSpan.FromHours(1)),
            ["24h"] = ("Last 24 hours", TimeSpan.FromHours(24)),
            ["7d"] = ("Last 7 days", TimeSpan.FromDays(7)),
            ["all"] = ("Everything saved", null),
        };

    public static string DefaultFileName(DateTime local) =>
        "gecko-peeks-diagnostics-" + local.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";

    public static ExportResult Write(ExportRequest request)
    {
        var fromLocal = request.FromUtc?.ToLocalTime();
        var toLocal = request.ToUtc.ToLocalTime();

        // Pending rows come after the main rows in the log: order by time first (stable).
        var csv = request.CsvRows
            .Select(r => (Row: r, At: CsvTime(r.Timestamp)))
            .Where(x => x.At is { } t && t <= toLocal && (fromLocal == null || t >= fromLocal))
            .OrderBy(x => x.At)
            .Select(x => x.Row)
            .ToList();
        var activity = ActivityLines(request.ActivityFolders, fromLocal, toLocal);

        // Each store file is read once, into a temp copy, and the summary and the zip both use that
        // copy: rows saved during the export (League rows carry times from the past) cannot make
        // the summary and the files disagree. The copies are this call's own and are removed after.
        var scratch = Path.Combine(Path.GetTempPath(), "gecko-peeks-export-" + Guid.NewGuid().ToString("N"));
        var temp = request.ZipPath + ".partial";
        try
        {
            Directory.CreateDirectory(scratch);
            // Cloud data is saved only when it changes: the answer in effect at the window start comes first.
            var cloudLines = (request.FromUtc is { } from ? request.Store.LastCloudBefore(from) : Array.Empty<string>())
                .Concat(request.Store.ReadLines(DiagnosticsStore.CloudPrefix, request.FromUtc, request.ToUtc));
            var testsFile = Copy(scratch, "tests", request.Store.ReadLines(DiagnosticsStore.TestsPrefix, request.FromUtc, request.ToUtc));
            var cloudFile = Copy(scratch, "cloud", cloudLines);
            var leagueFile = Copy(scratch, "league", request.Store.ReadLines(DiagnosticsStore.LeaguePrefix, request.FromUtc, request.ToUtc));
            // Each other service (v2.2) has its own file, in the zip too.
            var serviceFiles = request.Store.ServiceIds()
                .Select(id => (Id: id, File: Copy(scratch, "service-" + id, request.Store.ReadLines(DiagnosticsStore.ServicePrefix(id), request.FromUtc, request.ToUtc))))
                .ToList();

            var counts = new Counts();
            var summary = Summary(request, csv, SharedText.ReadLines(testsFile), SharedText.ReadLines(cloudFile),
                SharedText.ReadLines(leagueFile), activity.Count, counts,
                serviceFiles.Select(f => (f.Id, SharedText.ReadLines(f.File))).ToList());

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, "summary.txt", w => w.Write(summary));
                Add(zip, "route-log.csv", w =>
                {
                    w.Write(CsvFormat.HeaderLine);
                    foreach (var row in csv)
                        w.Write(CsvFormat.FormatLine(row));
                });
                Add(zip, "tests.jsonl", w => WriteLines(w, SharedText.ReadLines(testsFile), "\n"));
                Add(zip, "activity.log", w => WriteLines(w, activity, "\r\n"));
                Add(zip, "cloud.jsonl", w => WriteLines(w, SharedText.ReadLines(cloudFile), "\n"));
                Add(zip, "league.jsonl", w => WriteLines(w, SharedText.ReadLines(leagueFile), "\n"));
                foreach (var (id, file) in serviceFiles)
                    Add(zip, "services/" + id + ".jsonl", w => WriteLines(w, SharedText.ReadLines(file), "\n"));
            }

            File.Move(temp, request.ZipPath, overwrite: true);
            return new ExportResult(counts.Tests, csv.Count, activity.Count, counts.Cloud, counts.League, counts.Services);
        }
        catch
        {
            try
            {
                // Only the half-written temp file this call made.
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Nothing more to do.
            }

            throw;
        }
        finally
        {
            try
            {
                // Only the temp copies this call made.
                Directory.Delete(scratch, recursive: true);
            }
            catch
            {
                // Left in %TEMP%; Windows cleans it.
            }
        }
    }

    private static string Copy(string folder, string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(folder, name + ".jsonl");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        WriteLines(writer, lines, "\n");
        return path;
    }

    /// <summary>Line counts the summary pass found.</summary>
    public sealed class Counts
    {
        public int Tests { get; set; }
        public int Cloud { get; set; }
        public int League { get; set; }
        public int Services { get; set; }
        public Dictionary<string, int> ServiceLines { get; } = new(StringComparer.Ordinal);
    }

    // ---------- Summary ----------

    /// <param name="csv">Route log rows of the window, oldest first.</param>
    public static string Summary(ExportRequest request, IReadOnlyList<RouteRow> csv, IEnumerable<string> tests,
        IEnumerable<string> cloud, IEnumerable<string> league, int activityLines, Counts? counts = null,
        IReadOnlyList<(string Id, IEnumerable<string> Lines)>? services = null)
    {
        counts ??= new Counts();
        var s = new StringBuilder();
        var now = request.ToUtc.ToLocalTime();
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        var zone = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        s.AppendLine("Gecko Peeks diagnostics");
        s.AppendLine("=======================");
        s.AppendLine($"App version: {request.AppVersion}");
        s.AppendLine($"Made: {Local(now)} (local time, {zone})");
        s.AppendLine(request.FromUtc is { } from
            ? $"Window: {request.WindowText}, {Local(from.ToLocalTime())} to {Local(now)}"
            : $"Window: {request.WindowText}, up to {Local(now)}");
        s.AppendLine($"Region now: {Or(request.RegionNow)}");
        s.AppendLine($"Test target now: {Or(request.TargetNow)}");
        s.AppendLine();

        // Route log (every saved test, all versions).
        s.AppendLine("Saved tests (route log)");
        s.AppendLine("-----------------------");
        if (csv.Count == 0)
        {
            s.AppendLine("No saved tests in this window.");
        }
        else
        {
            s.AppendLine($"{csv.Count} tests, {csv[0].Timestamp} to {csv[^1].Timestamp}.");
            var last = csv[^1];
            s.AppendLine($"Latest route: {RouteText(last)}");
            var changes = RouteChanges(csv);
            if (changes.Count == 0)
            {
                s.AppendLine("Route changes: none seen in this window.");
            }
            else
            {
                s.AppendLine($"Route changes: {changes.Count}");
                foreach (var change in changes.TakeLast(20))
                    s.AppendLine("  " + change);
            }

            var avgs = csv.Select(r => Num(r.LastHopAvg)).OfType<double>().ToList();
            if (avgs.Count > 0)
            {
                var mins = csv.Select(r => Num(r.LastHopMin)).OfType<double>().ToList();
                var maxs = csv.Select(r => Num(r.LastHopMax)).OfType<double>().ToList();
                var losses = csv.Select(r => Num(r.LastHopLossPct)).OfType<double>().ToList();
                s.AppendLine($"Last router that answered: average {Ms(avgs.Average())} over {avgs.Count} tests"
                    + (mins.Count > 0 ? $", lowest {Ms(mins.Min())}" : "")
                    + (maxs.Count > 0 ? $", highest {Ms(maxs.Max())}" : "") + ".");
                if (losses.Count > 0)
                    s.AppendLine($"  Packet loss to it: average {Pct(losses.Average())}, {losses.Count(l => l > 0)} of {losses.Count} tests had some loss.");
            }
            else
            {
                s.AppendLine("Last router that answered: not measured in this window.");
            }

            // Three kinds of test: answered (a time), no reply (no time, 100 % loss), and not
            // measured (both blank: the test stopped before this ping ran).
            var riot = csv.Select(r => Num(r.RiotHopAvg)).OfType<double>().ToList();
            var noReply = csv.Count(r => LatencyLabels.RiotNoReply(r.RiotHopAvg, r.RiotHopLossPct));
            var notMeasured = csv.Count(r => Num(r.RiotHopAvg) == null && !LatencyLabels.RiotNoReply(r.RiotHopAvg, r.RiotHopLossPct));
            var pinged = riot.Count + noReply;
            if (riot.Count > 0)
            {
                // Loss only over the tests that got replies; a no-reply test saves 100 %.
                var riotLoss = csv.Where(r => Num(r.RiotHopAvg) != null).Select(r => Num(r.RiotHopLossPct)).OfType<double>().ToList();
                s.AppendLine($"Ping to the test target (ICMP): answered in {riot.Count} of {pinged} tests that pinged it, average {Ms(riot.Average())}"
                    + (riotLoss.Count > 0 ? $", average loss {Pct(riotLoss.Average())}" : "") + ".");
            }

            if (noReply > 0)
                s.AppendLine($"Ping to the test target (ICMP): no reply in {noReply} tests. Riot filters ping, so no reply is not a fault and not 0 ms.");
            if (notMeasured > 0)
                s.AppendLine($"Ping to the test target (ICMP): not measured in {notMeasured} tests (the test stopped before that ping).");
        }

        s.AppendLine();

        // Per-test detail (2.1.5 on).
        s.AppendLine("Test details (from version 2.1.5 on)");
        s.AppendLine("-------------------------------------");
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        var servers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in tests)
        {
            using var doc = TryParse(line);
            if (doc == null)
                continue;
            counts.Tests++;
            var root = doc.RootElement;
            var outcome = Str(root, "outcome") ?? "?";
            outcomes[outcome] = outcomes.TryGetValue(outcome, out var n) ? n + 1 : 1;
            if (Str(root, "targetIp") is { } target)
                targets.Add(target);
            if (Str(root, "matchServer") is { } server)
                servers.Add(server);
        }

        if (counts.Tests == 0)
        {
            s.AppendLine("No test details recorded in this window. They are recorded from version 2.1.5 on, while the app runs.");
        }
        else
        {
            s.AppendLine($"{counts.Tests} tests: " + string.Join(", ", outcomes.Select(o => $"{o.Value} {OutcomeText(o.Key)}")) + ".");
            if (targets.Count > 0)
                s.AppendLine("Addresses traced: " + string.Join(", ", targets));
            if (servers.Count > 0)
                s.AppendLine("League match servers (from League's own match log): " + string.Join(", ", servers));
        }

        s.AppendLine();

        // League's own ping.
        s.AppendLine("League in-game ping (League's own network log)");
        s.AppendLine("----------------------------------------------");
        var pings = new List<(int Ping, double? Loss, int? Jitter, bool Reconnect)>();
        foreach (var line in league)
        {
            using var doc = TryParse(line);
            if (doc == null)
                continue;
            counts.League++;
            var root = doc.RootElement;
            if (Int(root, "pingMs") is { } ping)
                pings.Add((ping, Dbl(root, "lossPct"), Int(root, "jitterMs"), root.TryGetProperty("reconnect", out var r) && r.ValueKind == JsonValueKind.True));
        }

        if (pings.Count == 0)
        {
            s.AppendLine("No League match ping recorded in this window (recorded from version 2.1.5 on, during a match).");
        }
        else
        {
            s.AppendLine($"{pings.Count} samples (about every 10 s): ping lowest {pings.Min(p => p.Ping)} ms, average {Ms(pings.Average(p => p.Ping))}, highest {pings.Max(p => p.Ping)} ms.");
            var losses = pings.Select(p => p.Loss).OfType<double>().ToList();
            var jitters = pings.Select(p => p.Jitter).OfType<int>().ToList();
            if (losses.Count > 0)
                s.AppendLine($"Packet loss: highest {Pct(losses.Max())}, {losses.Count(l => l > 0)} of {losses.Count} samples had some.");
            if (jitters.Count > 0)
                s.AppendLine($"Jitter: highest {jitters.Max()} ms.");
            var reconnects = pings.Count(p => p.Reconnect);
            if (reconnects > 0)
                s.AppendLine($"Samples with League's reconnect flag set: {reconnects}.");
        }

        s.AppendLine();

        // Riot status.
        s.AppendLine("Riot service status (Riot's status page, via the Gecko Peeks Worker)");
        s.AppendLine("--------------------------------------------------------------------");
        s.AppendLine("Saved only when it changes; the status in effect at the window start is included.");
        var games = RiotStatusEntries(cloud, counts);
        if (games.Count == 0)
        {
            s.AppendLine("No Riot status recorded for this window. It is saved from version 2.1.5 on, when Cloudflare live data is on.");
        }
        else
        {
            // Per game: a feed Riot did not answer leaves that game's last answer in place.
            foreach (var game in games)
            {
                var before = request.FromUtc is { } windowStart && game.LatestAt < windowStart ? ", before this window" : "";
                s.AppendLine($"{game.Label}, latest answer saved {Local(game.LatestAt.ToLocalTime())}{before}:");
                if (game.Latest.Count == 0)
                    s.AppendLine("  No incidents or maintenance listed.");
                foreach (var entry in game.Latest)
                    s.AppendLine("  " + entry);
                if (game.Earlier.Count > 0)
                {
                    s.AppendLine("  Listed in earlier answers, not in the latest:");
                    foreach (var entry in game.Earlier)
                        s.AppendLine("    " + entry);
                }
            }
        }

        s.AppendLine();

        // Other services (v2.2).
        s.AppendLine("Other services (from version 2.2.0 on)");
        s.AppendLine("--------------------------------------");
        var anyService = false;
        foreach (var (id, lines) in services ?? Array.Empty<(string, IEnumerable<string>)>())
        {
            var n = ServiceSection(s, id, lines);
            counts.ServiceLines[id] = n;
            counts.Services += n;
            anyService |= n > 0;
        }

        if (!anyService)
            s.AppendLine("No other service was tested in this window. Other services are off until you turn one on in Settings.");
        s.AppendLine();
        s.AppendLine("Not measured");
        s.AppendLine("------------");
        s.AppendLine("- The route past the last router that answered, including the game server's own time.");
        s.AppendLine("- Anything while this PC was off, asleep, or Gecko Peeks was closed.");
        s.AppendLine("- Other devices on your network.");
        s.AppendLine();
        s.AppendLine("Files in this zip");
        s.AppendLine("-----------------");
        s.AppendLine($"route-log.csv  {csv.Count} saved tests (summary per test).");
        s.AppendLine($"tests.jsonl    {counts.Tests} tests with every hop, network names, outcome and League ping.");
        s.AppendLine($"activity.log   {activityLines} lines of what the app did.");
        s.AppendLine($"cloud.jsonl    {counts.Cloud} Cloudflare and Riot status answers (only when they changed).");
        s.AppendLine($"league.jsonl   {counts.League} League ping samples.");
        foreach (var (id, n) in counts.ServiceLines)
            s.AppendLine($"services/{id}.jsonl  {n} tests of this service, with every hop, the target class and how the target was chosen.");
        s.AppendLine();
        s.AppendLine("Privacy: " + PrivacyText);
        s.AppendLine("Gecko Peeks made this zip on this PC and sent it nowhere.");
        return s.ToString();
    }

    /// <summary>
    /// One service: what its target is (the class in words), how many tests, the target's ping, the
    /// last router before it, and whether the target was in the owner's network. Facts only. Returns
    /// the number of records read.
    /// </summary>
    private static int ServiceSection(StringBuilder s, string id, IEnumerable<string> lines)
    {
        var n = 0;
        string? name = null, classText = null, label = null, caveat = null, statusNote = null, ownerName = null;
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var pingAvgs = new List<double>();
        var pingLosses = new List<double>();
        var noReply = 0;
        var lastHop = new List<double>();
        int inOwner = 0, outOwner = 0, notChecked = 0;
        var otherAsns = new SortedSet<long>();
        foreach (var line in lines)
        {
            using var doc = TryParse(line);
            if (doc == null)
                continue;
            n++;
            var r = doc.RootElement;
            name = Str(r, "serviceName") ?? name;
            classText = Str(r, "targetClassText") ?? classText;
            label = Str(r, "targetLabel") ?? label;
            caveat = Str(r, "caveat") ?? caveat;
            statusNote = Str(r, "statusNote") ?? statusNote;
            if (r.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                ownerName = Str(owner, "name") ?? ownerName;
            var outcome = Str(r, "outcome") ?? "?";
            outcomes[outcome] = outcomes.TryGetValue(outcome, out var c) ? c + 1 : 1;
            if (outcome == "noTarget" && Str(r, "reason") is { } why)
                reasons.Add(why);
            if (Str(r, "targetIp") is { } ip)
            {
                var desc = Str(r, "targetName");
                targets.Add(desc == null ? ip : $"{desc} ({ip})");
            }

            if (outcome != "measured")
                continue;
            if (r.TryGetProperty("targetIcmp", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                if (Dbl(t, "avgMs") is { } avg)
                {
                    pingAvgs.Add(avg);
                    if (Dbl(t, "lossPct") is { } loss)
                        pingLosses.Add(loss);
                }
                else if (Dbl(t, "lossPct") is { } loss && loss >= 100)
                {
                    noReply++;
                }
            }

            if (r.TryGetProperty("lastHop", out var lh) && lh.ValueKind == JsonValueKind.Object && Dbl(lh, "avgMs") is { } la)
                lastHop.Add(la);
            var io = r.TryGetProperty("targetInOwnerNetwork", out var ioEl) ? ioEl.ValueKind : JsonValueKind.Null;
            if (io == JsonValueKind.True)
            {
                inOwner++;
            }
            else if (io == JsonValueKind.False)
            {
                outOwner++;
                if (r.TryGetProperty("targetAsn", out var ta) && ta.ValueKind == JsonValueKind.Number && ta.TryGetInt64(out var asn))
                    otherAsns.Add(asn);
            }
            else
            {
                notChecked++;
            }
        }

        if (n == 0)
            return 0;
        s.AppendLine($"{name ?? id} [{classText ?? "target class not known"}]");
        if (label != null)
            s.AppendLine($"  Target: {label}.");
        if (targets.Count > 0)
            s.AppendLine("  Addresses traced: " + string.Join(", ", targets.Take(10)) + (targets.Count > 10 ? $" and {targets.Count - 10} more" : ""));
        s.AppendLine($"  {n} tests: " + string.Join(", ", outcomes.Select(o => $"{o.Value} {ServiceOutcomeText(o.Key)}")) + ".");
        foreach (var why in reasons.Take(5))
            s.AppendLine("  No target: " + why);
        if (pingAvgs.Count > 0)
            s.AppendLine($"  Ping to the target (ICMP): answered in {pingAvgs.Count} of {pingAvgs.Count + noReply} tests, average {Ms(pingAvgs.Average())}"
                + (pingLosses.Count > 0 ? $", average loss {Pct(pingLosses.Average())}" : "") + ".");
        if (noReply > 0)
            s.AppendLine($"  Ping to the target (ICMP): no reply in {noReply} tests. No reply is not 0 ms, and it does not prove the service is down.");
        if (lastHop.Count > 0)
            s.AppendLine($"  Last router before the target: average {Ms(lastHop.Average())} over {lastHop.Count} tests.");
        if (inOwner + outOwner + notChecked > 0)
        {
            var who = ownerName ?? "the owner";
            s.AppendLine($"  Target network check: in the network of {who} in {inOwner} tests"
                + (outOwner > 0 ? $", in another network in {outOwner} tests ({string.Join(", ", otherAsns.Select(a => "AS" + a.ToString(CultureInfo.InvariantCulture)))})" : "")
                + (notChecked > 0 ? $", not checked in {notChecked} tests (no network number)" : "") + ".");
        }

        if (caveat != null)
            s.AppendLine("  Note: " + caveat);
        if (statusNote != null)
            s.AppendLine("  Status feed: " + statusNote);
        return n;
    }

    private static string ServiceOutcomeText(string outcome) => outcome switch
    {
        "measured" => "measured",
        "noReply" => "with no router answering",
        "noTarget" => "with no target to test",
        "cancelled" => "cancelled",
        "stopped" => "stopped by an error",
        _ => outcome,
    };

    /// <summary>"time: Upstream, City → Upstream, City" for each change between consecutive tests with known routes.</summary>
    public static List<string> RouteChanges(IReadOnlyList<RouteRow> rows)
    {
        var list = new List<string>();
        string? previous = null;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Upstream) && string.IsNullOrWhiteSpace(row.HandoffCity))
                continue;
            var route = RouteText(row);
            if (previous != null && route != previous)
                list.Add($"{row.Timestamp}: {previous} -> {route}");
            previous = route;
        }

        return list;
    }

    private static string RouteText(RouteRow row)
    {
        var upstream = Or(row.Upstream, "upstream not named");
        var handoff = string.IsNullOrWhiteSpace(row.HandoffNetwork) ? "" : " to " + row.HandoffNetwork;
        var city = string.IsNullOrWhiteSpace(row.HandoffCity) ? "" : " in " + row.HandoffCity;
        return upstream + handoff + city;
    }

    private sealed record GameStatus(string Label, DateTime LatestAt, List<string> Latest, List<string> Earlier);

    /// <summary>
    /// Riot status per game and region, from the saved answers in time order. Only an answered
    /// feed (an object) counts: a null feed says nothing, so that game's last answer stays.
    /// </summary>
    private static List<GameStatus> RiotStatusEntries(IEnumerable<string> cloud, Counts counts)
    {
        var games = new Dictionary<string, (DateTime At, Dictionary<string, string> Latest, Dictionary<string, string> Seen)>(StringComparer.Ordinal);
        foreach (var line in cloud)
        {
            using var doc = TryParse(line);
            if (doc == null)
                continue;
            counts.Cloud++;
            if (Str(doc.RootElement, "kind") != "riotStatus" || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object || DiagnosticsStore.AtUtc(line) is not { } lineAt)
                continue;
            var region = Str(data, "region");
            foreach (var (game, name) in new[] { ("lol", "League"), ("valorant", "VALORANT") })
            {
                if (!data.TryGetProperty(game, out var g) || g.ValueKind != JsonValueKind.Object)
                    continue;
                var label = region == null ? name : $"{name} ({region})";
                if (!games.TryGetValue(label, out var state))
                    state = (DateTime.MinValue, new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
                var newer = lineAt >= state.At;
                if (newer)
                {
                    state.At = lineAt;
                    state.Latest.Clear();
                }

                foreach (var list in new[] { "incidents", "maintenances" })
                {
                    if (!g.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                        var kind = Str(item, "type") ?? (list == "incidents" ? "incident" : "maintenance");
                        var key = kind + "/" + id + "/" + Str(item, "title");
                        var text = $"{label} {kind}: {Or(Str(item, "title"), "(no title)")}"
                            + (Str(item, "status") is { } st ? $", {st}" : "")
                            + (Str(item, "createdAt") is { } at ? $", posted {at}" : "");
                        state.Seen[key] = text;
                        if (newer)
                            state.Latest[key] = text;
                    }
                }

                games[label] = state;
            }
        }

        return games.OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GameStatus(g.Key, g.Value.At, g.Value.Latest.Values.ToList(),
                g.Value.Seen.Where(e => !g.Value.Latest.ContainsKey(e.Key)).Select(e => e.Value).ToList()))
            .ToList();
    }

    /// <summary>What the files hold that the user may not want to share. The export dialog shows it too.</summary>
    public const string PrivacyText =
        "these files hold your router addresses (public and private), your internet provider's name, your route, "
        + "your Wi-Fi name, your gateway address, your network adapter and VPN, your region, the League match server, "
        + "the city, approximate coordinates and time zone Cloudflare sees for you, and folder paths that can "
        + "include your Windows user name. When you turned on other services, they also hold the routes to "
        + "those services and the relay or edge address nearest to you.";

    // ---------- Helpers ----------

    /// <summary>
    /// Activity lines of the window from each folder, oldest first. A line with no time stamp (for
    /// example a second line of an error) takes the time of the line before it. A line that sits in
    /// two folders (activity.log is copied when the log folder moves) counts once per copy in the
    /// folder that has more of it, so real repeats stay.
    /// </summary>
    private static List<string> ActivityLines(IReadOnlyList<string> folders, DateTime? fromLocal, DateTime toLocal)
    {
        var kept = new List<(DateTime At, string Line)>();
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var here = new Dictionary<string, int>(StringComparer.Ordinal);
            DateTime? last = null;
            foreach (var line in ActivityLog.ReadAll(folder))
            {
                var at = ActivityLog.LineTime(line) ?? last;
                last = at;
                var inWindow = at is { } t ? t <= toLocal && (fromLocal == null || t >= fromLocal) : fromLocal == null;
                if (!inWindow)
                    continue;
                here[line] = here.TryGetValue(line, out var n) ? n + 1 : 1;
                if (here[line] > (taken.TryGetValue(line, out var had) ? had : 0))
                {
                    taken[line] = here[line];
                    kept.Add((at ?? DateTime.MinValue, line));
                }
            }
        }

        return kept.OrderBy(l => l.At).Select(l => l.Line).ToList();
    }

    public static DateTime? CsvTime(string? timestamp) =>
        DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static void Add(ZipArchive zip, string name, Action<StreamWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        write(writer);
    }

    private static void WriteLines(StreamWriter writer, IEnumerable<string> lines, string newline)
    {
        foreach (var line in lines)
        {
            writer.Write(line);
            writer.Write(newline);
        }
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? Num(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;

    private static string Ms(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Pct(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture) + " %";

    private static string Local(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Or(string? value, string fallback = "not known") => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string OutcomeText(string outcome) => outcome switch
    {
        "saved" => "saved",
        "pending" => "saved to the pending file (main log was locked)",
        "notSaved" => "not saved to the CSV (write failed)",
        "cancelled" => "cancelled",
        "noReply" => "with no router answering",
        "stopped" => "stopped by an error",
        _ => outcome,
    };
}
