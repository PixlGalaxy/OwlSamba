using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OwlSamba.Core;

namespace OwlSamba.UI.ViewModels;

public sealed partial class HourBar : ObservableObject
{
    public const double ChartHeight = 150;
    public required long Start { get; init; }
    public string Label => Fmt.Local(Start).ToString("HH:00");
    public bool ShowLabel { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Height), nameof(Tooltip))] private long _value;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Height))] private long _max;

    public double Height => Max <= 0 || Value <= 0 ? 2 : Math.Max(3, Value / (double)Max * ChartHeight);
    public string Tooltip => $"{Label} – {Fmt.Local(Start).AddHours(1):HH:00} · {Value} attempt(s)";
}

public sealed class FeedItem
{
    public required string Kind { get; init; }   // attempt | ban | unban | alert
    public required long At { get; init; }
    public string? Ip { get; init; }
    public string? CountryCode { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string Icon => Kind switch
    {
        "ban" => "ShieldBan",
        "unban" => "LockOpen",
        "alert" => "TriangleAlert",
        _ => "Fingerprint",
    };
    public string Tone => Kind switch
    {
        "ban" => "danger",
        "unban" => "ok",
        "alert" => "alert",
        _ => "temp",
    };

    public static FeedItem From(AttemptRecord a) => new()
    {
        Kind = "attempt",
        At = a.OccurredAt,
        Ip = a.Ip,
        CountryCode = a.CountryCode,
        Title = string.IsNullOrEmpty(a.User) || a.User == "-" ? "Failed attempt" : a.User,
        Detail = $"{Fmt.Source(a.Source)} · {a.Workstation}" + (a.Whitelisted ? " · whitelist" : ""),
    };

    public static FeedItem? From(LiveEvent e) => e.Type switch
    {
        "attempt" when e.Attempt != null => From(e.Attempt),
        "ban" when e.Ban != null => new FeedItem
        {
            Kind = "ban", At = e.At, Ip = e.Ip, CountryCode = e.Ban.CountryCode,
            Title = e.Ban.ExpiresAt == null ? "PERMANENT BAN" : $"BANNED {Fmt.Remaining(e.Ban.ExpiresAt).ToUpperInvariant()}",
            Detail = e.Ban.Reason,
        },
        "unban" => new FeedItem { Kind = "unban", At = e.At, Ip = e.Ip, Title = "UNBANNED", Detail = e.Message == "expired" ? "ban expired" : e.Message },
        "alert" => new FeedItem { Kind = "alert", At = e.At, Ip = e.Ip, Title = "ALERT", Detail = e.Message },
        _ => null,
    };
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly DispatcherTimer _debounce;

    public DashboardViewModel(ShellViewModel shell) : base(shell)
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SafeLoadStatsAsync(); };
    }

    public override string Key => "dashboard";
    public override string Title => "Dashboard";

    [ObservableProperty] private long _attempts24h;
    [ObservableProperty] private long _uniqueIps24h;
    [ObservableProperty] private long _activeTempBans;
    [ObservableProperty] private long _permanentBans;
    [ObservableProperty] private long _bans24h;
    [ObservableProperty] private long _attemptsTotal;
    [ObservableProperty] private long? _lastAttemptAt;
    [ObservableProperty] private List<TopItem> _topIps = new();
    [ObservableProperty] private List<TopItem> _topUsers = new();

    public ObservableCollection<HourBar> Hours { get; } = new();
    public ObservableCollection<FeedItem> Feed { get; } = new();

    public override async Task LoadAsync()
    {
        await LoadStatsAsync();
        var recent = await Api.Get<List<AttemptRecord>>("api/attempts?limit=60");
        Feed.Clear();
        foreach (var a in recent) Feed.Add(FeedItem.From(a));
    }

    private async Task SafeLoadStatsAsync()
    {
        try { if (Shell.IsConnected) await LoadStatsAsync(); } catch { /* next event retries */ }
    }

    private async Task LoadStatsAsync()
    {
        var s = await Api.Get<DashboardStats>("api/stats");
        Attempts24h = s.Attempts24h;
        UniqueIps24h = s.UniqueIps24h;
        ActiveTempBans = s.ActiveTempBans;
        PermanentBans = s.PermanentBans;
        Bans24h = s.Bans24h;
        AttemptsTotal = s.AttemptsTotal;
        LastAttemptAt = s.LastAttemptAt;
        TopIps = s.TopIps;
        TopUsers = s.TopUsers;

        var max = Math.Max(1, s.Hourly.Max());
        if (Hours.Count != 24 || Hours[0].Start != s.HourlyStart)
        {
            Hours.Clear();
            for (int i = 0; i < 24; i++)
                Hours.Add(new HourBar { Start = s.HourlyStart + i * 3_600_000L, ShowLabel = i % 3 == 2 });
        }
        for (int i = 0; i < 24; i++)
        {
            Hours[i].Max = max;
            Hours[i].Value = s.Hourly[i];
        }
    }

    public override void OnLiveEvent(LiveEvent e)
    {
        var item = FeedItem.From(e);
        if (item == null) return;
        Feed.Insert(0, item);
        while (Feed.Count > 200) Feed.RemoveAt(Feed.Count - 1);

        if (e.Type == "attempt" && e.Attempt is { Whitelisted: false } a)
        {
            Attempts24h++;
            AttemptsTotal++;
            LastAttemptAt = a.OccurredAt;
            var bar = Hours.LastOrDefault(h => h.Start <= a.OccurredAt);
            if (bar != null)
            {
                bar.Value++;
                if (bar.Value > bar.Max) foreach (var h in Hours) h.Max = bar.Value;
            }
        }
        _debounce.Stop();
        _debounce.Start();
    }
}
