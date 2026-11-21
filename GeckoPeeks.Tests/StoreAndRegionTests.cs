using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class AsnCacheTests
{
    [Fact]
    public void Round_trips_through_a_file()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, AsnCache.FileName);
            var cache = new AsnCache(path);
            cache.Put(new AsnResult("184.105.80.1", 6939, "HURRICANE", "US"));
            cache.Put(new AsnResult("192.168.1.1", 64500, "home", null)); // private: ignored
            cache.Put(new AsnResult("104.160.131.1", null, null, null)); // no ASN: ignored
            cache.Flush();

            var text = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(text))
            {
                Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
                Assert.Equal(6939, doc.RootElement.GetProperty("entries").GetProperty("184.105.80.1").GetProperty("asn").GetInt64());
            }

            var again = new AsnCache(path);
            Assert.Equal(1, again.Count);
            var row = again.Get("184.105.80.1");
            Assert.Equal(6939, row?.Asn);
            Assert.Equal("HURRICANE", row?.Name);
            Assert.Null(again.Get("192.168.1.1"));
            Assert.Null(again.Get("104.160.131.1"));
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void Rows_expire_after_30_days()
    {
        var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        var cache = new AsnCache(null, () => now);
        cache.Put(new AsnResult("184.105.80.1", 6939, null, null));
        now = now.AddDays(29);
        Assert.NotNull(cache.Get("184.105.80.1"));
        now = now.AddDays(2);
        Assert.Null(cache.Get("184.105.80.1"));
    }

    [Fact]
    public void A_broken_file_loads_as_empty()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, AsnCache.FileName);
            File.WriteAllText(path, "{not json");
            Assert.Equal(0, new AsnCache(path).Count);
            File.WriteAllText(path, "{\"schema\":1,\"entries\":{\"184.105.80.1\":{\"asn\":\"x\"},\"10.0.0.1\":{\"asn\":1,\"t\":1}}}");
            Assert.Equal(0, new AsnCache(path).Count);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void Null_path_writes_nothing()
    {
        var folder = V21.TempFolder();
        try
        {
            var before = Directory.GetFiles(folder).Length;
            var cache = new AsnCache(null);
            cache.Put(new AsnResult("184.105.80.1", 6939, null, null));
            cache.Flush();
            Assert.Equal(before, Directory.GetFiles(folder).Length);
            Assert.Equal(6939, cache.Get("184.105.80.1")?.Asn);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void The_oldest_rows_go_first_over_the_cap()
    {
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new AsnCache(null, () => now);
        for (var i = 0; i < AsnCache.MaxEntries + 5; i++)
        {
            now = now.AddSeconds(1);
            cache.Put(new AsnResult("8." + (i / 65536) + "." + (i / 256 % 256) + "." + (i % 256), 64500, null, null));
        }

        Assert.Equal(AsnCache.MaxEntries, cache.Count);
        Assert.Null(cache.Get("8.0.0.0"));
        Assert.NotNull(cache.Get("8.0.16.4"));
    }

    [Fact]
    public void Paths_follow_the_profile()
    {
        Assert.Null(AsnCache.PathFor(RunProfile.Dev, null));
        Assert.Equal(Path.Combine(Path.GetFullPath(@"C:\scratch\dev"), AsnCache.FileName), AsnCache.PathFor(RunProfile.Dev, @"C:\scratch\dev"));
        var production = AsnCache.PathFor(RunProfile.Production, null);
        Assert.NotNull(production);
        Assert.EndsWith(Path.Combine("GeckoPeeks", AsnCache.FileName), production);
        Assert.EndsWith(Path.Combine("GeckoPeeks", RegionSettings.FileName), RegionSettings.PathFor(RunProfile.Production, null));
        Assert.EndsWith(Path.Combine("GeckoPeeks", RefDataStore.FileName), RefDataStore.PathFor(RunProfile.Production, null));
        Assert.Null(RegionSettings.PathFor(RunProfile.Dev, " "));
    }
}

public class RegionSettingsTests
{
    [Fact]
    public void Round_trips_through_a_file()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, RegionSettings.FileName);
            var settings = new RegionSettings(path) { Mode = "manual", ManualId = "EUW1", LastAutoId = "na1" };
            settings.Save();

            var again = new RegionSettings(path);
            Assert.Equal("manual", again.Mode);
            Assert.Equal("euw1", again.ManualId);
            Assert.Equal("na1", again.LastAutoId);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void A_broken_file_loads_as_defaults()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, RegionSettings.FileName);
            File.WriteAllText(path, "[1,2");
            var settings = new RegionSettings(path);
            Assert.Equal("auto", settings.Mode);
            Assert.Null(settings.ManualId);
            Assert.Null(settings.LastAutoId);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void Null_path_writes_nothing_and_bad_values_are_cleaned()
    {
        var settings = new RegionSettings(null) { Mode = "weird", ManualId = "../../x", LastAutoId = "toolongid9" };
        settings.Save();
        Assert.Equal("auto", settings.Mode);
        Assert.Null(settings.ManualId);
        Assert.Null(settings.LastAutoId);
    }
}

