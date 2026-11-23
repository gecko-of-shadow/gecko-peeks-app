using System.IO.Compression;
using System.Text.Json;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Ui.State;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>v2.2: other services (Valve relay first). Made-up sample data and documentation addresses only.</summary>
public sealed class ServicesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", "svc-" + Guid.NewGuid().ToString("N"));

    public ServicesTests() => Directory.CreateDirectory(_folder);

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

    // ---------- refdata ----------

    private static readonly string ValveEntry = """
        {"id":"valve-sdr","name":"Steam (CS2, Dota 2)","group":"pc","requires":["services-v1","sdr-nearest-pop"],"targetClass":"owned",
         "owner":{"name":"Valve","asns":[32590]},
         "target":{"kind":"sdr-nearest-pop","appId":730,"icmpExpected":true,"label":"Route to your nearest Valve relay (ICMP)"},
         "caveat":"c","processes":["cs2.exe"],"processesVerified":false,"status":null,"statusNote":"Valve publishes no status feed."}
        """;

    private static JsonElement Root(string servicesJson)
    {
        using var doc = JsonDocument.Parse("{\"services\":" + servicesJson + "}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Built_in_refdata_has_the_valve_service_and_provider()
    {
        var data = RefData.BuiltIn;
        var valve = Assert.Single(data.Services, s => s.Id == "valve-sdr");
        Assert.Equal(TargetClass.Owned, valve.Class);
        Assert.Equal("Owned network", valve.ClassText);
        Assert.Equal(ServiceInfo.KindSdr, valve.Target.Kind);
        Assert.Equal(730, valve.Target.AppId);
        Assert.Equal(new long[] { 32590 }, valve.Owner.Asns);
        Assert.Null(valve.Status);
        Assert.Equal("Valve", data.ProviderForAsn(32590)?.Name);
        Assert.Equal("Valve", data.ProviderForHost("162-254-199-170.valve.net")?.Name);
    }

    [Fact]
    public void Unverified_process_names_are_never_used()
    {
        var list = ServiceCatalog.Parse(Root("[" + ValveEntry + "]"));
        Assert.False(list[0].ProcessesVerified);
        Assert.Empty(list[0].Processes);
    }

    [Theory]
    [InlineData("\"targetClass\":\"owned\"", "\"targetClass\":\"best\"")]
    [InlineData("\"kind\":\"sdr-nearest-pop\"", "\"kind\":\"user-entered\"")]
    [InlineData("\"sdr-nearest-pop\"]", "\"sdr-nearest-pop\",\"future-feature\"]")]
    [InlineData("\"requires\":[\"services-v1\",\"sdr-nearest-pop\"],", "")]
    [InlineData("\"appId\":730", "\"appId\":\"730\"")]
    [InlineData("\"asns\":[32590]", "\"asns\":[\"x\"]")]
    [InlineData("\"id\":\"valve-sdr\"", "\"id\":\"Valve SDR\"")]
    public void A_bad_or_newer_entry_is_skipped(string from, string to)
    {
        var bad = ValveEntry.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(ValveEntry, bad);
        Assert.Empty(ServiceCatalog.Parse(Root("[" + bad + "]")));
    }

    [Fact]
    public void A_bad_services_block_never_fails_the_refdata_file()
    {
        var json = File.ReadAllText(RefDataPath());
        using var doc = JsonDocument.Parse(json);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["services"] = System.Text.Json.Nodes.JsonNode.Parse("[1, {\"id\":\"x\"}, null, \"bad\"]");
        Assert.True(RefData.TryParse(node.ToJsonString(), out var data, out var error), error);
        Assert.Empty(data!.Services);
        Assert.NotEmpty(data.Regions);
        node["services"] = "not a list";
        Assert.True(RefData.TryParse(node.ToJsonString(), out data, out error), error);
        Assert.Empty(data!.Services);
    }

    private static string RefDataPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cloud", "worker", "data", "refdata.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "cloud", "worker", "data", "refdata.json");
    }

    // ---------- relay list ----------

    private static string SdrJson(long revision = 100) => $$"""
        {"app":730,"revision":{{revision}},"fetchedAt":"2026-11-22T00:00:00Z","pops":[
          {"code":"ams","desc":"Amsterdam (Netherlands)","lat":52.37,"lon":4.9,"relays":["155.133.248.36","10.0.0.1"]},
          {"code":"atl","desc":"Atlanta (Georgia)","lat":33.76,"lon":-84.39,"relays":["162.254.199.170"]},
          {"code":"ord","desc":"Chicago (Illinois)","lat":41.88,"lon":-87.63,"relays":["162.254.192.71"]},
          {"code":"iad","desc":"Sterling (Virginia)","lat":39.04,"lon":-77.49,"relays":["162.254.193.6"]},
          {"code":"sea","desc":"Seattle (Washington)","lat":47.6,"lon":-122.3,"relays":["205.196.6.67"]},
          {"code":"pvt","desc":"Private only","lat":40,"lon":-80,"relays":["192.168.0.1"]}
        ]}
        """;

    [Fact]
    public void Relay_list_keeps_public_ipv4_relays_only()
    {
        var c = SdrConfig.Parse(SdrJson(), DateTime.UtcNow)!;
        Assert.Equal(730, c.App);
        Assert.Equal(5, c.Pops.Count);
        Assert.Equal(new[] { "155.133.248.36" }, c.Pops.Single(p => p.Code == "ams").Relays);
        Assert.Null(SdrConfig.Parse("{\"app\":730}", DateTime.UtcNow));
        Assert.Null(SdrConfig.Parse("not json", DateTime.UtcNow));
    }

    private sealed class FakeResolver : IHopResolver
    {
        public Dictionary<string, long> Asns { get; } = new();

        public Task<string> ReverseAsync(string ip, bool allowDoh, CancellationToken cancellationToken) => Task.FromResult("");

        public Task<IReadOnlyDictionary<string, long>> AsnAsync(IReadOnlyList<string> publicIps, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(publicIps.Where(Asns.ContainsKey).ToDictionary(ip => ip, ip => Asns[ip]));
    }

    private sealed class Rig
    {
        public DateTime Now = new(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);
        public bool CloudOn = true;
        public (double, double)? Location = (41.5, -87.0); // near Chicago
        public string? SdrAnswer;
        public string? StatusAnswer;
        public string? Resolved = "104.16.0.10";
        public string? Continent = "NA";
        public string? Country;
        public readonly Dictionary<string, double?> Pings = new();
        public readonly List<string> Traced = new();
        public Func<string, RouteRow>? Row;
        public ServiceProbes Probes = null!;
        public ServiceSettings Settings = null!;
        public ServiceFeeds Feeds = null!;
        public DiagnosticsStore Store = null!;
        public FakeResolver Resolver = new();
    }

    private Rig NewRig(bool on = true)
    {
        var rig = new Rig();
        rig.Settings = new ServiceSettings(Path.Combine(_folder, "service-settings.json"));
        if (on)
            rig.Settings.Set("valve-sdr", true);
        var handler = new StubHandler(path => path.StartsWith("/v1/sdr", StringComparison.Ordinal) ? rig.SdrAnswer
            : path.StartsWith("/v1/service-status", StringComparison.Ordinal) ? rig.StatusAnswer : null);
        var client = new CloudClient(handler, "https://example.invalid");
        rig.Feeds = new ServiceFeeds(client, () => rig.CloudOn, () => rig.Probes.Enabled(), () => rig.Continent, () => rig.Now, () => rig.Country);
        rig.Store = new DiagnosticsStore(Path.Combine(_folder, "Diagnostics"), "9.9.9");
        rig.Probes = new ServiceProbes(() => RefData.BuiltIn, rig.Settings, rig.Feeds, rig.Resolver, rig.Store,
            () => rig.CloudOn, () => rig.Location)
        {
            UtcNow = () => rig.Now,
            PingAvg = (ip, _) => Task.FromResult(rig.Pings.TryGetValue(ip, out var v) ? v : null),
            Resolve = (_, _) => Task.FromResult(rig.Resolved),
            Runner = (request, _, _) =>
            {
                rig.Traced.Add(request.TargetIp);
                Assert.NotEqual("Riot", request.PingLabel);
                return Task.FromResult(rig.Row?.Invoke(request.TargetIp) ?? MeasuredRow(request.TargetIp));
            },
        };
        return rig;
    }

    private static RouteRow MeasuredRow(string target, long targetAsn = 32590, string targetAvg = "24") => new()
    {
        Timestamp = "2026-11-22 12:00:00",
        TargetIp = target,
        Upstream = "HE",
        HandoffNetwork = "Valve",
        LastHopIP = "162.254.199.247",
        LastHopAvg = "23",
        LastHopLossPct = "0",
        RiotHopAvg = targetAvg,
        RiotHopLossPct = targetAvg.Length == 0 ? "100" : "0",
        Hops = new List<HopObservation>
        {
            new(1, "192.168.1.1", "", 1, true, false),
            new(2, "184.105.64.1", "core1.example.he.net", 12, true, false),
            new(3, "162.254.199.247", "162-254-199-247.valve.net", 23, true, false),
            new(4, target, "", 24, true, true),
        },
        HopAsns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["184.105.64.1"] = 6939, [target] = targetAsn },
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _answer;

        public StubHandler(Func<string, string?> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _answer(request.RequestUri!.PathAndQuery);
            return Task.FromResult(body == null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static Task Run(Rig rig, bool all = false) => rig.Probes.RunDueAsync(all, _ => { }, CancellationToken.None);

    private string[] Lines(string id = "valve-sdr") =>
        new DiagnosticsStore(Path.Combine(_folder, "Diagnostics"), "x").ReadLines(DiagnosticsStore.ServicePrefix(id), null, DateTime.UtcNow.AddYears(5)).ToArray();

    [Fact]
    public async Task Cloudflare_live_data_off_gives_no_target_and_no_trace()
    {
        var rig = NewRig();
        rig.CloudOn = false;
        await Run(rig);
        var r = rig.Probes.Last("valve-sdr")!;
        Assert.Equal("noTarget", r.Outcome);
        Assert.Contains("Cloudflare live data is off", r.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Traced);
    }

    [Fact]
    public async Task A_service_that_is_off_never_runs()
    {
        var rig = NewRig(on: false);
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        await Run(rig, all: true);
        Assert.Empty(rig.Traced);
        Assert.False(rig.Probes.AnyDue());
        Assert.Null(rig.Feeds.Sdr(730));
    }

    [Fact]
    public async Task Lowest_ping_of_the_three_nearest_pops_is_traced_and_recorded_with_its_class()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 30; // ord, nearest
        rig.Pings["162.254.193.6"] = 18;  // iad
        rig.Pings["162.254.199.170"] = 22; // atl
        rig.Pings["155.133.248.36"] = 1;   // ams: far away, never pinged
        await Run(rig);

        Assert.Equal(new[] { "162.254.193.6" }, rig.Traced);
        var r = rig.Probes.Last("valve-sdr")!;
        Assert.Equal("measured", r.Outcome);
        Assert.True(r.OwnerMatch);
        Assert.Equal("Sterling (Virginia)", r.TargetName);
        Assert.Equal(new[] { "ord", "iad", "atl" }, r.Pop!.Candidates);
        Assert.Contains("Lowest ping of the 3 relays nearest", r.Pop.How, StringComparison.Ordinal);

        var line = Assert.Single(Lines());
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("owned", root.GetProperty("targetClass").GetString());
        Assert.Equal("Owned network", root.GetProperty("targetClassText").GetString());
        Assert.Equal(32590, root.GetProperty("targetAsn").GetInt64());
        Assert.True(root.GetProperty("targetInOwnerNetwork").GetBoolean());
        Assert.Equal("iad", root.GetProperty("relay").GetProperty("pop").GetString());
        Assert.Equal(24, root.GetProperty("targetIcmp").GetProperty("avgMs").GetDouble());
        Assert.False(root.TryGetProperty("riotIcmp", out _));
        Assert.Equal(4, root.GetProperty("hops").GetArrayLength());

        // The Riot test files are untouched.
        Assert.Empty(rig.Store.ReadLines(DiagnosticsStore.TestsPrefix, null, DateTime.UtcNow.AddYears(5)));
    }

    [Fact]
    public async Task A_relay_outside_the_owner_network_says_so()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Row = ip => MeasuredRow(ip, targetAsn: 64500);
        await Run(rig);
        var r = rig.Probes.Last("valve-sdr")!;
        Assert.False(r.OwnerMatch);
        Assert.Equal(64500, r.TargetAsn);
        using var doc = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.False(doc.RootElement.GetProperty("targetInOwnerNetwork").GetBoolean());
    }

    [Fact]
    public async Task No_answering_relay_falls_back_to_the_nearest_and_says_so()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        await Run(rig);
        Assert.Equal(new[] { "162.254.192.71" }, rig.Traced);
        Assert.Contains("None of the 3 nearest relays answered", rig.Probes.Last("valve-sdr")!.Pop!.How, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_service_keeps_its_own_15_minute_interval()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10;
        await Run(rig);
        Assert.False(rig.Probes.AnyDue());
        rig.Now = rig.Now.AddMinutes(5);
        await Run(rig);
        Assert.Single(rig.Traced);
        rig.Now = rig.Now.AddMinutes(10);
        Assert.True(rig.Probes.AnyDue());
        await Run(rig);
        Assert.Equal(2, rig.Traced.Count);
        // Run test now runs every service that is on.
        await Run(rig, all: true);
        Assert.Equal(3, rig.Traced.Count);
    }

    [Fact]
    public async Task The_same_no_target_reason_is_recorded_once_and_retried_after_two_minutes()
    {
        var rig = NewRig();
        rig.SdrAnswer = null; // the Worker answers 502
        await rig.Feeds.TickAsync();
        await Run(rig);
        Assert.Equal("noTarget", rig.Probes.Last("valve-sdr")!.Outcome);
        Assert.False(rig.Probes.AnyDue());
        rig.Now = rig.Now.AddMinutes(2);
        Assert.True(rig.Probes.AnyDue());
        await Run(rig);
        Assert.Single(Lines());
    }

    [Fact]
    public async Task A_stale_relay_list_is_not_used()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        Assert.NotNull(rig.Feeds.Sdr(730));
        rig.SdrAnswer = null;
        rig.Now = rig.Now + ServiceFeeds.SdrMaxAge + TimeSpan.FromMinutes(1);
        await rig.Feeds.TickAsync();
        Assert.Null(rig.Feeds.Sdr(730));
    }

    [Fact]
    public async Task A_relay_with_no_reply_is_chosen_again_next_time()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10;
        rig.Row = ip => MeasuredRow(ip, targetAvg: "");
        await Run(rig);
        rig.Pings["162.254.192.71"] = null;
        rig.Pings["162.254.193.6"] = 12;
        rig.Row = null;
        await Run(rig, all: true);
        Assert.Equal(new[] { "162.254.192.71", "162.254.193.6" }, rig.Traced);
    }

    [Fact]
    public async Task A_new_list_revision_chooses_again_and_the_same_revision_keeps_the_choice()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson(100);
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10;
        await Run(rig);
        rig.Pings["162.254.192.71"] = 50;
        rig.Pings["162.254.199.170"] = 5;
        await Run(rig, all: true);
        Assert.Equal("162.254.192.71", rig.Traced[^1]);
        rig.SdrAnswer = SdrJson(101);
        rig.Now = rig.Now.AddHours(25);
        await rig.Feeds.TickAsync();
        await Run(rig, all: true);
        Assert.Equal("162.254.199.170", rig.Traced[^1]);
    }

    [Fact]
    public async Task An_error_before_any_hop_is_stopped_not_measured()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Row = ip => new RouteRow { TargetIp = ip, Error = "The network is down." };
        await Run(rig);
        var r = rig.Probes.Last("valve-sdr")!;
        Assert.Equal("stopped", r.Outcome);
        Assert.Contains("The network is down.", r.Reason, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("stopped", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_new_location_chooses_the_relay_again()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10; // ord, near Chicago
        rig.Pings["155.133.248.36"] = 10; // ams
        await Run(rig);
        rig.Location = (52.0, 5.0); // a VPN in the Netherlands
        await Run(rig, all: true);
        Assert.Equal(new[] { "162.254.192.71", "155.133.248.36" }, rig.Traced);
    }

    // ---------- Discord (v2.2.1): hostname target and status feed ----------

    // The answer is set before the switch: turning a service on asks the feeds at once.
    private Rig DiscordRig(string? status = DiscordStatus)
    {
        var rig = NewRig(on: false);
        rig.StatusAnswer = status;
        rig.Settings.Set("discord", true);
        return rig;
    }

    private const string DiscordStatus = """
        {"id":"discord","name":"Discord","source":"statuspage","pageUrl":"https://discordstatus.com/","fetchedAt":"2026-11-23T00:00:00Z",
         "status":{"indicator":"none","description":"All Systems Operational","incidents":[],"maintenances":[],"components":[{"name":"US East","group":"Voice","status":"operational"}]}}
        """;

    [Fact]
    public void Built_in_refdata_has_discord_as_a_cdn_edge_with_its_feed()
    {
        var d = Assert.Single(RefData.BuiltIn.Services, s => s.Id == "discord");
        Assert.Equal(TargetClass.CdnEdge, d.Class);
        Assert.Equal("CDN edge", d.ClassText);
        Assert.Equal(ServiceInfo.KindHostname, d.Target.Kind);
        Assert.Equal("latency.discord.media", d.Target.Host);
        Assert.Equal("discord", d.Status!.Feed);
        Assert.Contains("Not your voice server", d.Target.Label, StringComparison.Ordinal);
        Assert.Equal("Cloudflare", RefData.BuiltIn.ProviderForAsn(13335)?.Name);
    }

    [Fact]
    public async Task Hostname_target_is_resolved_and_traced_without_the_worker()
    {
        var rig = DiscordRig();
        rig.CloudOn = false; // the system resolver needs no Worker
        rig.Row = ip => MeasuredRow(ip, targetAsn: 13335);
        await Run(rig);
        Assert.Equal(new[] { "104.16.0.10" }, rig.Traced);
        var r = rig.Probes.Last("discord")!;
        Assert.Equal("measured", r.Outcome);
        Assert.True(r.OwnerMatch);
        Assert.Equal("latency.discord.media", r.TargetName);
        using var doc = JsonDocument.Parse(Assert.Single(Lines("discord")));
        Assert.Equal("cdn-edge", doc.RootElement.GetProperty("targetClass").GetString());
        Assert.Equal("system", doc.RootElement.GetProperty("resolver").GetString());
        Assert.Equal("latency.discord.media", doc.RootElement.GetProperty("targetHost").GetString());
        Assert.False(doc.RootElement.TryGetProperty("relay", out _));
    }

    [Fact]
    public void A_2_2_0_app_skips_discord_and_keeps_steam()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RefDataPath()));
        var old = new HashSet<string>(StringComparer.Ordinal) { "services-v1", "sdr-nearest-pop" };
        var list = ServiceCatalog.Parse(doc.RootElement, old);
        Assert.Equal(new[] { "valve-sdr" }, list.Select(s => s.Id));
    }

    [Fact]
    public async Task A_failed_dns_lookup_says_so()
    {
        var rig = DiscordRig();
        rig.Probes.Resolve = (_, _) => throw new System.Net.Sockets.SocketException(11001);
        await Run(rig);
        var r = rig.Probes.Last("discord")!;
        Assert.Equal("noTarget", r.Outcome);
        Assert.Contains("lookup of latency.discord.media failed", r.Reason, StringComparison.Ordinal);
        rig.Probes.Resolve = (_, _) => throw new OperationCanceledException();
        await Run(rig, all: true);
        Assert.Contains("did not answer within 5 seconds", rig.Probes.Last("discord")!.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Traced);
    }

    [Fact]
    public async Task No_dns_answer_is_no_target()
    {
        var rig = DiscordRig();
        rig.Resolved = null;
        await Run(rig);
        var r = rig.Probes.Last("discord")!;
        Assert.Equal("noTarget", r.Outcome);
        Assert.Contains("latency.discord.media", r.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Traced);
    }

    [Fact]
    public async Task Status_feed_is_shown_only_while_fresh_and_cloud_on()
    {
        var rig = DiscordRig();
        await rig.Feeds.TickAsync();
        string Feed(DateTime now)
        {
            using var doc = JsonDocument.Parse(ServiceStateJson.Build(RefData.BuiltIn.Services, rig.Settings, rig.Probes, rig.Feeds, rig.CloudOn, now));
            var d = doc.RootElement.GetProperty("list").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "discord");
            Assert.Equal("discord", d.GetProperty("feedId").GetString());
            return d.GetProperty("feed").ValueKind == JsonValueKind.Object
                ? d.GetProperty("feed").GetProperty("status").GetProperty("indicator").GetString()!
                : "null";
        }

        Assert.Equal("none", Feed(rig.Now));
        rig.Now += ServiceFeeds.StatusMaxAge + TimeSpan.FromMinutes(1);
        rig.StatusAnswer = null; // the next fetch fails
        await rig.Feeds.TickAsync();
        Assert.Equal("null", Feed(rig.Now));
    }

    [Fact]
    public async Task A_feed_the_worker_could_not_read_is_not_available()
    {
        var rig = DiscordRig("{\"id\":\"discord\",\"name\":\"Discord\",\"status\":null}");
        await rig.Feeds.TickAsync();
        var answer = rig.Feeds.Status("discord");
        Assert.NotNull(answer);
        Assert.False(answer!.HasStatus);
        Assert.False(ServiceFeeds.HasStatus("{\"id\":\"xbox\",\"status\":{}}", "discord"));
    }

    [Fact]
    public async Task Turning_cloud_data_off_drops_the_feed()
    {
        var rig = DiscordRig();
        await rig.Feeds.TickAsync();
        Assert.NotNull(rig.Feeds.Status("discord"));
        rig.CloudOn = false;
        await rig.Feeds.TickAsync();
        Assert.Null(rig.Feeds.Status("discord"));
    }

    // ---------- Consoles (v2.2.2) ----------

    [Fact]
    public void Built_in_refdata_has_three_console_services_as_shared_path_only()
    {
        var consoles = RefData.BuiltIn.Services.Where(s => s.Group == "console").ToList();
        Assert.Equal(new[] { "psn", "xbox", "nintendo" }, consoles.Select(s => s.Id));
        Assert.All(consoles, c =>
        {
            Assert.Equal(TargetClass.SharedPath, c.Class);
            Assert.Equal("Shared path only", c.ClassText);
            Assert.Equal(ServiceInfo.KindHostname, c.Target.Kind);
            Assert.Contains("Probably the same edge", c.Target.Label, StringComparison.Ordinal);
            Assert.Contains("not your console's ping, NAT type or Wi-Fi", c.Caveat, StringComparison.Ordinal);
            Assert.Empty(c.Processes);
        });
        Assert.Equal("Microsoft", RefData.BuiltIn.ProviderForAsn(8075)?.Name);
        Assert.Equal("Akamai", RefData.BuiltIn.ProviderForAsn(20940)?.Name);
    }

    [Fact]
    public void A_2_2_1_app_skips_the_consoles()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RefDataPath()));
        var old = new HashSet<string>(StringComparer.Ordinal) { "services-v1", "sdr-nearest-pop", "hostname", "status-feed" };
        Assert.Equal(new[] { "valve-sdr", "discord" }, ServiceCatalog.Parse(doc.RootElement, old).Select(s => s.Id));
    }

    [Theory]
    [InlineData("NA", "psn-scea")]
    [InlineData("SA", "psn-scea")]
    [InlineData("EU", "psn-scee")]
    [InlineData("OC", "psn-scee")]
    [InlineData("AS", "psn-sceja")]
    [InlineData(null, null)]
    [InlineData("AN", null)]
    public void PlayStation_feed_follows_the_cloudflare_continent(string? continent, string? feed)
    {
        var rig = NewRig(on: false);
        rig.Continent = continent;
        var psn = RefData.BuiltIn.FindService("psn")!;
        Assert.Equal(feed, rig.Feeds.FeedFor(psn));
        Assert.Equal("xbox", rig.Feeds.FeedFor(RefData.BuiltIn.FindService("xbox")!));
    }

    [Theory]
    [InlineData("AE", "AS", "psn-scee")]  // Gulf states are in PlayStation's Europe file
    [InlineData("IN", "AS", "psn-scee")]
    [InlineData("IL", "AS", "psn-scee")]
    [InlineData("RS", "EU", "psn-sceja")] // Serbia is in the Asia file
    [InlineData("US", "NA", "psn-scea")]
    [InlineData("PK", "AS", "psn-sceja")] // not listed: the continent decides
    public void PlayStation_feed_follows_the_country_first(string country, string continent, string feed)
    {
        var rig = NewRig(on: false);
        rig.Country = country;
        rig.Continent = continent;
        Assert.Equal(feed, rig.Feeds.FeedFor(RefData.BuiltIn.FindService("psn")!));
    }

    [Fact]
    public async Task Any_akamai_network_counts_as_the_owner()
    {
        var rig = NewRig(on: false);
        rig.Settings.Set("nintendo", true);
        rig.Row = ip => MeasuredRow(ip, targetAsn: 33905);
        await Run(rig);
        Assert.True(rig.Probes.Last("nintendo")!.OwnerMatch);
    }

    [Fact]
    public async Task The_switch_turned_on_mid_interval_waits_for_5_minutes()
    {
        var rig = NewRig(on: false);
        rig.Settings.Set("xbox", true);
        await Run(rig);
        rig.Now = rig.Now.AddMinutes(2);
        rig.Settings.SetConsolePlaying(true);
        Assert.False(rig.Probes.AnyDue());
        rig.Now = rig.Now.AddMinutes(3);
        Assert.True(rig.Probes.AnyDue());
    }

    [Fact]
    public async Task Playing_on_console_runs_console_services_every_5_minutes_only()
    {
        var rig = NewRig(on: false);
        rig.Settings.Set("xbox", true);
        rig.Settings.Set("discord", true);
        rig.Row = ip => MeasuredRow(ip, targetAsn: 8075);
        await Run(rig);
        Assert.Equal(2, rig.Traced.Count);

        rig.Now = rig.Now.AddMinutes(5);
        await Run(rig);
        Assert.Equal(2, rig.Traced.Count); // switch off: nothing is due after 5 minutes

        rig.Settings.SetConsolePlaying(true);
        Assert.True(rig.Probes.AnyDue());
        await Run(rig);
        Assert.Equal(3, rig.Traced.Count); // only the console service ran
        Assert.Equal(TimeSpan.FromMinutes(5), rig.Probes.IntervalFor(RefData.BuiltIn.FindService("xbox")!));
        Assert.Equal(TimeSpan.FromMinutes(15), rig.Probes.IntervalFor(RefData.BuiltIn.FindService("discord")!));

        rig.Settings.SetConsolePlaying(false);
        rig.Now = rig.Now.AddMinutes(5);
        Assert.False(rig.Probes.AnyDue());
    }

    // ---------- window state ----------

    [Fact]
    public async Task Window_state_leaves_out_old_results()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10;
        await Run(rig);
        using (var doc = JsonDocument.Parse(ServiceStateJson.Build(RefData.BuiltIn.Services, rig.Settings, rig.Probes, rig.Feeds, true, rig.Now)))
        {
            var valve = doc.RootElement.GetProperty("list")[0];
            Assert.True(valve.GetProperty("on").GetBoolean());
            Assert.Equal("Owned network", valve.GetProperty("classText").GetString());
            Assert.Equal(24, valve.GetProperty("result").GetProperty("targetAvgMs").GetDouble());
        }

        var later = rig.Now + ServiceProbes.ResultMaxAge + TimeSpan.FromMinutes(1);
        using (var doc = JsonDocument.Parse(ServiceStateJson.Build(RefData.BuiltIn.Services, rig.Settings, rig.Probes, rig.Feeds, true, later)))
        {
            var valve = doc.RootElement.GetProperty("list")[0];
            Assert.Equal(JsonValueKind.Null, valve.GetProperty("result").ValueKind);
            Assert.True(valve.GetProperty("lastAgeSec").GetDouble() > 3600);
        }
    }

    // ---------- store and export ----------

    [Fact]
    public void A_service_prefix_never_reads_another_service_file()
    {
        var folder = Path.Combine(_folder, "Diagnostics");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "service-valve-sdr-2026-11.jsonl"), "{\"v\":1,\"at\":\"2026-11-22T00:00:00.000Z\"}\n");
        var store = new DiagnosticsStore(folder, "x");
        Assert.Empty(store.ReadLines("service-valve", null, DateTime.UtcNow.AddYears(5)));
        Assert.Single(store.ReadLines("service-valve-sdr", null, DateTime.UtcNow.AddYears(5)));
        Assert.Equal(new[] { "valve-sdr" }, store.ServiceIds());
    }

    [Fact]
    public async Task Export_has_a_section_and_a_file_for_each_service()
    {
        var rig = NewRig();
        rig.SdrAnswer = SdrJson();
        await rig.Feeds.TickAsync();
        rig.Pings["162.254.192.71"] = 10;
        rig.Now = DateTime.UtcNow.AddMinutes(-5);
        await Run(rig);
        var zip = Path.Combine(_folder, "out.zip");
        var result = DiagnosticsExport.Write(new ExportRequest(zip, null, DateTime.UtcNow, "Everything saved", "9.9.9", rig.Store,
            Array.Empty<RouteRow>(), Array.Empty<string>(), "NA", "192.207.0.5"));
        Assert.Equal(1, result.ServiceLines);
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("services/valve-sdr.jsonl"));
        using var reader = new StreamReader(archive.GetEntry("summary.txt")!.Open());
        var summary = reader.ReadToEnd();
        Assert.Contains("Steam (CS2, Dota 2) [Owned network]", summary, StringComparison.Ordinal);
        Assert.Contains("Ping to the target (ICMP): answered in 1 of 1 tests, average 24 ms", summary, StringComparison.Ordinal);
        Assert.Contains("in the network of Valve in 1 tests", summary, StringComparison.Ordinal);
        Assert.Contains("Valve publishes no status feed.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_with_no_service_says_none_was_tested()
    {
        var store = new DiagnosticsStore(Path.Combine(_folder, "Diagnostics"), "x");
        var zip = Path.Combine(_folder, "none.zip");
        DiagnosticsExport.Write(new ExportRequest(zip, null, DateTime.UtcNow, "Everything saved", "9.9.9", store,
            Array.Empty<RouteRow>(), Array.Empty<string>(), "NA", "192.207.0.5"));
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("summary.txt")!.Open());
        Assert.Contains("No other service was tested in this window.", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    // ---------- settings and bridge ----------

    [Fact]
    public void Settings_keep_services_and_the_console_switch()
    {
        var path = Path.Combine(_folder, "s.json");
        var a = new ServiceSettings(path);
        var raised = 0;
        a.Changed += () => raised++;
        a.Set("valve-sdr", true);
        a.Set("valve-sdr", true);
        a.Set("Bad Id", true);
        a.SetConsolePlaying(true);
        Assert.Equal(2, raised);
        var b = new ServiceSettings(path);
        Assert.True(b.IsOn("valve-sdr"));
        Assert.False(b.ConsolePlaying); // the console switch turns off on restart
        Assert.DoesNotContain("consolePlaying", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(new[] { "valve-sdr" }, b.On);
    }

    [Theory]
    [InlineData("{\"cmd\":\"setService\",\"id\":\"valve-sdr\",\"value\":true}", true)]
    [InlineData("{\"cmd\":\"setService\",\"id\":\"valve-sdr\",\"value\":\"yes\"}", false)]
    [InlineData("{\"cmd\":\"setService\",\"id\":\"../x\",\"value\":true}", false)]
    [InlineData("{\"cmd\":\"setService\",\"value\":true}", false)]
    [InlineData("{\"cmd\":\"setConsolePlaying\",\"value\":false}", true)]
    [InlineData("{\"cmd\":\"setConsolePlaying\"}", false)]
    public void Bridge_reads_the_service_commands(string json, bool ok)
    {
        var m = BridgeMessage.Parse(json);
        Assert.Equal(ok, m != null);
        if (m?.Command == BridgeCommand.SetService)
            Assert.Equal("valve-sdr", m.ServiceId);
    }
}
