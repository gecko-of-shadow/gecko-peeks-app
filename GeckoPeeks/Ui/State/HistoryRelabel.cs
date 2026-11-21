using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Ui.State;

/// <summary>
/// Older rows were saved as "Unknown" when the PC's DNS could not name the routers (v2.0 and
/// before). Each row still has its hop addresses in FullPath. This names such a row again from
/// those addresses, with today's router names and network numbers, for display only. The log
/// file is never changed.
///
/// Facts first: a row is named only when every router a live test would look up was looked up
/// (see <see cref="Lookups"/>). Otherwise it stays Unknown. Hop 1 and the gateway are never sent
/// out and never count, exactly as in a live test (<see cref="PublicIp.HopsToLookUp"/>).
/// </summary>
public static class HistoryRelabel
{
    /// <summary>At most this many router addresses are looked up for one history load.</summary>
    public const int MaxLookups = 64;

    /// <summary>
    /// What the lookups returned. <c>Done</c> holds only routers that were answered: their network
    /// number came back and their name lookup finished. A router that timed out or failed is not
    /// done, so a row that uses it stays Unknown.
    /// </summary>
    public sealed record Lookups(
        IReadOnlyDictionary<string, string> HostByIp,
        IReadOnlyDictionary<string, long> AsnByIp,
        IReadOnlySet<string> Done)
    {
        public static readonly Lookups None = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for a row whose upstream was not named when it was saved. Rows from before the Gateway
    /// column (v1.0.12 and older) are left alone: without the gateway the app cannot be sure which
    /// address must stay on the PC.
    /// </summary>
    public static bool NeedsName(RouteRow row) =>
        row != null
        && (string.IsNullOrWhiteSpace(row.Upstream) || string.Equals(row.Upstream.Trim(), RouteClassifier.Unknown, StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(row.FullPath)
        && !string.IsNullOrWhiteSpace(row.Gateway);

    /// <summary>
    /// The responded hops of the rows that need a name that are NOT sent out (hop 1, the gateway,
    /// private and carrier-grade NAT addresses). A live test names them with this PC's DNS only;
    /// history does the same. At most <see cref="MaxLookups"/>.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses(IReadOnlyList<RouteRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        for (var i = rows.Count - 1; i >= 0 && list.Count < MaxLookups; i--)
        {
            if (!NeedsName(rows[i]))
                continue;
            var outside = new HashSet<string>(RowLookups(rows[i]), StringComparer.OrdinalIgnoreCase);
            foreach (var ip in PathIps(rows[i].FullPath))
            {
                // Only real addresses: a hand-edited CSV must never turn into a DNS query for its text.
                if (ip == "*" || outside.Contains(ip) || !System.Net.IPAddress.TryParse(ip, out _) || !seen.Add(ip))
                    continue;
                list.Add(ip);
                if (list.Count >= MaxLookups)
                    break;
            }
        }

        return list;
    }

    /// <summary>The hop addresses in a saved FullPath ("a &gt; b &gt; * &gt; c"), in order. "*" is a silent hop.</summary>
    public static IReadOnlyList<string> PathIps(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return Array.Empty<string>();
        return fullPath.Split('>', StringSplitOptions.TrimEntries).Select(p => p.Length == 0 ? "*" : p).ToArray();
    }

    /// <summary>
    /// The routers of one saved row that a live test would look up: public, past hop 1, not the
    /// gateway. The same rule as a live test, so nothing else ever leaves the PC.
    /// </summary>
    public static IReadOnlyList<string> RowLookups(RouteRow row)
    {
        var ips = PathIps(row.FullPath);
        return PublicIp.HopsToLookUp(ips.Select((ip, i) => (i + 1, ip, ip != "*")), row.Gateway);
    }

    /// <summary>
    /// The distinct routers still to look up for the rows that need a name, newest rows first,
    /// at most <see cref="MaxLookups"/>. <paramref name="skip"/> marks routers already answered or
    /// given up; they do not use up the cap, so later loads reach older rows. A row whose
    /// remaining routers do not all fit is left for a later load.
    /// </summary>
    public static IReadOnlyList<string> AddressesToLookUp(IReadOnlyList<RouteRow> rows, Func<string, bool>? skip = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!NeedsName(rows[i]))
                continue;
            var fresh = RowLookups(rows[i]).Where(ip => !seen.Contains(ip) && (skip == null || !skip(ip))).ToList();
            if (list.Count + fresh.Count > MaxLookups)
                continue;
            foreach (var ip in fresh)
            {
                seen.Add(ip);
                list.Add(ip);
            }
        }

        return list;
    }

    /// <summary>
    /// The classifier's answer for a row that needs a name. Null when the row does not need one,
    /// when any of its routers was not looked up, or when the routers still match no known network.
    /// </summary>
    public static RouteClassifier.Features? Name(RouteRow row, Lookups lookups, RefData refData)
    {
        if (!NeedsName(row))
            return null;

        var wanted = RowLookups(row);
        if (wanted.Count == 0 || wanted.Any(ip => !lookups.Done.Contains(ip)))
            return null;

        // Only the routers a live test would look up carry a name or a network number.
        var allowed = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var ips = PathIps(row.FullPath);
        var hops = new List<HopObservation>(ips.Count);
        for (var i = 0; i < ips.Count; i++)
        {
            var ip = ips[i];
            var silent = ip == "*";
            // Names of hop 1, the gateway and private hops come only from this PC's DNS (see
            // LocalAddresses), the same as in a live test; they never decide which routers must be done.
            var host = !silent && lookups.HostByIp.TryGetValue(ip, out var h) ? h ?? "" : "";
            hops.Add(new HopObservation(i + 1, silent ? "*" : ip, host, null, !silent, false));
        }

        var asns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, asn) in lookups.AsnByIp)
        {
            if (allowed.Contains(ip))
                asns[ip] = asn;
        }
        var features = RouteClassifier.Classify(hops, asns, refData);
        return string.Equals(features.Upstream, RouteClassifier.Unknown, StringComparison.Ordinal) ? null : features;
    }
}
