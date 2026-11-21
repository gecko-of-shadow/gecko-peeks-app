using System.Diagnostics;
using GeckoPeeks.Cloud;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using Xunit;

namespace GeckoPeeks.Tests;

public class DohClientTests
{
    private const string HeHost = "100ge0-1.core2.chi1.he.net";

    private static string Ptr(string data) =>
        "{\"Status\":0,\"Answer\":[{\"name\":\"1.80.105.184.in-addr.arpa.\",\"type\":12,\"TTL\":300,\"data\":\"" + data + "\"}]}";

    [Fact]
    public async Task Servfail_then_an_answer_gives_the_name()
    {
        var calls = 0;
        var http = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? FakeHttp.Json("{\"Status\":2}")
                : FakeHttp.Json(Ptr(HeHost + "."))),
        };
        using var doh = new DohClient(http);

        var result = await doh.PtrAsync("184.105.80.1", CancellationToken.None);

        Assert.Equal(DohStatus.Name, result.Status);
        Assert.Equal(HeHost, result.Hostname);
        Assert.Equal(2, http.Count);
        Assert.All(http.Requests, r => Assert.Equal("dns.google", r.RequestUri!.Host));
        Assert.Contains("name=1.80.105.184.in-addr.arpa", Uri.UnescapeDataString(http.Requests[0].RequestUri!.Query));
        Assert.Contains("type=PTR", http.Requests[0].RequestUri!.Query);
        Assert.Equal("GeckoPeeks", http.Requests[0].Headers.UserAgent.First().Product!.Name);
    }

    [Fact]
    public async Task Two_google_failures_go_to_cloudflare_with_the_json_accept_header()
    {
        var http = new FakeHttp
        {
            Respond = (r, _) => Task.FromResult(r.RequestUri!.Host == "cloudflare-dns.com"
                ? FakeHttp.Json(Ptr(HeHost))
                : FakeHttp.Json("{\"Status\":2}")),
        };
        using var doh = new DohClient(http);

        var result = await doh.PtrAsync("184.105.80.1", CancellationToken.None);

        Assert.Equal(DohStatus.Name, result.Status);
        Assert.Equal(3, http.Count);
        var cf = http.Requests[2];
        Assert.Equal("cloudflare-dns.com", cf.RequestUri!.Host);
        Assert.Contains(cf.Headers.Accept, a => a.MediaType == "application/dns-json");
    }

    [Fact]
    public async Task Google_queries_turn_off_client_subnet_and_cloudflare_needs_no_change()
    {
        var http = new FakeHttp
        {
            Respond = (r, _) => Task.FromResult(r.RequestUri!.Host == "cloudflare-dns.com"
                ? FakeHttp.Json(Ptr(HeHost))
                : FakeHttp.Json("{\"Status\":2}")),
        };
        using var doh = new DohClient(http);
        await doh.PtrAsync("184.105.80.1", CancellationToken.None);
        await doh.CymruAsnAsync("184.105.80.1", CancellationToken.None);

        var google = http.Requests.Where(r => r.RequestUri!.Host == "dns.google").ToList();
        Assert.Equal(3, google.Count); // two PTR attempts and one Cymru TXT
        Assert.All(google, r => Assert.Contains("edns_client_subnet=0.0.0.0/0", Uri.UnescapeDataString(r.RequestUri!.Query)));
        var cf = Assert.Single(http.Requests, r => r.RequestUri!.Host == "cloudflare-dns.com");
        Assert.DoesNotContain("edns_client_subnet", cf.RequestUri!.Query);
    }

    [Fact]
    public async Task Nxdomain_is_no_name_after_one_request()
    {
        var http = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":3}")) };
        using var doh = new DohClient(http);
        var result = await doh.PtrAsync("184.105.80.1", CancellationToken.None);
        Assert.Equal(DohStatus.NoName, result.Status);
        Assert.Equal(1, http.Count);
    }

    [Fact]
    public async Task No_ptr_answer_is_no_name()
    {
        var http = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":0}")) };
        using var doh = new DohClient(http);
        Assert.Equal(DohStatus.NoName, (await doh.PtrAsync("184.105.80.1", CancellationToken.None)).Status);
        Assert.Equal(1, http.Count);
    }

    [Theory]
    [InlineData("bad_host!.example.")]
    [InlineData("-lead.example.net")]
    [InlineData("nodots")]
    [InlineData("<script>.example.net")]
    public async Task Bad_hostname_text_is_no_name(string data)
    {
        var http = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json(Ptr(data))) };
        using var doh = new DohClient(http);
        var result = await doh.PtrAsync("184.105.80.1", CancellationToken.None);
        Assert.Equal(DohStatus.NoName, result.Status);
        Assert.Equal("", result.Hostname);
    }

    [Fact]
    public async Task Too_long_hostname_is_no_name()
    {
        var label = new string('a', 60);
        var host = string.Join(".", Enumerable.Repeat(label, 5)) + ".net";
        var http = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json(Ptr(host))) };
        using var doh = new DohClient(http);
        Assert.Equal(DohStatus.NoName, (await doh.PtrAsync("184.105.80.1", CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("2001:4860::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public async Task Ipv6_or_bad_input_fails_without_a_request(string ip)
    {
        var http = new FakeHttp();
        using var doh = new DohClient(http);
        Assert.Equal(DohStatus.Failed, (await doh.PtrAsync(ip, CancellationToken.None)).Status);
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public async Task Every_attempt_failing_is_failed()
    {
        var http = new FakeHttp { Respond = (_, _) => throw new HttpRequestException("offline") };
        using var doh = new DohClient(http);
        Assert.Equal(DohStatus.Failed, (await doh.PtrAsync("184.105.80.1", CancellationToken.None)).Status);
        Assert.Equal(3, http.Count);
    }

    [Fact]
    public async Task Cymru_txt_gives_the_origin_asn()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json(
                "{\"Status\":0,\"Answer\":[{\"type\":16,\"data\":\"\\\"6939 | 184.104.0.0/15 | US | arin | 2006-07-07\\\"\"}]}")),
        };
        using var doh = new DohClient(http);

        Assert.Equal(6939, await doh.CymruAsnAsync("184.105.80.1", CancellationToken.None));
        var query = Uri.UnescapeDataString(http.Requests[0].RequestUri!.Query);
        Assert.Contains("name=1.80.105.184.origin.asn.cymru.com", query);
        Assert.Contains("type=TXT", query);
        Assert.Equal("dns.google", http.Requests[0].RequestUri!.Host);
    }

    [Theory]
    [InlineData("\"6939 | 184.104.0.0/15 | US | arin | 2006-07-07\"", 6939L)]
    [InlineData("64500 64501 | 192.0.2.0/24 | ZZ | test | 2020-01-01", 64500L)]
    [InlineData("4294967295 | x", 4294967295L)]
    [InlineData("0 | x", null)]
    [InlineData("4294967296 | x", null)]
    [InlineData("AS6939 | x", null)]
    [InlineData("", null)]
    public void Cymru_parse(string txt, long? expected) => Assert.Equal(expected, DohClient.ParseCymru(txt));
}

public class HopResolverTests
{
    private static Task<string> NoSystemName(string ip, CancellationToken token) => Task.FromResult("");

    [Fact]
    public async Task System_name_wins_and_doh_is_not_asked()
    {
        var http = new FakeHttp();
        using var doh = new DohClient(http);
        var resolver = new HopResolver(new AsnCache(null), () => false, null, doh, (_, _) => Task.FromResult("core1.chi1.he.net"));
        Assert.Equal("core1.chi1.he.net", await resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None));
        Assert.Equal(0, http.Count);
    }

    [Fact]
    public async Task Doh_is_used_only_when_allowed_and_its_name_is_cached()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json(
                "{\"Status\":0,\"Answer\":[{\"type\":12,\"data\":\"100ge0-1.core2.chi1.he.net.\"}]}")),
        };
        using var doh = new DohClient(http);
        var resolver = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);

        Assert.Equal("", await resolver.ReverseAsync("184.105.80.1", false, CancellationToken.None));
        Assert.Equal(0, http.Count);

        Assert.Equal("100ge0-1.core2.chi1.he.net", await resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None));
        Assert.Equal(1, http.Count);

        Assert.Equal("100ge0-1.core2.chi1.he.net", await resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None));
        Assert.Equal(1, http.Count);
    }

    [Fact]
    public async Task Reverse_status_tells_a_name_a_definite_no_name_and_no_answer_apart()
    {
        // A name from system DNS.
        using (var doh = new DohClient(new FakeHttp()))
        {
            var r = new HopResolver(new AsnCache(null), () => true, null, doh, (_, _) => Task.FromResult("core1.chi1.he.net"));
            Assert.Equal(new PtrAnswer(PtrStatus.Name, "core1.chi1.he.net"), await r.ReverseWithStatusAsync("184.105.80.1", true, CancellationToken.None));
        }

        // DoH says NXDOMAIN: a definite "no name", also from the cache the second time.
        using (var doh = new DohClient(new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":3}")) }))
        {
            var r = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);
            Assert.Equal(PtrStatus.NoName, (await r.ReverseWithStatusAsync("184.105.80.1", true, CancellationToken.None)).Status);
            Assert.Equal(PtrStatus.NoName, (await r.ReverseWithStatusAsync("184.105.80.1", true, CancellationToken.None)).Status);
        }

        // DoH answers SERVFAIL: no answer, so not "no name".
        using (var doh = new DohClient(new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":2}")) }))
        {
            var r = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);
            Assert.Equal(PtrStatus.Unknown, (await r.ReverseWithStatusAsync("184.105.80.1", true, CancellationToken.None)).Status);
        }

        // Cloudflare live data off: system DNS only, and its silence is no answer.
        using (var doh = new DohClient(new FakeHttp()))
        {
            var r = new HopResolver(new AsnCache(null), () => false, null, doh, NoSystemName);
            Assert.Equal(PtrStatus.Unknown, (await r.ReverseWithStatusAsync("184.105.80.1", true, CancellationToken.None)).Status);
        }
    }

    [Fact]
    public async Task No_name_is_cached_too()
    {
        var http = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":3}")) };
        using var doh = new DohClient(http);
        var resolver = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);
        Assert.Equal("", await resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None));
        Assert.Equal("", await resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None));
        Assert.Equal(1, http.Count);
    }

    [Fact]
    public async Task Asn_cache_hit_makes_no_http_call()
    {
        var worker = new FakeHttp();
        var dns = new FakeHttp();
        using var client = new CloudClient(worker, "https://worker.test");
        using var doh = new DohClient(dns);
        var cache = new AsnCache(null);
        cache.Put(new AsnResult("184.105.80.1", 6939, "HE", "US"));
        var resolver = new HopResolver(cache, () => true, client, doh, NoSystemName);

        var asns = await resolver.AsnAsync(new[] { "184.105.80.1" }, CancellationToken.None);

        Assert.Equal(6939, asns["184.105.80.1"]);
        Assert.Equal(0, worker.Count);
        Assert.Equal(0, dns.Count);
    }

    /// <summary>Cloudflare live data off: no Worker, no DoH, no Team Cymru. Only the local cache.</summary>
    [Fact]
    public async Task Cloud_off_makes_no_third_party_lookups()
    {
        var worker = new FakeHttp();
        var dns = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":0,\"Answer\":[{\"type\":16,\"data\":\"\\\"6507 | 104.160.128.0/19 | US | arin | 2014-01-01\\\"\"}]}")),
        };
        using var client = new CloudClient(worker, "https://worker.test");
        using var doh = new DohClient(dns);
        var cache = new AsnCache(null);
        cache.Put(new AsnResult("184.105.80.1", 6939, "HE", "US"));
        var resolver = new HopResolver(cache, () => false, client, doh, NoSystemName);

        var asns = await resolver.AsnAsync(new[] { "184.105.80.1", "104.160.131.1", "192.168.1.1" }, CancellationToken.None);
        var name = await resolver.ReverseAsync("104.160.131.1", true, CancellationToken.None);

        Assert.Equal(0, worker.Count);
        Assert.Equal(0, dns.Count);
        Assert.Equal("", name);
        Assert.Equal(6939, asns["184.105.80.1"]); // the cache still answers
        Assert.Single(asns);
    }

    [Fact]
    public async Task Cloud_on_cymru_runs_for_public_addresses_only()
    {
        var dns = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":0,\"Answer\":[{\"type\":16,\"data\":\"\\\"6507 | 104.160.128.0/19 | US | arin | 2014-01-01\\\"\"}]}")),
        };
        using var doh = new DohClient(dns);
        var cache = new AsnCache(null);
        var resolver = new HopResolver(cache, () => true, null, doh, NoSystemName);

        var asns = await resolver.AsnAsync(new[] { "104.160.131.1", "192.168.1.1", "100.64.0.1" }, CancellationToken.None);

        Assert.Equal(1, dns.Count); // private addresses never go out
        Assert.Equal(6507, asns["104.160.131.1"]);
        Assert.Single(asns);
        Assert.Equal(6507, cache.Get("104.160.131.1")?.Asn);
    }

    /// <summary>The private-address rule holds in the resolver and in the DoH client, whatever the caller says.</summary>
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.10.1")]
    [InlineData("127.0.0.1")]
    public async Task Private_cgnat_and_link_local_addresses_never_go_out(string ip)
    {
        var dns = new FakeHttp { Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":0,\"Answer\":[{\"type\":12,\"data\":\"router.example.net.\"}]}")) };
        using var doh = new DohClient(dns);
        var resolver = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);

        Assert.Equal("", await resolver.ReverseAsync(ip, true, CancellationToken.None));
        Assert.Equal(DohStatus.Failed, (await doh.PtrAsync(ip, CancellationToken.None)).Status);
        Assert.Null(await doh.CymruAsnAsync(ip, CancellationToken.None));
        Assert.Equal(0, dns.Count);
    }

    [Fact]
    public async Task Cloud_on_asks_the_worker_once_then_cymru_for_the_rest()
    {
        var worker = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json(
                "{\"results\":[{\"ip\":\"184.105.80.1\",\"asn\":6939,\"name\":\"HURRICANE\",\"country\":\"US\"}]}")),
        };
        var dns = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"Status\":0,\"Answer\":[{\"type\":16,\"data\":\"6507 | x\"}]}")),
        };
        using var client = new CloudClient(worker, "https://worker.test");
        using var doh = new DohClient(dns);
        var resolver = new HopResolver(new AsnCache(null), () => true, client, doh, NoSystemName);

        var asns = await resolver.AsnAsync(new[] { "184.105.80.1", "104.160.131.1" }, CancellationToken.None);

        Assert.Equal(1, worker.CountPath("/v1/asn"));
        Assert.Equal(1, dns.Count);
        Assert.Equal(6939, asns["184.105.80.1"]);
        Assert.Equal(6507, asns["104.160.131.1"]);
    }

    [Fact]
    public async Task Budget_of_4_seconds_holds_with_hanging_handlers()
    {
        var worker = new FakeHttp { Respond = FakeHttp.Hang };
        var dns = new FakeHttp { Respond = FakeHttp.Hang };
        using var client = new CloudClient(worker, "https://worker.test");
        using var doh = new DohClient(dns);
        var resolver = new HopResolver(new AsnCache(null), () => true, client, doh,
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "";
            });

        var clock = Stopwatch.StartNew();
        var name = resolver.ReverseAsync("184.105.80.1", true, CancellationToken.None);
        var asns = resolver.AsnAsync(new[] { "184.105.80.1", "104.160.131.1" }, CancellationToken.None);
        await Task.WhenAll(name, asns);
        clock.Stop();

        Assert.Equal("", name.Result);
        Assert.Empty(asns.Result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5.5), "took " + clock.ElapsedMilliseconds + " ms");
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3.5), "budget ended early: " + clock.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public async Task Outer_cancel_throws_operation_cancelled()
    {
        var dns = new FakeHttp { Respond = FakeHttp.Hang };
        using var doh = new DohClient(dns);
        var resolver = new HopResolver(new AsnCache(null), () => true, null, doh, NoSystemName);
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ReverseAsync("184.105.80.1", true, cancel.Token));
    }
}

