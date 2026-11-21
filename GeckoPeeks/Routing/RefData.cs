using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeckoPeeks.Routing;

public sealed record RegionInfo(
    string Id, string ShortName, string Name,
    string LolStatusId, string ValorantShard,
    string TargetIp, string TargetKind, string TargetBasis, bool TargetInRegion,
    string ServerCity, bool CityConfirmed, double? Lat, double? Lon)
{
    /// <summary>"Riot Direct edge (AS6507)" for anycast, "Riot platform address (AS6507)" for dns.</summary>
    public string TargetLabel => TargetKind == "dns" ? RefData.PlatformLabel : RefData.EdgeLabel;
}

public sealed record ProviderInfo(
    string Key, string Name, IReadOnlyList<long> Asns, IReadOnlyList<string> Domains,
    IReadOnlyList<string> Stems, string? HostRegex, string CityGroup, string Tier);

public sealed record CityCodeRow(string Group, string Code, string City, string Country, double Lat, double Lon);

/// <summary>
/// Reference data: Riot regions and their trace targets, transit providers (names, ASNs,
/// hostname rules) and router city codes. One JSON file (cloud/worker/data/refdata.json) is
/// embedded in the app and served by the Worker. <see cref="Current"/> is the copy in use.
/// </summary>
public sealed class RefData
{
    public const string ResourceName = "GeckoPeeks.RefData/refdata.json";
    public const string EdgeLabel = "Riot Direct edge (AS6507)";
    public const string PlatformLabel = "Riot platform address (AS6507)";

    public const int MaxBytes = 512 * 1024;

    /// <summary>City-code groups that use IATA-style codes. A lookup without a group only uses these.</summary>
    internal static readonly string[] IataGroups = { "iata", "zayo", "cogent", "he", "gtt", "riotdirect" };

    private static readonly Regex RegionIdPattern = new("^[a-z]{2,3}[0-9]?$", RegexOptions.CultureInvariant);
    private static readonly Lazy<RefData> BuiltInLazy = new(LoadBuiltIn, LazyThreadSafetyMode.ExecutionAndPublication);
    private static RefData? _current;

    private readonly Dictionary<string, RegionInfo> _regionById;
    private readonly Dictionary<long, ProviderInfo> _providerByAsn;
    private readonly Dictionary<string, ProviderInfo> _providerByName;
    private readonly Dictionary<(string Group, string Code), CityCodeRow> _cityByGroup;
    private readonly Dictionary<string, CityCodeRow> _cityIata;
    private readonly Dictionary<string, Regex?> _hostRegexes = new(StringComparer.Ordinal);

    private RefData(
        int schema, string version, string defaultRegionId,
        IReadOnlyList<RegionInfo> regions,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> countryToRegion,
        IReadOnlyDictionary<string, string> continentToRegion,
        IReadOnlyList<ProviderInfo> providers,
        IReadOnlyList<CityCodeRow> cityCodes)
    {
        Schema = schema;
        Version = version;
        DefaultRegionId = defaultRegionId;
        Regions = regions;
        Aliases = aliases;
        CountryToRegion = countryToRegion;
        ContinentToRegion = continentToRegion;
        Providers = providers;
        CityCodes = cityCodes;

        _regionById = regions.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        _providerByAsn = new Dictionary<long, ProviderInfo>();
        _providerByName = new Dictionary<string, ProviderInfo>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            foreach (var asn in provider.Asns)
                _providerByAsn.TryAdd(asn, provider);
            _providerByName.TryAdd(provider.Name, provider);
            if (provider.HostRegex != null)
                _hostRegexes[provider.Key] = BuildRegex(provider.HostRegex);
        }

        _cityByGroup = new Dictionary<(string, string), CityCodeRow>();
        _cityIata = new Dictionary<string, CityCodeRow>(StringComparer.Ordinal);
        foreach (var row in cityCodes)
        {
            _cityByGroup.TryAdd((row.Group, row.Code), row);
            if (IataGroups.Contains(row.Group))
                _cityIata.TryAdd(row.Code, row);
        }

