using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeckoPeeks.App;

namespace GeckoPeeks.Logging;

/// <summary>
/// Keeps every diagnostic record the app makes, as JSON lines in monthly files:
/// tests-YYYY-MM.jsonl (one line per test, also cancelled and failed ones), cloud-YYYY-MM.jsonl
/// (Cloudflare and Riot status data, only when it changed) and league-YYYY-MM.jsonl (League's own
/// ping rows during a match). Nothing is ever deleted or sent anywhere. Every line carries "v",
/// "app" and "at" (UTC). A null folder keeps nothing. Writes never throw.
/// </summary>
public sealed class DiagnosticsStore
{
    public const string FolderName = "Diagnostics";
    public const int SchemaVersion = 1;
    public const string TestsPrefix = "tests";
    public const string CloudPrefix = "cloud";
    public const string LeaguePrefix = "league";

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _cloudHashes = new(StringComparer.Ordinal);
    private readonly string _appVersion;
    private long _lastGameTimeMs = -1;
    private DateTime? _gameStartUtc;
    private DateTime _lastPingAtUtc = DateTime.MinValue;
    private bool _leagueSeeded;
    private readonly List<(string File, string Text)> _unsaved = new();

    /// <summary>Lines kept in memory while the folder refuses writes; retried with the next write.</summary>
    public const int MaxUnsaved = 2000;

    /// <summary>Rows whose wall time minus game time differ by more than this belong to another game.</summary>
    public static readonly TimeSpan SameGameSlack = TimeSpan.FromMinutes(2);

