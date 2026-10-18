using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;

namespace OwlSamba.Platform;

public interface IFirewall
{
    bool Block(string ip, string description);
    bool Unblock(string ip);
    /// <summary>IPs that currently have an OwlSamba block rule.</summary>
    IReadOnlyCollection<string> ListBlocked();
    /// <summary>Removes rules created by OwlSamba v1 (SMB_block_*, SMB_block_V2_*). Returns the IPs they covered.</summary>
    IReadOnlyCollection<string> RemoveLegacyRules();
    (bool available, bool allProfilesEnabled, string? error) Health();
    /// <summary>Inbound allow rule for the web dashboard port (removed when port is null).</summary>
    void SetDashboardRule(int? port);
}

/// <summary>
/// Windows Defender Firewall through the HNetCfg.FwPolicy2 COM API. This is an in-process call
/// (no netsh process spawn), so a block takes effect in a few milliseconds.
/// </summary>
public sealed class WindowsFirewall : IFirewall
{
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_IP_PROTOCOL_ANY = 256;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;

    private readonly ILogger<WindowsFirewall> _log;
    private readonly object _gate = new();

    public WindowsFirewall(ILogger<WindowsFirewall> log) => _log = log;

    public static string RuleName(string ip) => AppInfo.FirewallRulePrefix + ip;

    private static dynamic Policy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    private static dynamic NewRule()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    public bool Block(string ip, string description)
    {
        lock (_gate)
        {
            try
            {
                dynamic policy = Policy();
                var name = RuleName(ip);
                RemoveByName(policy, name);
                dynamic rule = NewRule();
                rule.Name = name;
                rule.Description = description;
                rule.Grouping = AppInfo.FirewallGroup;
                rule.Protocol = NET_FW_IP_PROTOCOL_ANY;
                rule.Direction = NET_FW_RULE_DIR_IN;
                rule.Action = NET_FW_ACTION_BLOCK;
                rule.Profiles = NET_FW_PROFILE2_ALL;
                rule.RemoteAddresses = ip;
                rule.Enabled = true;
                policy.Rules.Add(rule);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Firewall block failed for {Ip}", ip);
                return false;
            }
        }
    }

    public bool Unblock(string ip)
    {
        lock (_gate)
        {
            try
            {
                RemoveByName(Policy(), RuleName(ip));
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Firewall unblock failed for {Ip}", ip);
                return false;
            }
        }
    }

    private static void RemoveByName(dynamic policy, string name)
    {
        // Rules.Remove deletes one rule per call; loop in case duplicates exist.
        for (int i = 0; i < 16; i++)
        {
            try
            {
                dynamic existing = policy.Rules.Item(name);
                if (existing == null) return;
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException)
            {
                return;
            }
            policy.Rules.Remove(name);
        }
    }

    public IReadOnlyCollection<string> ListBlocked()
    {
        var result = new List<string>();
        lock (_gate)
        {
            try
            {
                foreach (dynamic rule in Policy().Rules)
                {
                    string name = rule.Name ?? "";
                    if (name.StartsWith(AppInfo.FirewallRulePrefix, StringComparison.Ordinal))
                        result.Add(name[AppInfo.FirewallRulePrefix.Length..]);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not enumerate firewall rules");
            }
        }
        return result;
    }

    public IReadOnlyCollection<string> RemoveLegacyRules()
    {
        var ips = new HashSet<string>();
        lock (_gate)
        {
            try
            {
                dynamic policy = Policy();
                var names = new List<string>();
                foreach (dynamic rule in policy.Rules)
                {
                    string name = rule.Name ?? "";
                    if (name.StartsWith("SMB_block_", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                }
                foreach (var name in names.Distinct())
                {
                    var ip = name.StartsWith("SMB_block_V2_", StringComparison.OrdinalIgnoreCase)
                        ? name["SMB_block_V2_".Length..]
                        : name["SMB_block_".Length..];
                    if (IpUtil.Normalize(ip) is { } n) ips.Add(n);
                    RemoveByName(policy, name);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not remove legacy firewall rules");
            }
        }
        return ips;
    }

    private const string DashboardRuleName = "OwlSamba Dashboard";

    public void SetDashboardRule(int? port)
    {
        lock (_gate)
        {
            try
            {
                dynamic policy = Policy();
                RemoveByName(policy, DashboardRuleName);
                if (port == null) return;
                dynamic rule = NewRule();
                rule.Name = DashboardRuleName;
                rule.Description = "Allows access to the OwlSamba web dashboard (banned IPs stay blocked).";
                rule.Grouping = AppInfo.FirewallGroup;
                rule.Protocol = 6; // TCP
                rule.LocalPorts = port.Value.ToString();
                rule.Direction = NET_FW_RULE_DIR_IN;
                rule.Action = 1; // allow
                rule.Profiles = NET_FW_PROFILE2_ALL;
                rule.Enabled = true;
                policy.Rules.Add(rule);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not update dashboard firewall rule");
            }
        }
    }

    public (bool available, bool allProfilesEnabled, string? error) Health()
    {
        try
        {
            dynamic policy = Policy();
            bool all = true;
            foreach (var profile in new[] { 1, 2, 4 }) // domain, private, public
                all &= (bool)policy.FirewallEnabled[profile];
            return (true, all, all ? null : "Windows Firewall is disabled on at least one profile: bans do not apply there.");
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }
}

/// <summary>No-op firewall used in monitor-only mode and tests.</summary>
public sealed class NullFirewall : IFirewall
{
    private readonly HashSet<string> _blocked = new();
    public bool Block(string ip, string description) { lock (_blocked) _blocked.Add(ip); return true; }
    public bool Unblock(string ip) { lock (_blocked) _blocked.Remove(ip); return true; }
    public IReadOnlyCollection<string> ListBlocked() { lock (_blocked) return _blocked.ToList(); }
    public IReadOnlyCollection<string> RemoveLegacyRules() => Array.Empty<string>();
    public (bool available, bool allProfilesEnabled, string? error) Health() => (true, true, null);
    public void SetDashboardRule(int? port) { }
}
