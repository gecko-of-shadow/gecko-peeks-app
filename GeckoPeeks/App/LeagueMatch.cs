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
