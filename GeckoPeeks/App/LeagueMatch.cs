using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using GeckoPeeks.Cloud;

namespace GeckoPeeks.App;

/// <summary>The address and port a running League match connected to, from League's own match log.</summary>
public sealed record MatchServer(string Ip, int Port)
{
    public string Text => Ip + ":" + Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One row of League's own network log (Logs\GameLogs\&lt;start&gt;\&lt;start&gt;_netlog.txt), about every
/// 10 s during a match: League's in-game ping, the reliable-packet loss % and jitter in that window.
/// </summary>
public sealed record GamePing(int PingMs, double LossPct, int JitterMs, int LatencyWindowMs, DateTime AtUtc, long GameTimeMs, bool Reconnect);

/// <summary>
/// Reads which server the current League match connected to. League writes it in its own match log
/// (Logs\GameLogs\&lt;start time&gt;\&lt;start time&gt;_r3dlog.txt) as "ServerAddress(a.b.c.d:port)".
/// The file is only read, shared, on this PC; the game process is never touched. Only the server
/// address is taken; nothing else in the log (player and game ids) is kept or sent anywhere.
/// </summary>
public static class LeagueMatch
{
    /// <summary>A match log not written for this long belongs to a finished match.</summary>
    public static readonly TimeSpan MaxLogAge = TimeSpan.FromHours(3);

    /// <summary>The server line sits near the top of the log; this much is read at most.</summary>
    public const int MaxReadBytes = 256 * 1024;

    /// <summary>A match log is created a few seconds after the game process starts; this allows for clock rounding.</summary>
    public static readonly TimeSpan StartSlack = TimeSpan.FromMinutes(2);

    public const string MetadataFile =
        @"Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml";

