using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Platform;
using OwlSamba.Service;

namespace OwlSamba.Detection;

public interface INotifier
{
    void BanApplied(BanRecord ban);
    void SuspiciousLogon(string ip, string user, long at, long recentFailures);
}

public interface IGeoQueue
{
    void Enqueue(string ip);
}

/// <summary>
/// Ban policy (all values configurable):
///  • BurstThreshold failures within BurstWindowSeconds → temporary ban of TempBanMinutes.
///  • Attempts keep accumulating across temporary bans; reaching PermanentBanThreshold within
///    AccumulationWindowHours (0 = forever) → permanent ban.
/// With defaults (3 / 60 s / 15 min / 10 / 24 h) an attacker gets 3 tries every 15 minutes and is
/// banned forever on the 10th try, i.e. within ~45 minutes.
///
/// All mutations are serialized through one gate, so the event consumer and API calls never race.
/// </summary>
public sealed class BruteForceEngine
{
    private readonly Repository _repo;
    private readonly IFirewall _firewall;
    private readonly ISmbSessionKiller _sessions;
    private readonly WhitelistMatcher _whitelist;
    private readonly SettingsProvider _settings;
    private readonly LiveHub _hub;
    private readonly INotifier _notifier;
    private readonly IGeoQueue _geo;
    private readonly TimeProvider _time;
    private readonly ILogger<BruteForceEngine> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CrossSourceDeduplicator _dedup = new(TimeSpan.FromSeconds(3));

    // IPs with recent failures, used to cheaply filter the (high-volume) successful logon stream.
    private readonly Dictionary<string, long> _recentFailures = new();

    public long EventsProcessed { get; private set; }
    public long? LastEventAt { get; private set; }

    public BruteForceEngine(Repository repo, IFirewall firewall, ISmbSessionKiller sessions, WhitelistMatcher whitelist,
        SettingsProvider settings, LiveHub hub, INotifier notifier, IGeoQueue geo, TimeProvider time,
        ILogger<BruteForceEngine> log)
    {
        _repo = repo;
        _firewall = firewall;
        _sessions = sessions;
        _whitelist = whitelist;
        _settings = settings;
        _hub = hub;
        _notifier = notifier;
        _geo = geo;
        _time = time;
        _log = log;
    }

    private const long Minute = 60_000L;

