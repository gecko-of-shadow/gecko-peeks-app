using System.Globalization;
using System.Text.Json.Nodes;
using GeckoPeeks.App;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using RegionInfo = GeckoPeeks.Routing.RegionInfo;

namespace GeckoPeeks.Ui.State;

/// <summary>
/// Canned state for screenshots of a Dev build (GECKO_PEEKS_DEMO=1). The route, the ISP and
/// the place are neutral demo values (contract 6.4): documentation ASN 64500, "Demo ISP",
/// Denver. It is nobody's real connection. The demo makes no ASN lookups and shows no live
/// cloud data. Production never uses this.
/// </summary>
public static class DemoData
{
    public const string Variable = "GECKO_PEEKS_DEMO";

    public const string Gateway = "192.168.1.1";
    public const string TargetIp = "192.207.0.5";
    public const string HandoffCity = "chi01";
    public const string HandoffCityName = "Chicago";
    public const double HandoffLat = 41.88;
    public const double HandoffLon = -87.63;
    public const long UpstreamAsn = 6939;
    public const long IspAsn = 64500;
    public const string IspName = "Demo ISP";

    public static bool IsOn(RunProfile profile, string? value) =>
        profile == RunProfile.Dev && string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    public sealed record DemoHop(
        int N, string Ip, string Host, double? RttMs, bool ReachedTarget,
        long? Asn = null, string? AsName = null, string Network = "", string NameSource = "", string CityName = "");

    /// <summary>The frozen demo route. Hop 5 is the last pure upstream hop (HE); hop 6 is the handoff.</summary>
    public static IReadOnlyList<DemoHop> Hops { get; } = new DemoHop[]
    {
        new(1, "192.168.1.1", "", 1, false),
        new(2, "100.64.0.1", "", 6, false),
        new(3, "*", "", null, false),
        new(4, "184.105.64.1", "", 17, false, 6939, "Hurricane Electric LLC", "HE", RouteClassifier.SourceAsn),
        new(5, "184.105.80.1", "100ge0-1.core2.chi1.he.net", 38, false, 6939, "Hurricane Electric LLC", "HE", RouteClassifier.SourceHostname, HandoffCityName),
        new(6, "104.160.131.1", "ae1.er01.chi01.riotdirect.net", 39, false, 6507, "Riot Games", "Riot Direct", RouteClassifier.SourceHostname, HandoffCityName),
        new(7, TargetIp, "", 40, true, 6507, "Riot Games", "Riot Direct", RouteClassifier.SourceAsn),
    };

    /// <param name="asnLookup">
    /// Only for a public hop without a fixed demo ASN. Every demo hop has one, so the demo
    /// makes no lookups. Null is fine.
    /// </param>
    public static ProbeView Probe(DateTime nowLocal, Func<string, (long? Asn, string? Name)?>? asnLookup = null)
    {
        var last = nowLocal.AddMinutes(-6).AddSeconds(-nowLocal.Second);
        var hops = Hops.Select(h =>
        {
            var responded = h.Ip != "*";
            var isPrivate = responded && !GeckoPeeks.Cloud.PublicIp.IsPublic(h.Ip);
            var asn = h.Asn;
            var name = h.AsName;
            if (asn == null && responded && !isPrivate && asnLookup?.Invoke(h.Ip) is { } found)
            {
                asn = found.Asn;
                name = found.Name;
            }

            return new HopView(h.N, h.Ip, h.Host, h.RttMs, responded, h.ReachedTarget, false, isPrivate, asn, name,
                h.Network, h.NameSource, h.CityName);
        }).ToArray();

        return new ProbeView
        {
            Paused = false,
            LeagueRunning = false,
            TestRunning = false,
            Phase = "Idle",
            Mode = "Every 15 min",
            NextDueUtc = last.AddMinutes(15).ToUniversalTime(),
            ActivityLine = "Test saved at " + last.ToString("HH:mm", CultureInfo.InvariantCulture) + ".",
            HasRoute = true,
            RouteLine = RouteCardText.RouteLine("HE", "Riot Direct", HandoffCity),
            Upstream = "HE",
            HandoffNetwork = "Riot Direct",
            HandoffCity = HandoffCity,
            HandoffCityName = HandoffCityName,
            UpstreamSource = RouteClassifier.SourceHostname,
            HandoffSource = RouteClassifier.SourceHostname,
            UpstreamAsn = UpstreamAsn,
            UpstreamAsns = new[] { UpstreamAsn },
            HandoffLat = HandoffLat,
            HandoffLon = HandoffLon,
            LastHop = new LatencyFigure(39, 0, RouteCardText.LastHopFigure("39", "0")),
            // Demo hop 7 (the target) answers at 40 ms, so the Riot ping series answers too.
            Riot = new LatencyFigure(40, 0, RouteCardText.RiotFigure("40", "0")),
            ContextLine = RouteCardText.Context(false, "Ethernet", "", Gateway, "N", "6 min ago"),
            ChangeLine = "",
            ChangedHops = Array.Empty<int>(),
            TestTimestamp = last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Adapter = "Ethernet",
            Wifi = "",
            Gateway = Gateway,
            Vpn = false,
            League = false,
            Hops = hops,
            HopsAddressesOnly = false,
            Tooltip = "Gecko Peeks — HE → Riot Direct Chicago · Riot 40 ms",
        };
    }

