using System.Net;
using System.Net.Sockets;

namespace OwlSamba.Core;

public static class IpUtil
{
    /// <summary>
    /// Canonical string form of an IP, or null when the value is not an IP address
    /// (e.g. "-" which Windows writes when the source address is unknown).
    /// IPv4-mapped IPv6 addresses are unwrapped and IPv6 scope ids are dropped.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().Trim('[', ']');
        if (s == "-" ) return null;
        var pct = s.IndexOf('%');
        if (pct > 0) s = s[..pct];
        if (!IPAddress.TryParse(s, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) ip.ScopeId = 0;
        return ip.ToString();
    }

    public static bool IsLoopback(string ip) =>
        IPAddress.TryParse(ip, out var a) && IPAddress.IsLoopback(a);

    public static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT
        }
        return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
    }

    public static bool TryParseCidr(string value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var parts = value.Trim().Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var net)) return false;
        if (!int.TryParse(parts[1], out prefix)) return false;
        var max = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > max) return false;
        network = net;
        return true;
    }

    public static bool InCidr(string ip, string cidr)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (!TryParseCidr(cidr, out var net, out var prefix)) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (addr.AddressFamily != net.AddressFamily) return false;

        var a = addr.GetAddressBytes();
        var n = net.GetAddressBytes();
        int fullBytes = prefix / 8, rem = prefix % 8;
        for (int i = 0; i < fullBytes; i++)
            if (a[i] != n[i]) return false;
        if (rem == 0) return true;
        int mask = 0xFF << (8 - rem) & 0xFF;
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }

    /// <summary>Classifies a whitelist value as ip, cidr or domain; null if invalid.</summary>
    public static string? ClassifyWhitelistValue(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return null;
        if (v.Contains('/')) return TryParseCidr(v, out _, out _) ? "cidr" : null;
        if (Normalize(v) != null) return "ip";
        // Hostname: letters, digits, dots and hyphens.
        return Uri.CheckHostName(v) == UriHostNameType.Dns ? "domain" : null;
    }
}
