using System.Collections.ObjectModel;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwlSamba.Core;
using OwlSamba.Platform;

namespace OwlSamba.UI.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(ShellViewModel shell) => Shell = shell;
    public ShellViewModel Shell { get; }
    protected ApiClient Api => Shell.Api;

    public abstract string Key { get; }
    public abstract string Title { get; }
    public virtual string? Subtitle => null;

    [ObservableProperty] private bool _isBusy;

    public abstract Task LoadAsync();
    public virtual void OnLiveEvent(LiveEvent e) { }
    public virtual void OnActivated() => _ = SafeLoadAsync();

    public async Task SafeLoadAsync()
    {
        if (!Shell.IsConnected) return;
        try
        {
            IsBusy = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Shell.Toast(ex.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs an API action showing a toast on failure (and optionally on success).</summary>
    protected async Task<bool> Run(Func<Task> action, string? success = null)
    {
        try
        {
            IsBusy = true;
            await action();
            if (success != null) Shell.Toast(success, "ok");
            return true;
        }
        catch (Exception ex)
        {
            Shell.Toast(ex.Message, "error");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record ToastItem(string Message, string Kind);

public enum ConnectionState { Checking, NotInstalled, Stopped, Starting, Connected }

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _streamStarted;

    public ApiClient Api { get; } = new();
    public UiPrefs Prefs { get; } = UiPrefs.Load();

    public DashboardViewModel Dashboard { get; }
    public ActivityViewModel Activity { get; }
    public IpsViewModel Ips { get; }
    public BansViewModel Bans { get; }
    public WhitelistViewModel Whitelist { get; }
    public SettingsViewModel Settings { get; }
    public AuditViewModel Audit { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private ConnectionState _state = ConnectionState.Checking;
    [ObservableProperty] private ServiceStatus? _status;
    [ObservableProperty] private string _installProgress = "";
    [ObservableProperty] private bool _installing;

    public bool IsConnected => State == ConnectionState.Connected;
    public string AppVersion => AppInfo.Version;
    public string Hostname => Status?.Hostname ?? Environment.MachineName;

    /// <summary>Raised for ban/alert events so the tray can show a notification.</summary>
    public event Action<LiveEvent>? Notification;

    public ShellViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Dashboard = new DashboardViewModel(this);
        Activity = new ActivityViewModel(this);
        Ips = new IpsViewModel(this);
        Bans = new BansViewModel(this);
        Whitelist = new WhitelistViewModel(this);
        Settings = new SettingsViewModel(this);
        Audit = new AuditViewModel(this);
        Pages = [Dashboard, Activity, Ips, Bans, Whitelist, Audit, Settings];
        _currentPage = Dashboard;

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => await RefreshStateAsync(), dispatcher);
    }

    public async Task StartAsync()
    {
        await RefreshStateAsync();
        _statusTimer.Start();
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        if (value == ConnectionState.Connected) CurrentPage.OnActivated();
    }

    partial void OnStatusChanged(ServiceStatus? value) => OnPropertyChanged(nameof(Hostname));

    partial void OnCurrentPageChanged(PageViewModel value) => value.OnActivated();

    [RelayCommand]
    private void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page != null && page != CurrentPage) CurrentPage = page;
    }

    /// <summary>Jump to the IP explorer with an IP selected.</summary>
    [RelayCommand]
    public void OpenIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        Ips.Focus(ip);
        CurrentPage = Ips;
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            Status = await Api.Get<ServiceStatus>("api/status");
            State = ConnectionState.Connected;
            if (!_streamStarted)
            {
                _streamStarted = true;
                _ = Task.Run(() => Api.RunStreamAsync(
                    e => _dispatcher.BeginInvoke(() => Dispatch(e)),
                    connected => _dispatcher.BeginInvoke(() => { if (!connected && State == ConnectionState.Connected) _ = RefreshStateAsync(); }),
                    _cts.Token));
            }
        }
        catch
        {
            if (Installing) return;
            var svc = await Task.Run(ServiceInstaller.Status);
            State = svc switch
            {
                null => ConnectionState.NotInstalled,
                ServiceControllerStatus.Running or ServiceControllerStatus.StartPending => ConnectionState.Starting,
                _ => ConnectionState.Stopped,
            };
        }
    }

    private void Dispatch(LiveEvent e)
    {
        foreach (var p in Pages)
        {
            try { p.OnLiveEvent(e); }
            catch { /* a page bug must never kill the stream */ }
        }
        if (e.Type is "ban" or "alert") Notification?.Invoke(e);
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        Installing = true;
        try
        {
            var progress = new Progress<string>(s => InstallProgress = s);
            await Task.Run(() => ServiceInstaller.InstallAsync(progress));
            InstallProgress = "Connecting…";
            for (int i = 0; i < 40; i++)
            {
                try { await Api.Get<ServiceStatus>("api/status"); break; }
                catch { await Task.Delay(500); }
            }
            Toast("Service installed: protection is active", "ok");
        }
        catch (Exception ex)
        {
            Toast("Could not install the service: " + ex.Message, "error");
        }
        finally
        {
            Installing = false;
            InstallProgress = "";
            await RefreshStateAsync();
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        try
        {
            State = ConnectionState.Starting;
            await Task.Run(ServiceInstaller.StartAsync);
            await Task.Delay(800);
        }
        catch (Exception ex)
        {
            Toast("Could not start the service: " + ex.Message, "error");
        }
        await RefreshStateAsync();
    }

    public void Toast(string message, string kind = "info")
    {
        var t = new ToastItem(message, kind);
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == "error" ? 8 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    [RelayCommand]
    private void DismissToast(ToastItem t) => Toasts.Remove(t);

    public void Shutdown()
    {
        _cts.Cancel();
        _statusTimer.Stop();
        Api.Dispose();
    }
}

/// <summary>Per-user desktop preferences (not shared with the service).</summary>
public sealed class UiPrefs
{
    public bool MinimizeToTray { get; set; } = true;
    public bool BanNotifications { get; set; } = true;

    private static string File => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "ui.json");

    public static UiPrefs Load()
    {
        try { return JsonSerializer.Deserialize<UiPrefs>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch { /* best effort */ }
    }
}
