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