    public DiagnosticsStore(string? folder, string appVersion)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        _appVersion = appVersion ?? "";
    }

    /// <summary>Production %LOCALAPPDATA%\GeckoPeeks\Diagnostics, Dev &lt;devRoot&gt;\Diagnostics, else null.</summary>
    public static string? PathFor(RunProfile profile, string? devRoot) =>
        GeckoPeeks.Cloud.StorePaths.For(profile, devRoot, FolderName);

    public string? Folder { get; }

    /// <summary>The last write problem, or "".</summary>
    public string LastProblem { get; private set; } = "";

    /// <summary>Lines waiting in memory because the folder refused writes.</summary>
    public int UnsavedLines
    {
        get
        {
            lock (_lock)
                return _unsaved.Count;
        }
    }

    public static string FileName(string prefix, DateTime utc) =>
        $"{prefix}-{utc:yyyy-MM}.jsonl";

    // ---------- Tests ----------

    /// <summary>One line per test. <paramref name="outcome"/>: saved, pending, notSaved, cancelled, noReply or stopped.</summary>
    public void RecordTest(TestRecord test)
    {
        if (Folder == null)
            return;
        try
        {
            var row = test.Row;
            var o = Header(test.AtUtc);
            o["outcome"] = test.Outcome;
            if (!string.IsNullOrWhiteSpace(test.Reason))
                o["reason"] = test.Reason;
            o["csvTimestamp"] = Blank(row?.Timestamp);
            o["region"] = Blank(test.RegionId);
            o["league"] = test.League;
            if (test.Match is { } match)
                o["matchServer"] = match.Text;
            if (row != null)
            {
                o["targetIp"] = Blank(row.TargetIp);
                o["upstream"] = new JsonObject
                {
                    ["name"] = Blank(row.Upstream),
                    ["source"] = Blank(row.UpstreamSource),
                    ["asn"] = row.UpstreamAsn,
                };
                o["handoff"] = new JsonObject
                {
                    ["network"] = Blank(row.HandoffNetwork),
                    ["host"] = Blank(row.HandoffHost),
                    ["ip"] = Blank(row.HandoffIP),
                    ["city"] = Blank(row.HandoffCity),
                    ["rttMs"] = Num(row.HandoffRttMs),
                    ["source"] = Blank(row.HandoffSource),
                };
                o["lastHop"] = new JsonObject
                {
                    ["ip"] = Blank(row.LastHopIP),
                    ["host"] = Blank(row.LastHopHost),
                    ["minMs"] = Num(row.LastHopMin),
                    ["avgMs"] = Num(row.LastHopAvg),
                    ["maxMs"] = Num(row.LastHopMax),
                    ["lossPct"] = Num(row.LastHopLossPct),
                };
                // Blank Riot values mean the target gave no ICMP reply, not 0.
                o["riotIcmp"] = new JsonObject
                {
                    ["avgMs"] = Num(row.RiotHopAvg),
                    ["lossPct"] = Num(row.RiotHopLossPct),
                };
                o["fullPath"] = Blank(row.FullPath);
                o["adapter"] = Blank(row.Adapter);
                o["wifi"] = Blank(row.Wifi);
                o["gateway"] = Blank(row.Gateway);
                o["vpn"] = Blank(row.Vpn);
                o["error"] = Blank(row.Error);
                var hops = new JsonArray();
                foreach (var hop in row.Hops)
                {
                    var h = new JsonObject
                    {
                        ["n"] = hop.Number,
                        ["ip"] = Blank(hop.Ip),
                        ["host"] = Blank(hop.Hostname),
                        ["rttMs"] = hop.RttMs,
                        ["responded"] = hop.Responded,
                        ["reachedTarget"] = hop.ReachedTarget,
                    };
                    if (!string.IsNullOrWhiteSpace(hop.Ip) && row.HopAsns.TryGetValue(hop.Ip, out var asn))
                        h["asn"] = asn;
                    if (test.HopNames != null && test.HopNames.TryGetValue(hop.Number, out var name) && !string.IsNullOrWhiteSpace(name))
                        h["network"] = name;
                    hops.Add(h);
                }

                o["hops"] = hops;
            }

            if (test.GamePing is { } ping)
                o["leaguePing"] = PingNode(ping);
            Append(TestsPrefix, test.AtUtc, o);
        }
        catch (Exception ex)
        {
            LastProblem = ex.Message;
        }
    }

    // ---------- Other services (v2.2) ----------

    /// <summary>Each service has its own files: service-&lt;id&gt;-YYYY-MM.jsonl.</summary>
    public static string ServicePrefix(string serviceId) => ServicePrefixStart + serviceId;

    public const string ServicePrefixStart = "service-";

    /// <summary>
    /// One line per service test. It always names the target class and how the target was chosen,
    /// so the record never reads as more than it measured. "targetIcmp" is the ping to the target.
    /// </summary>
    public void RecordService(GeckoPeeks.Routing.ServiceInfo service, ServiceResult result)
    {
        if (Folder == null)
            return;
        try
        {
            var o = Header(result.AtUtc);
            o["service"] = service.Id;
            o["serviceName"] = service.Name;
            o["outcome"] = result.Outcome;
            if (!string.IsNullOrWhiteSpace(result.Reason))
                o["reason"] = result.Reason;
            o["targetClass"] = GeckoPeeks.Routing.ServiceInfo.ClassId(service.Class);
            o["targetClassText"] = service.ClassText;
            o["targetKind"] = service.Target.Kind;
            o["targetLabel"] = service.Target.Label;
            o["caveat"] = Blank(service.Caveat);
            o["statusNote"] = Blank(service.StatusNote);
            var owner = new JsonArray();
            foreach (var asn in service.Owner.Asns)
                owner.Add(asn);
            o["owner"] = new JsonObject { ["name"] = service.Owner.Name, ["asns"] = owner };
            o["targetIp"] = Blank(result.TargetIp);
            o["targetName"] = Blank(result.TargetName);
            o["targetAsn"] = result.TargetAsn;
            o["targetInOwnerNetwork"] = result.OwnerMatch;
            if (result.Pop is { } pop)
            {
                var candidates = new JsonArray();
                foreach (var c in pop.Candidates)
                    candidates.Add(c);
                o["relay"] = new JsonObject
                {
                    ["app"] = pop.App,
                    ["listRevision"] = pop.Revision,
                    ["pop"] = pop.PopCode,
                    ["popDesc"] = pop.PopDesc,
                    ["how"] = pop.How,
                    ["candidates"] = candidates,
                    ["chosenAt"] = Iso(pop.ChosenUtc),
                };
            }

            if (result.Row is { } row)
            {
                o["upstream"] = new JsonObject
                {
                    ["name"] = Blank(row.Upstream),
                    ["source"] = Blank(row.UpstreamSource),
                    ["asn"] = row.UpstreamAsn,
                };
                o["handoff"] = new JsonObject
                {
                    ["network"] = Blank(row.HandoffNetwork),
                    ["host"] = Blank(row.HandoffHost),
                    ["ip"] = Blank(row.HandoffIP),
                    ["city"] = Blank(row.HandoffCity),
                    ["rttMs"] = Num(row.HandoffRttMs),
                    ["source"] = Blank(row.HandoffSource),
                };
                o["lastHop"] = new JsonObject
                {
                    ["ip"] = Blank(row.LastHopIP),
                    ["host"] = Blank(row.LastHopHost),
                    ["minMs"] = Num(row.LastHopMin),
                    ["avgMs"] = Num(row.LastHopAvg),
                    ["maxMs"] = Num(row.LastHopMax),
                    ["lossPct"] = Num(row.LastHopLossPct),
                };
                // Blank values mean the target gave no ICMP reply, not 0.
                o["targetIcmp"] = new JsonObject
                {
                    ["avgMs"] = Num(row.RiotHopAvg),
                    ["lossPct"] = Num(row.RiotHopLossPct),
                };
                o["fullPath"] = Blank(row.FullPath);
                o["adapter"] = Blank(row.Adapter);
                o["wifi"] = Blank(row.Wifi);
                o["gateway"] = Blank(row.Gateway);
                o["vpn"] = Blank(row.Vpn);
                o["error"] = Blank(row.Error);
                var names = result.HopNames.ToDictionary(n => n.Number, n => n.Network);
                o["hops"] = HopsNode(row, names);
            }

            Append(ServicePrefix(service.Id), result.AtUtc, o);
        }
        catch (Exception ex)
        {
            LastProblem = ex.Message;
        }
    }

    private static JsonArray HopsNode(RouteRow row, IReadOnlyDictionary<int, string>? hopNames)
    {
        var hops = new JsonArray();
        foreach (var hop in row.Hops)
        {
            var h = new JsonObject
            {
                ["n"] = hop.Number,
                ["ip"] = Blank(hop.Ip),
                ["host"] = Blank(hop.Hostname),
                ["rttMs"] = hop.RttMs,
                ["responded"] = hop.Responded,
                ["reachedTarget"] = hop.ReachedTarget,
            };
            if (!string.IsNullOrWhiteSpace(hop.Ip) && row.HopAsns.TryGetValue(hop.Ip, out var asn))
                h["asn"] = asn;
            if (hopNames != null && hopNames.TryGetValue(hop.Number, out var name) && !string.IsNullOrWhiteSpace(name))
                h["network"] = name;
            hops.Add(h);
        }

        return hops;
    }

    /// <summary>The service ids that have files in the store, sorted.</summary>
    public IReadOnlyList<string> ServiceIds()
    {
        if (Folder == null || !Directory.Exists(Folder))
            return Array.Empty<string>();
        try
        {
            return Directory.GetFiles(Folder, ServicePrefixStart + "*.jsonl")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(n => n!.Length > ServicePrefixStart.Length + 8 ? n[ServicePrefixStart.Length..^8] : "")
                .Where(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,30}$"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---------- Cloud ----------

    /// <summary>
    /// Saves one Cloudflare or Riot status answer when its content changed since the last one of
    /// the same kind. The top-level fetch times ("time", "fetchedAt") are left out of the comparison.
    /// </summary>
    public bool RecordCloud(string kind, string? json, DateTime atUtc)
    {
        if (Folder == null || string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(kind))
            return false;
        try
        {
            JsonObject data;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject parsed)
                    return false;
                data = parsed;
            }
            catch (JsonException)
            {
                // Not JSON: nothing to keep, and not a save problem.
                return false;
            }

            var compare = data.DeepClone().AsObject();
            compare.Remove("time");
            compare.Remove("fetchedAt");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compare.ToJsonString())));
            lock (_lock)
            {
                if (_cloudHashes.TryGetValue(kind, out var last) && last == hash)
                    return false;
                _cloudHashes[kind] = hash;
            }

            var o = Header(atUtc);
            o["kind"] = kind;
            o["data"] = data;
            Append(CloudPrefix, atUtc, o);
            return true;
        }
        catch (Exception ex)
        {
            LastProblem = ex.Message;
            return false;
        }
    }

    // ---------- League ----------

    /// <summary>
    /// Saves League's netlog rows not saved yet. A game is known by its start (wall time minus game
    /// time); within one game only rows with a later game time are new. After an app restart the
    /// last saved row is read back, so a running match is not saved twice. Only time, ping, loss,
    /// jitter and the reconnect flag; never an address.
    /// </summary>
    public int RecordGamePings(IReadOnlyList<GamePing> rows)
    {
        if (Folder == null || rows.Count == 0)
            return 0;
        try
        {
            // One lock from choosing the rows to writing them, so two callers (the clock and the
            // test runner) never write rows out of order.
            lock (_lock)
            {
                if (!_leagueSeeded)
                {
                    _leagueSeeded = true;
                    SeedLastGame();
                }

                var newest = rows[^1];
                var start = newest.AtUtc - TimeSpan.FromMilliseconds(newest.GameTimeMs);
                // Rows are new when their game time is past the last saved one. That mark is cleared
                // only for another game: a newer read (not another caller's older copy) whose game
                // time is not past the mark, with a start that moved, or that moved by more than the
                // whole saved game (a new game after a very short one). A start that only drifted
                // (a paused game, a clock change) with a later game time is the same game.
                var newerRead = newest.AtUtc > _lastPingAtUtc;
                var moved = _gameStartUtc is { } known ? start - known : (TimeSpan?)null;
                var otherGame = _lastGameTimeMs < 0
                    || (newerRead && moved is { } m && m.Duration() > SameGameSlack
                        && (newest.GameTimeMs <= _lastGameTimeMs || m >= TimeSpan.FromMilliseconds(_lastGameTimeMs)))
                    || (newerRead && newest.GameTimeMs < _lastGameTimeMs);
                if (otherGame)
                    _lastGameTimeMs = -1;
                if (otherGame || newerRead)
                    _gameStartUtc = start;
                var fresh = rows.Where(r => r.GameTimeMs > _lastGameTimeMs).ToList();
                foreach (var row in fresh)
                {
                    var o = Header(row.AtUtc);
                    o["gameTimeMs"] = row.GameTimeMs;
                    foreach (var (key, value) in PingNode(row))
                    {
                        if (key != "atUtc")
                            o[key] = value?.DeepClone();
                    }

                    Append(LeaguePrefix, row.AtUtc, o);
                    _lastGameTimeMs = row.GameTimeMs;
                    if (row.AtUtc > _lastPingAtUtc)
                        _lastPingAtUtc = row.AtUtc;
                }

                return fresh.Count;
            }
        }
        catch (Exception ex)
        {
            LastProblem = ex.Message;
            return 0;
        }
    }

    /// <summary>
    /// The game closed. The mark of the last saved row stays, so a late copy of the finished game's
    /// log (read by a test just before the game closed) saves nothing again; the next game is told
    /// apart by its start time.
    /// </summary>
    public void EndGame()
    {
        lock (_lock)
            _leagueSeeded = true;
    }

    /// <summary>Caller holds the lock. The last saved League row (newest league file), if any.</summary>
    private void SeedLastGame()
    {
        try
        {
            if (Folder == null || !Directory.Exists(Folder))
                return;
            var newest = Directory.GetFiles(Folder, LeaguePrefix + "-*.jsonl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
            if (newest == null)
                return;
            // The highest game time among the rows of the last saved game (same start time).
            DateTime? lastStart = null;
            long best = -1;
            DateTime bestAt = DateTime.MinValue;
            foreach (var line in SharedText.ReadLines(newest))
            {
                if (AtUtc(line) is not { } at)
                    continue;
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("gameTimeMs", out var g) || !g.TryGetInt64(out var gameTime))
                    continue;
                var start = at - TimeSpan.FromMilliseconds(gameTime);
                if (lastStart is not { } known || (start - known).Duration() > SameGameSlack)
                {
                    lastStart = start;
                    best = -1;
                    bestAt = DateTime.MinValue;
                }

                if (gameTime > best)
                {
                    best = gameTime;
                    bestAt = at;
                }
            }

            if (lastStart is { } s && best >= 0)
            {
                _lastGameTimeMs = best;
                _gameStartUtc = s;
                _lastPingAtUtc = bestAt;
            }
        }
        catch
        {
            // No seed: at worst some rows of a running match are saved twice.
        }
    }

    // ---------- Reading ----------

    /// <summary>Bytes on disk in the folder, or null when there is no folder.</summary>
    public long? SizeBytes()
    {
        if (Folder == null)
            return null;
        try
        {
            return Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("*.jsonl").Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The lines of one kind whose "at" is in [fromUtc, toUtc]; null from = all. Oldest file first.</summary>
    public IEnumerable<string> ReadLines(string prefix, DateTime? fromUtc, DateTime toUtc)
    {
        if (Folder == null || !Directory.Exists(Folder))
            yield break;
        string[] files;
        try
        {
            files = Directory.GetFiles(Folder, prefix + "-*.jsonl");
        }
        catch
        {
            yield break;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            // tests-2026-11.jsonl: skip months wholly outside the window.
            // Only prefix-YYYY-MM.jsonl: "service-valve" must not read "service-valve-sdr-2026-11.jsonl".
            var month = Path.GetFileNameWithoutExtension(file)[(prefix.Length + 1)..];
            if (month.Length != 7 || !DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
                continue;
            if (start > toUtc || (fromUtc is { } from && start.AddMonths(1) < from))
                continue;

            foreach (var line in SharedText.ReadLines(file))
            {
                if (AtUtc(line) is { } at && at <= toUtc && (fromUtc == null || at >= fromUtc))
                    yield return line;
            }
        }
    }

    /// <summary>The "at" time of a stored line, or null for a torn or foreign line.</summary>
    public static DateTime? AtUtc(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("at", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Helpers ----------

    private JsonObject Header(DateTime atUtc) => new()
    {
        ["v"] = SchemaVersion,
        ["app"] = _appVersion,
        ["at"] = Iso(atUtc),
    };

    public static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static JsonObject PingNode(GamePing ping) => new()
    {
        ["pingMs"] = ping.PingMs,
        ["lossPct"] = ping.LossPct,
        ["jitterMs"] = ping.JitterMs,
        ["latencyWindowMs"] = ping.LatencyWindowMs,
        ["reconnect"] = ping.Reconnect,
        ["atUtc"] = Iso(ping.AtUtc),
    };

    private void Append(string prefix, DateTime atUtc, JsonObject line)
    {
        var text = line.ToJsonString() + "\n";
        var file = FileName(prefix, atUtc);
        lock (_lock)
        {
            _unsaved.Add((file, text));
            try
            {
                Directory.CreateDirectory(Folder!);
                // Earlier lines that could not be written go first, in order, each to its own file.
                while (_unsaved.Count > 0)
                {
                    var (name, pending) = _unsaved[0];
                    var path = Path.Combine(Folder!, name);
                    // A write cut off part way left a line with no end: end it first, so the next
                    // line is not glued onto it.
                    if (_unsaved.Count > 1 || _hadWriteFailure || _checkedFiles.Add(path))
                        EndTornLine(path);
                    File.AppendAllText(path, pending, new UTF8Encoding(false));
                    _unsaved.RemoveAt(0);
                }

                _hadWriteFailure = false;

                LastProblem = "";
            }
            catch (Exception ex)
            {
                _hadWriteFailure = true;
                LastProblem = ex.Message;
                if (_unsaved.Count > MaxUnsaved)
                {
                    _unsaved.RemoveRange(0, _unsaved.Count - MaxUnsaved);
                    LastProblem = ex.Message + " (oldest unsaved lines dropped)";
                }
            }
        }
    }

    private bool _hadWriteFailure;

    /// <summary>Files whose end was checked in this run (a crash can leave a cut-off last line).</summary>
    private readonly HashSet<string> _checkedFiles = new(StringComparer.OrdinalIgnoreCase);

    private static void EndTornLine(string path)
    {
        if (!File.Exists(path))
            return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (stream.Length == 0)
            return;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() != '\n')
            stream.WriteByte((byte)'\n');
    }

    /// <summary>
    /// The newest line of each cloud kind saved before <paramref name="beforeUtc"/>: the state in
    /// effect when a window starts (cloud data is saved only when it changes).
    /// </summary>
    public IReadOnlyList<string> LastCloudBefore(DateTime beforeUtc)
    {
        var newest = new Dictionary<string, (DateTime At, string Line)>(StringComparer.Ordinal);
        foreach (var line in ReadLines(CloudPrefix, null, beforeUtc.AddTicks(-1)))
        {
            if (AtUtc(line) is not { } at)
                continue;
            string? kind = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                    kind = k.GetString();
            }
            catch (JsonException)
            {
                continue;
            }

            if (kind != null && (!newest.TryGetValue(kind, out var had) || at >= had.At))
                newest[kind] = (at, line);
        }

        return newest.Values.OrderBy(v => v.At).Select(v => v.Line).ToList();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double? Num(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
}

/// <summary>What one test produced, for <see cref="DiagnosticsStore.RecordTest"/>.</summary>
public sealed record TestRecord(
    DateTime AtUtc,
    string Outcome,
    string Reason,
    RouteRow? Row,
    string RegionId,
    bool League,
    MatchServer? Match,
    GamePing? GamePing,
    IReadOnlyDictionary<int, string>? HopNames = null);

/// <summary>Reads text files another writer may still have open; a torn last line is dropped by the caller's parser.</summary>
public static class SharedText
{
    public static IEnumerable<string> ReadLines(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            yield break;
        }

        using (stream)
        using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                yield return line;
        }
    }
}
