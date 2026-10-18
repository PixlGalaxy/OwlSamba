using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwlSamba.Core;

namespace OwlSamba.UI.ViewModels;

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public static class Durations
{
    public static readonly IReadOnlyList<Choice<int?>> All =
    [
        new("15 minutes", 15),
        new("1 hour", 60),
        new("24 hours", 1440),
        new("7 days", 10080),
        new("30 days", 43200),
        new("Permanent", null),
    ];
}

// =============================================================================== activity

public sealed partial class ActivityViewModel : PageViewModel
{
    private readonly DispatcherTimer _searchDebounce;

    public ActivityViewModel(ShellViewModel shell) : base(shell)
    {
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await SafeLoadAsync(); };
    }

    public override string Key => "activity";
    public override string Title => "Live activity";
    public override string Subtitle => "Every failed logon attempt, the instant it happens.";

    public ObservableCollection<AttemptRecord> Items { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _live = true;
    [ObservableProperty] private bool _hideWhitelisted;
    [ObservableProperty] private AttemptRecord? _selected;
    [ObservableProperty] private bool _canLoadMore;

    partial void OnSearchChanged(string value) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    partial void OnHideWhitelistedChanged(bool value) => _ = SafeLoadAsync();

    public override async Task LoadAsync()
    {
        var list = await Api.Get<List<AttemptRecord>>($"api/attempts?limit=300&search={Uri.EscapeDataString(Search)}");
        Items.Clear();
        foreach (var a in list.Where(Visible)) Items.Add(a);
        CanLoadMore = list.Count == 300;
    }

    private bool Visible(AttemptRecord a) => !(HideWhitelisted && a.Whitelisted);

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (Items.Count == 0) return;
        await Run(async () =>
        {
            var list = await Api.Get<List<AttemptRecord>>(
                $"api/attempts?limit=300&search={Uri.EscapeDataString(Search)}&beforeId={Items[^1].Id}");
            foreach (var a in list.Where(Visible)) Items.Add(a);
            CanLoadMore = list.Count == 300;
        });
    }

    [RelayCommand] private Task RefreshAsync() => SafeLoadAsync();

    public override void OnLiveEvent(LiveEvent e)
    {
        if (!Live || e.Type != "attempt" || e.Attempt is not { } a || !Visible(a)) return;
        if (Search.Length > 0 && !(a.Ip.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                   || (a.User ?? "").Contains(Search, StringComparison.OrdinalIgnoreCase)
                                   || (a.Workstation ?? "").Contains(Search, StringComparison.OrdinalIgnoreCase))) return;
        Items.Insert(0, a);
        while (Items.Count > 3000) Items.RemoveAt(Items.Count - 1);
    }
}

// =============================================================================== IPs

public sealed partial class IpsViewModel : PageViewModel
{
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _liveDebounce;
    private string? _pendingFocus;
    private const int PageSize = 100;

