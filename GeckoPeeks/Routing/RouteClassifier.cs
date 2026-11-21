namespace GeckoPeeks.Routing;

/// <summary>
/// Upstream is the provider of the last hop that matches one of the five
/// patterns and does not also name a different network.
/// A hostname like "zayo-telia..." names both, so it is the handoff
/// (HandoffNetwork = the other network, usually Telia), not another upstream hop.
/// The handoff is the first hop after that upstream hop whose hostname belongs
/// to a different recognized network (Telia, or another of the five providers).
/// </summary>
public static class RouteClassifier
{
    public const string Unknown = "Unknown";

    private static readonly (string Domain, string Name)[] OfficialDomains =
    {
        ("zayo.com", "Zayo"),
        ("cogentco.com", "Cogent"),
        ("telus.net", "TELUS"),
        ("telus.com", "TELUS"),
        ("wolfpaw.net", "Wolfpaw"),
        ("he.net", "HE"),
        ("twelve99.net", "Telia"),
        ("telia.net", "Telia"),
        ("telia.com", "Telia"),
    };

    public const string SourceHostname = "hostname";
    public const string SourceAsn = "asn";

    private const string TierUpstream = "upstream";
    private const string TierBackbone = "backbone";

    public sealed record Features(
        string Upstream,
        string HandoffNetwork,
        string HandoffHost,
        string HandoffIP,
        string HandoffCity,
        string HandoffRttMs,
        string UpstreamSource = "",
        string HandoffSource = "",
        long? UpstreamAsn = null);

    public sealed record HopClass(IReadOnlyList<string> Networks, string? PureUpstream);

    public static Features Classify(IReadOnlyList<HopObservation> hops) => Classify(hops, null, RefData.BuiltIn);

    /// <summary>
    /// Upstream: the last pure hop of an upstream-tier provider (by hostname, or by ASN when the
    /// hostname names no network), else the last pure backbone hop, else the last hop whose ASN
    /// names an upstream (then backbone) provider, else the last hop whose mixed hostname does.
    /// Handoff: the first later hop that names another network (Telia preferred).
    /// </summary>
    public static Features Classify(IReadOnlyList<HopObservation> hops, IReadOnlyDictionary<string, long>? asnByIp, RefData refData)
    {
        ArgumentNullException.ThrowIfNull(hops);
        refData ??= RefData.BuiltIn;
        var info = Analyze(hops, asnByIp, refData);

        var index = -1;
        ProviderInfo? upstream = null;
        var source = "";
        var handoffFromSelf = false;

        // Pass 1 and 2: pure hops, upstream tier first, then backbone.
        foreach (var tier in new[] { TierUpstream, TierBackbone })
        {
            for (var i = 0; i < info.Length; i++)
            {
                if (info[i] is { Pure: { } pure } hop && pure.Tier == tier)
                {
                    index = i;
                    upstream = pure;
                    source = hop.PureSource;
                }
            }

            if (upstream != null)
                break;
        }

        // Pass 3: the ASN names the provider, whatever the hostname says.
        if (upstream == null)
        {
            foreach (var tier in new[] { TierUpstream, TierBackbone })
            {
                for (var i = 0; i < info.Length; i++)
                {
                    if (info[i] is { AsnProvider: { } byAsn } && byAsn.Tier == tier)
                    {
                        index = i;
                        upstream = byAsn;
                        source = SourceAsn;
                    }
                }

                if (upstream != null)
                    break;
            }

            handoffFromSelf = upstream != null;
        }

        // Pass 4 (never Unknown): a mixed hostname names the provider.
        if (upstream == null)
        {
            foreach (var tier in new[] { TierUpstream, TierBackbone })
            {
                for (var i = 0; i < info.Length; i++)
                {
                    var match = info[i]?.HostNets.FirstOrDefault(p => p.Tier == tier);
                    if (match != null)
                    {
                        index = i;
                        upstream = match;
                        source = SourceHostname;
                    }
                }

                if (upstream != null)
                    break;
            }

            handoffFromSelf = upstream != null;
        }

        if (upstream == null)
            return new Features(Unknown, "", "", "", "", "");

        var upstreamAsn = UpstreamAsnFor(upstream, info[index]!.Asn, info);

        for (var i = handoffFromSelf ? index : index + 1; i < info.Length; i++)
        {
            var hop = info[i];
            if (hop == null)
                continue;

            List<string> names;
            string handoffSource;
            if (hop.HostNets.Count > 0)
            {
                names = hop.HostNets.Select(p => p.Name).ToList();
                handoffSource = SourceHostname;
            }
            else if (hop.AsnProvider != null)
            {
                names = new List<string> { hop.AsnProvider.Name };
                handoffSource = SourceAsn;
            }
            else
            {
                continue;
            }

            var others = names.Where(n => !string.Equals(n, upstream.Name, StringComparison.Ordinal)).Distinct().ToList();
            if (others.Count == 0)
                continue;

            // Telia is the usual far-side network in a mixed name like zayo-telia.
            var handoffNetwork = others.Contains("Telia") ? "Telia" : others[0];
            var observation = hops[i];
            var rtt = observation.RttMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var city = string.IsNullOrWhiteSpace(observation.Hostname) ? "" : CityCode.Extract(observation.Hostname, refData);
            return new Features(
                upstream.Name,
                handoffNetwork,
                observation.Hostname ?? "",
                observation.Ip,
                city,
                rtt,
                source,
                handoffSource,
                upstreamAsn);
        }

        return new Features(upstream.Name, "", "", "", "", "", source, "", upstreamAsn);
    }