public class RegionServiceTests
{
    private static RegionService Make(RegionSettings? settings = null) =>
        new(settings ?? new RegionSettings(null), () => RefData.BuiltIn);

    [Fact]
    public void Starts_on_the_default_region()
    {
        var service = Make();
        Assert.Equal("na1", service.Current.Region.Id);
        Assert.True(service.Current.Auto);
        Assert.Equal("default", service.Current.Basis);
    }

    [Fact]
    public void Auto_picks_from_the_edge_country_and_saves_it()
    {
        var settings = new RegionSettings(null);
        var service = Make(settings);
        var events = new List<RegionSelection>();
        service.Changed += events.Add;

        service.OnEdgeJson("{\"country\":\"KR\",\"continent\":\"AS\"}");
        service.OnEdgeJson("{\"country\":\"KR\",\"continent\":\"AS\"}");

        Assert.Equal("kr", service.Current.Region.Id);
        Assert.Equal("country", service.Current.Basis);
        Assert.Equal("kr", settings.LastAutoId);
        Assert.Single(events);
    }

    [Fact]
    public void Continent_is_the_fallback()
    {
        var service = Make();
        service.OnEdgeJson("{\"country\":\"XX\",\"continent\":\"EU\"}");
        Assert.Equal("euw1", service.Current.Region.Id);
        Assert.Equal("continent", service.Current.Basis);
    }

    [Fact]
    public void Without_edge_data_the_last_auto_pick_is_used()
    {
        var service = Make(new RegionSettings(null) { LastAutoId = "oc1" });
        Assert.Equal("oc1", service.Current.Region.Id);
        Assert.Equal("saved", service.Current.Basis);
        Assert.True(service.Current.Auto);
    }

    [Theory]
    [InlineData("{\"country\":\"XX\"}")]
    [InlineData("{\"country\":\"T1\",\"continent\":\"\"}")]
    [InlineData("{\"colo\":\"AMS\"}")]
    public void An_edge_with_no_usable_place_keeps_the_saved_auto_pick(string edge)
    {
        var settings = new RegionSettings(null) { LastAutoId = "euw1" };
        var service = Make(settings);
        var events = 0;
        service.Changed += _ => events++;

        service.OnEdgeJson(edge);

        Assert.Equal("euw1", service.Current.Region.Id);
        Assert.Equal("saved", service.Current.Basis);
        Assert.Equal("euw1", settings.LastAutoId);
        Assert.Equal(0, events);
    }

    [Fact]
    public void An_edge_with_no_usable_place_and_no_saved_pick_uses_the_default()
    {
        var service = Make();
        service.OnEdgeJson("{\"country\":\"XX\"}");
        Assert.Equal("na1", service.Current.Region.Id);
        Assert.Equal("default", service.Current.Basis);
    }

    [Fact]
    public void Manual_wins_over_edge_data_and_unknown_ids_are_refused()
    {
        var service = Make();
        var events = 0;
        service.Changed += _ => events++;

        Assert.True(service.SetManual("EUW1"));
        Assert.Equal("euw1", service.Current.Region.Id);
        Assert.False(service.Current.Auto);
        Assert.Equal("manual", service.Current.Basis);
        Assert.Equal(1, events);

        service.OnEdgeJson("{\"country\":\"KR\"}");
        Assert.Equal("euw1", service.Current.Region.Id);
        Assert.Equal(1, events);

        Assert.False(service.SetManual("zz9"));
        Assert.Equal("euw1", service.Current.Region.Id);

        Assert.True(service.SetManual("ph2")); // alias
        Assert.Equal("sg2", service.Current.Region.Id);
        Assert.Equal(2, events);

        Assert.True(service.SetAuto());
        Assert.Equal("kr", service.Current.Region.Id);
        Assert.Equal(3, events);
    }