public class AnalyzeTests
{
    /// <summary>Records every call. Names come from a table; ASNs from another.</summary>
    private sealed class FakeResolver : IHopResolver
    {
        public Dictionary<string, string> Names { get; } = new();
        public Dictionary<string, long> Asns { get; } = new();
        public List<(string Ip, bool AllowDoh)> Reverse { get; } = new();
        public List<string> AsnAsked { get; } = new();

        public Task<string> ReverseAsync(string ip, bool allowDoh, CancellationToken cancellationToken)
        {
            lock (Reverse)
                Reverse.Add((ip, allowDoh));
            return Task.FromResult(Names.TryGetValue(ip, out var n) ? n : "");
        }

        public Task<IReadOnlyDictionary<string, long>> AsnAsync(IReadOnlyList<string> publicIps, CancellationToken cancellationToken)
        {
            AsnAsked.AddRange(publicIps);
            // Answers for every address, even ones that should not have been asked.
            IReadOnlyDictionary<string, long> map = Asns;
            return Task.FromResult(map);
        }
    }

    private static ProbeRequest Request(IHopResolver resolver) =>
        new(false, "192.207.0.5", resolver, RefData.BuiltIn);

    [Fact]
    public async Task Doh_and_asn_never_touch_hop_1_the_gateway_or_private_hops()
    {
        var resolver = new FakeResolver();
        var hops = new[]
        {
            V21.Hop(1, "184.105.64.1"),   // public first hop
            V21.Hop(2, "184.105.80.1"),   // the gateway, public
            V21.Hop(3, "172.16.0.1"),
            V21.Hop(4, "100.64.0.1"),
            V21.Hop(5, "104.160.131.1"),
            V21.Silent(6),
            V21.Hop(7, "192.207.0.5", reached: true),
        };

        await TestRunner.AnalyzeAsync(hops, "184.105.80.1", Request(resolver), null, CancellationToken.None);

        var doh = resolver.Reverse.ToDictionary(r => r.Ip, r => r.AllowDoh);
        Assert.False(doh["184.105.64.1"]);
        Assert.False(doh["184.105.80.1"]);
        Assert.False(doh["172.16.0.1"]);
        Assert.False(doh["100.64.0.1"]);
        Assert.True(doh["104.160.131.1"]);
        Assert.True(doh["192.207.0.5"]);
        Assert.DoesNotContain("*", doh.Keys);

        Assert.Equal(new[] { "104.160.131.1", "192.207.0.5" }, resolver.AsnAsked);
    }

