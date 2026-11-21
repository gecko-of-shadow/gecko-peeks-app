using System.Diagnostics;
using System.Text.Json.Nodes;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class RefDataBuiltInTests
{
    [Fact]
    public void Embedded_file_loads_with_15_regions()
    {
        var data = RefData.BuiltIn;
        Assert.Equal(1, data.Schema);
        Assert.NotEqual("builtin-minimal", data.Version);
        Assert.Equal(15, data.Regions.Count);
        Assert.Equal(10, data.Providers.Count);
        Assert.NotEmpty(data.CityCodes);
        Assert.Equal("na1", data.DefaultRegion.Id);
    }

    [Fact]
    public void Regions_are_found_by_id_and_alias()
    {
        var data = RefData.BuiltIn;
        Assert.Equal("kr", data.FindRegion("kr1")?.Id);
        Assert.Equal("sg2", data.FindRegion("PH2")?.Id);
        Assert.Equal("euw1", data.FindRegion(" EUW1 ")?.Id);
        Assert.Null(data.FindRegion("zz9"));
        Assert.Null(data.FindRegion(null));
    }

    [Fact]
    public void Region_for_location_uses_country_then_continent_then_default()
    {
        var data = RefData.BuiltIn;
        Assert.Equal("euw1", data.RegionForLocation("XX", "EU").Id);
        Assert.Equal("euw1", data.RegionForLocation("T1", "EU").Id);
        Assert.Equal("na1", data.RegionForLocation(null, null).Id);
        Assert.Equal("na1", data.RegionForLocation("US", null).Id);
        Assert.Equal("kr", data.RegionForLocation("KR", "AS").Id);
        Assert.Equal("br1", data.RegionForLocation("br", null).Id);
        Assert.Equal("sg2", data.RegionForLocation("PH", null).Id);
        Assert.Equal("sg2", data.RegionForLocation("TH", null).Id);
        Assert.Equal("oc1", data.RegionForLocation("ZZ", "OC").Id);
    }

    [Fact]
    public void Frozen_region_values_hold()
    {
        var data = RefData.BuiltIn;
        var na = data.FindRegion("na1")!;
        Assert.Equal("192.207.0.5", na.TargetIp);
        Assert.Equal(RefData.EdgeLabel, na.TargetLabel);
        Assert.True(na.TargetInRegion);
        Assert.Equal("Chicago", na.ServerCity);

        var euw = data.FindRegion("euw1")!;
        Assert.Equal("dns", euw.TargetKind);
        Assert.Equal(RefData.PlatformLabel, euw.TargetLabel);

        var ru = data.FindRegion("ru")!;
        Assert.Equal("", ru.ServerCity);
        Assert.Null(ru.Lat);
        Assert.Null(ru.Lon);
        Assert.False(ru.TargetInRegion);
    }

    [Fact]
    public void Providers_are_found_by_asn_host_and_name()
    {
        var data = RefData.BuiltIn;
        Assert.Equal("Zayo", data.ProviderForAsn(6461)?.Name);
        Assert.Equal("Riot Direct", data.ProviderForAsn(6507)?.Name);
        Assert.Null(data.ProviderForAsn(64500));
        Assert.Equal("HE", data.ProviderForHost("100ge0-1.core2.chi1.he.net")?.Name);
        Assert.Equal("Telia", data.ProviderForHost("sea-b1-link.ip.twelve99.net")?.Name);
        Assert.Null(data.ProviderForHost("cdn.the.net"));
        Assert.Null(data.ProviderForHost(""));
        Assert.Equal("Cogent", data.ProviderByName("Cogent")?.Name);
        Assert.Null(data.ProviderByName("cogent"));
    }

    [Fact]
    public void Minimal_set_has_na1_and_ten_providers()
    {
        var data = RefData.Minimal();
        Assert.Single(data.Regions);
        Assert.Equal("na1", data.DefaultRegion.Id);
        Assert.Equal(10, data.Providers.Count);
        Assert.Empty(data.CityCodes);
        Assert.Equal("HE", data.ProviderForAsn(6939)?.Name);
    }
}