    public async Task ProcessFailureAsync(FailedLogon e)
    {
        await _gate.WaitAsync();
        try
        {
            var s = _settings.Current;
            if (s.MonitorSecurityLog && s.MonitorSmbServerLog && _dedup.IsDuplicate(e)) return;

            var now = _time.NowMs();
            var whitelisted = _whitelist.IsWhitelisted(e.Ip);
            var (attempt, rec) = _repo.RecordAttempt(e, whitelisted);
            EventsProcessed++;
            LastEventAt = now;
            _hub.Publish(new LiveEvent { Type = "attempt", At = now, Ip = e.Ip, Attempt = attempt });
            if (rec.GeoAt == null) _geo.Enqueue(e.Ip);
            if (whitelisted) return;

            _recentFailures[e.Ip] = Math.Max(e.OccurredAt, _recentFailures.GetValueOrDefault(e.Ip));
            if (_recentFailures.Count > 50_000) PruneRecentFailures(now);

            var t = e.OccurredAt;
            var banned = rec.IsBannedAt(now);
            if (banned && rec.IsPermanent) return;

            // 1) Accumulated attempts → permanent ban.
            var accSince = s.AccumulationWindowHours == 0
                ? rec.CounterResetAt
                : Math.Max(t - s.AccumulationWindowHours * 60 * Minute, rec.CounterResetAt);
            var accumulated = _repo.CountAttempts(e.Ip, accSince, t);
            if (accumulated >= s.PermanentBanThreshold)
            {
                if (s.EnableBanning)
                    ApplyBan(e.Ip, BanKinds.Permanent, now, null,
                        $"{accumulated} accumulated failed attempts (limit {s.PermanentBanThreshold})", "engine");
                else
                    Alert(e.Ip, now, $"Monitor mode: {e.Ip} reached {accumulated} attempts (would have been banned permanently)");
                return;
            }
            if (banned) return; // temp-banned: attempts still accumulate (handled above)

            // 2) Burst → temporary ban. Attempts already "spent" on a previous ban don't count again.
            var burstSince = Math.Max(t - s.BurstWindowSeconds * 1000L, Math.Max(rec.BanStartedAt ?? 0, rec.CounterResetAt));
            var burst = _repo.CountAttempts(e.Ip, burstSince, t);
            if (burst >= s.BurstThreshold)
            {
                var until = t + s.TempBanMinutes * Minute;
                if (until <= now) return; // replaying old history: that ban would already be over
                if (s.EnableBanning)
                    ApplyBan(e.Ip, BanKinds.Temporary, now, until,
                        $"{burst} attempts in {s.BurstWindowSeconds}s · accumulated {accumulated}/{s.PermanentBanThreshold}", "engine");
                else
                    Alert(e.Ip, now, $"Monitor mode: {e.Ip} made {burst} attempts in {s.BurstWindowSeconds}s (would have been banned)");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to process attempt from {Ip}", e.Ip);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ProcessSuccessAsync(SuccessfulLogon e)
    {
        var s = _settings.Current;
        if (!s.AlertOnSuccessAfterFailures && !s.BanOnSuccessAfterFailures) return;

        await _gate.WaitAsync();
        try
        {
            var lookbackStart = e.OccurredAt - s.SuccessAfterFailuresLookbackMinutes * Minute;
            if (!_recentFailures.TryGetValue(e.Ip, out var lastFailure) || lastFailure < lookbackStart) return;
            if (_whitelist.IsWhitelisted(e.Ip)) return;

            var failures = _repo.CountAttempts(e.Ip, lookbackStart, e.OccurredAt);
            if (failures == 0) return;

            var now = _time.NowMs();
            _repo.MarkSuspicious(e.Ip, now);
            var msg = $"SUCCESSFUL logon from {e.Ip} as '{e.User}' after {failures} failed attempt(s)";
            _log.LogWarning("{Message}", msg);
            _repo.Audit("engine", "alert", e.Ip, msg, now);
            _hub.Publish(new LiveEvent { Type = "alert", At = now, Ip = e.Ip, Message = msg });
            _notifier.SuspiciousLogon(e.Ip, e.User, now, failures);

            if (s.BanOnSuccessAfterFailures && s.EnableBanning)
                ApplyBan(e.Ip, BanKinds.Permanent, now, null, "Successful logon after failed attempts", "engine");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to process successful logon from {Ip}", e.Ip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Manual ban. minutes == null → permanent.</summary>
    public async Task<BanRecord> BanAsync(string ip, int? minutes, string? reason, string actor)
    {
        var norm = IpUtil.Normalize(ip) ?? throw new ArgumentException("Invalid IP");
        await _gate.WaitAsync();
        try
        {
            var now = _time.NowMs();
            long? until = minutes is > 0 ? now + minutes.Value * Minute : null;
            var ban = ApplyBan(norm, BanKinds.Manual, now, until,
                string.IsNullOrWhiteSpace(reason) ? "Manual ban" : reason.Trim(), actor);
            _geo.Enqueue(norm);
            return ban;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UnbanAsync(string ip, bool resetCounters, string actor)
    {
        var norm = IpUtil.Normalize(ip) ?? throw new ArgumentException("Invalid IP");
        await _gate.WaitAsync();
        try
        {
            var rec = _repo.GetIp(norm);
            var now = _time.NowMs();
            if (rec == null || rec.BanKind == null)
            {
                _firewall.Unblock(norm); // clean up stray rules anyway
                if (rec != null && resetCounters) _repo.ClearBan(norm, now, "manual", true);
                return false;
            }
            Lift(norm, now, "manual", resetCounters, actor);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ExpireDueBansAsync()
    {
        var now = _time.NowMs();
        var due = _repo.ExpiredBans(now);
        if (due.Count == 0) return 0;
        await _gate.WaitAsync();
        try
        {
            foreach (var ip in due) Lift(ip, now, "expired", resetCounters: false, actor: "engine");
            return due.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes the firewall match the database (after restarts, restores or manual rule edits).</summary>
    public async Task<(int added, int removed)> ReconcileFirewallAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var now = _time.NowMs();
            var wanted = _repo.ActiveBannedIps(now).ToHashSet();
            var present = _firewall.ListBlocked().ToHashSet();
            int added = 0, removed = 0;
            foreach (var ip in wanted.Except(present))
                if (_firewall.Block(ip, "OwlSamba: ban restored")) added++;
            foreach (var ip in present.Except(wanted))
                if (_firewall.Unblock(ip)) removed++;
            if (added + removed > 0)
                _log.LogInformation("Firewall reconciled: {Added} rule(s) added, {Removed} removed", added, removed);
            return (added, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ internals (gate held)

    private BanRecord ApplyBan(string ip, string kind, long now, long? until, string reason, string actor)
    {
        var ban = _repo.SetBan(ip, kind, now, until, reason, actor);
        var blocked = _firewall.Block(ip, $"OwlSamba · {reason}");
        var label = until == null ? "PERMANENT" : $"until {DateTimeOffset.FromUnixTimeMilliseconds(until.Value).ToLocalTime():HH:mm:ss}";
        _log.LogWarning("BAN {Kind} {Ip} {Label} — {Reason}{Fw}", kind, ip, label, reason, blocked ? "" : " (FIREWALL RULE FAILED!)");
        _repo.Audit(actor, "ban", ip, $"{kind} {label} · {reason}", now);
        _hub.Publish(new LiveEvent { Type = "ban", At = now, Ip = ip, Ban = ban, Message = reason });
        _notifier.BanApplied(ban);

        if (_settings.Current.KillSmbSessionsOnBan)
            _ = Task.Run(async () =>
            {
                try { await _sessions.CloseSessionsAsync(ip); }
                catch (Exception ex) { _log.LogWarning(ex, "Closing SMB sessions of {Ip} failed", ip); }
            });
        return ban;
    }

    private void Lift(string ip, long now, string reason, bool resetCounters, string actor)
    {
        _repo.ClearBan(ip, now, reason, resetCounters);
        _firewall.Unblock(ip);
        _log.LogInformation("UNBAN {Ip} ({Reason})", ip, reason);
        _repo.Audit(actor, "unban", ip, reason + (resetCounters ? " · counters reset" : ""), now);
        _hub.Publish(new LiveEvent { Type = "unban", At = now, Ip = ip, Message = reason });
    }

    private void Alert(string ip, long now, string message)
    {
        _log.LogWarning("{Message}", message);
        _hub.Publish(new LiveEvent { Type = "alert", At = now, Ip = ip, Message = message });
    }

    private void PruneRecentFailures(long now)
    {
        var cutoff = now - 7 * 24 * 60 * Minute;
        foreach (var k in _recentFailures.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _recentFailures.Remove(k);
    }
}