        DefaultRegion = _regionById[defaultRegionId];
    }

    public int Schema { get; }
    public string Version { get; }
    public string DefaultRegionId { get; }
    public IReadOnlyList<RegionInfo> Regions { get; }
    public IReadOnlyDictionary<string, string> Aliases { get; }
    public IReadOnlyDictionary<string, string> CountryToRegion { get; }
    public IReadOnlyDictionary<string, string> ContinentToRegion { get; }
    public IReadOnlyList<ProviderInfo> Providers { get; }
    public IReadOnlyList<CityCodeRow> CityCodes { get; }

    /// <summary>The copy embedded in the running assembly. Never null. If the resource is
    /// missing or broken, a hard-coded minimal set (na1 only, the 10 providers, no city codes).</summary>
    public static RefData BuiltIn => BuiltInLazy.Value;

    /// <summary>Starts as BuiltIn. Thread-safe read.</summary>
    public static RefData Current => Volatile.Read(ref _current) ?? BuiltIn;

    public static void SetCurrent(RefData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Volatile.Write(ref _current, data);
    }

    public RegionInfo DefaultRegion { get; }

    public RegionInfo? FindRegion(string? idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias))
            return null;
        var id = idOrAlias.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(id, out var canonical))
            id = canonical;
        return _regionById.TryGetValue(id, out var region) ? region : null;
    }

    /// <summary>Country, then continent, then the default region. "XX" and "T1" are no country.</summary>
    public RegionInfo RegionForLocation(string? country, string? continent) =>
        RegionForLocation(country, continent, out _);

    /// <param name="basis">"country", "continent" or "default".</param>
    internal RegionInfo RegionForLocation(string? country, string? continent, out string basis)
    {
        var cc = (country ?? "").Trim().ToUpperInvariant();
        if (cc.Length == 2 && cc != "XX" && cc != "T1"
            && CountryToRegion.TryGetValue(cc, out var byCountry) && FindRegion(byCountry) is { } r1)
        {
            basis = "country";
            return r1;
        }

        var ct = (continent ?? "").Trim().ToUpperInvariant();
        if (ct.Length == 2 && ContinentToRegion.TryGetValue(ct, out var byContinent) && FindRegion(byContinent) is { } r2)
        {
            basis = "continent";
            return r2;
        }

        basis = "default";
        return DefaultRegion;
    }

    public ProviderInfo? ProviderForAsn(long asn) => _providerByAsn.TryGetValue(asn, out var p) ? p : null;

    /// <summary>Domain-suffix match: the host equals a domain or ends with "." + domain.</summary>
    public ProviderInfo? ProviderForHost(string? hostname)
    {
        var host = NormalizeHost(hostname);
        if (host.Length == 0)
            return null;
        foreach (var provider in Providers)
        {
            foreach (var domain in provider.Domains)
            {
                if (RouteClassifier.HostMatchesDomain(host, domain))
                    return provider;
            }
        }

        return null;
    }

    public ProviderInfo? ProviderByName(string? name) =>
        name != null && _providerByName.TryGetValue(name, out var p) ? p : null;

    /// <summary>
    /// With a group: the row of that group. Without one: the first row with that code in an
    /// IATA-style group. Carrier-specific codes (Telia, NTT, Lumen) are never matched without their group.
    /// </summary>
    public CityCodeRow? FindCity(string? code, string? group)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var c = code.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(group))
            return _cityByGroup.TryGetValue((group.Trim().ToLowerInvariant(), c), out var row) ? row : null;
        return _cityIata.TryGetValue(c, out var iata) ? iata : null;
    }

    /// <summary>The compiled host pattern of a provider, or null. Built once per instance.</summary>
    internal Regex? HostRegexFor(ProviderInfo provider) =>
        _hostRegexes.TryGetValue(provider.Key, out var regex) ? regex : null;

    internal static string NormalizeHost(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return "";
        return hostname.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static Regex? BuildRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- parsing ----

    public static bool TryParse(string json, out RefData? data, out string error)
    {
        data = null;
        error = "";
        try
        {
            if (string.IsNullOrEmpty(json))
                return Fail("empty", out error);
            if (System.Text.Encoding.UTF8.GetByteCount(json) >= MaxBytes)
                return Fail("too large", out error);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("not an object", out error);

            if (!root.TryGetProperty("schema", out var schemaEl) || schemaEl.ValueKind != JsonValueKind.Number
                || !schemaEl.TryGetInt32(out var schema) || schema != 1)
                return Fail("schema is not 1", out error);

            var version = Str(root, "version");
            if (string.IsNullOrWhiteSpace(version))
                return Fail("version is empty", out error);

            if (!root.TryGetProperty("regions", out var regionsEl) || regionsEl.ValueKind != JsonValueKind.Array)
                return Fail("regions missing", out error);

            var regions = new List<RegionInfo>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in regionsEl.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object)
                    return Fail("region is not an object", out error);
                var id = Str(r, "id") ?? "";
                if (!RegionIdPattern.IsMatch(id))
                    return Fail("bad region id: " + id, out error);
                if (!ids.Add(id))
                    return Fail("duplicate region id: " + id, out error);
                var targetIp = Str(r, "targetIp") ?? "";
                if (!IsPublicV4(targetIp))
                    return Fail("targetIp is not a public IPv4 address: " + id, out error);
                var kind = Str(r, "targetKind") ?? "anycast";
                if (kind != "anycast" && kind != "dns")
                    return Fail("bad targetKind: " + id, out error);
                var city = Str(r, "serverCity") ?? "";
                double? lat = Num(r, "lat");
                double? lon = Num(r, "lon");
                if (lat == null || lon == null)
                {
                    lat = null;
                    lon = null;
                }

                regions.Add(new RegionInfo(
                    id,
                    Str(r, "shortName") ?? id.ToUpperInvariant(),
                    Str(r, "name") ?? id,
                    Str(r, "lolStatusId") ?? id,
                    Str(r, "valorantShard") ?? "",
                    targetIp,
                    kind,
                    Str(r, "targetBasis") ?? "",
                    Bool(r, "targetInRegion"),
                    city,
                    Bool(r, "cityConfirmed"),
                    lat,
                    lon));
            }

            if (regions.Count == 0)
                return Fail("no regions", out error);

            var defaultRegion = Str(root, "defaultRegion") ?? "";
            if (!ids.Contains(defaultRegion))
                return Fail("defaultRegion is not a region id", out error);

            if (!TryMap(root, "aliases", ids, false, out var aliases, out error)
                || !TryMap(root, "countryToRegion", ids, true, out var countries, out error)
                || !TryMap(root, "continentToRegion", ids, true, out var continents, out error))
                return false;

            if (!root.TryGetProperty("providers", out var providersEl) || providersEl.ValueKind != JsonValueKind.Array)
                return Fail("providers missing", out error);

            var providers = new List<ProviderInfo>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var asnsSeen = new HashSet<long>();
            foreach (var p in providersEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                    return Fail("provider is not an object", out error);
                var key = Str(p, "key") ?? "";
                var name = Str(p, "name") ?? "";
                if (key.Length == 0 || name.Length == 0)
                    return Fail("provider key or name is empty", out error);
                if (!keys.Add(key))
                    return Fail("duplicate provider key: " + key, out error);

                var asns = new List<long>();
                if (p.TryGetProperty("asns", out var asnsEl) && asnsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in asnsEl.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out var asn) || asn < 1 || asn > 4294967295)
                            return Fail("bad ASN in provider " + key, out error);
                        if (!asnsSeen.Add(asn))
                            return Fail("ASN in two providers: " + asn.ToString(CultureInfo.InvariantCulture), out error);
                        asns.Add(asn);
                    }
                }

                var hostRegex = Str(p, "hostRegex");
                if (hostRegex != null)
                {
                    try
                    {
                        _ = new Regex(hostRegex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
                    }
                    catch (ArgumentException)
                    {
                        return Fail("hostRegex does not compile: " + key, out error);
                    }
                }

                var tier = Str(p, "tier") ?? "upstream";
                if (tier != "upstream" && tier != "backbone" && tier != "destination")
                    return Fail("bad tier: " + key, out error);

                providers.Add(new ProviderInfo(
                    key,
                    name,
                    asns,
                    StrList(p, "domains").Select(d => d.ToLowerInvariant()).ToList(),
                    StrList(p, "stems").Select(s => s.ToLowerInvariant()).ToList(),
                    hostRegex,
                    (Str(p, "cityGroup") ?? key).ToLowerInvariant(),
                    tier));
            }

            var cities = new List<CityCodeRow>();
            if (root.TryGetProperty("cityCodes", out var citiesEl) && citiesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in citiesEl.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object)
                        continue;
                    var group = (Str(c, "group") ?? "").ToLowerInvariant();
                    var code = (Str(c, "code") ?? "").ToLowerInvariant();
                    var city = Str(c, "city") ?? "";
                    if (group.Length == 0 || code.Length == 0 || city.Length == 0 || !code.All(ch => ch is >= 'a' and <= 'z'))
                        continue;
                    if (Num(c, "lat") is not { } lat || Num(c, "lon") is not { } lon)
                        continue;
                    cities.Add(new CityCodeRow(group, code, city, Str(c, "country") ?? "", lat, lon));
                }
            }

            data = new RefData(1, version!, defaultRegion, regions, aliases, countries, continents, providers, cities);
            return true;
        }
        catch (JsonException)
        {
            return Fail("broken JSON", out error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        {
            return Fail("invalid data", out error);
        }
    }

    private static bool TryMap(JsonElement root, string name, HashSet<string> ids, bool upperKeys,
        out IReadOnlyDictionary<string, string> map, out string error)
    {
        var dict = new Dictionary<string, string>(upperKeys ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        map = dict;
        error = "";
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind != JsonValueKind.Object)
            return Fail(name + " is not an object", out error);
        foreach (var prop in el.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
            if (!ids.Contains(value))
                return Fail(name + " value is not a region id: " + prop.Name, out error);
            var key = upperKeys ? prop.Name.ToUpperInvariant() : prop.Name.ToLowerInvariant();
            dict[key] = value;
        }

        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim());
            }
        }

        return list;
    }

    private static bool IsPublicV4(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (text.Count(ch => ch == '.') != 3)
            return false;
        var b = address.GetAddressBytes();
        int a0 = b[0], a1 = b[1], a2 = b[2];
        if (a0 == 0 || a0 == 10 || a0 == 127 || a0 >= 224) return false;
        if (a0 == 100 && (a1 & 0xC0) == 64) return false;
        if (a0 == 169 && a1 == 254) return false;
        if (a0 == 172 && (a1 & 0xF0) == 16) return false;
        if (a0 == 192 && a1 == 168) return false;
        if (a0 == 192 && a1 == 0 && (a2 == 0 || a2 == 2)) return false;
        if (a0 == 198 && (a1 & 0xFE) == 18) return false;
        if (a0 == 198 && a1 == 51 && a2 == 100) return false;
        if (a0 == 203 && a1 == 0 && a2 == 113) return false;
        return true;
    }

    // ---- built-in copy ----

    private static RefData LoadBuiltIn()
    {
        try
        {
            using var stream = typeof(RefData).Assembly.GetManifestResourceStream(ResourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                if (TryParse(reader.ReadToEnd(), out var data, out _) && data != null)
                    return data;
            }
        }
        catch
        {
            // Fall through to the hard-coded set.
        }

        return Minimal();
    }

    /// <summary>na1 only, the 10 providers, no city codes. Used when the embedded file is missing or broken.</summary>
    internal static RefData Minimal()
    {
        var regions = new List<RegionInfo>
        {
            new("na1", "NA", "North America", "na1", "na", "192.207.0.5", "anycast",
                "Riot Direct anycast prefix 192.207.0.0/24 (AS6507)", true, "Chicago", true, 41.88, -87.63),
        };

        static ProviderInfo P(string key, string name, long[] asns, string[] domains, string[] stems, string? regex, string group, string tier) =>
            new(key, name, asns, domains, stems, regex, group, tier);

        var providers = new List<ProviderInfo>
        {
            P("zayo", "Zayo", new long[] { 6461 }, new[] { "zayo.com", "above.net" }, new[] { "zayo" },
                @"^.+\.([a-z]{3})\d+\.[a-z]{2}\.[a-z]{3}\.zayo\.com$", "zayo", "upstream"),
            P("cogent", "Cogent", new long[] { 174 }, new[] { "cogentco.com" }, new[] { "cogent" },
                @"^.+\.([a-z]{3})\d+\.[a-z\d]+\.cogentco\.com$", "cogent", "upstream"),
            P("telus", "TELUS", new long[] { 852 }, new[] { "telus.net", "telus.com" }, new[] { "telus" },
                @"^([a-z]{6})[a-z\d]{6}\.bb\.telus\.com$", "telus", "upstream"),
            P("wolfpaw", "Wolfpaw", new long[] { 30500 }, new[] { "wolfpaw.net" }, new[] { "wolfpaw" },
                @"^.+\.([a-z]{3})\.core\.wolfpaw\.net$", "iata", "upstream"),
            P("he", "HE", new long[] { 6939 }, new[] { "he.net" }, Array.Empty<string>(),
                @"^.+\.([a-z]{3})\d+\.he\.net$", "he", "upstream"),
            P("lumen", "Lumen", new long[] { 3356, 3549, 209 }, new[] { "level3.net", "lumen.tech" }, Array.Empty<string>(),
                @"^.+\.([a-z]+)\d+\.(?:level3\.net|net\.lumen\.tech)$", "lumen", "upstream"),
            P("ntt", "NTT", new long[] { 2914 }, new[] { "ntt.net" }, Array.Empty<string>(),
                @"^.+\.([a-z]{6})\d+\.[a-z]{2}\.[a-z]{2}\.gin\.ntt\.net$", "ntt", "upstream"),
            P("gtt", "GTT", new long[] { 3257 }, new[] { "gtt.net" }, Array.Empty<string>(),
                @"^.+-([a-z]{3})\d+\.ip4\.gtt\.net$", "gtt", "upstream"),
            P("telia", "Telia", new long[] { 1299 }, new[] { "twelve99.net", "twelve99-cust.net", "telia.net", "telia.com" },
                new[] { "telia", "twelve99" }, @"^([a-z]{3,4})-[a-z0-9-]+\.ip\.twelve99\.net$", "telia", "backbone"),
            P("riotdirect", "Riot Direct", new long[] { 6507 }, new[] { "riotdirect.net" }, new[] { "riotdirect" },
                @"^.+\.([a-z]{3})\d+\.riotdirect\.net$", "riotdirect", "destination"),
        };

        var empty = new Dictionary<string, string>();
        return new RefData(1, "builtin-minimal", "na1", regions, empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            providers, Array.Empty<CityCodeRow>());
    }
}