    [Fact]
    public void Same_region_with_a_new_auto_flag_raises_changed()
    {
        var service = Make();
        var events = 0;
        service.Changed += _ => events++;
        Assert.True(service.SetManual("na1"));
        Assert.Equal(1, events);
        Assert.True(service.SetManual("na1"));
        Assert.Equal(1, events);
    }

    [Fact]
    public void An_unknown_saved_manual_id_falls_back_to_auto()
    {
        var service = Make(new RegionSettings(null) { Mode = "manual", ManualId = "zz9" });
        Assert.True(service.Current.Auto);
        Assert.Equal("na1", service.Current.Region.Id);
    }

    [Fact]
    public void Broken_edge_json_is_ignored()
    {
        var service = Make();
        service.OnEdgeJson("{broken");
        service.OnEdgeJson(null);
        service.OnEdgeJson("[]");
        Assert.Equal("default", service.Current.Basis);
    }
}

public class DiagnosisRegionTests
{
    [Fact]
    public void Region_and_target_lines_are_added_when_set()
    {
        var input = new DiagnosisInput("2.1.0", true, "2026-11-21 10:00:00", "HE", "Riot Direct", "chi01",
            "39", "0", "", "100", "N", "Ethernet", "", "192.168.1.1", "N", "", Array.Empty<DiagnosisHop>(),
            "NA (North America), automatic", "Riot Direct edge (AS6507) 192.207.0.5");
        var text = DiagnosisText.Format(input);
        Assert.Contains("\r\nRegion: NA (North America), automatic\r\n", text);
        Assert.Contains("\r\nTarget: Riot Direct edge (AS6507) 192.207.0.5", text);

        var old = DiagnosisText.Format(input with { RegionText = "", TargetText = "" });
        Assert.DoesNotContain("Region:", old);
        Assert.DoesNotContain("Target:", old);
    }
}

[Collection(RefDataGlobalCollection.Name)]
public sealed class RefDataStoreTests : IDisposable
{
    public void Dispose() => RefData.SetCurrent(RefData.BuiltIn);

    private static string NewerJson(string version)
    {
        var root = JsonNode.Parse(V21.BuiltInJson())!.AsObject();
        root["version"] = version;
        return root.ToJsonString();
    }

    [Fact]
    public void Starts_on_the_built_in_copy()
    {
        var store = new RefDataStore(null);
        Assert.Same(RefData.BuiltIn, store.Current);
        Assert.Same(RefData.BuiltIn, RefData.Current);
        Assert.Equal("builtin", store.Status.Source);
        Assert.Null(store.Status.AgeSec);
        Assert.Null(store.ETag);
    }

