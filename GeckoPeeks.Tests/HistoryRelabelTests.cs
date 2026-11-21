using GeckoPeeks.Logging;
using GeckoPeeks.Routing;
using GeckoPeeks.Ui.State;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// Rows saved as Unknown are named again from their saved hops, for display only.
/// Addresses here are neutral demo values (Hurricane Electric, Cogent and Riot Direct examples).
/// </summary>
public sealed class HistoryRelabelTests
{
    private const string DemoPath = "192.168.1.1 > 100.64.0.1 > * > 184.105.64.1 > 184.105.80.1 > 104.160.131.1 > 192.207.0.5";
    private static readonly string[] DemoLookups = { "184.105.64.1", "184.105.80.1", "104.160.131.1", "192.207.0.5" };

    private static RouteRow Row(string upstream, string path = DemoPath, string time = "2026-11-02 00:01:10", string gateway = "192.168.1.1") =>
        new() { Timestamp = time, Upstream = upstream, FullPath = path, Gateway = gateway, LastHopAvg = "20" };

    private static HistoryRelabel.Lookups Looked(
        IEnumerable<string> done,
        IDictionary<string, long>? asns = null,
        IDictionary<string, string>? hosts = null) =>
        new(
            new Dictionary<string, string>(hosts ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(asns ?? new Dictionary<string, long>()),
            new HashSet<string>(done, StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("Unknown", DemoPath, true)]
    [InlineData("unknown", DemoPath, true)]
    [InlineData("", DemoPath, true)]
    [InlineData("  ", DemoPath, true)]
    [InlineData("HE", DemoPath, false)]
    [InlineData("Unknown", "", false)]
    public void NeedsName_only_for_unnamed_rows_with_a_saved_path(string upstream, string path, bool expected) =>
        Assert.Equal(expected, HistoryRelabel.NeedsName(Row(upstream, path)));

    [Fact]
    public void Rows_from_before_the_gateway_column_are_left_as_saved()
    {
        Assert.False(HistoryRelabel.NeedsName(Row("Unknown", gateway: "")));
        Assert.Empty(HistoryRelabel.AddressesToLookUp(new[] { Row("Unknown", gateway: "") }));
    }

    [Fact]
    public void Local_addresses_are_the_hops_that_never_go_out()
    {
        var local = HistoryRelabel.LocalAddresses(new[] { Row("Unknown") });

        Assert.Equal(new[] { "192.168.1.1", "100.64.0.1" }, local);
        Assert.Empty(local.Intersect(HistoryRelabel.AddressesToLookUp(new[] { Row("Unknown") })));
    }

    [Fact]
    public void A_local_router_name_counts_like_in_a_live_test()
    {
        // The private hop 2 has a Cogent name from this PC's DNS; the public hops are HE by ASN.
        var path = "192.168.1.1 > 184.105.64.1 > 10.0.0.9 > 104.160.131.1";
        var asns = new Dictionary<string, long> { ["184.105.64.1"] = 6939, ["104.160.131.1"] = 6507 };
        var hosts = new Dictionary<string, string> { ["10.0.0.9"] = "be2000.ccr41.ord01.atlas.cogentco.com" };

        var named = HistoryRelabel.Name(Row("Unknown", path), Looked(new[] { "184.105.64.1", "104.160.131.1" }, asns, hosts), RefData.BuiltIn);

        Assert.Equal("Cogent", named!.Upstream);
    }

    [Fact]
    public void PathIps_keeps_order_and_silent_hops()
    {
        Assert.Equal(new[] { "192.168.1.1", "*", "184.105.64.1" }, HistoryRelabel.PathIps("192.168.1.1 > * > 184.105.64.1"));
        Assert.Empty(HistoryRelabel.PathIps(""));
        Assert.Empty(HistoryRelabel.PathIps(null));
    }

    [Fact]
    public void Only_public_routers_of_unnamed_rows_are_looked_up_once_each()
    {
        var rows = new[]
        {
            Row("HE", "184.105.99.9 > 104.160.131.9"),
            Row("Unknown"),
            Row("Unknown"),
        };

        var ips = HistoryRelabel.AddressesToLookUp(rows);

        Assert.Equal(DemoLookups, ips);
        Assert.DoesNotContain("192.168.1.1", ips);   // private
        Assert.DoesNotContain("100.64.0.1", ips);    // carrier-grade NAT
        Assert.DoesNotContain("184.105.99.9", ips);  // that row already has a name
    }

    [Fact]
    public void Hop_1_and_the_gateway_never_go_out_even_when_public()
    {
        // A PC that dials PPPoE itself: hop 1 is a public address on the user's own line.
        var row = Row("Unknown", "184.105.1.1 > 184.105.2.2 > 184.105.64.1 > 104.160.131.1", gateway: "184.105.2.2");

        var ips = HistoryRelabel.AddressesToLookUp(new[] { row });

        Assert.Equal(new[] { "184.105.64.1", "104.160.131.1" }, ips);
        Assert.Equal(ips, HistoryRelabel.RowLookups(row));
    }

    [Fact]
    public void A_row_whose_routers_do_not_all_fit_the_cap_is_left_out_whole()
    {
        var rows = Enumerable.Range(1, 30)
            .Select(i => Row("Unknown", $"192.168.1.1 > 184.105.{i}.1 > 184.105.{i}.2 > 184.105.{i}.3"))
            .ToArray();

        var ips = HistoryRelabel.AddressesToLookUp(rows);

        Assert.True(ips.Count <= HistoryRelabel.MaxLookups);
        Assert.Equal(0, ips.Count % 3); // whole rows only
    }

    [Fact]
    public void Answered_routers_do_not_use_up_the_cap_so_older_rows_are_reached()
    {
        var rows = Enumerable.Range(1, 30)
            .Select(i => Row("Unknown", $"192.168.1.1 > 184.105.{i}.1 > 184.105.{i}.2 > 184.105.{i}.3"))
            .ToArray();
        var firstLoad = HistoryRelabel.AddressesToLookUp(rows);
        var done = new HashSet<string>(firstLoad);

        var secondLoad = HistoryRelabel.AddressesToLookUp(rows, done.Contains);

        Assert.NotEmpty(secondLoad);
        Assert.DoesNotContain(secondLoad, done.Contains);
        Assert.Contains("184.105.1.1", firstLoad.Concat(secondLoad).Concat(HistoryRelabel.AddressesToLookUp(rows, ip => done.Contains(ip) || secondLoad.Contains(ip))));
    }

    [Fact]
    public void Network_numbers_name_the_upstream_when_every_router_was_looked_up()
    {
        var asns = new Dictionary<string, long> { ["184.105.64.1"] = 6939, ["184.105.80.1"] = 6939, ["104.160.131.1"] = 6507 };

        var named = HistoryRelabel.Name(Row("Unknown"), Looked(DemoLookups, asns), RefData.BuiltIn);

        Assert.NotNull(named);
        Assert.Equal("HE", named!.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, named.UpstreamSource);
    }

    [Fact]
    public void Router_names_give_the_handoff_and_city_too()
    {
        var hosts = new Dictionary<string, string>
        {
            ["184.105.80.1"] = "100ge0-1.core2.chi1.he.net",
            ["104.160.131.1"] = "ae1.er01.chi01.riotdirect.net",
        };

        var named = HistoryRelabel.Name(Row("Unknown"), Looked(DemoLookups, hosts: hosts), RefData.BuiltIn);

        Assert.NotNull(named);
        Assert.Equal("HE", named!.Upstream);
        Assert.Equal("Riot Direct", named.HandoffNetwork);
        Assert.Equal("chi01", named.HandoffCity);
    }

    [Fact]
    public void A_row_with_a_router_not_looked_up_stays_unnamed()
    {
        // HE was looked up, the Cogent hop after it was not: naming the row HE could be wrong.
        var path = "192.168.1.1 > 184.105.80.1 > 154.54.1.1 > 104.160.131.1";
        var asns = new Dictionary<string, long> { ["184.105.80.1"] = 6939, ["104.160.131.1"] = 6507 };

        Assert.Null(HistoryRelabel.Name(Row("Unknown", path), Looked(new[] { "184.105.80.1", "104.160.131.1" }, asns), RefData.BuiltIn));

        var all = Looked(new[] { "184.105.80.1", "154.54.1.1", "104.160.131.1" }, new Dictionary<string, long>(asns) { ["154.54.1.1"] = 174 });
        Assert.Equal("Cogent", HistoryRelabel.Name(Row("Unknown", path), all, RefData.BuiltIn)!.Upstream);
    }

    [Fact]
    public void Hop_1_never_counts_toward_the_name()
    {
        // Hop 1 has a public address in HE's space (a tunnel). A live test never names it.
        var row = Row("Unknown", "184.105.1.1 > 100.64.0.1 > 104.160.131.1", gateway: "100.64.0.1");
        var asns = new Dictionary<string, long> { ["184.105.1.1"] = 6939, ["104.160.131.1"] = 6507 };

        Assert.Null(HistoryRelabel.Name(row, Looked(new[] { "184.105.1.1", "104.160.131.1" }, asns), RefData.BuiltIn));
    }

    [Fact]
    public void A_row_with_no_known_network_stays_unnamed()
    {
        Assert.Null(HistoryRelabel.Name(Row("Unknown"), Looked(DemoLookups), RefData.BuiltIn));
        Assert.Null(HistoryRelabel.Name(Row("Unknown"), Looked(DemoLookups, new Dictionary<string, long> { ["184.105.64.1"] = 64500 }), RefData.BuiltIn));
        Assert.Null(HistoryRelabel.Name(Row("Unknown", "192.168.1.1 > 10.0.0.1 > *"), Looked(DemoLookups), RefData.BuiltIn));
    }

    [Fact]
    public void A_named_row_is_never_renamed()
    {
        var asns = new Dictionary<string, long> { ["184.105.64.1"] = 174 };
        Assert.Null(HistoryRelabel.Name(Row("HE"), Looked(DemoLookups, asns), RefData.BuiltIn));
    }

    [Fact]
    public void History_marks_rows_named_later_and_leaves_the_others_alone()
    {
        var rows = new[]
        {
            Row("Unknown", time: "2026-11-02 00:01:10"),
            new RouteRow { Timestamp = "2026-11-02 00:16:39", Upstream = "HE", HandoffNetwork = "Riot Direct", HandoffCity = "chi01", FullPath = DemoPath, LastHopAvg = "21" },
            Row("Unknown", "192.168.1.1 > *", "2026-11-02 00:32:08"),
        };
        var lookups = Looked(DemoLookups, new Dictionary<string, long> { ["184.105.64.1"] = 6939 });
        var calls = 0;

        var mapped = UiMapping.HistoryRows(rows, row =>
        {
            calls++;
            return HistoryRelabel.Name(row, lookups, RefData.BuiltIn);
        });

        Assert.Equal(2, calls); // only the two Unknown rows
        Assert.Equal("HE", mapped[0].Upstream);
        Assert.True(mapped[0].NamedLater);
        Assert.Equal("HE", mapped[1].Upstream);
        Assert.False(mapped[1].NamedLater);
        Assert.Equal("Riot Direct", mapped[1].HandoffNetwork);
        Assert.Equal("Unknown", mapped[2].Upstream);  // no public hop: stays as saved
        Assert.False(mapped[2].NamedLater);
    }

    [Fact]
    public void Json_carries_namedLater_only_when_true()
    {
        var rows = new[]
        {
            new HistoryRow("2026-11-02T00:01:10", "HE", "", "", 20, 0, null, 100, false, false, "Ethernet", "", true),
            new HistoryRow("2026-11-02T00:16:39", "HE", "", "", 21, 0, null, 100, false, false, "Ethernet"),
        };

        var json = UiStateJson.History(rows);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"namedLater\":true").Count);
    }
}