public class RefDataParseTests
{
    private static JsonObject Base() => JsonNode.Parse(V21.BuiltInJson())!.AsObject();

    private static void AssertRejected(JsonObject root)
    {
        Assert.False(RefData.TryParse(root.ToJsonString(), out var data, out var error));
        Assert.Null(data);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void The_embedded_file_parses()
    {
        Assert.True(RefData.TryParse(V21.BuiltInJson(), out var data, out var error), error);
        Assert.NotNull(data);
    }

    [Fact]
    public void Schema_must_be_1()
    {
        var root = Base();
        root["schema"] = 2;
        AssertRejected(root);
    }

    [Fact]
    public void Version_must_not_be_empty()
    {
        var root = Base();
        root["version"] = "";
        AssertRejected(root);
    }

    [Fact]
    public void Region_ids_must_be_unique()
    {
        var root = Base();
        root["regions"]![1]!["id"] = "na1";
        AssertRejected(root);
    }

    [Fact]
    public void Region_ids_must_match_the_pattern()
    {
        var root = Base();
        root["regions"]![1]!["id"] = "EUW1";
        AssertRejected(root);
    }

    [Fact]
    public void Default_region_must_be_a_region_id()
    {
        var root = Base();
        root["defaultRegion"] = "zz9";
        AssertRejected(root);
    }

    [Theory]
    [InlineData("aliases", "kr1")]
    [InlineData("countryToRegion", "US")]
    [InlineData("continentToRegion", "EU")]
    public void Map_values_must_be_region_ids(string map, string key)
    {
        var root = Base();
        root[map]![key] = "zz9";
        AssertRejected(root);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("203.0.113.5")]
    [InlineData("not-an-ip")]
    [InlineData("2001:4860::1")]
    public void Target_ip_must_be_public_ipv4(string ip)
    {
        var root = Base();
        root["regions"]![0]!["targetIp"] = ip;
        AssertRejected(root);
    }

    [Fact]
    public void Host_regex_must_compile()
    {
        var root = Base();
        root["providers"]![0]!["hostRegex"] = "^(.+$";
        AssertRejected(root);
    }

    [Fact]
    public void Provider_keys_must_be_unique()
    {
        var root = Base();
        root["providers"]![1]!["key"] = root["providers"]![0]!["key"]!.GetValue<string>();
        AssertRejected(root);
    }

    [Fact]
    public void An_asn_must_not_be_in_two_providers()
    {
        var root = Base();
        root["providers"]![1]!["asns"] = new JsonArray(6461);
        AssertRejected(root);
    }

    [Fact]
    public void File_must_be_under_512_KB()
    {
        var root = Base();
        root["padding"] = new string('x', RefData.MaxBytes);
        AssertRejected(root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public void Broken_text_is_rejected(string json) =>
        Assert.False(RefData.TryParse(json, out _, out _));
}

public class ClassifierAsnTests
{
    [Fact]
    public void Asn_names_the_hops_when_every_hostname_is_empty()
    {
        var hops = new[]
        {
            V21.Hop(1, "192.168.1.1"),
            V21.Hop(2, "198.51.100.10"),
            V21.Hop(3, "198.51.100.20"),
            V21.Hop(4, "192.207.0.5", reached: true),
        };
        var asns = new Dictionary<string, long> { ["198.51.100.10"] = 6939, ["198.51.100.20"] = 6507 };

        var f = RouteClassifier.Classify(hops, asns, RefData.BuiltIn);

        Assert.Equal("HE", f.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, f.UpstreamSource);
        Assert.Equal(6939, f.UpstreamAsn);
        Assert.Equal("Riot Direct", f.HandoffNetwork);
        Assert.Equal(RouteClassifier.SourceAsn, f.HandoffSource);
        Assert.Equal("198.51.100.20", f.HandoffIP);
        Assert.Equal("", f.HandoffCity);
        Assert.Equal("", f.HandoffHost);
    }

    /// <summary>
    /// The shape of a real failing path: home router, a private carrier hop, then several
    /// upstream hops whose PTR lookups failed. Neutral addresses; the ASN map decides.
    /// </summary>
    [Fact]
    public void Upstream_hops_without_ptr_are_named_zayo_by_asn()
    {
        var hops = new[]
        {
            V21.Hop(1, "192.168.1.1", "", 1),
            V21.Hop(2, "172.16.0.1", "", 3),
            V21.Hop(3, "198.51.100.1", "", 9),
            V21.Hop(4, "198.51.100.2", "", 12),
            V21.Hop(5, "198.51.100.3", "", 15),
            V21.Hop(6, "198.51.100.4", "", 18),
            V21.Silent(7),
            V21.Hop(8, "198.51.100.5", "", 20),
            V21.Silent(9),
        };
        var asns = new Dictionary<string, long>
        {
            ["198.51.100.1"] = 6461,
            ["198.51.100.2"] = 6461,
            ["198.51.100.3"] = 6461,
            ["198.51.100.4"] = 6461,
            ["198.51.100.5"] = 6461,
        };

        var f = RouteClassifier.Classify(hops, asns, RefData.BuiltIn);

        Assert.Equal("Zayo", f.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, f.UpstreamSource);
        Assert.Equal(6461, f.UpstreamAsn);
        Assert.Equal("", f.HandoffNetwork);

        // Without ASN data the v2.0 result stays Unknown.
        Assert.Equal(RouteClassifier.Unknown, RouteClassifier.Classify(hops).Upstream);
    }

    [Fact]
    public void Hostname_beats_asn()
    {
        var hops = new[]
        {
            V21.Hop(1, "198.51.100.1", "be3012.ccr31.sea02.atlas.cogentco.com"),
            V21.Hop(2, "192.207.0.5", reached: true),
        };
        var f = RouteClassifier.Classify(hops, new Dictionary<string, long> { ["198.51.100.1"] = 6939 }, RefData.BuiltIn);
        Assert.Equal("Cogent", f.Upstream);
        Assert.Equal(RouteClassifier.SourceHostname, f.UpstreamSource);
        // The hop's measured ASN (6939) is not one of Cogent's, so Cogent's own ASN is used.
        Assert.Equal(174, f.UpstreamAsn);
    }

    /// <summary>An interconnect hop named by its hostname can carry the ISP's own origin ASN.</summary>
    [Fact]
    public void Upstream_asn_is_a_provider_asn_not_the_isp_asn_of_the_hop()
    {
        var hops = new[]
        {
            V21.Hop(1, "192.168.1.1"),
            V21.Hop(2, "198.51.100.1", ""),
            V21.Hop(3, "198.51.100.2", "ae1.den3.zayo.com"),
            V21.Hop(4, "198.51.100.3", "ae2.den4.zayo.com"),
            V21.Hop(5, "192.207.0.5", reached: true),
        };
        // The last Zayo hop (by name) has the ISP's documentation ASN; an earlier one has Zayo's.
        var asns = new Dictionary<string, long>
        {
            ["198.51.100.1"] = 64500,
            ["198.51.100.2"] = 6461,
            ["198.51.100.3"] = 64500,
        };

        var f = RouteClassifier.Classify(hops, asns, RefData.BuiltIn);
        Assert.Equal("Zayo", f.Upstream);
        Assert.Equal(6461, f.UpstreamAsn);

        // No Zayo ASN on any hop: the provider's first ASN.
        var none = RouteClassifier.Classify(hops, new Dictionary<string, long> { ["198.51.100.3"] = 64500 }, RefData.BuiltIn);
        Assert.Equal(6461, none.UpstreamAsn);
    }

    [Fact]
    public void Telia_only_route_has_telia_upstream()
    {
        var hops = new[]
        {
            V21.Hop(1, "198.51.100.1", "sea-b1-link.ip.twelve99.net"),
            V21.Hop(2, "198.51.100.2", "chi-b2-link.ip.twelve99.net"),
        };
        var f = RouteClassifier.Classify(hops, null, RefData.BuiltIn);
        Assert.Equal("Telia", f.Upstream);
        Assert.Equal(1299, f.UpstreamAsn);
    }

    [Fact]
    public void Zayo_then_telia_keeps_the_v20_result()
    {
        var hops = new[]
        {
            V21.Hop(1, "198.51.100.1", "ae1.den3.zayo.com", 21),
            V21.Hop(2, "198.51.100.2", "ic-1.sea1.twelve99.net", 40),
            V21.Hop(3, "192.207.0.5", "", 55, reached: true),
        };
        var f = RouteClassifier.Classify(hops, null, RefData.BuiltIn);
        Assert.Equal("Zayo", f.Upstream);
        Assert.Equal("Telia", f.HandoffNetwork);
        Assert.Equal(RouteClassifier.SourceHostname, f.HandoffSource);
        Assert.Equal("sea1", f.HandoffCity);
    }

    [Fact]
    public void One_hop_with_asn_174_is_cogent()
    {
        var hops = new[] { V21.Hop(1, "198.51.100.9") };
        var f = RouteClassifier.Classify(hops, new Dictionary<string, long> { ["198.51.100.9"] = 174 }, RefData.BuiltIn);
        Assert.Equal("Cogent", f.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, f.UpstreamSource);
    }

    [Fact]
    public void A_mixed_name_alone_is_never_unknown()
    {
        var hops = new[] { V21.Hop(1, "198.51.100.1", "peer.sea1.zayo-telia.zayo.com", 22) };
        var f = RouteClassifier.Classify(hops, null, RefData.BuiltIn);
        Assert.Equal("Zayo", f.Upstream);
        Assert.Equal("Telia", f.HandoffNetwork);
        Assert.Equal("sea1", f.HandoffCity);
    }

    [Fact]
    public void Asn_names_an_upstream_under_another_hostname()
    {
        // Pass 3: no pure hop, but the ASN says Zayo; the hostname names Riot Direct.
        var hops = new[] { V21.Hop(1, "198.51.100.1", "ae1.er01.chi01.riotdirect.net", 30) };
        var f = RouteClassifier.Classify(hops, new Dictionary<string, long> { ["198.51.100.1"] = 6461 }, RefData.BuiltIn);
        Assert.Equal("Zayo", f.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, f.UpstreamSource);
        Assert.Equal("Riot Direct", f.HandoffNetwork);
        Assert.Equal("chi01", f.HandoffCity);
    }

    [Fact]
    public void Riot_direct_is_never_the_upstream()
    {
        var hops = new[]
        {
            V21.Hop(1, "198.51.100.1", "ae1.er01.chi01.riotdirect.net"),
            V21.Hop(2, "192.207.0.5", reached: true),
        };
        var f = RouteClassifier.Classify(hops, new Dictionary<string, long> { ["192.207.0.5"] = 6507 }, RefData.BuiltIn);
        Assert.Equal(RouteClassifier.Unknown, f.Upstream);
    }

    [Fact]
    public void Demo_route_gives_he_then_riot_direct_chi01()
    {
        var (hops, asns) = V21.DemoRoute();
        var f = RouteClassifier.Classify(hops, asns, RefData.BuiltIn);
        Assert.Equal("HE", f.Upstream);
        Assert.Equal(RouteClassifier.SourceHostname, f.UpstreamSource);
        Assert.Equal("Riot Direct", f.HandoffNetwork);
        Assert.Equal(RouteClassifier.SourceHostname, f.HandoffSource);
        Assert.Equal("104.160.131.1", f.HandoffIP);
        Assert.Equal("chi01", f.HandoffCity);
        Assert.Equal("39", f.HandoffRttMs);
    }

    [Fact]
    public void Name_hops_gives_network_source_and_city()
    {
        var (hops, asns) = V21.DemoRoute();
        var names = RouteClassifier.NameHops(hops, asns, RefData.BuiltIn);
        Assert.Equal(7, names.Count);

        Assert.Equal(new HopName(1, "", "", null, "", ""), names[0]);
        Assert.Equal(new HopName(3, "", "", null, "", ""), names[2]);
        Assert.Equal(new HopName(4, "HE", RouteClassifier.SourceAsn, 6939, "", ""), names[3]);
        Assert.Equal("HE", names[4].Network);
        Assert.Equal(RouteClassifier.SourceHostname, names[4].Source);
        Assert.Equal("chi1", names[4].CityCode);
        Assert.Equal("Chicago", names[4].CityName);
        Assert.Equal("Riot Direct", names[5].Network);
        Assert.Equal("chi01", names[5].CityCode);
        Assert.Equal("Chicago", names[5].CityName);
        Assert.Equal(new HopName(7, "Riot Direct", RouteClassifier.SourceAsn, 6507, "", ""), names[6]);
    }

    [Fact]
    public void Name_hops_joins_a_mixed_hostname()
    {
        var names = RouteClassifier.NameHops(new[] { V21.Hop(1, "198.51.100.1", "peer.sea1.zayo-telia.zayo.com") }, null, RefData.BuiltIn);
        Assert.Equal("Zayo + Telia", names[0].Network);
        Assert.Equal(RouteClassifier.DescribeHop("peer.sea1.zayo-telia.zayo.com"), names[0].Network);
    }
}

public class CityCode21Tests
{
    /// <summary>Every hostname of the v2.0 city tests (CityCodeTests, CityCodeLabelTests, ClassifierEdgeTests).</summary>
    public static IEnumerable<object?[]> V20Hostnames() => new[]
    {
        "ae-1.sea1.zayo.com", "den3.example.net", "core-chi.telia.net", "sea1.den3.example.net",
        "net.com.org", "rtr-edge.example.net", "www.example.com", "to-of.example.net", null,
        "ae5.cr2.chi1.us.zayo.com", "be3012.ccr31.sea02.atlas.cogentco.com", "100ge0-45.core1.sea1.he.net",
        "ae5.ccr31.core1.example.net", "ae27.cs3.msp1.us.eth.zayo.com", "ae5.cs1.den1.us.eth.zayo.com",
        "kanc-b1-link.ip.twelve99.net", "palo-b2-link.ip.twelve99.net", "link.ip.example.net",
        "ae5.ccr31.core1.edge.example.net", "lo0.rtr1.example.net", "peer1.bb2.example.net",
        "xe-0-0-0.example.net", "foobar.example.net", "ic-1.sea1.twelve99.net",
        "peer.sea1.zayo-telia.zayo.com",
    }.Select(h => new object?[] { h });

    [Theory]
    [MemberData(nameof(V20Hostnames))]
    public void Two_argument_form_keeps_the_v20_result(string? host) =>
        Assert.Equal(CityCode.Extract(host), CityCode.Extract(host, RefData.BuiltIn));

    [Theory]
    [InlineData("ae-3.r24.chcgil09.us.bb.gin.ntt.net", "chcgil09")]
    [InlineData("ae1.er01.chi01.riotdirect.net", "chi01")]
    [InlineData("100ge0-1.core2.chi1.he.net", "chi1")]
    [InlineData("AE1.ER01.CHI01.RIOTDIRECT.NET.", "chi01")]
    public void Provider_pattern_picks_the_city(string host, string expected) =>
        Assert.Equal(expected, CityCode.Extract(host, RefData.BuiltIn));

    [Theory]
    [InlineData("ae1.er01.sjc01.riotdirect.net", "sjc01")]
    [InlineData("et-0.cr02.ord1.us.eth.zayo.com", "ord1")]
    public void Provider_pattern_skips_router_tokens(string host, string expected) =>
        Assert.Equal(expected, CityCode.Extract(host, RefData.BuiltIn));

    /// <summary>One non-US router name per provider pattern. Codes come from refdata.json rows.</summary>
    [Theory]
    [InlineData("be2.ccr41.ams03.atlas.cogentco.com", "ams03", "cogent", "Amsterdam")]
    [InlineData("100ge1-1.core1.ber1.he.net", "ber1", "he", "Berlin")]
    [InlineData("ae2.cr1.cdg11.fr.zip.zayo.com", "cdg11", "zayo", "Paris")]
    [InlineData("ae1.cr2-bru1.ip4.gtt.net", "bru1", "gtt", "Brussels")]
    [InlineData("ae-1-3.edge2.Berlin1.Level3.net", "berlin1", "lumen", "Berlin")]
    [InlineData("ae-5.r20.amstnl02.nl.bb.gin.ntt.net", "amstnl02", "ntt", "Amsterdam")]
    [InlineData("adm-b1-link.ip.twelve99.net", "adm", "telia", "Amsterdam")]
    [InlineData("ae1.er01.bom01.riotdirect.net", "bom01", "riotdirect", "Mumbai")]
    public void Worldwide_router_names_give_city_and_place(string host, string code, string group, string city)
    {
        Assert.Equal(code, CityCode.Extract(host, RefData.BuiltIn));
        Assert.Equal(group, RefData.BuiltIn.ProviderForHost(host)!.CityGroup);
        Assert.Equal(city, CityNames.Display(code, group));
        Assert.NotNull(CityNames.Coordinates(code, group));

        var names = RouteClassifier.NameHops(new[] { V21.Hop(1, "198.51.100.1", host) }, null, RefData.BuiltIn);
        Assert.Equal(code, names[0].CityCode);
        Assert.Equal(city, names[0].CityName);
    }

    [Fact]
    public void Group_lookups_give_city_names()
    {
        if (RefData.BuiltIn.FindCity("chcgil", "ntt") != null)
            Assert.Equal("Chicago", CityNames.Display("chcgil", "ntt"));
        Assert.Equal("Dallas", CityNames.Display("dls", "telia"));
        Assert.Equal("Chicago", CityNames.Display("chi01", "riotdirect"));
        Assert.NotNull(CityNames.Coordinates("chi01", "riotdirect"));
        Assert.Null(CityNames.Coordinates("qqq", null));
    }

    [Fact]
    public void Telia_codes_never_match_without_the_telia_group()
    {
        Assert.Null(RefData.BuiltIn.FindCity("dls", null));
        Assert.Equal("dls", CityNames.Display("dls"));
        Assert.Equal("dls1", CityNames.Display("dls1", null));
    }

    [Fact]
    public void V20_display_values_stay()
    {
        Assert.Equal("Kansas City", CityNames.Display("kanc"));
        Assert.Equal("Palo Alto", CityNames.Display("palo"));
        Assert.Equal("Seattle", CityNames.Display("sea1"));
        Assert.Equal("Denver", CityNames.Display("den3"));
        Assert.Equal("", CityNames.Display(""));
        Assert.Equal("zz9", CityNames.Display("zz9"));
    }

    [Fact]
    public void A_catastrophic_pattern_times_out_and_falls_back()
    {
        // A provider pattern with nested quantifiers: without the 50 ms timeout this takes minutes.
        var root = JsonNode.Parse(V21.BuiltInJson())!.AsObject();
        foreach (var p in root["providers"]!.AsArray())
        {
            if (p!["key"]!.GetValue<string>() == "zayo")
                p["hostRegex"] = "^([a-z]+)+$";
        }

        Assert.True(RefData.TryParse(root.ToJsonString(), out var evil, out var error), error);
        var host = new string('a', 40) + ".zayo.com";
        CityCode.Extract("ae1.zayo.com", evil!); // warm up

        var clock = Stopwatch.StartNew();
        var city = CityCode.Extract(host, evil!);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 200, "took " + clock.ElapsedMilliseconds + " ms");
        Assert.Equal(CityCode.Extract(host), city);
    }

    [Fact]
    public void A_very_long_hostname_returns_quickly()
    {
        var host = string.Join(".", Enumerable.Repeat("ab1", 30000)) + ".cogentco.com";
        CityCode.Extract("be1.ccr1.sea02.atlas.cogentco.com", RefData.BuiltIn);
        var clock = Stopwatch.StartNew();
        CityCode.Extract(host, RefData.BuiltIn);
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 200, "took " + clock.ElapsedMilliseconds + " ms");
    }
}