    /// <summary>Seven days of tests every 15 minutes, oldest first. Same numbers every run.</summary>
    public static IReadOnlyList<HistoryRow> History(DateTime nowLocal)
    {
        var random = new Random(20261120);
        var end = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute / 15 * 15, 0, DateTimeKind.Local);
        var start = end.AddDays(-7);
        var rows = new List<HistoryRow>(7 * 96 + 1);
        for (var t = start; t <= end; t = t.AddMinutes(15))
        {
            var hour = t.Hour + t.Minute / 60.0;
            var evening = Math.Max(0, Math.Sin((hour - 15) / 9 * Math.PI)); // busier from 15:00 to 24:00
            var dayIndex = (int)(t - start).TotalDays;

            // Day 3 afternoon took a Cogent path for a few hours. The handoff stays Riot Direct Chicago.
            var cogent = dayIndex == 3 && t.Hour is >= 13 and < 18;
            var upstream = cogent ? "Cogent" : "HE";
            var baseMs = cogent ? 44 : 38;
            var lastHop = Math.Round(baseMs + evening * 6 + random.NextDouble() * 3, 1);
            var spike = random.NextDouble() < 0.015;
            if (spike)
                lastHop += 25 + random.Next(20);
            var lastLoss = spike ? 10 * (1 + random.Next(3)) : 0;
            var league = t.Hour is >= 19 and < 23 && dayIndex % 2 == 0;
            var riotNoReply = random.NextDouble() < 0.01;
            double? riot = riotNoReply ? null : Math.Round(lastHop + 0.5 + random.NextDouble(), 1);
            double? riotLoss = riotNoReply ? 100 : lastLoss;

            rows.Add(new HistoryRow(
                t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                upstream,
                "Riot Direct",
                HandoffCity,
                lastHop,
                lastLoss,
                riot,
                riotLoss,
                league,
                false,
                "Ethernet",
                HandoffCityName));
        }

