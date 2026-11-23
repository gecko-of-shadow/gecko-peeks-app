using System.IO.Compression;
using System.Text.Json.Nodes;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>v2.2.3: the export's trace to the address Riot support asks for. Documentation addresses only.</summary>
public sealed class SupportTraceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", "tr-" + Guid.NewGuid().ToString("N"));

    public SupportTraceTests() => Directory.CreateDirectory(_folder);

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
    public void Refdata_lists_riots_published_addresses_and_keeps_the_test_target()
    {
        var data = RefData.BuiltIn;
        var na = data.FindRegion("na1")!;
        Assert.Equal("192.207.0.1", na.SupportTracert!.Ip);
        Assert.Equal("NA (including Mexico)", na.SupportTracert.Server);
        Assert.Equal("192.207.0.5", na.TargetIp); // the app's own test target does not change
        var expected = new Dictionary<string, (string Ip, string Server)>
        {
            ["na1"] = ("192.207.0.1", "NA (including Mexico)"),
            ["euw1"] = ("162.249.72.1", "EU"),
            ["kr"] = ("103.219.128.1", "KR"),
            ["br1"] = ("45.7.37.1", "BR"),
            ["la2"] = ("151.106.249.1", "LATAM Chile"),
            ["sg2"] = ("151.106.248.1", "AP (Asia Pacific) Singapore"),
            ["me1"] = ("99.83.199.240", "Bahrain"),
            ["ru"] = ("13.248.220.97", "EU Eastern Russia"),
        };
        foreach (var (id, (ip, server)) in expected)
        {
            Assert.Equal(ip, data.FindRegion(id)!.SupportTracert!.Ip);
            Assert.Equal(server, data.FindRegion(id)!.SupportTracert!.Server);
        }

        Assert.Equal(new[] { "75.2.105.73" }, data.FindRegion("me1")!.SupportTracert!.AlsoListed);
        Assert.Equal(new[] { "76.223.72.224" }, data.FindRegion("ru")!.SupportTracert!.AlsoListed);
        // No server on Riot's page matches these regions: no address, never a guess.
        foreach (var id in new[] { "eun1", "la1", "oc1", "jp1", "tw2", "vn2", "tr1" })
            Assert.Null(data.FindRegion(id)!.SupportTracert);
    }

    [Fact]
    public void A_bad_support_address_is_left_out_and_never_fails_the_file()
    {
        var node = JsonNode.Parse(File.ReadAllText(RefDataPath()))!.AsObject();
        node["regions"]![0]!["supportTracert"] = JsonNode.Parse("{\"ip\":\"10.0.0.1\",\"server\":\"NA\"}");
        node["regions"]![1]!["supportTracert"] = "not an object";
        Assert.True(RefData.TryParse(node.ToJsonString(), out var data, out var error), error);
        Assert.Null(data!.Regions[0].SupportTracert);
        Assert.Null(data.Regions[1].SupportTracert);
    }

    [Fact]
    public void The_command_is_riots_tracert_with_a_per_probe_limit()
    {
        Assert.Equal("-w 1000 -h 30 192.207.0.1", SupportTracertRunner.Arguments("192.207.0.1"));
    }

    [Fact]
    public void Reached_means_a_hop_line_names_the_address()
    {
        Assert.True(SupportTracertRunner.ReachedTarget(FakeOutput, "192.0.2.1"));
        Assert.True(SupportTracertRunner.ReachedTarget("  3    20 ms    21 ms    20 ms  edge.example.net [192.0.2.1]\r\n", "192.0.2.1"));
        // The header names the address too, but it is not a hop.
        Assert.False(SupportTracertRunner.ReachedTarget("\r\nTracing route to 192.0.2.1 over a maximum of 30 hops\r\n  1     *        *        *     Request timed out.\r\n", "192.0.2.1"));
        Assert.False(SupportTracertRunner.ReachedTarget("  2     9 ms     8 ms     9 ms  192.0.2.10\r\n", "192.0.2.1"));
    }

    [Fact]
    public async Task A_bad_address_fails_without_running_anything()
    {
        var r = await SupportTracertRunner.RunAsync(new SupportTracert("not-an-ip", "NA", Array.Empty<string>()), CancellationToken.None);
        Assert.Equal("failed", r.Outcome);
    }

    private const string FakeOutput =
        "\r\nTracing route to 192.0.2.1 over a maximum of 30 hops\r\n\r\n  1    <1 ms    <1 ms    <1 ms  192.168.0.1\r\n  2     9 ms     8 ms     9 ms  198.51.100.7\r\n  3    20 ms    21 ms    20 ms  192.0.2.1\r\n\r\nTrace complete.\r\n";

    private (string Summary, ZipArchive Zip) Export(SupportTrace? support)
    {
        var store = new DiagnosticsStore(Path.Combine(_folder, "Diagnostics"), "9.9.9");
        var zip = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".zip");
        DiagnosticsExport.Write(new ExportRequest(zip, null, DateTime.UtcNow, "Everything saved", "9.9.9", store,
            Array.Empty<RouteRow>(), Array.Empty<string>(), "NA", "192.207.0.5", support));
        var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("summary.txt")!.Open());
        return (reader.ReadToEnd(), archive);
    }

    private static readonly SupportTracert Na = new("192.207.0.1", "NA (including Mexico)", Array.Empty<string>());

    [Fact]
    public void A_complete_trace_goes_in_as_tracert_txt_with_its_label()
    {
        var (summary, zip) = Export(new SupportTrace("NA", Na, new TracertResult("finished", FakeOutput, "", 41, true)));
        using (zip)
        {
            using var reader = new StreamReader(zip.GetEntry("Tracert.txt")!.Open());
            Assert.Equal(FakeOutput, reader.ReadToEnd());
        }

        Assert.Contains("Trace to the address Riot support asks for", summary, StringComparison.Ordinal);
        Assert.Contains("Riot's tracert page lists 192.207.0.1 for its server \"NA (including Mexico)\"", summary, StringComparison.Ordinal);
        Assert.Contains("Riot's VALORANT support site", summary, StringComparison.Ordinal);
        Assert.Contains("Trace: finished in 41 s; 192.207.0.1 answered.", summary, StringComparison.Ordinal);
        Assert.Contains("It is not the address the app's own tests trace.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_trace_keeps_its_part_and_says_so()
    {
        var part = "\r\nTracing route to 192.0.2.1 over a maximum of 30 hops\r\n\r\n  1    <1 ms    <1 ms    <1 ms  192.168.0.1\r\n";
        var (summary, zip) = Export(new SupportTrace("NA", Na, new TracertResult("timedOut", part, "The trace was stopped after 150 seconds.", 150)));
        using (zip)
        {
            using var reader = new StreamReader(zip.GetEntry("Tracert.txt")!.Open());
            var text = reader.ReadToEnd();
            Assert.StartsWith(part, text, StringComparison.Ordinal);
            Assert.Contains("[Gecko Peeks] The trace was stopped after 150 seconds.", text, StringComparison.Ordinal);
        }

        Assert.Contains("Trace: not finished. The trace was stopped after 150 seconds.", summary, StringComparison.Ordinal);
        Assert.Contains("192.207.0.1 had not answered by then.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_trace_still_makes_the_zip()
    {
        var (summary, zip) = Export(new SupportTrace("NA", Na, new TracertResult("failed", "", "Windows tracert.exe was not found.", 0)));
        using (zip)
            Assert.Null(zip.GetEntry("Tracert.txt"));
        Assert.Contains("Trace: failed. Windows tracert.exe was not found.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_region_with_no_published_address_says_so_and_guesses_nothing()
    {
        var (summary, zip) = Export(new SupportTrace("EUNE", null, null));
        using (zip)
            Assert.Null(zip.GetEntry("Tracert.txt"));
        Assert.Contains("Riot publishes no tracert address for this region (EUNE). No extra trace was run.", summary, StringComparison.Ordinal);
    }

    private static string RefDataPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cloud", "worker", "data", "refdata.json")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "cloud", "worker", "data", "refdata.json");
    }
}