    /// <summary>
    /// The upstream's ASN. A hop named by its hostname often has an interconnect address from the
    /// ISP's prefix, so its origin ASN is the ISP's. Use the chosen hop's ASN only when it is one of
    /// the provider's ASNs; else the first provider ASN seen on any hop; else the provider's first ASN.
    /// </summary>
    private static long? UpstreamAsnFor(ProviderInfo upstream, long? chosenHopAsn, HopInfo?[] info)
    {
        if (chosenHopAsn is { } asn && upstream.Asns.Contains(asn))
            return asn;
        foreach (var hop in info)
        {
            if (hop?.Asn is { } seen && upstream.Asns.Contains(seen))
                return seen;
        }

        return upstream.Asns.Count > 0 ? upstream.Asns[0] : null;
    }

    /// <summary>The network, its source, the ASN and the city of every hop, for the route page.</summary>
    public static IReadOnlyList<HopName> NameHops(IReadOnlyList<HopObservation> hops, IReadOnlyDictionary<string, long>? asnByIp, RefData refData)
    {
        ArgumentNullException.ThrowIfNull(hops);
        refData ??= RefData.BuiltIn;
        var info = Analyze(hops, asnByIp, refData);
        var list = new List<HopName>(hops.Count);
        for (var i = 0; i < hops.Count; i++)
        {
            var hop = info[i];
            if (hop == null)
            {
                list.Add(new HopName(hops[i].Number, "", "", null, "", ""));
                continue;
            }

            string network;
            string source;
            if (hop.HostNets.Count > 0)
            {
                network = string.Join(" + ", hop.HostNets.Select(p => p.Name));
                source = SourceHostname;
            }
            else if (hop.AsnProvider != null)
            {
                network = hop.AsnProvider.Name;
                source = SourceAsn;
            }
            else
            {
                network = "";
                source = "";
            }

            var code = "";
            var cityName = "";
            var host = hops[i].Hostname;
            if (!string.IsNullOrWhiteSpace(host))
            {
                code = CityCode.Extract(host, refData);
                if (code.Length > 0)
                    cityName = GeckoPeeks.Text.CityNames.Lookup(code, refData.ProviderForHost(host)?.CityGroup, refData) ?? "";
            }

            list.Add(new HopName(hops[i].Number, network, source, hop.Asn, code, cityName));
        }

        return list;
    }

    /// <summary>What one responded hop says. Null for a silent hop.</summary>
    private sealed record HopInfo(List<ProviderInfo> HostNets, ProviderInfo? Pure, string PureSource, ProviderInfo? AsnProvider, long? Asn);

    private static HopInfo?[] Analyze(IReadOnlyList<HopObservation> hops, IReadOnlyDictionary<string, long>? asnByIp, RefData refData)
    {
        var result = new HopInfo?[hops.Count];
        for (var i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            if (!hop.Responded)
                continue;

            var (nets, domainMatched) = HostNetworks(hop.Hostname, refData);
            long? asn = null;
            ProviderInfo? byAsn = null;
            if (asnByIp != null && !string.IsNullOrWhiteSpace(hop.Ip) && asnByIp.TryGetValue(hop.Ip.Trim(), out var a))
            {
                asn = a;
                byAsn = refData.ProviderForAsn(a);
            }

            ProviderInfo? pure = null;
            var pureSource = "";
            // A pure hostname needs the provider's own domain, as in v2.0; a stem alone only
            // marks a mixed handoff name.
            if (nets.Count == 1 && domainMatched.Contains(nets[0]))
            {
                pure = nets[0];
                pureSource = SourceHostname;
            }
            else if (nets.Count == 0 && byAsn != null)
            {
                pure = byAsn;
                pureSource = SourceAsn;
            }

            result[i] = new HopInfo(nets, pure, pureSource, byAsn, asn);
        }

        return result;
    }

