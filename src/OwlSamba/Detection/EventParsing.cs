using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OwlSamba.Core;

namespace OwlSamba.Detection;

/// <summary>A successful network logon (event 4624, type 3).</summary>
public sealed record SuccessfulLogon(string Ip, long OccurredAt, string User);

/// <summary>Turns raw event XML into domain records. Pure functions, unit-tested.</summary>
public static partial class EventParsing
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static Dictionary<string, string> ReadEventData(string xml)
    {
        var doc = XDocument.Parse(xml);
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int unnamed = 0;
        foreach (var d in doc.Descendants(Ns + "Data").Concat(doc.Descendants("Data")))
        {
            var name = (string?)d.Attribute("Name") ?? $"#{unnamed++}";
            data[name] = d.Value;
        }
        // Some providers (UserData instead of EventData) use element names as keys.
        foreach (var e in doc.Descendants().Where(e => e.Parent?.Name.LocalName is "EventXML" && !e.HasElements))
            data.TryAdd(e.Name.LocalName, e.Value);
        foreach (var e in doc.Descendants().Where(e => e.Parent?.Parent?.Name.LocalName is "UserData" && !e.HasElements))
            data.TryAdd(e.Name.LocalName, e.Value);
        return data;
    }

    private static string Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) ? v.Trim() : "";

    private static string JoinUser(string domain, string user)
    {
        if (string.IsNullOrEmpty(user) || user == "-") return "-";
        if (string.IsNullOrEmpty(domain) || domain == "-") return user;
        return $"{domain}\\{user}";
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Security event 4625: "An account failed to log on".</summary>
    public static FailedLogon? ParseSecurity4625(string xml, long occurredAt)
    {
        var d = ReadEventData(xml);
        var ip = IpUtil.Normalize(Get(d, "IpAddress"));
        if (ip == null) return null;
        var status = Get(d, "SubStatus");
        if (string.IsNullOrEmpty(status) || status == "0x0") status = Get(d, "Status");
        return new FailedLogon(
            ip, occurredAt,
            JoinUser(Get(d, "TargetDomainName"), Get(d, "TargetUserName")),
            NullIfDash(Get(d, "WorkstationName")),
            Sources.Security,
            string.IsNullOrEmpty(status) ? null : status.ToLowerInvariant(),
            ParseInt(Get(d, "LogonType")));
    }

    /// <summary>Security event 4624 (successful logon) — only network logons are interesting.</summary>
    public static SuccessfulLogon? ParseSecurity4624(string xml, long occurredAt)
    {
        var d = ReadEventData(xml);
        if (ParseInt(Get(d, "LogonType")) is not (3 or 8 or 10)) return null;
        var ip = IpUtil.Normalize(Get(d, "IpAddress"));
        if (ip == null) return null;
        var user = Get(d, "TargetUserName");
        if (user.EndsWith('$') || user.Equals("ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase)) return null;
        return new SuccessfulLogon(ip, occurredAt, JoinUser(Get(d, "TargetDomainName"), user));
    }

    /// <summary>
    /// Microsoft-Windows-SMBServer/Security event 551: "SMB Session Authentication Failure".
    /// The client address is a raw SOCKADDR in hex; ClientName usually holds "\\ip".
    /// </summary>
    public static FailedLogon? ParseSmb551(string xml, long occurredAt)
    {
        var d = ReadEventData(xml);
        string? ip = null;
        foreach (var key in d.Keys.Where(k => k.Contains("Address", StringComparison.OrdinalIgnoreCase) && !k.Contains("Length", StringComparison.OrdinalIgnoreCase)))
        {
            ip = ParseSockAddrHex(d[key]) ?? IpUtil.Normalize(d[key]) ?? FindIp(d[key]);
            if (ip != null) break;
        }
        ip ??= FindIp(Get(d, "ClientName"));
        if (ip == null) return null;

        var status = Get(d, "Status");
        if (uint.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            status = $"0x{code:x8}";
        var user = Get(d, "UserName");
        var domain = Get(d, "DomainName");
        return new FailedLogon(
            ip, occurredAt,
            JoinUser(domain, string.IsNullOrEmpty(user) ? "-" : user),
            NullIfDash(Get(d, "ClientName").TrimStart('\\')),
            Sources.SmbServer,
            string.IsNullOrEmpty(status) ? null : status.ToLowerInvariant());
    }

    /// <summary>Decodes a hex SOCKADDR_IN / SOCKADDR_IN6 blob.</summary>
    public static string? ParseSockAddrHex(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length < 16 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return null;
        var bytes = Convert.FromHexString(hex);
        var family = bytes[0] | bytes[1] << 8;
        if (family == 2 && bytes.Length >= 8)
            return IpUtil.Normalize($"{bytes[4]}.{bytes[5]}.{bytes[6]}.{bytes[7]}");
        if (family == 23 && bytes.Length >= 24)
            return IpUtil.Normalize(new System.Net.IPAddress(bytes[8..24]).ToString());
        return null;
    }

    private static string? FindIp(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var v4 = Ipv4Regex().Match(text);
        if (v4.Success && IpUtil.Normalize(v4.Value) is { } a) return a;
        foreach (Match m in Ipv6Regex().Matches(text))
            if (IpUtil.Normalize(m.Value) is { } b && b.Contains(':')) return b;
        return null;
    }

    private static string NullIfDash(string s) => string.IsNullOrEmpty(s) ? "-" : s;

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"[0-9A-Fa-f:]{2,}(?:%\w+)?")]
    private static partial Regex Ipv6Regex();
}

/// <summary>
/// A single SMB failure can be logged both as Security/4625 and SMBServer/551.
/// When an event arrives from one source and an unmatched event for the same IP from the
/// other source was seen within the window, the second one is treated as a duplicate.
/// </summary>
public sealed class CrossSourceDeduplicator
{
    private readonly long _windowMs;
    private readonly Dictionary<string, List<(string source, long at)>> _pending = new();

    public CrossSourceDeduplicator(TimeSpan window) => _windowMs = (long)window.TotalMilliseconds;

    public bool IsDuplicate(FailedLogon e)
    {
        if (e.Source is not (Sources.Security or Sources.SmbServer)) return false;
        if (!_pending.TryGetValue(e.Ip, out var list))
            _pending[e.Ip] = list = new List<(string, long)>();

        list.RemoveAll(p => Math.Abs(e.OccurredAt - p.at) > _windowMs && p.at < e.OccurredAt);
        var match = list.FindIndex(p => p.source != e.Source && Math.Abs(e.OccurredAt - p.at) <= _windowMs);
        if (match >= 0)
        {
            list.RemoveAt(match);
            if (list.Count == 0) _pending.Remove(e.Ip);
            return true;
        }
        list.Add((e.Source, e.OccurredAt));
        if (_pending.Count > 10_000) Prune(e.OccurredAt);
        return false;
    }

    private void Prune(long now)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            _pending[key].RemoveAll(p => now - p.at > _windowMs);
            if (_pending[key].Count == 0) _pending.Remove(key);
        }
    }
}
