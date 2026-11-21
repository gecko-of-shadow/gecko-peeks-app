using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>Reading the match server from League's own match log. All values are made-up samples.</summary>
public sealed class LeagueMatchTests : IDisposable
{
    private const string SampleLog =
        "000000.155| ALWAYS|   CFG| Command Line:  \"192.207.0.5 7363 abc= 123\" \"-Product=LoL\" \"-PlayerID=1\"\n"
        + "000002.458| ALWAYS|  CONN| Starting Multiplayer Session: PlayerClient GameID(1) ServerAddress(192.207.0.5:7363) SummonerID(1)\n"
        + "000002.458| ALWAYS|  CONN| Connecting to address (192.207.0.5) port (7363)\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", "match-" + Guid.NewGuid().ToString("N"));

    public LeagueMatchTests() => Directory.CreateDirectory(_folder);

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

    [Fact]
    public void Parse_takes_the_server_address_and_port_only()
    {
        var server = LeagueMatch.Parse(SampleLog);

        Assert.Equal(new MatchServer("192.207.0.5", 7363), server);
        Assert.Equal("192.207.0.5:7363", server!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no server line here")]
    [InlineData("ServerAddress(192.168.1.5:7363)")]   // private: never a game server
    [InlineData("ServerAddress(10.0.0.1:7363)")]
    [InlineData("ServerAddress(999.1.1.1:7363)")]     // not an address
    [InlineData("ServerAddress(192.207.0.5:0)")]
    [InlineData("ServerAddress(192.207.0.5:70000)")]
    [InlineData("ServerAddress(192.207.000.5:7363)")] // not the canonical form
    public void Parse_refuses_anything_but_a_public_address_and_a_real_port(string text) =>
        Assert.Null(LeagueMatch.Parse(text));

    [Theory]
    [InlineData("product_install_full_path: \"G:/Riot/Riot Games/League of Legends\"", @"G:\Riot\Riot Games\League of Legends")]
    [InlineData("x: 1\nproduct_install_full_path: \"C:/Riot Games/League of Legends\"\ny: 2", @"C:\Riot Games\League of Legends")]
    [InlineData("product_install_full_path: relative/path", null)]
    [InlineData("nothing here", null)]
    public void Install_folder_comes_from_riots_metadata(string yaml, string? expected) =>
        Assert.Equal(expected, LeagueMatch.InstallFromMetadata(yaml));

    [Fact]
    public void Current_reads_the_newest_match_log()
    {
        var older = WriteLog("2026-11-20T20-00-00", "ServerAddress(192.207.0.9:7000)", DateTime.UtcNow.AddMinutes(-90));
        var newer = WriteLog("2026-11-21T12-41-14", SampleLog, DateTime.UtcNow.AddMinutes(-2));

        Assert.Equal(new MatchServer("192.207.0.5", 7363), LeagueMatch.Current(_folder, DateTime.UtcNow));
        Assert.True(File.Exists(older) && File.Exists(newer));
    }

    [Fact]
    public void A_log_from_before_this_game_started_is_an_earlier_match()
    {
        // The last match's log is 20 minutes old; the new game process started 1 minute ago and
        // has not written its own log yet.
        var path = WriteLog("2026-11-21T12-00-00", "ServerAddress(192.207.0.9:7000)", DateTime.UtcNow.AddMinutes(-20));
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-45));

        Assert.Null(LeagueMatch.Current(_folder, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-1)));

        // Its own log, created just after the game started, counts.
        var own = WriteLog("2026-11-21T12-41-14", SampleLog, DateTime.UtcNow);
        File.SetCreationTimeUtc(own, DateTime.UtcNow.AddSeconds(-50));
        Assert.Equal("192.207.0.5:7363", LeagueMatch.Current(_folder, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-1))!.Text);
    }

    [Fact]
    public void A_log_older_than_3_hours_is_a_finished_match()
    {
        WriteLog("2026-11-21T08-00-00", SampleLog, DateTime.UtcNow.AddHours(-4));

        Assert.Null(LeagueMatch.Current(_folder, DateTime.UtcNow));
    }

    [Fact]
    public void A_log_that_League_still_has_open_can_be_read()
    {
        var path = WriteLog("2026-11-21T12-41-14", SampleLog, DateTime.UtcNow);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.NotNull(LeagueMatch.Current(_folder, DateTime.UtcNow));
    }

    [Fact]
    public void No_folder_or_no_logs_is_no_match()
    {
        Assert.Null(LeagueMatch.Current(null, DateTime.UtcNow));
        Assert.Null(LeagueMatch.Current(Path.Combine(_folder, "missing"), DateTime.UtcNow));
        Assert.Null(LeagueMatch.Current(_folder, DateTime.UtcNow));
    }

    // Made-up netlog in League's format: header lines "time: text", then 24-column CSV rows.
    private const string SampleNetlog =
        "2026-11-21T13-47-11: LogOut_File: : logfile started\n"
        + "2026-11-21T13-47-11: ping = how long it takes for a packet to get back and forth. Spikes can indicate lag.\n"
        + "2026-11-21T13-47-11: [time], [address], [incoming], [outgoing], [app_ctos], [app_stoc], [loss], [sent], [ping], [variance], [reliable delayed], [unreliable delayed], [app update delayed], [Time spent in critical section (frame)], [Latency in Window], [Packet Loss percentage in Window], [Jitter in Window], [Overall Latency Min], [Overall Latency Max], [Latency Min in Window], [Latency Maxin Window], [Latency packet Samples], [Jitter Packet Samples],[reconnect]\n"
        + "12046,X.X.X.X,58788,9144,9144,58788,0,41,51,0,0,0,0,0,51,0.000000,1,51,51,51,51,38,38,1\n"
        + "22047,X.X.X.X,62012,11297,2153,3224,0,59,59,0,0,0,0,0,59,1.500000,2,59,59,59,59,56,56,0\n"
        + "bad,row\n"
        + "32048,X.X.X.X,68292,15413,4116,6280,0,111,66,0,0,0,0,0,66,0.000000,4,66,66,66,66,107,107,0\n";

    [Fact]
    public void Netlog_rows_give_ping_loss_jitter_and_wall_time()
    {
        var written = new DateTime(2026, 11, 21, 19, 47, 44, DateTimeKind.Utc);

        var rows = LeagueMatch.ParseNetlog(SampleNetlog, written);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 51, 59, 66 }, rows.Select(r => r.PingMs));
        Assert.Equal(1.5, rows[1].LossPct);
        Assert.Equal(4, rows[2].JitterMs);
        Assert.True(rows[0].Reconnect);
        Assert.Equal(written, rows[2].AtUtc);                                   // newest row = last write
        Assert.Equal(written.AddMilliseconds(-(32048 - 12046)), rows[0].AtUtc); // older rows by game time
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-11-21T13-47-11: only header\n")]
    [InlineData("12046,X.X.X.X,1,2,3\n")]                                                          // too few columns
    [InlineData("12046,X.X.X.X,58788,9144,9144,58788,0,41,-5,0,0,0,0,0,51,0.000000,1,51,51,51,51,38,38,0\n")] // negative ping
    [InlineData("12046,X.X.X.X,58788,9144,9144,58788,0,41,51,0,0,0,0,0,51,250.0,1,51,51,51,51,38,38,0\n")]   // loss over 100 %
    public void Netlog_text_without_valid_rows_gives_nothing(string text) =>
        Assert.Empty(LeagueMatch.ParseNetlog(text, DateTime.UtcNow));

    [Fact]
    public void The_live_netlog_of_this_game_is_read_and_an_old_one_is_not()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_folder, "2026-11-21T13-46-59"));
        var path = Path.Combine(dir.FullName, "2026-11-21T13-46-59_netlog.txt");
        File.WriteAllText(path, SampleNetlog);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));

        var live = LeagueMatch.CurrentPings(_folder, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-11));
        Assert.Equal(66, live[^1].PingMs);

        // Written 2 minutes ago: the match is over.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        Assert.Empty(LeagueMatch.CurrentPings(_folder, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-11)));

        // A log from before this game process started is an earlier match.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));
        Assert.Empty(LeagueMatch.CurrentPings(_folder, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void After_a_game_clock_reset_only_the_rows_after_it_count()
    {
        var text = SampleNetlog
            + "5000,X.X.X.X,1,1,1,1,0,1,80,0,0,0,0,0,80,0.000000,1,80,80,80,80,1,1,0\n"
            + "15001,X.X.X.X,1,1,1,1,0,1,81,0,0,0,0,0,81,0.000000,1,81,81,81,81,1,1,0\n";
        var written = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

        var rows = LeagueMatch.ParseNetlog(text, written);

        Assert.Equal(new[] { 80, 81 }, rows.Select(r => r.PingMs));
        Assert.Equal(written.AddMilliseconds(-10001), rows[0].AtUtc);
    }

    [Fact]
    public void A_test_pairs_with_the_netlog_row_nearest_in_time_within_30_seconds()
    {
        var t0 = new DateTime(2026, 11, 21, 19, 50, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new GamePing(60, 0, 1, 60, t0.AddSeconds(-20), 1, false),
            new GamePing(64, 0, 1, 64, t0.AddSeconds(-8), 2, false),
            new GamePing(70, 0, 1, 70, t0.AddSeconds(2), 3, false),
        };

        Assert.Equal(70, LeagueMatch.Nearest(rows, t0, TimeSpan.FromSeconds(30))!.PingMs);
        Assert.Null(LeagueMatch.Nearest(rows, t0.AddMinutes(5), TimeSpan.FromSeconds(30)));
    }

    private string WriteLog(string name, string text, DateTime writtenUtc)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_folder, name));
        var path = Path.Combine(dir.FullName, name + "_r3dlog.txt");
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        Directory.SetLastWriteTimeUtc(dir.FullName, writtenUtc);
        return path;
    }
}
