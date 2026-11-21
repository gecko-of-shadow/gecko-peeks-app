using System.Net;
using System.Net.Sockets;

namespace GeckoPeeks.Cloud;

/// <summary>
/// Decides which hop addresses may leave the PC for an ASN lookup. Only public unicast
/// addresses go out. Home, carrier-grade NAT, loopback, link-local, unique-local, multicast,
/// documentation and reserved ranges stay local, and so do the first hop and the gateway.
/// </summary>
public static class PublicIp
{
    public static bool IsPublic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out var address))
            return false;
        return IsPublic(address);
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicV4(address.GetAddressBytes());
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsPublicV6(address);
        return false;
    }

    /// <summary>
    /// The public hop addresses to look up, in hop order, without repeats. Hop 1 and the
    /// gateway are always left out, even when they are public.
    /// </summary>
    public static IReadOnlyList<string> HopsToLookUp(IEnumerable<(int Number, string Ip, bool Responded)> hops, string? gateway)
    {
        var gatewayText = gateway?.Trim() ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var (number, ip, responded) in hops)
        {
            if (number <= 1 || !responded || string.IsNullOrWhiteSpace(ip))
                continue;
            var trimmed = ip.Trim();
            if (gatewayText.Length > 0 && string.Equals(trimmed, gatewayText, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsPublic(trimmed) || !seen.Add(trimmed))
                continue;
            list.Add(trimmed);
        }

        return list;
    }

    private static bool IsPublicV4(byte[] b)
    {
        int a0 = b[0], a1 = b[1], a2 = b[2];
        if (a0 == 0) return false;                                  // 0.0.0.0/8 this network
        if (a0 == 10) return false;                                 // 10/8 private
        if (a0 == 100 && (a1 & 0xC0) == 64) return false;           // 100.64/10 carrier-grade NAT
        if (a0 == 127) return false;                                // 127/8 loopback
        if (a0 == 169 && a1 == 254) return false;                   // 169.254/16 link-local
        if (a0 == 172 && (a1 & 0xF0) == 16) return false;           // 172.16/12 private
        if (a0 == 192 && a1 == 0 && a2 == 0) return false;          // 192.0.0/24 protocol assignments
        if (a0 == 192 && a1 == 0 && a2 == 2) return false;          // 192.0.2/24 documentation
        if (a0 == 192 && a1 == 88 && a2 == 99) return false;        // 192.88.99/24 old 6to4 relay
        if (a0 == 192 && a1 == 168) return false;                   // 192.168/16 private
        if (a0 == 198 && (a1 & 0xFE) == 18) return false;           // 198.18/15 benchmarking
        if (a0 == 198 && a1 == 51 && a2 == 100) return false;       // 198.51.100/24 documentation
        if (a0 == 203 && a1 == 0 && a2 == 113) return false;        // 203.0.113/24 documentation
        if (a0 >= 224) return false;                                // multicast, reserved, broadcast
        return true;
    }

    private static bool IsPublicV6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Loopback))
            return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;

        var b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC) return false;                    // fc00::/7 unique local
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentation
        if (b[0] == 0x00) return false;                             // ::/8 reserved (also v4-compatible)
        if ((b[0] & 0xE0) != 0x20) return false;                    // only 2000::/3 is global unicast
        return true;
    }
}
