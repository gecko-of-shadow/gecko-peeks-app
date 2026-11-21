using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using GeckoPeeks.Ui.State;

namespace GeckoPeeks.Ui.WebHost;

/// <summary>
/// ProbeSnapshot to the Contract 2 probe record. Hop networks come from the classifier
/// (HopNames); ASN numbers come from the test, then the cloud cache; AS names from the cache.
/// </summary>
internal static class ProbeMapper
{
    public static ProbeView Map(ProbeSnapshot s, CloudPoller cloud)
    {
        var changed = new HashSet<int>(s.ChangedHops);
        var names = new Dictionary<int, HopName>();
        foreach (var name in s.HopNames)
            names[name.Number] = name;

        var hops = new List<HopView>(s.Hops.Count);
        foreach (var hop in s.Hops)
        {
            var ip = hop.Responded ? hop.Ip : "*";
            var isPublic = hop.Responded && PublicIp.IsPublic(hop.Ip);
            names.TryGetValue(hop.Number, out var hopName);
            var cached = isPublic && hop.Number > 1 ? cloud.Asn(hop.Ip) : null;
            var asn = isPublic ? hopName?.Asn ?? cached?.Asn : null;
            var asName = cached != null && cached.Asn == asn ? cached.Name : null;
            hops.Add(new HopView(
                hop.Number,
                ip,
                hop.Hostname ?? "",
                hop.Responded && hop.RttMs is { } rtt ? rtt : null,
                hop.Responded,
                hop.ReachedTarget,
                changed.Contains(hop.Number),
                hop.Responded && !isPublic,
                asn,
                asName,
                hopName?.Network ?? "",
                hopName?.Source ?? "",
                hopName?.CityName ?? ""));
        }

        return new ProbeView
        {
            Paused = s.Paused,
            LeagueRunning = s.LeagueRunning,
            TestRunning = s.TestRunning,
            Phase = s.Phase,
            Mode = s.Mode,
            NextDueUtc = s.NextDueLocal?.ToUniversalTime(),
            LastError = s.LastError,
            ActivityLine = s.ActivityLine,
            NoMoreTests = s.NoMoreTests,
            HasRoute = s.HasRoute,
            RouteLine = s.RouteLine,
            Upstream = s.TestUpstream,
            HandoffNetwork = s.TestHandoffNetwork,
            HandoffCity = s.TestHandoffCity,
            HandoffCityName = string.IsNullOrEmpty(s.TestHandoffCityName) ? CityNames.Display(s.TestHandoffCity) : s.TestHandoffCityName,
            UpstreamSource = s.TestUpstreamSource,
            HandoffSource = s.TestHandoffSource,
            UpstreamAsn = s.TestUpstreamAsn,
            UpstreamAsns = UiMapping.ProviderAsns(s.TestUpstream, RefData.Current),
            HandoffLat = s.TestHandoffLat,
            HandoffLon = s.TestHandoffLon,
            LastHop = new LatencyFigure(UiMapping.Number(s.TestLastHopAvg), UiMapping.Number(s.TestLastHopLoss), s.LastHopFigure),
            Riot = new LatencyFigure(UiMapping.Number(s.TestRiotAvg), UiMapping.Number(s.TestRiotLoss), s.RiotFigure),
            ContextLine = s.ContextLine,
            ChangeLine = s.ChangeLine,
            ChangedHops = s.ChangedHops,
            TestTimestamp = s.TestTimestamp,
            Adapter = s.TestAdapter,
            Wifi = s.TestWifi,
            Gateway = s.TestGateway,
            Vpn = UiMapping.IsYes(s.TestVpn),
            League = UiMapping.IsYes(s.TestLeague),
            Hops = hops,
            HopsAddressesOnly = s.HopsAreAddressesOnly,
            Tooltip = s.Tooltip,
        };
    }

    /// <summary>The hop addresses that may go to the Worker for an ASN lookup.</summary>
    public static IReadOnlyList<string> PublicHops(ProbeSnapshot s) =>
        PublicIp.HopsToLookUp(s.Hops.Select(h => (h.Number, h.Ip, h.Responded)), s.TestGateway);
}