    private static readonly Regex ServerLine = new(
        @"ServerAddress\((?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d{1,5})\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex InstallLine = new(
        @"^\s*product_install_full_path\s*:\s*""?(?<path>[^""\r\n]+)""?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(200));

    /// <summary>The first "ServerAddress(ip:port)" in the text. Null unless it is a public IPv4 address and a real port.</summary>
    public static MatchServer? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        Match match;
        try
        {
            match = ServerLine.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
            return null;
        var ip = match.Groups["ip"].Value;
        if (!IPAddress.TryParse(ip, out var address) || address.ToString() != ip || !PublicIp.IsPublic(ip))
            return null;
        if (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return null;
        return new MatchServer(ip, port);
    }

    /// <summary>The League install folder from Riot's metadata file, else null.</summary>
    public static string? InstallFromMetadata(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return null;
        try
        {
            var match = InstallLine.Match(yaml);
            if (!match.Success)
                return null;
            var path = match.Groups["path"].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Logs\GameLogs of the League install on this PC, else null.</summary>
    public static string? FindGameLogs()
    {
        var candidates = new List<string>();
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var meta = Path.Combine(programData, MetadataFile);
            if (File.Exists(meta) && InstallFromMetadata(ReadShared(meta, 64 * 1024)) is { } install)
                candidates.Add(install);
        }
        catch
        {
            // Fall back to the default folder.
        }

        candidates.Add(@"C:\Riot Games\League of Legends");
        foreach (var install in candidates)
        {
            var logs = Path.Combine(install, "Logs", "GameLogs");
            if (Directory.Exists(logs))
                return logs;
        }

        return null;
    }

    /// <summary>
    /// The server of the newest match log under <paramref name="gameLogs"/>, when that log was
    /// written in the last <see cref="MaxLogAge"/> and, with <paramref name="gameStartedUtc"/>,
    /// created after the running game process started (so a log from an earlier match never counts).
    /// The caller asks only while League runs.
    /// </summary>
    public static MatchServer? Current(string? gameLogs, DateTime utcNow, DateTime? gameStartedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(gameLogs))
            return null;
        try
        {
            var log = new DirectoryInfo(gameLogs)
                .EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(3)
                .SelectMany(d => d.EnumerateFiles("*_r3dlog.txt"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log == null || utcNow - log.LastWriteTimeUtc > MaxLogAge)
                return null;
            if (gameStartedUtc is { } started && log.CreationTimeUtc < started - StartSlack)
                return null;
            return Parse(ReadShared(log.FullName, MaxReadBytes));
        }
        catch
        {
            return null;
        }
    }

    // ---------- In-game ping from League's own network log ----------

    /// <summary>A netlog not written for this long is not a live match (League writes a row about every 10 s).</summary>
    public static readonly TimeSpan MaxPingAge = TimeSpan.FromSeconds(60);

    /// <summary>The netlog grows about 30 KB an hour; only its end is read for the latest rows.</summary>
    public const int MaxNetlogTailBytes = 64 * 1024;

    /// <summary>Columns per data row in League's netlog (the header lists them; League masks the address as X.X.X.X).</summary>
    public const int NetlogColumns = 24;

    /// <summary>
    /// The data rows of a netlog text, oldest first. Header lines ("time: text") and malformed rows are
    /// skipped. Only time, ping, loss %, jitter, latency window and the reconnect flag are kept; the
    /// address column is never kept. <paramref name="lastWriteUtc"/> is when the newest row was written,
    /// so each row's wall time is the last write minus its distance in game time from the newest row.
    /// </summary>
    public static IReadOnlyList<GamePing> ParseNetlog(string? text, DateTime lastWriteUtc)
    {
        var rows = new List<(long T, GamePing P)>();
        if (string.IsNullOrEmpty(text))
            return Array.Empty<GamePing>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !char.IsDigit(line[0]))
                continue;
            var f = line.Split(',');
            if (f.Length != NetlogColumns)
                continue;
            if (!long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                || !int.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var ping)
                || !int.TryParse(f[14], NumberStyles.None, CultureInfo.InvariantCulture, out var window)
                || !double.TryParse(f[15], NumberStyles.Float, CultureInfo.InvariantCulture, out var loss)
                || !int.TryParse(f[16], NumberStyles.None, CultureInfo.InvariantCulture, out var jitter))
                continue;
            if (ping is < 0 or > 10000 || window is < 0 or > 10000 || !double.IsFinite(loss) || loss is < 0 or > 100 || jitter is < 0 or > 10000)
                continue;
            var reconnect = f[23].Trim() == "1";
            rows.Add((t, new GamePing(ping, loss, jitter, window, DateTime.MinValue, t, reconnect)));
        }

        if (rows.Count == 0)
            return Array.Empty<GamePing>();

        // If League's game clock went back inside one log (a reset), only the rows after the last
        // reset share a time base with the newest row.
        var from = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].T < rows[i - 1].T)
                from = i;
        }

        if (from > 0)
            rows = rows.GetRange(from, rows.Count - from);
        var lastT = rows[^1].T;
        return rows.Select(r => r.P with { AtUtc = lastWriteUtc - TimeSpan.FromMilliseconds(Math.Max(0, lastT - r.T)) }).ToArray();
    }

    /// <summary>
    /// The netlog rows of the running match (newest *_netlog.txt, same folder rules as <see cref="Current"/>),
    /// or empty when there is none or it was not written in the last <see cref="MaxPingAge"/>.
    /// </summary>
    public static IReadOnlyList<GamePing> CurrentPings(string? gameLogs, DateTime utcNow, DateTime? gameStartedUtc)
    {
        if (string.IsNullOrWhiteSpace(gameLogs))
            return Array.Empty<GamePing>();
        try
        {
            var log = new DirectoryInfo(gameLogs)
                .EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(3)
                .SelectMany(d => d.EnumerateFiles("*_netlog.txt"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log == null || utcNow - log.LastWriteTimeUtc > MaxPingAge)
                return Array.Empty<GamePing>();
            if (gameStartedUtc is { } started && log.CreationTimeUtc < started - StartSlack)
                return Array.Empty<GamePing>();
            return ParseNetlog(ReadTail(log.FullName, MaxNetlogTailBytes), log.LastWriteTimeUtc);
        }
        catch
        {
            return Array.Empty<GamePing>();
        }
    }

    /// <summary>The row nearest to <paramref name="atUtc"/>, if one is within <paramref name="within"/>.</summary>
    public static GamePing? Nearest(IReadOnlyList<GamePing> rows, DateTime atUtc, TimeSpan within)
    {
        GamePing? best = null;
        var bestGap = TimeSpan.MaxValue;
        foreach (var row in rows)
        {
            var gap = (row.AtUtc - atUtc).Duration();
            // On a tie the later row wins (rows are oldest first).
            if (gap <= within && gap <= bestGap)
            {
                best = row;
                bestGap = gap;
            }
        }

        return best;
    }

    /// <summary>The end of a file that League may still be writing; a cut first line is dropped.</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (start > 0)
        {
            var cut = text.IndexOf('\n');
            text = cut >= 0 ? text[(cut + 1)..] : "";
        }

        return text;
    }

    /// <summary>Reads the start of a file that League may still be writing.</summary>
    private static string ReadShared(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(maxBytes, Math.Max(0, stream.Length))];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;
            read += n;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
