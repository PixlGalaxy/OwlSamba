using OwlSamba.Core;

namespace OwlSamba.Tests;

public class EngineTests
{
    private const string Attacker = "203.0.113.50";

    [Fact]
    public async Task Three_failures_within_window_trigger_15_minute_ban()
    {
        using var h = new TestHarness();
        await h.Fail(Attacker);
        h.Advance(TimeSpan.FromMilliseconds(200));
        await h.Fail(Attacker);
        Assert.False(h.Blocked(Attacker));
        h.Advance(TimeSpan.FromMilliseconds(200));
        await h.Fail(Attacker);

        Assert.True(h.Blocked(Attacker));
        var ip = h.Repo.GetIp(Attacker)!;
        Assert.Equal(BanKinds.Temporary, ip.BanKind);
        Assert.Equal(h.Now + 15 * 60_000, ip.BanUntil);
        Assert.Contains(Attacker, h.Sessions.Closed.Count > 0 ? h.Sessions.Closed : await WaitForSessions(h));
    }

    private static async Task<List<string>> WaitForSessions(TestHarness h)
    {
        for (int i = 0; i < 50 && h.Sessions.Closed.Count == 0; i++) await Task.Delay(20);
        return h.Sessions.Closed;
    }

    [Fact]
    public async Task Slow_attempts_outside_window_do_not_trigger_temp_ban()
    {
        using var h = new TestHarness();
        for (int i = 0; i < 3; i++)
        {
            await h.Fail(Attacker);
            h.Advance(TimeSpan.FromSeconds(40));
        }
        Assert.False(h.Blocked(Attacker));
    }

    [Fact]
    public async Task Temp_ban_expires_and_is_lifted()
    {
        using var h = new TestHarness();
        for (int i = 0; i < 3; i++) await h.Fail(Attacker);
        Assert.True(h.Blocked(Attacker));

        h.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(0, await h.Engine.ExpireDueBansAsync());
        Assert.True(h.Blocked(Attacker));

        h.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await h.Engine.ExpireDueBansAsync());
        Assert.False(h.Blocked(Attacker));
        Assert.Null(h.Repo.GetIp(Attacker)!.BanKind);
    }

    [Fact]
    public async Task Persistent_attacker_is_permanently_banned_within_an_hour()
    {
        using var h = new TestHarness();
        var start = h.Now;
        // Attacker hammers as fast as possible; while banned the firewall drops its traffic,
        // so it only gets new tries after each temporary ban expires.
        int tries = 0;
        while (!(h.Repo.GetIp(Attacker)?.IsPermanent ?? false) && tries < 100)
        {
            if (h.Blocked(Attacker))
            {
                h.Advance(TimeSpan.FromSeconds(30));
                await h.Engine.ExpireDueBansAsync();
                continue;
            }
            await h.Fail(Attacker);
            tries++;
            h.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(h.Repo.GetIp(Attacker)!.IsPermanent);
        Assert.Equal(10, tries);
        Assert.True(h.Now - start < 60 * 60_000, "should be permanent in under one hour");
        Assert.True(h.Blocked(Attacker));
        // 3 temporary bans + 1 permanent
        Assert.Equal(4, h.Notifier.Bans.Count);
        Assert.Equal(BanKinds.Permanent, h.Notifier.Bans[^1].Kind);
    }

    [Fact]
    public async Task Whitelisted_ip_is_never_banned()
    {
        using var h = new TestHarness();
        h.Repo.AddWhitelist("10.0.0.0/8", "cidr", null, h.Now);
        await h.Whitelist.RefreshAsync();
        for (int i = 0; i < 20; i++) await h.Fail("10.1.2.3");
        Assert.False(h.Blocked("10.1.2.3"));
        Assert.Null(h.Repo.GetIp("10.1.2.3")!.BanKind);
    }

    [Fact]
    public async Task Loopback_is_always_trusted()
    {
        using var h = new TestHarness();
        await h.Whitelist.RefreshAsync();
        for (int i = 0; i < 5; i++) await h.Fail("127.0.0.1");
        Assert.False(h.Blocked("127.0.0.1"));
    }

    [Fact]
    public async Task Manual_unban_with_reset_clears_counters()
    {
        using var h = new TestHarness();
        for (int i = 0; i < 9; i++)
        {
            await h.Fail(Attacker);
            h.Advance(TimeSpan.FromSeconds(61)); // stay under the burst rule
        }
        Assert.False(h.Blocked(Attacker));
        await h.Engine.BanAsync(Attacker, null, "test", "tester");
        Assert.True(h.Blocked(Attacker));

        Assert.True(await h.Engine.UnbanAsync(Attacker, resetCounters: true, "tester"));
        Assert.False(h.Blocked(Attacker));

        h.Advance(TimeSpan.FromSeconds(61));
        await h.Fail(Attacker); // would be the 10th → permanent without reset
        Assert.False(h.Blocked(Attacker));
    }

    [Fact]
    public async Task Monitor_mode_never_touches_firewall()
    {
        using var h = new TestHarness();
        h.Settings.Update(s => s with { EnableBanning = false });
        for (int i = 0; i < 12; i++) await h.Fail(Attacker);
        Assert.False(h.Blocked(Attacker));
        Assert.Null(h.Repo.GetIp(Attacker)!.BanKind);
    }

    [Fact]
    public async Task Duplicate_events_from_security_and_smb_count_once()
    {
        using var h = new TestHarness();
        await h.Fail(Attacker, source: Sources.Security);
        await h.Fail(Attacker, source: Sources.SmbServer);
        h.Advance(TimeSpan.FromMilliseconds(300));
        await h.Fail(Attacker, source: Sources.SmbServer);
        await h.Fail(Attacker, source: Sources.Security);
        Assert.Equal(2, h.Repo.GetIp(Attacker)!.TotalAttempts);
        Assert.False(h.Blocked(Attacker));
    }

    [Fact]
    public async Task Successful_logon_after_failures_raises_alert()
    {
        using var h = new TestHarness();
        await h.Fail(Attacker);
        h.Advance(TimeSpan.FromSeconds(5));
        await h.Engine.ProcessSuccessAsync(new Detection.SuccessfulLogon(Attacker, h.Now, "CORP\\admin"));
        Assert.Contains(Attacker, h.Notifier.Suspicious);
        Assert.NotNull(h.Repo.GetIp(Attacker)!.SuspiciousAt);

        // A host with no failures logging on is normal.
        await h.Engine.ProcessSuccessAsync(new Detection.SuccessfulLogon("198.51.100.7", h.Now, "bob"));
        Assert.DoesNotContain("198.51.100.7", h.Notifier.Suspicious);
    }

    [Fact]
    public async Task Reconcile_restores_missing_rules_and_removes_stale_ones()
    {
        using var h = new TestHarness();
        await h.Engine.BanAsync(Attacker, null, null, "t");
        h.Firewall.Unblock(Attacker);           // someone deleted the rule
        h.Firewall.Block("192.0.2.99", "stale"); // rule without a ban
        var (added, removed) = await h.Engine.ReconcileFirewallAsync();
        Assert.Equal((1, 1), (added, removed));
        Assert.True(h.Blocked(Attacker));
        Assert.False(h.Blocked("192.0.2.99"));
    }
}