    /// <summary>The v2.0 failure: no PTR for any hop. The ASN of the public hops names Zayo.</summary>
    [Fact]
    public async Task Empty_hostnames_plus_asn_6461_give_zayo_by_asn()
    {
        var resolver = new FakeResolver();
        // Neutral addresses; the fake resolver decides their ASN.
        resolver.Asns["184.105.64.1"] = 6461;
        resolver.Asns["184.105.80.1"] = 6461;
        resolver.Asns["104.160.131.1"] = 6461;
        resolver.Asns["172.16.0.1"] = 6461; // must be ignored: never asked
        var hops = new[]
        {
            V21.Hop(1, "192.168.1.1", "", 1),
            V21.Hop(2, "172.16.0.1", "", 3),
            V21.Hop(3, "184.105.64.1", "", 9),
            V21.Hop(4, "184.105.80.1", "", 12),
            V21.Hop(5, "104.160.131.1", "", 15),
            V21.Silent(6),
        };

        var row = await TestRunner.AnalyzeAsync(hops, "192.168.1.1", Request(resolver), null, CancellationToken.None);

        Assert.Equal("Zayo", row.Upstream);
        Assert.Equal(RouteClassifier.SourceAsn, row.UpstreamSource);
        Assert.Equal(6461, row.UpstreamAsn);
        Assert.Equal("192.207.0.5", row.TargetIp);
        Assert.Equal("192.168.1.1 > 172.16.0.1 > 184.105.64.1 > 184.105.80.1 > 104.160.131.1 > *", row.FullPath);
        Assert.Equal(3, row.HopAsns.Count);
        Assert.False(row.HopAsns.ContainsKey("172.16.0.1"));
        Assert.DoesNotContain("192.168.1.1", resolver.AsnAsked);
        Assert.DoesNotContain("172.16.0.1", resolver.AsnAsked);
    }

