using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeckoPeeks.Routing;

/// <summary>What a good trace to a service target proves (research report, section 2).</summary>
public enum TargetClass
{
    /// <summary>The target is inside the service's own network.</summary>
    Owned,
    /// <summary>The target is a CDN or cloud edge in front of the service.</summary>
    CdnEdge,
    /// <summary>Only the path this PC shares with another device (a console) is measured.</summary>
    SharedPath,
    /// <summary>No network target; only the vendor's status feed.</summary>
    StatusOnly,
}

public sealed record ServiceOwner(string Name, IReadOnlyList<long> Asns);

/// <param name="Kind">"sdr-nearest-pop" (Valve relay list) or "hostname".</param>
/// <param name="AppId">For sdr-nearest-pop: the Valve app id whose relay list the Worker serves.</param>
/// <param name="Host">For hostname: the name to resolve before each test.</param>
public sealed record ServiceTarget(string Kind, int? AppId, string? Host, bool IcmpExpected, string Label);

/// <param name="Feed">The Worker feed id (/v1/service-status?id=...), or null.</param>
public sealed record ServiceStatusRef(string? Feed, IReadOnlyDictionary<string, string> FeedByContinent);

/// <summary>One service of the refdata "services" block.</summary>
/// <param name="Processes">Empty unless <paramref name="ProcessesVerified"/> is true: unverified names are never used.</param>
public sealed record ServiceInfo(
    string Id,
    string Name,
    string Group,
    TargetClass Class,
    ServiceOwner Owner,
    ServiceTarget Target,
    string Caveat,
    IReadOnlyList<string> Processes,
    bool ProcessesVerified,
    ServiceStatusRef? Status,
    string StatusNote,
    IReadOnlyList<string> Requires)
{
    public const string KindSdr = "sdr-nearest-pop";
    public const string KindHostname = "hostname";

    /// <summary>The class in plain words, for every card and every export.</summary>
    public string ClassText => ClassWords(Class);

    public static string ClassWords(TargetClass c) => c switch
    {
        TargetClass.Owned => "Owned network",
        TargetClass.CdnEdge => "CDN edge",
        TargetClass.SharedPath => "Shared path only",
        _ => "Status only",
    };

    public static string ClassId(TargetClass c) => c switch
    {
        TargetClass.Owned => "owned",
        TargetClass.CdnEdge => "cdn-edge",
        TargetClass.SharedPath => "shared-path",
        _ => "status-only",
    };
}

/// <summary>
/// Reads the optional refdata "services" block. A bad or unknown entry is skipped and never fails
/// the file, so a services problem can never take Riot's data away. An entry that "requires" a
/// feature this build does not have, or that has a target kind this build cannot measure, is
/// skipped too: an older app never shows a service it cannot show correctly.
/// </summary>
public static class ServiceCatalog
{
    /// <summary>Target kinds this build can measure.</summary>
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceInfo.KindSdr,
    };

    /// <summary>Features this build has. A service that requires another one is not shown.</summary>
    public static readonly IReadOnlySet<string> Features = new HashSet<string>(StringComparer.Ordinal)
    {
        "services-v1",
        ServiceInfo.KindSdr,
    };

    public const int MaxServices = 40;

    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex FeedPattern = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostPattern = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProcessPattern = new(@"^[A-Za-z0-9 ._-]{1,60}\.exe$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ServiceInfo> Parse(JsonElement root, IReadOnlySet<string>? features = null)
    {
        var list = new List<ServiceInfo>();
        if (!root.TryGetProperty("services", out var el) || el.ValueKind != JsonValueKind.Array)
            return list;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in el.EnumerateArray())
        {
            if (list.Count >= MaxServices)
                break;
            if (TryOne(s, features ?? Features) is { } info && ids.Add(info.Id))
                list.Add(info);
        }

        return list;
    }

    private static ServiceInfo? TryOne(JsonElement s, IReadOnlySet<string> features)
    {
        if (s.ValueKind != JsonValueKind.Object)
            return null;
        var id = Str(s, "id") ?? "";
        var name = Str(s, "name") ?? "";
        if (!IdPattern.IsMatch(id) || name.Length is 0 or > 60)
            return null;

        if (!s.TryGetProperty("requires", out var reqEl) || reqEl.ValueKind != JsonValueKind.Array)
            return null;
        var requires = new List<string>();
        foreach (var r in reqEl.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.String || r.GetString() is not { } feature || !features.Contains(feature))
                return null;
            requires.Add(feature);
        }

        if (requires.Count == 0)
            return null;

        TargetClass? cls = Str(s, "targetClass") switch
        {
            "owned" => TargetClass.Owned,
            "cdn-edge" => TargetClass.CdnEdge,
            "shared-path" => TargetClass.SharedPath,
            "status-only" => TargetClass.StatusOnly,
            _ => null,
        };
        if (cls == null)
            return null;

        if (!s.TryGetProperty("owner", out var ownerEl) || ownerEl.ValueKind != JsonValueKind.Object)
            return null;
        var ownerName = Str(ownerEl, "name") ?? "";
        if (ownerName.Length is 0 or > 60)
            return null;
        var asns = new List<long>();
        if (ownerEl.TryGetProperty("asns", out var asnsEl) && asnsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in asnsEl.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out var asn) || asn < 1 || asn > 4294967295)
                    return null;
                asns.Add(asn);
            }
        }

        if (!s.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object)
            return null;
        var kind = Str(t, "kind") ?? "";
        if (!SupportedKinds.Contains(kind))
            return null;
        var label = Str(t, "label") ?? "";
        if (label.Length is 0 or > 120)
            return null;
        int? appId = null;
        string? host = null;
        if (kind == ServiceInfo.KindSdr)
        {
            if (!t.TryGetProperty("appId", out var appEl) || appEl.ValueKind != JsonValueKind.Number
                || !appEl.TryGetInt32(out var a) || a is < 1 or > 9999999)
                return null;
            appId = a;
        }
        else if (kind == ServiceInfo.KindHostname)
        {
            host = (Str(t, "host") ?? "").Trim().ToLowerInvariant();
            if (!HostPattern.IsMatch(host))
                return null;
        }

        var icmp = t.TryGetProperty("icmpExpected", out var ie) && ie.ValueKind == JsonValueKind.True;

        ServiceStatusRef? status = null;
        if (s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            var feed = Str(st, "feed");
            if (feed != null && !FeedPattern.IsMatch(feed))
                return null;
            var byContinent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (st.TryGetProperty("feedByContinent", out var bc) && bc.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in bc.EnumerateObject())
                {
                    var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                    if (p.Name.Length != 2 || !FeedPattern.IsMatch(v))
                        return null;
                    byContinent[p.Name.ToUpperInvariant()] = v;
                }
            }

            if (feed == null && byContinent.Count == 0)
                return null;
            status = new ServiceStatusRef(feed, byContinent);
        }

        var verified = s.TryGetProperty("processesVerified", out var pv) && pv.ValueKind == JsonValueKind.True;
        var processes = new List<string>();
        if (verified && s.TryGetProperty("processes", out var pe) && pe.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pe.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String && p.GetString() is { } n && ProcessPattern.IsMatch(n))
                    processes.Add(n);
            }
        }

        return new ServiceInfo(
            id,
            name,
            (Str(s, "group") ?? "pc") is "console" ? "console" : "pc",
            cls.Value,
            new ServiceOwner(ownerName, asns),
            new ServiceTarget(kind, appId, host, icmp, label),
            Cap(Str(s, "caveat"), 300),
            processes,
            verified,
            status,
            Cap(Str(s, "statusNote"), 200),
            requires);
    }

    private static string Cap(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