    public IpsViewModel(ShellViewModel shell) : base(shell)
    {
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); Page = 0; await SafeLoadAsync(); };
        _liveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveDebounce.Tick += async (_, _) =>
        {
            _liveDebounce.Stop();
            if (Shell.CurrentPage == this) await SafeLoadAsync();
        };
        _statusFilter = StatusFilters[0];
        _sort = Sorts[0];
        _banDuration = Durations.All[^1];
    }

    public override string Key => "ips";
    public override string Title => "IP explorer";
    public override string Subtitle => "Search any IP, review its history and ban or unban it manually.";

    public IReadOnlyList<Choice<string>> StatusFilters { get; } =
    [
        new("All", "all"), new("Banned", "banned"), new("Temporary", "temp"),
        new("Permanent", "perm"), new("Not banned", "free"), new("Suspicious", "suspicious"),
    ];
    public IReadOnlyList<Choice<string>> Sorts { get; } =
    [
        new("Last activity", "last_seen"), new("Attempts", "attempts"), new("First seen", "first_seen"),
        new("IP", "ip"), new("Country", "country"),
    ];
    public IReadOnlyList<Choice<int?>> DurationChoices => Durations.All;

    public ObservableCollection<IpRecord> Items { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private Choice<string> _statusFilter;
    [ObservableProperty] private Choice<string> _sort;
    [ObservableProperty] private int _page;
    [ObservableProperty] private long _total;
    [ObservableProperty] private IpRecord? _selected;
    [ObservableProperty] private IpDetail? _detail;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private Choice<int?> _banDuration;
    [ObservableProperty] private string _banReason = "";
    [ObservableProperty] private bool _resetCounters = true;

    /// <summary>The query is a valid IP with no records: offer to ban it directly.</summary>
    public string? UnknownQueryIp => Items.Count == 0 && IpUtil.Normalize(Query) is { } ip ? ip : null;
    public string PageLabel => Total == 0 ? "0 results" : $"{Page * PageSize + 1}–{Math.Min(Total, (Page + 1) * PageSize)} of {Total}";
    public bool HasPrev => Page > 0;
    public bool HasNext => (Page + 1) * PageSize < Total;
    public bool SelectedIsBanned => Detail?.Record.IsBannedAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == true;
    public string SelectedState => Detail?.Record is { } r
        ? r.IsBannedAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ? $"{Fmt.BanKind(r.BanKind, r.BanUntil)} · {Fmt.Remaining(r.BanUntil)}"
            : r.Whitelisted ? "WHITELISTED" : "NOT BANNED"
        : "";

    partial void OnQueryChanged(string value) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    partial void OnStatusFilterChanged(Choice<string> value) { Page = 0; _ = SafeLoadAsync(); }
    partial void OnSortChanged(Choice<string> value) { Page = 0; _ = SafeLoadAsync(); }
    partial void OnSelectedChanged(IpRecord? value) => _ = LoadDetailAsync(value?.Ip);
    partial void OnDetailChanged(IpDetail? value)
    {
        Notes = value?.Record.Notes ?? "";
        OnPropertyChanged(nameof(SelectedIsBanned));
        OnPropertyChanged(nameof(SelectedState));
    }

    public void Focus(string ip)
    {
        _pendingFocus = IpUtil.Normalize(ip) ?? ip;
        StatusFilter = StatusFilters[0];
        Query = _pendingFocus;
    }

    public override async Task LoadAsync()
    {
        var url = $"api/ips?q={Uri.EscapeDataString(Query.Trim())}&status={StatusFilter.Value}&sort={Sort.Value}" +
                  $"&desc={(Sort.Value == "ip" || Sort.Value == "country" ? "false" : "true")}&page={Page}&pageSize={PageSize}";
        var result = await Api.Get<PagedResult<IpRecord>>(url);
        var keep = _pendingFocus ?? Selected?.Ip;
        Items.Clear();
        foreach (var r in result.Items) Items.Add(r);
        Total = result.Total;
        Selected = Items.FirstOrDefault(i => i.Ip == keep) ?? (_pendingFocus != null ? Items.FirstOrDefault() : null);
        _pendingFocus = null;
        if (Selected == null) Detail = null;
        OnPropertyChanged(nameof(UnknownQueryIp));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(HasNext));
    }

    private async Task LoadDetailAsync(string? ip)
    {
        if (ip == null) { Detail = null; return; }
        try { Detail = await Api.Get<IpDetail>($"api/ips/{Uri.EscapeDataString(ip)}"); }
        catch (Exception ex) { Shell.Toast(ex.Message, "error"); }
    }

    [RelayCommand] private async Task PrevPageAsync() { if (Page > 0) { Page--; await SafeLoadAsync(); } }
    [RelayCommand] private async Task NextPageAsync() { if (HasNext) { Page++; await SafeLoadAsync(); } }
    [RelayCommand] private Task RefreshAsync() => SafeLoadAsync();

    [RelayCommand]
    private async Task BanAsync(string? ipOverride)
    {
        var ip = ipOverride ?? Detail?.Record.Ip;
        if (ip == null) return;
        var label = BanDuration.Value == null ? "permanently" : $"for {BanDuration.Label}";
        if (!Dialogs.Confirm($"Ban {ip} {label}?", "A block rule will be added to Windows Firewall and its open SMB sessions will be closed.", "BAN"))
            return;
        if (await Run(() => Api.Post<BanRecord>("api/bans", new BanRequest(ip, BanDuration.Value, BanReason)), $"{ip} banned"))
        {
            BanReason = "";
            _pendingFocus = ip;
            await SafeLoadAsync();
        }
    }

    [RelayCommand]
    private async Task UnbanAsync()
    {
        if (Detail?.Record.Ip is not { } ip) return;
        if (await Run(() => Api.Post<bool>("api/unban", new UnbanRequest(ip, ResetCounters)), $"{ip} unbanned"))
            await LoadDetailAsync(ip);
    }

    [RelayCommand]
    private async Task WhitelistAsync()
    {
        if (Detail?.Record.Ip is not { } ip) return;
        if (!Dialogs.Confirm($"Add {ip} to the whitelist?", "It will never be banned, and any current ban will be lifted.", "ADD"))
            return;
        if (await Run(() => Api.Post<bool>("api/whitelist", new WhitelistRequest(ip, "Added from the IP explorer")), $"{ip} added to the whitelist"))
            await LoadDetailAsync(ip);
    }

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        if (Detail?.Record.Ip is not { } ip) return;
        await Run(() => Api.Post<bool>("api/ips/notes", new NoteRequest(ip, Notes)), "Note saved");
    }

    [RelayCommand]
    private void CopyIp()
    {
        if (Detail?.Record.Ip is { } ip)
        {
            try { System.Windows.Clipboard.SetText(ip); Shell.Toast($"{ip} copied", "ok"); } catch { /* clipboard busy */ }
        }
    }

    public override void OnLiveEvent(LiveEvent e)
    {
        if (e.Type is not ("attempt" or "ban" or "unban" or "changed")) return;
        if (Detail?.Record.Ip is { } ip && e.Ip == ip) _ = LoadDetailAsync(ip);
        _liveDebounce.Stop();
        _liveDebounce.Start();
    }
}

