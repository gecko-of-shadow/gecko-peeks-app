using System.Net;
using System.Text;
using GeckoPeeks.Cloud;
using Xunit;

namespace GeckoPeeks.Tests;

public class CloudTests
{
    private const string Base = "https://worker.test";

    /// <summary>Answers by path; records every request. No real network.</summary>
    private sealed class FakeWorker : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }

        public int Count(string path)
        {
            lock (Requests)
                return Requests.Count(r => r.AbsolutePath == path);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (FakeWorker Worker, CloudClient Client, CloudPoller Poller, Clock Clock) Make(bool enabled = true)
    {
        var worker = new FakeWorker
        {
            Respond = r => r.RequestUri!.AbsolutePath switch
            {
                "/v1/edge" => Json("{\"colo\":\"SEA\",\"asn\":64500}"),
                "/v1/pulse" => Json("{\"radar\":true,\"riot\":{\"asn\":6507}}"),
                _ => Json("{\"error\":\"not_found\"}", HttpStatusCode.NotFound),
            },
        };
        var client = new CloudClient(worker, Base);
        var clock = new Clock();
        return (worker, client, new CloudPoller(client, enabled, () => clock.Now), clock);
    }

    // ---- public address filter ----

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.2.5")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("router.home")]
    public void Private_and_reserved_addresses_are_not_public(string? ip) => Assert.False(PublicIp.IsPublic(ip));

    [Theory]
    [InlineData("192.207.0.5")]
    [InlineData("4.68.111.181")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("62.115.49.194")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Routed_unicast_addresses_are_public(string ip) => Assert.True(PublicIp.IsPublic(ip));

    [Fact]
    public void Hop_lookup_skips_hop_one_the_gateway_private_silent_and_repeated_hops()
    {
        var hops = new (int, string, bool)[]
        {
            (1, "8.8.4.4", true),          // first hop, even public
            (2, "203.0.113.1", true),      // documentation
            (3, "*", false),
            (4, "100.70.0.1", true),       // CGNAT
            (5, "64.125.29.17", true),
            (6, "64.125.29.17", true),     // repeat
            (7, "9.9.9.9", true),          // the gateway, even public
            (8, "62.115.49.194", false),   // did not answer
            (9, "192.207.0.5", true),
        };

        var ips = PublicIp.HopsToLookUp(hops, "9.9.9.9");

        Assert.Equal(new[] { "64.125.29.17", "192.207.0.5" }, ips);
    }

    // ---- client ----

    [Theory]
    [InlineData(null, CloudClient.DefaultBaseUrl)]
    [InlineData("", CloudClient.DefaultBaseUrl)]
    [InlineData("https://example.test", "https://example.test")]
    [InlineData("https://example.test/", "https://example.test")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787")]
    [InlineData("http://example.test", CloudClient.DefaultBaseUrl)]
    [InlineData("https://user:pw@example.test", CloudClient.DefaultBaseUrl)]
    [InlineData("https://example.test/v1?x=1", CloudClient.DefaultBaseUrl)]
    [InlineData("ftp://example.test", CloudClient.DefaultBaseUrl)]
    [InlineData("not a url", CloudClient.DefaultBaseUrl)]
    public void Base_url_override_must_be_an_https_origin(string? value, string expected) =>
        Assert.Equal(expected, CloudClient.ResolveBaseUrl(value));

    [Fact]
    public async Task Pulse_sends_only_valid_parameters()
    {
        var (worker, client, _, _) = Make();
        await client.GetPulseJsonAsync(852, "CA", CancellationToken.None);
        await client.GetPulseJsonAsync(0, "ca", CancellationToken.None);
        await client.GetPulseJsonAsync(null, null, CancellationToken.None);

        Assert.Equal("?isp=852&country=CA", worker.Requests[0].Query);
        Assert.Equal("", worker.Requests[1].Query);
        Assert.Equal("", worker.Requests[2].Query);
    }

    [Fact]
    public async Task Asn_lookup_sends_at_most_15_public_ips_and_matches_rows_by_ip()
    {
        var (worker, client, _, _) = Make();
        worker.Respond = _ => Json("{\"results\":[{\"ip\":\"192.207.0.5\",\"asn\":6507,\"name\":\"RIOT-NA1\",\"country\":\"US\"},"
            + "{\"ip\":\"4.68.111.181\",\"asn\":null,\"name\":null,\"country\":null}]}");
        var ips = new List<string> { "10.0.0.1", "4.68.111.181", "192.207.0.5" };
        for (var i = 1; i <= 20; i++)
            ips.Add("62.115.49." + i);

        var rows = await client.LookupAsnAsync(ips, CancellationToken.None);

        var sent = Uri.UnescapeDataString(worker.Requests.Single().Query).TrimStart('?');
        Assert.StartsWith("ips=", sent);
        var list = sent["ips=".Length..].Split(',');
        Assert.Equal(CloudClient.MaxIpsPerCall, list.Length);
        Assert.DoesNotContain("10.0.0.1", list);
        Assert.Equal(6507, rows.Single(r => r.Ip == "192.207.0.5").Asn);
        Assert.Null(rows.Single(r => r.Ip == "4.68.111.181").Asn);
    }

    [Fact]
    public async Task Asn_lookup_with_no_public_ips_makes_no_call()
    {
        var (worker, client, _, _) = Make();
        var rows = await client.LookupAsnAsync(new[] { "192.168.1.1", "*" }, CancellationToken.None);
        Assert.Empty(rows);
        Assert.Empty(worker.Requests);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Non_object_bodies_are_errors(string body)
    {
        var (worker, client, _, _) = Make();
        worker.Respond = _ => Json(body);
        await Assert.ThrowsAsync<CloudException>(() => client.GetEdgeJsonAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Error_status_and_oversize_bodies_are_errors()
    {
        var (worker, client, _, _) = Make();
        worker.Respond = _ => Json("{\"error\":\"bad\"}", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<CloudException>(() => client.GetEdgeJsonAsync(CancellationToken.None));
        Assert.Contains("400", ex.Message);

        worker.Respond = _ => Json("{\"x\":\"" + new string('a', CloudClient.MaxBodyBytes) + "\"}");
        await Assert.ThrowsAsync<CloudException>(() => client.GetEdgeJsonAsync(CancellationToken.None));
    }

    // ---- poller ----

    [Fact]
    public async Task First_tick_fetches_edge_and_pulse_then_waits_for_their_intervals()
    {
        var (worker, _, poller, clock) = Make();

        await poller.TickAsync();
        Assert.Equal(1, worker.Count("/v1/edge"));
        Assert.Equal(1, worker.Count("/v1/pulse"));
        var snap = poller.Snapshot();
        Assert.Equal("ok", snap.Status);
        Assert.Equal("SEA", System.Text.Json.JsonDocument.Parse(snap.EdgeJson!).RootElement.GetProperty("colo").GetString());
        Assert.Equal(0, snap.EdgeAgeSec);

        clock.Now += TimeSpan.FromMinutes(4);
        await poller.TickAsync();
        Assert.Equal(1, worker.Count("/v1/pulse"));

        clock.Now += TimeSpan.FromMinutes(1);
        await poller.TickAsync();
        Assert.Equal(2, worker.Count("/v1/pulse"));
        Assert.Equal(1, worker.Count("/v1/edge"));
        Assert.Equal(300, poller.Snapshot().EdgeAgeSec);

        clock.Now += TimeSpan.FromMinutes(10);
        await poller.TickAsync();
        Assert.Equal(2, worker.Count("/v1/edge"));
    }

    [Fact]
    public async Task Disabled_poller_makes_no_calls_and_reports_off()
    {
        var (worker, _, poller, _) = Make(enabled: false);
        await poller.TickAsync();
        await poller.RefreshAsync();
        poller.SetHops(new[] { "192.207.0.5" });
        await poller.LookupAsnAsync();

        Assert.Empty(worker.Requests);
        var snap = poller.Snapshot();
        Assert.Equal("off", snap.Status);
        Assert.False(snap.Enabled);
        Assert.Null(snap.PulseJson);
    }

    [Fact]
    public async Task Failure_keeps_old_data_sets_error_and_waits_before_retrying()
    {
        var (worker, _, poller, clock) = Make();
        await poller.TickAsync();

        worker.Respond = _ => throw new HttpRequestException("offline");
        clock.Now += TimeSpan.FromMinutes(5);
        await poller.TickAsync();
        var snap = poller.Snapshot();
        Assert.Equal("error", snap.Status);
        Assert.Equal("Could not reach the Gecko Peeks service.", snap.Error);
        Assert.NotNull(snap.PulseJson);
        Assert.Equal(300, snap.PulseAgeSec);
        var calls = worker.Requests.Count;

        clock.Now += TimeSpan.FromSeconds(30);
        await poller.TickAsync();
        Assert.Equal(calls, worker.Requests.Count);

        worker.Respond = r => Json(r.RequestUri!.AbsolutePath == "/v1/edge" ? "{\"colo\":\"PDX\"}" : "{\"radar\":true}");
        clock.Now += CloudPoller.RetryAfterError;
        await poller.TickAsync();
        Assert.Equal("ok", poller.Snapshot().Status);
        Assert.Equal("", poller.Snapshot().Error);
    }

    [Fact]
    public async Task Manual_refresh_never_fetches_pulse_faster_than_every_5_minutes()
    {
        var (worker, _, poller, clock) = Make();
        await poller.TickAsync();

        clock.Now += TimeSpan.FromSeconds(90);
        await poller.RefreshAsync();
        Assert.Equal(1, worker.Count("/v1/pulse"));
        Assert.Equal(2, worker.Count("/v1/edge"));

        clock.Now += TimeSpan.FromSeconds(10);
        await poller.RefreshAsync();
        Assert.Equal(2, worker.Count("/v1/edge"));

        clock.Now += TimeSpan.FromMinutes(4);
        await poller.RefreshAsync();
        Assert.Equal(2, worker.Count("/v1/pulse"));
    }

    [Fact]
    public async Task Turning_cloud_off_clears_the_snapshot_and_on_fetches_again()
    {
        var (worker, _, poller, _) = Make();
        await poller.TickAsync();
        poller.SetEnabled(false);
        Assert.Equal("off", poller.Snapshot().Status);
        Assert.Null(poller.Snapshot().EdgeJson);

        poller.SetEnabled(true);
        await poller.TickAsync();
        Assert.True(poller.Snapshot().Enabled);
        Assert.NotNull(poller.Snapshot().EdgeJson);
        Assert.True(worker.Count("/v1/edge") >= 1);
    }

    [Fact]
    public async Task Asn_names_are_cached_and_only_new_hops_are_looked_up()
    {
        var (worker, _, poller, _) = Make();
        worker.Respond = r =>
        {
            var ips = Uri.UnescapeDataString(r.RequestUri!.Query)["?ips=".Length..].Split(',');
            var rows = ips.Where(ip => ip != "64.125.29.17")
                .Select(ip => "{\"ip\":\"" + ip + "\",\"asn\":6507,\"name\":\"RIOT-NA1\",\"country\":\"US\"}");
            return Json("{\"results\":[" + string.Join(",", rows) + "]}");
        };

        poller.SetHops(new[] { "192.207.0.5", "64.125.29.17", "10.0.0.1" });
        await poller.LookupAsnAsync();
        Assert.Equal(6507, poller.Asn("192.207.0.5")?.Asn);
        Assert.Null(poller.Asn("64.125.29.17"));
        Assert.Null(poller.Asn("10.0.0.1"));
        var calls = worker.Count("/v1/asn");
        Assert.Equal(1, calls);

        poller.SetHops(new[] { "192.207.0.5", "64.125.29.17" });
        await poller.LookupAsnAsync();
        Assert.Equal(1, worker.Count("/v1/asn"));

        poller.SetHops(new[] { "192.207.0.5", "62.115.49.194" });
        await poller.LookupAsnAsync();
        Assert.Equal(2, worker.Count("/v1/asn"));
        var last = Uri.UnescapeDataString(worker.Requests.Last().Query);
        Assert.Equal("?ips=62.115.49.194", last);
    }

    [Fact]
    public async Task A_null_asn_row_is_asked_again_after_the_miss_retry()
    {
        var (worker, _, poller, clock) = Make();
        worker.Respond = _ => Json("{\"results\":[{\"ip\":\"64.125.29.17\",\"asn\":null,\"name\":null,\"country\":null}]}");
        poller.SetHops(new[] { "64.125.29.17" });
        await poller.LookupAsnAsync();
        Assert.Null(poller.Asn("64.125.29.17"));
        Assert.Equal(1, worker.Count("/v1/asn"));

        clock.Now += TimeSpan.FromMinutes(5);
        await poller.LookupAsnAsync();
        Assert.Equal(1, worker.Count("/v1/asn"));

        worker.Respond = _ => Json("{\"results\":[{\"ip\":\"64.125.29.17\",\"asn\":6461,\"name\":\"ZAYO\",\"country\":\"US\"}]}");
        clock.Now += CloudPoller.AsnMissRetry;
        await poller.LookupAsnAsync();
        Assert.Equal(2, worker.Count("/v1/asn"));
        Assert.Equal(6461, poller.Asn("64.125.29.17")?.Asn);
    }

    [Fact]
    public async Task Many_new_hops_go_out_in_batches_of_15()
    {
        var (worker, _, poller, _) = Make();
        worker.Respond = _ => Json("{\"results\":[]}");
        var ips = Enumerable.Range(1, 20).Select(i => "62.115.49." + i).ToArray();
        poller.SetHops(ips);
        await poller.LookupAsnAsync();
        Assert.Equal(2, worker.Count("/v1/asn"));
    }

    [Fact]
    public async Task Asn_failure_waits_before_asking_again()
    {
        var (worker, _, poller, clock) = Make();
        worker.Respond = _ => throw new HttpRequestException("offline");
        poller.SetHops(new[] { "192.207.0.5" });
        await poller.LookupAsnAsync();
        var calls = worker.Count("/v1/asn");
        await poller.LookupAsnAsync();
        Assert.Equal(calls, worker.Count("/v1/asn"));

        worker.Respond = _ => Json("{\"results\":[{\"ip\":\"192.207.0.5\",\"asn\":6507,\"name\":\"RIOT-NA1\",\"country\":\"US\"}]}");
        clock.Now += CloudPoller.RetryAfterError;
        await poller.LookupAsnAsync();
        Assert.Equal(6507, poller.Asn("192.207.0.5")?.Asn);
    }

    [Fact]
    public async Task Changed_is_raised_and_a_throwing_listener_does_not_stop_polling()
    {
        var (_, _, poller, _) = Make();
        var count = 0;
        poller.Changed += () =>
        {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("listener bug");
        };

        await poller.TickAsync();

        Assert.True(count >= 1);
        Assert.Equal("ok", poller.Snapshot().Status);
    }

    [Fact]
    public async Task Dispose_stops_further_calls()
    {
        var (worker, _, poller, _) = Make();
        poller.Dispose();
        await poller.TickAsync();
        Assert.Empty(worker.Requests);
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException), "The Gecko Peeks service did not answer in time.")]
    [InlineData(typeof(HttpRequestException), "Could not reach the Gecko Peeks service.")]
    [InlineData(typeof(InvalidOperationException), "The Gecko Peeks service did not answer.")]
    public void Errors_are_short_plain_sentences(Type type, string expected) =>
        Assert.Equal(expected, CloudPoller.Describe((Exception)Activator.CreateInstance(type)!));
}