    /// <summary>Domain-suffix matches in provider order, then stem matches. Same order as v2.0.</summary>
    private static (List<ProviderInfo> Networks, HashSet<ProviderInfo> DomainMatched) HostNetworks(string? hostname, RefData refData)
    {
        var networks = new List<ProviderInfo>();
        var domainMatched = new HashSet<ProviderInfo>();
        var host = RefData.NormalizeHost(hostname);
        if (host.Length == 0)
            return (networks, domainMatched);

        foreach (var provider in refData.Providers)
        {
            if (provider.Domains.Any(domain => HostMatchesDomain(host, domain)))
            {
                networks.Add(provider);
                domainMatched.Add(provider);
            }
        }

        foreach (var provider in refData.Providers)
        {
            if (!networks.Contains(provider) && provider.Stems.Any(stem => LabelContains(host, stem)))
                networks.Add(provider);
        }

        return (networks, domainMatched);
    }

    public static HopClass ClassifyHop(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return new HopClass(Array.Empty<string>(), null);

        var host = hostname.ToLowerInvariant();
        var networks = new List<string>();

        void Add(string name)
        {
            if (!networks.Contains(name))
                networks.Add(name);
        }

        string? official = null;
        foreach (var (domain, name) in OfficialDomains)
        {
            if (!HostMatchesDomain(host, domain))
                continue;
            Add(name);
            // Pure upstream is limited to the five ISP patterns, not Telia.
            if (official == null && name != "Telia")
                official = name;
        }

        // Stem matches for mixed handoff names (zayo-telia) and TELUS/Wolfpaw
        // hostnames that omit the full corporate domain. Anchored to label boundaries.
        if (LabelContains(host, "zayo"))
            Add("Zayo");
        if (LabelContains(host, "cogent"))
            Add("Cogent");
        if (LabelContains(host, "telus"))
            Add("TELUS");
        if (LabelContains(host, "wolfpaw"))
            Add("Wolfpaw");
        if (LabelContains(host, "telia") || LabelContains(host, "twelve99"))
            Add("Telia");

        string? pure = null;
        if (official != null)
        {
            var mixed = false;
            foreach (var name in networks)
            {
                if (!string.Equals(name, official, StringComparison.Ordinal))
                {
                    mixed = true;
                    break;
                }
            }

            if (!mixed)
                pure = official;
        }

        return new HopClass(networks, pure);
    }

    /// <summary>
    /// True when host equals domain or ends with ".domain".
    /// </summary>
    public static bool HostMatchesDomain(string host, string domain)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain))
            return false;

        if (host.Equals(domain, StringComparison.Ordinal))
            return true;

        return host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a '.' / '-' separated label equals stem or starts with stem
    /// followed by a non-letter (for example zayo-telia, telus2).
    /// </summary>
    public static bool LabelContains(string host, string stem)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(stem))
            return false;

        var index = 0;
        while (index < host.Length)
        {
            var nextDot = host.IndexOf('.', index);
            var nextDash = host.IndexOf('-', index);
            var end = host.Length;
            if (nextDot >= 0)
                end = Math.Min(end, nextDot);
            if (nextDash >= 0)
                end = Math.Min(end, nextDash);

            var length = end - index;
            if (length >= stem.Length &&
                string.Compare(host, index, stem, 0, stem.Length, StringComparison.Ordinal) == 0 &&
                (length == stem.Length || !char.IsLetter(host[index + stem.Length])))
            {
                return true;
            }

            index = end + 1;
        }

        return false;
    }

    public static string DescribeHop(string? hostname)
    {
        var hop = ClassifyHop(hostname);
        if (hop.Networks.Count == 0)
            return "";
        return string.Join(" + ", hop.Networks);
    }

    public static string BuildFullPath(IReadOnlyList<HopObservation> hops)
    {
        if (hops.Count == 0)
            return "";

        var parts = new string[hops.Count];
        for (var i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            parts[i] = hop.Responded && !string.IsNullOrWhiteSpace(hop.Ip) && hop.Ip != "*"
                ? hop.Ip
                : "*";
        }

        return string.Join(" > ", parts);
    }

    public static List<HopObservation> HopsFromFullPath(string? fullPath)
    {
        var list = new List<HopObservation>();
        if (string.IsNullOrWhiteSpace(fullPath))
            return list;

        var parts = fullPath.Split(" > ", StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            var ip = parts[i].Trim();
            var responded = ip.Length > 0 && ip != "*";
            list.Add(new HopObservation(i + 1, responded ? ip : "*", "", null, responded, false));
        }

        return list;
    }
}