// =============================================================================== bans

public sealed partial class BanRow : ObservableObject
{
    public required BanRecord Ban { get; init; }
    public string Remaining => Fmt.Remaining(Ban.ExpiresAt);
    public double Progress => Ban.ExpiresAt is { } e && e > Ban.StartedAt
        ? Math.Clamp((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Ban.StartedAt) / (double)(e - Ban.StartedAt), 0, 1)
        : 1;
    public string KindLabel => Fmt.BanKind(Ban.Kind, Ban.ExpiresAt);
    public string Tone => Ban.ExpiresAt == null ? "perm" : Ban.Kind;
    public void Tick()
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(Progress));
    }
}

public sealed partial class BansViewModel : PageViewModel
{
    private readonly DispatcherTimer _ticker;

    public BansViewModel(ShellViewModel shell) : base(shell)
    {
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => { foreach (var r in Active) r.Tick(); };
        _ticker.Start();
        _banDuration = Durations.All[^1];
    }

    public override string Key => "bans";
    public override string Title => "Bans";
    public override string Subtitle => "Active Windows Firewall blocks and the full ban history.";

    public IReadOnlyList<Choice<int?>> DurationChoices => Durations.All;
    public ObservableCollection<BanRow> Active { get; } = new();
    public ObservableCollection<BanRecord> History { get; } = new();

    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string _newReason = "";
    [ObservableProperty] private Choice<int?> _banDuration;
    [ObservableProperty] private bool _resetCounters = true;

    partial void OnShowHistoryChanged(bool value) => _ = SafeLoadAsync();

    public override async Task LoadAsync()
    {
        var active = await Api.Get<List<BanRecord>>("api/bans/active");
        Active.Clear();
        foreach (var b in active) Active.Add(new BanRow { Ban = b });
        if (ShowHistory)
        {
            var history = await Api.Get<List<BanRecord>>("api/bans/history?limit=1000");
            History.Clear();
            foreach (var b in history) History.Add(b);
        }
    }

    [RelayCommand]
    private async Task BanAsync()
    {
        var ip = IpUtil.Normalize(NewIp);
        if (ip == null) { Shell.Toast("Enter a valid IP (IPv4 or IPv6)", "error"); return; }
        if (await Run(() => Api.Post<BanRecord>("api/bans", new BanRequest(ip, BanDuration.Value, NewReason)), $"{ip} banned"))
        {
            NewIp = "";
            NewReason = "";
            await SafeLoadAsync();
        }
    }

