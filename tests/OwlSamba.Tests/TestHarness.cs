using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Detection;
using OwlSamba.Platform;
using OwlSamba.Service;

namespace OwlSamba.Tests;

public sealed class FakeSessions : ISmbSessionKiller
{
    public List<string> Closed { get; } = new();
    public Task CloseSessionsAsync(string ip) { lock (Closed) Closed.Add(ip); return Task.CompletedTask; }
}

public sealed class FakeNotifier : INotifier
{
    public List<BanRecord> Bans { get; } = new();
    public List<string> Suspicious { get; } = new();
    public void BanApplied(BanRecord ban) => Bans.Add(ban);
    public void SuspiciousLogon(string ip, string user, long at, long recentFailures) => Suspicious.Add(ip);
}

public sealed class FakeGeo : IGeoQueue
{
    public void Enqueue(string ip) { }
}

/// <summary>Engine wired to a temp SQLite DB, an in-memory firewall and a controllable clock.</summary>
public sealed class TestHarness : IDisposable
{
    private readonly string _dir;
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    public Repository Repo { get; }
    public NullFirewall Firewall { get; } = new();
    public FakeSessions Sessions { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public SettingsProvider Settings { get; }
    public WhitelistMatcher Whitelist { get; }
    public LiveHub Hub { get; } = new();
    public BruteForceEngine Engine { get; }

    public TestHarness()
    {
        _dir = Path.Combine(Path.GetTempPath(), "owlsamba-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var db = new Db(Path.Combine(_dir, "test.db"));
        db.EnsureSchema();
        Repo = new Repository(db);
        Settings = new SettingsProvider(Repo);
        Whitelist = new WhitelistMatcher(Repo, NullLogger<WhitelistMatcher>.Instance);
        Engine = new BruteForceEngine(Repo, Firewall, Sessions, Whitelist, Settings, Hub, Notifier, new FakeGeo(), Time,
            NullLogger<BruteForceEngine>.Instance);
    }

    public string Dir => _dir;
    public long Now => Time.GetUtcNow().ToUnixTimeMilliseconds();

    public Task Fail(string ip, string user = "admin", string source = Sources.Security) =>
        Engine.ProcessFailureAsync(new FailedLogon(ip, Now, user, "KALI", source, "0xc000006a", 3));

    public void Advance(TimeSpan by) => Time.Advance(by);

    public bool Blocked(string ip) => Firewall.ListBlocked().Contains(ip);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
