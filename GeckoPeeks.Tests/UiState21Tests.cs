using System.Text.Json;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;
using GeckoPeeks.Ui.State;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>v2.1 state additions (contract 6.1), setRegion (6.2) and the neutral demo (6.4).</summary>
public class UiState21Tests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Local);

    private static RegionInfo Na1 => RefData.BuiltIn.FindRegion("na1")!;

    private static UiState State(CloudView? cloud = null, RegionView? region = null) => new(
        new AppInfo("2.1.0", "Production", false),
        new ProbeView
        {
            HasRoute = true,
            Upstream = "HE",
            HandoffNetwork = "Riot Direct",
            HandoffCity = "chi01",
            HandoffCityName = "Chicago",
            UpstreamSource = "asn",
            HandoffSource = "hostname",
            UpstreamAsn = 6939,
            HandoffLat = 41.88,
            HandoffLon = -87.63,
            Hops = new[]
            {
                new HopView(4, "198.51.100.4", "", 17, true, false, false, false, 64501, "Example Net", "HE", "asn", ""),
                new HopView(5, "198.51.100.5", "ae1.er01.chi01.riotdirect.net", 38, true, false, false, false, 6507, "Riot Games",
                    "Riot Direct", "hostname", "Chicago"),
            },
        },
        UiMapping.Log(false, LogHealth.Saved, @"C:\logs", "riot-route-log.csv", "Saved row at 10:00:00.", "", "", 0),
        new[] { new HistoryRow("2026-11-21T09:45:00", "HE", "Riot Direct", "chi01", 39, 0, null, 100, false, false, "Ethernet", "Chicago") },
        cloud ?? new CloudView(true, "ok", "", 10, 20, "{\"asn\":64500}", "{\"isp\":{\"asn\":64500}}",
            "{\"region\":\"na1\",\"lol\":null,\"valorant\":null}", 40, new RefDataView("2026.11.21-1", "worker", 3600)),
        new UpdateView("upToDate", "2.1.0", null, null, "", "", "", null, false),
        new SettingsView(true, false, true, "euw1"),
        region ?? UiMapping.Region(Na1, true, "country", "192.207.0.5"),
        UiMapping.Regions(RefData.BuiltIn));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void State_can_leave_the_history_out()
    {
        var with = UiStateJson.Build(State(), "[]");
        var without = UiStateJson.Build(State(), "[]", includeHistory: false);

        Assert.Contains("\"history\":", with);
        Assert.DoesNotContain("\"history\":", without);
        Assert.Equal("state", Parse(without).GetProperty("type").GetString());

        // The log's test count goes with the history, never without it.
        Assert.Equal(25000, Parse(UiStateJson.Build(State(), "[]", true, 25000)).GetProperty("historyTotal").GetInt32());
        Assert.False(Parse(UiStateJson.Build(State(), "[]", false, 25000)).TryGetProperty("historyTotal", out _));
    }

    [Fact]
    public void State_has_every_v21_field_with_the_frozen_names()
    {
        var root = Parse(UiStateJson.Build(State()));

        var region = root.GetProperty("region");
        foreach (var name in new[] { "id", "shortName", "name", "auto", "basis", "serverCity", "cityConfirmed", "lat", "lon",
                     "lolStatusId", "valorantShard", "target", "lastTestTarget" })
            Assert.True(region.TryGetProperty(name, out _), "region." + name);
        Assert.Equal("na1", region.GetProperty("id").GetString());
        Assert.Equal("NA", region.GetProperty("shortName").GetString());
        Assert.True(region.GetProperty("auto").GetBoolean());
        Assert.Equal("country", region.GetProperty("basis").GetString());
        Assert.Equal("Chicago", region.GetProperty("serverCity").GetString());
        Assert.Equal("192.207.0.5", region.GetProperty("lastTestTarget").GetString());
        var target = region.GetProperty("target");
        Assert.Equal("192.207.0.5", target.GetProperty("ip").GetString());
        Assert.Equal(RefData.EdgeLabel, target.GetProperty("label").GetString());
        Assert.Equal("anycast", target.GetProperty("kind").GetString());
        Assert.True(target.GetProperty("inRegion").GetBoolean());

        var regions = root.GetProperty("regions");
        Assert.Equal(RefData.BuiltIn.Regions.Count, regions.GetArrayLength());
        var first = regions[0];
        foreach (var name in new[] { "id", "shortName", "name", "serverCity", "cityConfirmed" })
            Assert.True(first.TryGetProperty(name, out _), "regions[0]." + name);
        Assert.False(first.TryGetProperty("target", out _));

        var probe = root.GetProperty("probe");
        Assert.Equal("asn", probe.GetProperty("upstreamSource").GetString());
        Assert.Equal("hostname", probe.GetProperty("handoffSource").GetString());
        Assert.Equal(6939, probe.GetProperty("upstreamAsn").GetInt64());
        Assert.Equal(41.88, probe.GetProperty("handoffLat").GetDouble());
        Assert.Equal(-87.63, probe.GetProperty("handoffLon").GetDouble());
        Assert.Equal("Chicago", probe.GetProperty("handoffCityName").GetString());
        var hop = probe.GetProperty("hops")[1];
        Assert.Equal("Riot Direct", hop.GetProperty("network").GetString());
        Assert.Equal("hostname", hop.GetProperty("nameSource").GetString());
        Assert.Equal("Chicago", hop.GetProperty("cityName").GetString());

        var cloud = root.GetProperty("cloud");
        Assert.Equal(JsonValueKind.Object, cloud.GetProperty("riotStatus").ValueKind);
        Assert.Equal(40, cloud.GetProperty("riotStatusAgeSec").GetDouble());
        var refdata = cloud.GetProperty("refdata");
        Assert.Equal("2026.11.21-1", refdata.GetProperty("version").GetString());
        Assert.Equal("worker", refdata.GetProperty("source").GetString());
        Assert.Equal(3600, refdata.GetProperty("ageSec").GetDouble());

        Assert.Equal("euw1", root.GetProperty("settings").GetProperty("region").GetString());
        Assert.Equal("Chicago", root.GetProperty("history")[0].GetProperty("handoffCityName").GetString());
    }

    [Fact]
    public void Unknown_values_are_null_or_empty()
    {
        var probe = new ProbeView();
        var root = Parse(UiStateJson.Build(State() with { Probe = probe, Settings = new SettingsView(false, false, false) }));
        var p = root.GetProperty("probe");
        Assert.Equal(JsonValueKind.Null, p.GetProperty("upstreamAsn").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("handoffLat").ValueKind);
        Assert.Equal("", p.GetProperty("upstreamSource").GetString());
        Assert.Equal("auto", root.GetProperty("settings").GetProperty("region").GetString());
    }

    [Fact]
    public void Region_without_a_city_has_null_coordinates_and_dns_label()
    {
        var ru = UiMapping.Region(RefData.BuiltIn.FindRegion("ru")!, false, "manual", "");
        var euw = UiMapping.Region(RefData.BuiltIn.FindRegion("euw1")!, false, "manual", "");
        var root = Parse(UiStateJson.Build(State(region: ru)));
        var region = root.GetProperty("region");
        Assert.Equal("", region.GetProperty("serverCity").GetString());
        Assert.Equal(JsonValueKind.Null, region.GetProperty("lat").ValueKind);
        Assert.Equal(JsonValueKind.Null, region.GetProperty("lon").ValueKind);
        Assert.False(region.GetProperty("target").GetProperty("inRegion").GetBoolean());
        Assert.Equal("manual", region.GetProperty("basis").GetString());
        Assert.False(region.GetProperty("auto").GetBoolean());
        Assert.Equal(RefData.PlatformLabel, euw.Target.Label);
        Assert.Equal("dns", euw.Target.Kind);
    }

    [Fact]
    public void Broken_riot_status_becomes_null()
    {
        foreach (var bad in new[] { "not json", "[1,2]", "", "{\"a\":" })
        {
            var cloud = new CloudView(true, "ok", "", null, null, null, null, bad, null, null);
            var root = Parse(UiStateJson.Build(State(cloud)));
            Assert.Equal(JsonValueKind.Null, root.GetProperty("cloud").GetProperty("riotStatus").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("cloud").GetProperty("refdata").ValueKind);
        }
    }

    [Theory]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"auto\"}", "auto")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"AUTO\"}", "auto")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"euw1\"}", "euw1")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"KR\"}", "kr")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"sg2\"}", "sg2")]
    public void SetRegion_parses_valid_ids(string json, string expected)
    {
        var message = BridgeMessage.Parse(json);
        Assert.NotNull(message);
        Assert.Equal(BridgeCommand.SetRegion, message!.Command);
        Assert.Equal(expected, message.RegionId);
    }

    [Theory]
    [InlineData("{\"cmd\":\"setRegion\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":null}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":5}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"automatic\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"euw12\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"e\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"abcd1\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"eu w\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"../x\"}")]
    [InlineData("{\"cmd\":\"setRegion\",\"id\":\"1na\"}")]
    public void SetRegion_ignores_bad_ids(string json)
    {
        Assert.Null(BridgeMessage.Parse(json));
    }

    [Fact]
    public void Demo_route_is_the_neutral_he_to_riot_direct_route()
    {
        var probe = DemoData.Probe(Now);
        Assert.Equal("HE", probe.Upstream);
        Assert.Equal("hostname", probe.UpstreamSource);
        Assert.Equal("Riot Direct", probe.HandoffNetwork);
        Assert.Equal("chi01", probe.HandoffCity);
        Assert.Equal("Chicago", probe.HandoffCityName);
        Assert.Equal(6939, probe.UpstreamAsn);
        Assert.Equal(7, probe.Hops.Count);
        Assert.Equal("asn", probe.Hops[3].NameSource);
        Assert.Equal("HE", probe.Hops[3].Network);
        Assert.True(probe.Hops[6].ReachedTarget);
        Assert.Equal("192.207.0.5", probe.Hops[6].Ip);
        Assert.True(probe.Hops[0].IsPrivate);
        Assert.True(probe.Hops[1].IsPrivate);
        Assert.False(probe.Hops[2].Responded);
        // The demo target hop answers, so the Riot figure does too (Overview and Route agree).
        Assert.Equal(40, probe.Riot.AvgMs);
        Assert.Equal(probe.Hops[6].RttMs, probe.Riot.AvgMs);
        Assert.DoesNotContain("no reply", probe.Riot.Figure);
        Assert.Equal(new long[] { 6939 }, probe.UpstreamAsns);

        var history = DemoData.History(Now);
        Assert.Contains(history, r => r.Upstream == "HE" && r.HandoffNetwork == "Riot Direct" && r.HandoffCity == "chi01");
        Assert.Contains(history, r => r.Upstream == "Cogent");
        Assert.All(history, r => Assert.Equal("Riot Direct", r.HandoffNetwork));

        var state = State(DemoData.Cloud(Now.ToUniversalTime(), Na1, RefData.BuiltIn)) with
        {
            App = new AppInfo("2.1.0", "Dev", true),
            Probe = probe,
            History = history,
        };
        var root = Parse(UiStateJson.Build(state));
        Assert.True(root.GetProperty("app").GetProperty("demo").GetBoolean());
        Assert.Equal("HE", root.GetProperty("probe").GetProperty("upstream").GetString());
    }

    [Fact]
    public void Demo_cloud_has_upstreams_and_fixed_status()
    {
        var cloud = DemoData.Cloud(Now.ToUniversalTime(), Na1, RefData.BuiltIn);
        Assert.Equal("builtin", cloud.RefData!.Source);
        Assert.Equal(RefData.BuiltIn.Version, cloud.RefData.Version);
        Assert.Null(cloud.RefData.AgeSec);

        var edge = Parse(cloud.EdgeJson!);
        Assert.Equal(64500, edge.GetProperty("asn").GetInt64());
        Assert.Equal("Demo ISP", edge.GetProperty("asOrg").GetString());
        Assert.Equal("US", edge.GetProperty("country").GetString());
        Assert.Equal("NA", edge.GetProperty("continent").GetString());

        var isp = Parse(cloud.PulseJson!).GetProperty("isp");
        var ups = isp.GetProperty("upstreams");
        Assert.Equal(3, ups.GetArrayLength());
        Assert.Equal(174, ups[0].GetProperty("asn").GetInt64());
        Assert.Equal("provider", ups[1].GetProperty("rel").GetString());
        Assert.Equal("peer", ups[2].GetProperty("rel").GetString());

        var status = Parse(cloud.RiotStatusJson!);
        Assert.Equal("na1", status.GetProperty("region").GetString());
        var maint = status.GetProperty("lol").GetProperty("maintenances");
        Assert.Equal(1, maint.GetArrayLength());
        Assert.Equal("scheduled", maint[0].GetProperty("status").GetString());
        Assert.Equal("Demo: scheduled maintenance", maint[0].GetProperty("title").GetString());
        Assert.Equal(0, status.GetProperty("valorant").GetProperty("incidents").GetArrayLength());
        Assert.Equal(0, status.GetProperty("valorant").GetProperty("maintenances").GetArrayLength());
    }

    [Fact]
    public void Demo_data_no_longer_holds_the_v20_demo_route()
    {
        var text = string.Join("\n",
            DemoData.EdgeJson,
            DemoData.PulseJson(Now.ToUniversalTime()),
            UiStateJson.Build(State() with { Probe = DemoData.Probe(Now), History = DemoData.History(Now) }));
        foreach (var old in new[] { "sea1", "Seattle", "Zayo", "Telia", "twelve99" })
            Assert.DoesNotContain(old, text, StringComparison.OrdinalIgnoreCase);
    }
}