    [RelayCommand]
    private async Task UnbanAsync(BanRow? row)
    {
        if (row == null) return;
        if (await Run(() => Api.Post<bool>("api/unban", new UnbanRequest(row.Ban.Ip, ResetCounters)), $"{row.Ban.Ip} unbanned"))
            Active.Remove(row);
    }

    [RelayCommand]
    private async Task UnbanAllTemporaryAsync()
    {
        var temps = Active.Where(r => r.Ban.ExpiresAt != null).ToList();
        if (temps.Count == 0) return;
        if (!Dialogs.Confirm($"Lift {temps.Count} temporary ban(s)?", "Permanent bans are not touched.", "UNBAN")) return;
        await Run(async () =>
        {
            foreach (var r in temps) await Api.Post<bool>("api/unban", new UnbanRequest(r.Ban.Ip, ResetCounters));
        }, "Temporary bans lifted");
        await SafeLoadAsync();
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        await Run(async () =>
        {
            var r = await Api.Post<System.Text.Json.JsonElement>("api/firewall/reconcile", null);
            Shell.Toast($"Firewall synced: {r.GetProperty("added").GetInt32()} rule(s) created, {r.GetProperty("removed").GetInt32()} removed", "ok");
        });
    }

    [RelayCommand] private Task RefreshAsync() => SafeLoadAsync();

    public override void OnLiveEvent(LiveEvent e)
    {
        if (e.Type == "ban" && e.Ban != null)
        {
            var existing = Active.FirstOrDefault(r => r.Ban.Ip == e.Ban.Ip);
            if (existing != null) Active.Remove(existing);
            Active.Insert(0, new BanRow { Ban = e.Ban });
            if (ShowHistory) History.Insert(0, e.Ban);
        }
        else if (e.Type == "unban")
        {
            var existing = Active.FirstOrDefault(r => r.Ban.Ip == e.Ip);
            if (existing != null) Active.Remove(existing);
        }
    }
}

// =============================================================================== whitelist

public sealed partial class WhitelistViewModel : PageViewModel
{
    public WhitelistViewModel(ShellViewModel shell) : base(shell) { }

    public override string Key => "whitelist";
    public override string Title => "Whitelist";
    public override string Subtitle => "IPs, CIDR ranges and domains that are never banned. Loopback and this machine's own IPs are always trusted.";

    public ObservableCollection<WhitelistEntry> Items { get; } = new();

    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string _newNote = "";

    public override async Task LoadAsync()
    {
        var list = await Api.Get<List<WhitelistEntry>>("api/whitelist");
        Items.Clear();
        foreach (var w in list) Items.Add(w);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewValue)) return;
        if (await Run(() => Api.Post<bool>("api/whitelist", new WhitelistRequest(NewValue.Trim(), NewNote)), $"{NewValue.Trim()} added"))
        {
            NewValue = "";
            NewNote = "";
            await SafeLoadAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(WhitelistEntry? entry)
    {
        if (entry == null) return;
        if (!Dialogs.Confirm($"Remove {entry.Value} from the whitelist?", "It will be evaluated by the detection engine again.", "REMOVE")) return;
        if (await Run(() => Api.Delete($"api/whitelist/{entry.Id}"), $"{entry.Value} removed"))
            Items.Remove(entry);
    }

    public override void OnLiveEvent(LiveEvent e)
    {
        if (e.Type == "changed" && e.Message is "whitelist" or "migration" or "restore" && Shell.CurrentPage == this)
            _ = SafeLoadAsync();
    }
}

// =============================================================================== audit

public sealed partial class AuditViewModel : PageViewModel
{
    public AuditViewModel(ShellViewModel shell) : base(shell) { }

    public override string Key => "audit";
    public override string Title => "Audit log";
    public override string Subtitle => "Who banned, unbanned or changed the configuration, and when.";

    public ObservableCollection<AuditEntry> Items { get; } = new();

    public override async Task LoadAsync()
    {
        var list = await Api.Get<List<AuditEntry>>("api/audit?limit=1000");
        Items.Clear();
        foreach (var a in list) Items.Add(a);
    }

    [RelayCommand] private Task RefreshAsync() => SafeLoadAsync();

    public override void OnLiveEvent(LiveEvent e)
    {
        if (e.Type is "ban" or "unban" or "alert" or "changed" && Shell.CurrentPage == this) _ = SafeLoadAsync();
    }
}