    [Fact]
    public async Task Names_from_the_resolver_fill_the_hops()
    {
        var resolver = new FakeResolver();
        resolver.Names["184.105.80.1"] = "100ge0-1.core2.chi1.he.net";
        resolver.Names["104.160.131.1"] = "ae1.er01.chi01.riotdirect.net";
        var (demo, asns) = V21.DemoRoute();
        foreach (var (ip, asn) in asns)
            resolver.Asns[ip] = asn;
        var bare = demo.Select(h => h with { Hostname = "" }).ToArray();
        var phases = new List<string>();
        var progress = new SyncProgress(p => phases.Add(p.Phase));

        var row = await TestRunner.AnalyzeAsync(bare, "192.168.1.1", Request(resolver), progress, CancellationToken.None);

        Assert.Equal("HE", row.Upstream);
        Assert.Equal("Riot Direct", row.HandoffNetwork);
        Assert.Equal("chi01", row.HandoffCity);
        Assert.Equal("100ge0-1.core2.chi1.he.net", row.Hops[4].Hostname);
        Assert.Contains(TestRunner.NamesPhase, phases);
    }

    private sealed class SyncProgress : IProgress<ProbeProgress>
    {
        private readonly Action<ProbeProgress> _handler;
        public SyncProgress(Action<ProbeProgress> handler) => _handler = handler;
        public void Report(ProbeProgress value) => _handler(value);
    }
}
