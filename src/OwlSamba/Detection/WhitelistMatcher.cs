using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;

namespace OwlSamba.Detection;

/// <summary>
/// Fast in-memory whitelist check. Loopback and this machine's own addresses are always trusted;
/// domains are resolved periodically.
/// </summary>
public sealed class WhitelistMatcher
{
    private readonly Repository _repo;
    private readonly ILogger<WhitelistMatcher> _log;
    private volatile Snapshot _snapshot = new([], [], []);

    private sealed record Snapshot(HashSet<string> Ips, List<string> Cidrs, HashSet<string> LocalIps);

    public WhitelistMatcher(Repository repo, ILogger<WhitelistMatcher> log)
    {
        _repo = repo;
        _log = log;
    }

    public bool IsWhitelisted(string ip)
    {
        if (IpUtil.IsLoopback(ip)) return true;
        var s = _snapshot;
        if (s.Ips.Contains(ip) || s.LocalIps.Contains(ip)) return true;
        foreach (var c in s.Cidrs)
            if (IpUtil.InCidr(ip, c)) return true;
        return false;
    }

    /// <summary>Rebuilds the snapshot (DB entries + DNS resolution of domains + local NICs).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cidrs = new List<string>();
        foreach (var e in _repo.Whitelist())
        {
            switch (e.Kind)
            {
                case "ip" when IpUtil.Normalize(e.Value) is { } ip: ips.Add(ip); break;
                case "cidr": cidrs.Add(e.Value); break;
                case "domain":
                    try
                    {
                        foreach (var a in await Dns.GetHostAddressesAsync(e.Value, ct))
                            if (IpUtil.Normalize(a.ToString()) is { } r) ips.Add(r);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning("Whitelist domain {Domain} could not be resolved: {Error}", e.Value, ex.Message);
                    }
                    break;
            }
        }
        _snapshot = new Snapshot(ips, cidrs, LocalAddresses());
    }

    private static HashSet<string> LocalAddresses()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (IpUtil.Normalize(a.Address.ToString()) is { } ip) set.Add(ip);
        }
        catch { /* best effort */ }
        return set;
    }
}