        return rows;
    }

    /// <summary>The fixed demo /v1/edge object.</summary>
    public static string EdgeJson { get; } = new JsonObject
    {
        ["asn"] = IspAsn,
        ["asOrg"] = IspName,
        ["city"] = "Denver",
        ["region"] = "Colorado",
        ["country"] = "US",
        ["continent"] = "NA",
        ["lat"] = 39.74,
        ["lon"] = -104.99,
        ["timezone"] = "America/Denver",
        ["colo"] = "DEN",
        ["httpProtocol"] = "HTTP/2",
        ["tlsVersion"] = "TLSv1.3",
    }.ToJsonString();

    /// <summary>The fixed demo /v1/pulse object: the Riot block, the demo ISP with upstreams, and US.</summary>
    public static string PulseJson(DateTime nowUtc)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var hourEnd = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

        JsonObject Traffic(double floor, double peak, double startHour, double span)
        {
            var times = new JsonArray();
            var values = new JsonArray();
            var raw = new double[24];
            for (var i = 0; i < 24; i++)
            {
                var t = hourEnd.AddHours(i - 23);
                raw[i] = floor + peak * Math.Max(0, Math.Sin((t.Hour - startHour) / span * Math.PI));
            }

            var max = raw.Max();
            for (var i = 0; i < 24; i++)
            {
                times.Add(UiStateJson.IsoUtc(hourEnd.AddHours(i - 23)));
                values.Add(Math.Round(raw[i] / max, 3));
            }

            return new JsonObject { ["timestamps"] = times, ["values"] = values };
        }

        var leakStart = now.AddDays(-2).AddHours(-5);
        var pulse = new JsonObject
        {
            ["time"] = UiStateJson.IsoUtc(now.AddMinutes(-2)),
            ["radar"] = true,
            ["riot"] = new JsonObject
            {
                ["asn"] = 6507,
                ["name"] = "Riot Games",
                ["prefix"] = new JsonObject
                {
                    ["prefix"] = "192.207.0.0/24", ["origin"] = 6507, ["rpki"] = "Valid", ["peers"] = 1874, ["totalPeers"] = 1902,
                },
                ["traffic"] = Traffic(40, 55, 9, 15),
                ["outages"] = new JsonArray(),
                ["anomalies"] = new JsonArray(),
                ["hijacks"] = new JsonArray(),
                ["leaks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "leak",
                        ["start"] = UiStateJson.IsoUtc(leakStart),
                        ["end"] = UiStateJson.IsoUtc(leakStart.AddMinutes(40)),
                        ["title"] = "Demo: route leak seen for a Riot prefix",
                        ["detail"] = "Demo data. AS64511 leaked 192.207.0.0/24 to its peers. 37 route collector peers saw it.",
                        ["url"] = "https://radar.cloudflare.com/routing/as6507",
                    },
                },
            },
            ["isp"] = new JsonObject
            {
                ["asn"] = IspAsn,
                ["name"] = IspName,
                ["traffic"] = Traffic(30, 70, 7, 17),
                ["outages"] = new JsonArray(),
                ["anomalies"] = new JsonArray(),
                ["hijacks"] = new JsonArray(),
                ["leaks"] = new JsonArray(),
                ["upstreams"] = new JsonArray
                {
                    new JsonObject { ["asn"] = 174, ["name"] = "Cogent Communications", ["rel"] = "provider" },
                    new JsonObject { ["asn"] = 6939, ["name"] = "Hurricane Electric LLC", ["rel"] = "provider" },
                    new JsonObject { ["asn"] = 3356, ["name"] = "Lumen (Level 3)", ["rel"] = "peer" },
                },
            },
            ["country"] = new JsonObject
            {
                ["code"] = "US",
                ["outages"] = new JsonArray(),
                ["iqi"] = new JsonObject
                {
                    ["latency"] = new JsonObject { ["p25"] = 17, ["p50"] = 31, ["p75"] = 58 },
                    ["bandwidth"] = new JsonObject { ["p25"] = 22, ["p50"] = 86, ["p75"] = 210 },
                },
            },
        };
        return pulse.ToJsonString();
    }

    /// <summary>The fixed demo /v1/riot-status object for a region: one scheduled LoL maintenance, VALORANT clear.</summary>
    public static string RiotStatusJson(DateTime nowUtc, RegionInfo region)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        JsonObject Game(string id, JsonArray maintenances) => new()
        {
            ["id"] = id.ToUpperInvariant(),
            ["name"] = region.Name,
            ["incidents"] = new JsonArray(),
            ["maintenances"] = maintenances,
        };

        var status = new JsonObject
        {
            ["region"] = region.Id,
            ["lolStatusId"] = region.LolStatusId,
            ["valorantShard"] = region.ValorantShard,
            ["fetchedAt"] = UiStateJson.IsoUtc(now.AddSeconds(-40)),
            ["lol"] = Game(region.LolStatusId, new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1001,
                    ["type"] = "maintenance",
                    ["severity"] = null,
                    ["status"] = "scheduled",
                    ["title"] = "Demo: scheduled maintenance",
                    ["text"] = "Demo text. Ranked queues close for a short time during the update.",
                    ["platforms"] = new JsonArray("windows", "macos"),
                    ["createdAt"] = UiStateJson.IsoUtc(now.AddHours(-5)),
                    ["updatedAt"] = UiStateJson.IsoUtc(now.AddHours(-2)),
                    ["archiveAt"] = null,
                },
            }),
            ["valorant"] = Game(region.ValorantShard, new JsonArray()),
        };
        return status.ToJsonString();
    }

    /// <summary>The demo cloud block: fixed edge, pulse and Riot status, built-in reference data.</summary>
    public static CloudView Cloud(DateTime nowUtc, RegionInfo region, RefData refData) => new(
        true,
        "ok",
        "",
        95,
        120,
        EdgeJson,
        PulseJson(nowUtc),
        RiotStatusJson(nowUtc, region),
        40,
        new RefDataView(refData.Version, "builtin", null));
}
