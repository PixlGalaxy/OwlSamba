using Microsoft.Extensions.Hosting;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Detection;
using OwlSamba.Platform;

namespace OwlSamba.Service;

public static class Demo
{
    /// <summary>Set by --demo: in-memory firewall, loopback-only dashboard, simulated attacks.</summary>
    public static bool Enabled { get; set; }
}

public sealed class NoopSessionKiller : ISmbSessionKiller
{
    public Task CloseSessionsAsync(string ip) => Task.CompletedTask;
}

public sealed class NoopGeoQueue : IGeoQueue
{
    public void Enqueue(string ip) { }
}

/// <summary>
/// Feeds synthetic failed logons (documentation IP ranges only: 192.0.2/24, 198.51.100/24, 203.0.113/24)
/// through the real engine, so detection, bans, the SSE stream and both UIs can be exercised without
/// admin rights. Banned attackers stop (as if the firewall dropped them) and new ones appear.
/// </summary>
public sealed class DemoSimulator : BackgroundService
{
    private static readonly string[] Prefixes = ["192.0.2.", "198.51.100.", "203.0.113."];
    private static readonly (string country, string cc, string isp)[] Geo =
    [
        ("China", "CN", "Demo Telecom"), ("Russia", "RU", "Demo Hosting LLC"), ("Netherlands", "NL", "Demo Cloud BV"),
        ("United States", "US", "Demo VPS Inc."), ("Brazil", "BR", "Demo Fibra"), ("Vietnam", "VN", "Demo Net"),
        ("Germany", "DE", "Demo Rechenzentrum"), ("India", "IN", "Demo Broadband"), ("South Korea", "KR", "Demo Telecom KR"),
    ];
    private static readonly string[] Users =
        ["administrator", "admin", "backup", "sql", "guest", "scanner", "root", "CORP\\jdoe", "user", "test", "ftp", "operator"];

    private readonly BruteForceEngine _engine;
    private readonly Repository _repo;
    private readonly TimeProvider _time;
    private readonly Random _rng = new(7);
    private readonly List<string> _campaign = new();

    public DemoSimulator(BruteForceEngine engine, Repository repo, TimeProvider time)
    {
        _engine = engine;
        _repo = repo;
        _time = time;
    }

    private string RandomIp() => Prefixes[_rng.Next(Prefixes.Length)] + _rng.Next(2, 254);

    private void SetGeo(string ip)
    {
        var g = Geo[Math.Abs(ip.GetHashCode()) % Geo.Length];
        _repo.UpdateGeo(ip, g.country, g.cc, g.isp, _time.NowMs());
    }

    private FailedLogon Attempt(string ip, long at) => new(ip, at, Users[_rng.Next(Users.Length)],
        _rng.Next(3) == 0 ? "-" : "DESKTOP-" + (ip.GetHashCode() & 0xFFFFF).ToString("X5"),
        _rng.Next(3) == 0 ? Sources.SmbServer : Sources.Security,
        _rng.Next(2) == 0 ? "0xc000006a" : "0xc0000064", 3);

    private string NextAttacker()
    {
        var now = _time.NowMs();
        _campaign.RemoveAll(ip => _repo.GetIp(ip)?.IsBannedAt(now) == true);
        while (_campaign.Count < 4) _campaign.Add(RandomIp());
        return _rng.Next(5) == 0 ? RandomIp() : _campaign[_rng.Next(_campaign.Count)];
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        // Some history (older than the accumulation window's typical bursts) so the dashboard isn't empty.
        var now = _time.NowMs();
        var history = new List<FailedLogon>();
        for (int i = 0; i < 70; i++)
        {
            var ip = RandomIp();
            var at = now - (long)(Math.Pow(_rng.NextDouble(), 1.5) * 23 * 3_600_000) - 600_000;
            for (int k = _rng.Next(1, 4); k > 0; k--) history.Add(Attempt(ip, at + k * 2500));
        }
        _repo.ImportAttempts(history);
        foreach (var g in history.GroupBy(h => h.Ip))
        {
            _repo.ImportIp(g.Key, g.Min(x => x.OccurredAt), g.Max(x => x.OccurredAt), g.Count(), g.Last().User, g.Last().Workstation);
            SetGeo(g.Key);
        }
        foreach (var ip in history.Select(h => h.Ip).Distinct().Take(3))
            await _engine.BanAsync(ip, null, "Known SMB scanner (demo)", "demo");

        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            tick++;
            var ip = NextAttacker();
            var burst = tick % 5 == 0 ? 3 : 1;
            for (int i = 0; i < burst; i++)
            {
                await _engine.ProcessFailureAsync(Attempt(ip, _time.NowMs()));
                SetGeo(ip);
                await Task.Delay(_rng.Next(90, 300), ct);
            }
            if (tick % 23 == 0)
                await _engine.ProcessSuccessAsync(new SuccessfulLogon(ip, _time.NowMs(), "CORP\\backup"));
            await Task.Delay(TimeSpan.FromMilliseconds(_rng.Next(1200, 3200)), ct);
        }
    }
}