    [Fact]
    public void Apply_saves_the_cache_and_a_new_store_loads_it()
    {
        var folder = V21.TempFolder();
        try
        {
            var now = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
            var path = Path.Combine(folder, RefDataStore.FileName);
            var store = new RefDataStore(path, () => now);
            var changed = 0;
            store.Changed += () => changed++;

            Assert.False(store.Apply("{broken", "\"x\""));
            Assert.Equal(0, changed);

            Assert.True(store.Apply(NewerJson("2099.01.01-1"), "\"rd-2099.01.01-1\""));
            Assert.Equal(1, changed);
            Assert.Equal("2099.01.01-1", RefData.Current.Version);
            Assert.Equal("worker", store.Status.Source);
            Assert.Equal(0, store.Status.AgeSec);

            now = now.AddHours(1);
            var again = new RefDataStore(path, () => now);
            Assert.Equal("cache", again.Status.Source);
            Assert.Equal("2099.01.01-1", again.Current.Version);
            Assert.Equal("\"rd-2099.01.01-1\"", again.ETag);
            Assert.Equal(3600, again.Status.AgeSec);

            again.MarkNotModified();
            Assert.Equal(0, again.Status.AgeSec);
            Assert.Equal("worker", again.Status.Source);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void An_older_or_broken_cache_is_ignored()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, RefDataStore.FileName);
            var store = new RefDataStore(path);
            Assert.True(store.Apply(NewerJson("2020.01.01-1"), "\"old\""));

            var again = new RefDataStore(path);
            Assert.Equal("builtin", again.Status.Source);
            Assert.Same(RefData.BuiltIn, RefData.Current);

            File.WriteAllText(path, "{\"data\":{\"schema\":9}}");
            Assert.Equal("builtin", new RefDataStore(path).Status.Source);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void Worker_data_older_than_the_data_in_use_is_ignored()
    {
        var folder = V21.TempFolder();
        try
        {
            var path = Path.Combine(folder, RefDataStore.FileName);
            var store = new RefDataStore(path);
            var changed = 0;
            store.Changed += () => changed++;

            // Older than the built-in copy: accepted as "nothing to do", nothing changes or is saved.
            Assert.True(store.Apply(NewerJson("2020.01.01-1"), "\"old\""));
            Assert.Equal(0, changed);
            Assert.Same(RefData.BuiltIn, store.Current);
            Assert.Same(RefData.BuiltIn, RefData.Current);
            Assert.Equal("builtin", store.Status.Source);
            Assert.Null(store.ETag);
            Assert.False(File.Exists(path));

            // Newer than built-in, then a step back: the newer copy stays.
            Assert.True(store.Apply(NewerJson("2099.01.02-1"), "\"new\""));
            Assert.True(store.Apply(NewerJson("2099.01.01-1"), "\"back\""));
            Assert.Equal(1, changed);
            Assert.Equal("2099.01.02-1", store.Current.Version);
            Assert.Equal("\"new\"", store.ETag);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }

    [Fact]
    public void Version_order_is_numeric()
    {
        Assert.True(RefDataStore.CompareVersions("2026.11.21-10", "2026.11.21-9") > 0);
        Assert.True(RefDataStore.CompareVersions("2026.11.21-1", "2026.12.01-1") < 0);
        Assert.True(RefDataStore.CompareVersions("junk", "2026.11.21-1") < 0);
    }
}

[Collection(RefDataGlobalCollection.Name)]
public sealed class CloudPoller21Tests : IDisposable
{
    private const string Base = "https://worker.test";

    public void Dispose() => RefData.SetCurrent(RefData.BuiltIn);

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    }

    private static HttpResponseMessage Route(HttpRequestMessage r, string refdata, string? etag)
    {
        switch (r.RequestUri!.AbsolutePath)
        {
            case "/v1/edge":
                return FakeHttp.Json("{\"asn\":64500,\"country\":\"US\",\"continent\":\"NA\"}");
            case "/v1/pulse":
                return FakeHttp.Json("{\"radar\":true}");
            case "/v1/riot-status":
                var region = r.RequestUri.Query.Split('=').Last();
                return FakeHttp.Json("{\"region\":\"" + region + "\",\"lol\":null,\"valorant\":null}");
            case "/v1/refdata":
                if (etag != null && r.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains(etag))
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                var ok = FakeHttp.Json(refdata);
                if (etag != null)
                    ok.Headers.TryAddWithoutValidation("ETag", etag);
                return ok;
            default:
                return FakeHttp.Json("{\"error\":\"not_found\"}", HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Old_worker_404s_on_refdata_and_riot_status_leave_status_ok()
    {
        var clock = new Clock();
        var worker = new FakeHttp
        {
            Respond = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath switch
            {
                "/v1/edge" => FakeHttp.Json("{\"country\":\"US\"}"),
                "/v1/pulse" => FakeHttp.Json("{\"radar\":true}"),
                _ => FakeHttp.Json("{\"error\":\"not_found\"}", HttpStatusCode.NotFound),
            }),
        };
        using var client = new CloudClient(worker, Base);
        var store = new RefDataStore(null);
        var region = new RegionService(new RegionSettings(null), () => store.Current);
        using var poller = new CloudPoller(client, true, () => clock.Now, new CloudPollerOptions(new AsnCache(null), store, region));

        await poller.TickAsync();

        var snap = poller.Snapshot();
        Assert.Equal("ok", snap.Status);
        Assert.Equal("", snap.Error);
        Assert.Null(snap.RiotStatusJson);
        Assert.Equal("builtin", snap.RefData?.Source);
        Assert.Equal(1, worker.CountPath("/v1/refdata"));
        Assert.Equal(1, worker.CountPath("/v1/riot-status"));

        // No hammering of an old Worker: nothing again on the next ticks.
        clock.Now += TimeSpan.FromSeconds(30);
        await poller.TickAsync();
        clock.Now += TimeSpan.FromMinutes(3);
        await poller.TickAsync();
        Assert.Equal(1, worker.CountPath("/v1/refdata"));
        Assert.Equal(1, worker.CountPath("/v1/riot-status"));
    }

    [Fact]
    public async Task Refdata_200_then_304_with_the_stored_etag()
    {
        var clock = new Clock();
        var json = JsonNode.Parse(V21.BuiltInJson())!.AsObject();
        json["version"] = "2099.01.01-1";
        var body = json.ToJsonString();
        const string tag = "\"rd-2099.01.01-1\"";
        var worker = new FakeHttp { Respond = (r, _) => Task.FromResult(Route(r, body, tag)) };
        using var client = new CloudClient(worker, Base);
        var store = new RefDataStore(null, () => clock.Now);
        using var poller = new CloudPoller(client, true, () => clock.Now, new CloudPollerOptions(null, store, null));

        await poller.TickAsync();
        Assert.Equal("2099.01.01-1", RefData.Current.Version);
        Assert.Equal(tag, store.ETag);
        Assert.Equal("worker", poller.Snapshot().RefData?.Source);
        Assert.False(worker.Requests.First(r => r.RequestUri!.AbsolutePath == "/v1/refdata").Headers.Contains("If-None-Match"));

        clock.Now += TimeSpan.FromHours(25);
        await poller.TickAsync();
        Assert.Equal(2, worker.CountPath("/v1/refdata"));
        var second = worker.Requests.Last(r => r.RequestUri!.AbsolutePath == "/v1/refdata");
        Assert.Equal(tag, second.Headers.GetValues("If-None-Match").Single());
        Assert.Equal(0, poller.Snapshot().RefData?.AgeSec);
        Assert.Equal("ok", poller.Snapshot().Status);
        Assert.Equal("2099.01.01-1", RefData.Current.Version);
    }

    [Fact]
    public async Task Riot_status_follows_the_region_and_refetches_after_a_change()
    {
        var clock = new Clock();
        var worker = new FakeHttp { Respond = (r, _) => Task.FromResult(Route(r, "{}", null)) };
        using var client = new CloudClient(worker, Base);
        var region = new RegionService(new RegionSettings(null), () => RefData.BuiltIn);
        using var poller = new CloudPoller(client, true, () => clock.Now, new CloudPollerOptions(null, null, region));

        await poller.TickAsync();
        Assert.Equal("country", region.Current.Basis);
        var first = poller.Snapshot().RiotStatusJson;
        Assert.Contains("\"region\":\"na1\"", first);
        Assert.Equal(0, poller.Snapshot().RiotStatusAgeSec);

        clock.Now += TimeSpan.FromSeconds(61);
        Assert.True(region.SetManual("euw1"));
        await Eventually(poller, () => worker.Requests.Any(r => r.RequestUri!.Query == "?region=euw1"));
        await Eventually(poller, () => poller.Snapshot().RiotStatusJson?.Contains("euw1") == true);

        // Every 5 minutes after that.
        var count = worker.CountPath("/v1/riot-status");
        clock.Now += TimeSpan.FromMinutes(4);
        await poller.TickAsync();
        Assert.Equal(count, worker.CountPath("/v1/riot-status"));
        clock.Now += TimeSpan.FromMinutes(1);
        await poller.TickAsync();
        Assert.Equal(count + 1, worker.CountPath("/v1/riot-status"));
    }

    [Fact]
    public async Task A_region_change_waits_60_seconds_after_the_last_fetch()
    {
        var clock = new Clock();
        var worker = new FakeHttp { Respond = (r, _) => Task.FromResult(Route(r, "{}", null)) };
        using var client = new CloudClient(worker, Base);
        var region = new RegionService(new RegionSettings(null), () => RefData.BuiltIn);
        using var poller = new CloudPoller(client, true, () => clock.Now, new CloudPollerOptions(null, null, region));

        await poller.TickAsync();
        Assert.True(region.SetManual("kr"));
        await Task.Delay(100);
        await poller.TickAsync();
        Assert.DoesNotContain(worker.Requests, r => r.RequestUri!.Query == "?region=kr");
        Assert.Null(poller.Snapshot().RiotStatusJson); // the na1 status is not shown under KR

        clock.Now += TimeSpan.FromSeconds(60);
        await Eventually(poller, () => worker.Requests.Any(r => r.RequestUri!.Query == "?region=kr"));
    }

    [Fact]
    public async Task Cloud_off_fetches_nothing_new()
    {
        var worker = new FakeHttp();
        using var client = new CloudClient(worker, Base);
        var store = new RefDataStore(null);
        var region = new RegionService(new RegionSettings(null), () => store.Current);
        using var poller = new CloudPoller(client, false, null, new CloudPollerOptions(new AsnCache(null), store, region));
        await poller.TickAsync();
        Assert.Equal(0, worker.Count);
        Assert.Equal("off", poller.Snapshot().Status);
        Assert.Equal("builtin", poller.Snapshot().RefData?.Source);
    }

    [Fact]
    public async Task Asn_reads_go_through_the_cache()
    {
        var worker = new FakeHttp
        {
            Respond = (_, _) => Task.FromResult(FakeHttp.Json("{\"results\":[{\"ip\":\"184.105.80.1\",\"asn\":6939,\"name\":\"HE\",\"country\":\"US\"}]}")),
        };
        using var client = new CloudClient(worker, Base);
        var cache = new AsnCache(null);
        cache.Put(new AsnResult("104.160.131.1", 6507, "RIOT", "US"));
        using var poller = new CloudPoller(client, true, null, new CloudPollerOptions(cache));

        Assert.Equal(6507, poller.Asn("104.160.131.1")?.Asn);
        poller.SetHops(new[] { "104.160.131.1", "184.105.80.1" });
        await poller.LookupAsnAsync();
        Assert.Equal(6939, cache.Get("184.105.80.1")?.Asn);
        Assert.Equal("?ips=184.105.80.1", Uri.UnescapeDataString(worker.Requests.Single().RequestUri!.Query));
    }

    private static async Task Eventually(CloudPoller poller, Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await poller.TickAsync();
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}

[Collection(RefDataGlobalCollection.Name)]
public sealed class CoreServicesTests : IDisposable
{
    public void Dispose() => RefData.SetCurrent(RefData.BuiltIn);

    [Fact]
    public void Build_request_uses_the_region_target()
    {
        // Dev with no dev root: every store is memory only.
        using var core = CoreServices.Create(RunProfile.Dev, null, cloudEnabled: false);
        var first = core.BuildRequest(false);
        Assert.Equal("192.207.0.5", first.TargetIp);
        Assert.Same(core.Resolver, first.Resolver);

        Assert.True(core.Region.SetManual("euw1"));
        var request = core.BuildRequest(true);
        Assert.True(request.LeagueRunning);
        Assert.Equal(RefData.BuiltIn.FindRegion("euw1")!.TargetIp, request.TargetIp);
        Assert.NotEqual("192.207.0.5", request.TargetIp);
        Assert.Same(core.RefData.Current, request.RefData);
        Assert.False(core.Cloud.Enabled);
    }

    [Fact]
    public void Dev_stores_live_in_the_dev_root()
    {
        var folder = V21.TempFolder();
        try
        {
            using (var core = CoreServices.Create(RunProfile.Dev, folder, cloudEnabled: false))
            {
                Assert.True(core.Region.SetManual("kr"));
                core.Asn.Put(new AsnResult("184.105.80.1", 6939, null, null));
            }

            Assert.True(File.Exists(Path.Combine(folder, RegionSettings.FileName)));
            Assert.True(File.Exists(Path.Combine(folder, AsnCache.FileName)));
            using var again = CoreServices.Create(RunProfile.Dev, folder, cloudEnabled: false);
            Assert.Equal("kr", again.Region.Current.Region.Id);
            Assert.Equal(6939, again.Asn.Get("184.105.80.1")?.Asn);
        }
        finally
        {
            V21.RemoveFolder(folder);
        }
    }
}
