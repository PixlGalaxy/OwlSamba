using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OwlSamba.Core;
using OwlSamba.Platform;

namespace OwlSamba.UI.ViewModels;

/// <summary>Mutable, bindable mirror of <see cref="OwlSettings"/>.</summary>
public sealed partial class SettingsForm : ObservableObject
{
    [ObservableProperty] private int _burstThreshold;
    [ObservableProperty] private int _burstWindowSeconds;
    [ObservableProperty] private int _tempBanMinutes;
    [ObservableProperty] private int _permanentBanThreshold;
    [ObservableProperty] private int _accumulationWindowHours;
    [ObservableProperty] private bool _enableBanning;
    [ObservableProperty] private bool _killSmbSessionsOnBan;
    [ObservableProperty] private bool _alertOnSuccessAfterFailures;
    [ObservableProperty] private bool _banOnSuccessAfterFailures;
    [ObservableProperty] private int _successAfterFailuresLookbackMinutes;
    [ObservableProperty] private bool _monitorSecurityLog;
    [ObservableProperty] private bool _monitorSmbServerLog;
    [ObservableProperty] private bool _autoEnableAuditPolicy;
    [ObservableProperty] private bool _discordEnabled;
    [ObservableProperty] private string _discordWebhookUrl = "";
    [ObservableProperty] private bool _discordNotifyTempBans;
    [ObservableProperty] private bool _discordNotifyPermanentBans;
    [ObservableProperty] private bool _discordNotifySuspiciousLogons;
    [ObservableProperty] private bool _geoIpEnabled;
    [ObservableProperty] private bool _webDashboardEnabled;
    [ObservableProperty] private int _webDashboardPort;
    [ObservableProperty] private bool _webDashboardHttps;
    [ObservableProperty] private string _webDashboardUser = "";
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private string _hostnameOverride = "";

    public static SettingsForm From(OwlSettings s) => new()
    {
        BurstThreshold = s.BurstThreshold,
        BurstWindowSeconds = s.BurstWindowSeconds,
        TempBanMinutes = s.TempBanMinutes,
        PermanentBanThreshold = s.PermanentBanThreshold,
        AccumulationWindowHours = s.AccumulationWindowHours,
        EnableBanning = s.EnableBanning,
        KillSmbSessionsOnBan = s.KillSmbSessionsOnBan,
        AlertOnSuccessAfterFailures = s.AlertOnSuccessAfterFailures,
        BanOnSuccessAfterFailures = s.BanOnSuccessAfterFailures,
        SuccessAfterFailuresLookbackMinutes = s.SuccessAfterFailuresLookbackMinutes,
        MonitorSecurityLog = s.MonitorSecurityLog,
        MonitorSmbServerLog = s.MonitorSmbServerLog,
        AutoEnableAuditPolicy = s.AutoEnableAuditPolicy,
        DiscordEnabled = s.DiscordEnabled,
        DiscordWebhookUrl = s.DiscordWebhookUrl,
        DiscordNotifyTempBans = s.DiscordNotifyTempBans,
        DiscordNotifyPermanentBans = s.DiscordNotifyPermanentBans,
        DiscordNotifySuspiciousLogons = s.DiscordNotifySuspiciousLogons,
        GeoIpEnabled = s.GeoIpEnabled,
        WebDashboardEnabled = s.WebDashboardEnabled,
        WebDashboardPort = s.WebDashboardPort,
        WebDashboardHttps = s.WebDashboardHttps,
        WebDashboardUser = s.WebDashboardUser,
        RetentionDays = s.RetentionDays,
        HostnameOverride = s.HostnameOverride,
    };

    public OwlSettings To() => new()
    {
        BurstThreshold = BurstThreshold,
        BurstWindowSeconds = BurstWindowSeconds,
        TempBanMinutes = TempBanMinutes,
        PermanentBanThreshold = PermanentBanThreshold,
        AccumulationWindowHours = AccumulationWindowHours,
        EnableBanning = EnableBanning,
        KillSmbSessionsOnBan = KillSmbSessionsOnBan,
        AlertOnSuccessAfterFailures = AlertOnSuccessAfterFailures,
        BanOnSuccessAfterFailures = BanOnSuccessAfterFailures,
        SuccessAfterFailuresLookbackMinutes = SuccessAfterFailuresLookbackMinutes,
        MonitorSecurityLog = MonitorSecurityLog,
        MonitorSmbServerLog = MonitorSmbServerLog,
        AutoEnableAuditPolicy = AutoEnableAuditPolicy,
        DiscordEnabled = DiscordEnabled,
        DiscordWebhookUrl = DiscordWebhookUrl,
        DiscordNotifyTempBans = DiscordNotifyTempBans,
        DiscordNotifyPermanentBans = DiscordNotifyPermanentBans,
        DiscordNotifySuspiciousLogons = DiscordNotifySuspiciousLogons,
        GeoIpEnabled = GeoIpEnabled,
        WebDashboardEnabled = WebDashboardEnabled,
        WebDashboardPort = WebDashboardPort,
        WebDashboardHttps = WebDashboardHttps,
        WebDashboardUser = WebDashboardUser,
        RetentionDays = RetentionDays,
        HostnameOverride = HostnameOverride,
    };
}

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(ShellViewModel shell) : base(shell) { }

    public override string Key => "settings";
    public override string Title => "Settings";
    public override string Subtitle => "Ban policy, event sources, notifications, web dashboard and data.";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PolicySummary))] private SettingsForm? _form;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private bool _trayAutostart;
    [ObservableProperty] private string _migratePath = "";
    [ObservableProperty] private string _migrationResult = "";
    [ObservableProperty] private bool _passwordSet;

    private OwlSettings? _loaded;

    public bool MinimizeToTray
    {
        get => Shell.Prefs.MinimizeToTray;
        set { Shell.Prefs.MinimizeToTray = value; Shell.Prefs.Save(); OnPropertyChanged(); }
    }

    public bool BanNotifications
    {
        get => Shell.Prefs.BanNotifications;
        set { Shell.Prefs.BanNotifications = value; Shell.Prefs.Save(); OnPropertyChanged(); }
    }

    public string PolicySummary => Form is not { } f ? "" :
        $"With these settings an attacker can only try {f.BurstThreshold} password(s) before being blocked for {f.TempBanMinutes} min, " +
        $"and after {f.PermanentBanThreshold} accumulated attempts " +
        (f.AccumulationWindowHours == 0 ? "(never expiring) " : $"within {f.AccumulationWindowHours} h ") +
        $"it is banned forever (≈ {EstimatePermanentMinutes(f)} min of continuous attack).";

    private static int EstimatePermanentMinutes(SettingsForm f)
    {
        if (f.BurstThreshold <= 0) return 0;
        var cycles = (int)Math.Ceiling(f.PermanentBanThreshold / (double)f.BurstThreshold) - 1;
        return Math.Max(0, cycles) * f.TempBanMinutes;
    }

    public override async Task LoadAsync()
    {
        _loaded = await Api.Get<OwlSettings>("api/settings");
        if (Form != null) Form.PropertyChanged -= OnFormChanged;
        Form = SettingsForm.From(_loaded);
        Form.PropertyChanged += OnFormChanged;
        Dirty = false;
        TrayAutostart = await ServiceInstaller.IsTrayAutostartEnabledAsync();
        var state = await Api.Get<System.Text.Json.JsonElement>("api/auth/state");
        PasswordSet = state.TryGetProperty("passwordSet", out var p) && p.GetBoolean();
    }

    private void OnFormChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dirty = true;
        OnPropertyChanged(nameof(PolicySummary));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Form == null) return;
        if (!Form.EnableBanning && _loaded?.EnableBanning == true &&
            !Dialogs.Confirm("Disable banning?", "OwlSamba will only monitor and alert: no attacker will be blocked.", "DISABLE"))
            return;
        if (await Run(async () =>
            {
                _loaded = await Api.Put<OwlSettings>("api/settings", Form.To());
            }, "Settings saved and applied"))
            Dirty = false;
    }

    [RelayCommand]
    private void Revert()
    {
        if (_loaded == null) return;
        if (Form != null) Form.PropertyChanged -= OnFormChanged;
        Form = SettingsForm.From(_loaded);
        Form.PropertyChanged += OnFormChanged;
        Dirty = false;
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        if (Form != null) Form.PropertyChanged -= OnFormChanged;
        var d = new OwlSettings();
        var f = SettingsForm.From(d);
        // Keep integration settings the user typed.
        if (Form != null)
        {
            f.DiscordWebhookUrl = Form.DiscordWebhookUrl;
            f.DiscordEnabled = Form.DiscordEnabled;
            f.WebDashboardUser = Form.WebDashboardUser;
            f.HostnameOverride = Form.HostnameOverride;
        }
        Form = f;
        Form.PropertyChanged += OnFormChanged;
        Dirty = true;
    }

    public async Task SetPasswordAsync(string password, string confirm)
    {
        if (Form == null) return;
        if (password != confirm) { Shell.Toast("Passwords do not match", "error"); return; }
        if (await Run(() => Api.Post<bool>("api/settings/password", new PasswordRequest(Form.WebDashboardUser, password)),
                "Web dashboard password updated"))
            PasswordSet = true;
    }

    [RelayCommand]
    private Task TestDiscordAsync() =>
        Run(async () =>
        {
            await Api.Post<System.Text.Json.JsonElement>("api/discord/test", new { url = Form?.DiscordWebhookUrl });
            Shell.Toast("Test message sent to Discord", "ok");
        });

    [RelayCommand]
    private void OpenWebDashboard()
    {
        if (Shell.Status?.WebDashboardUrl is { } url)
            Process.Start(new ProcessStartInfo(url.Replace("://" + Shell.Hostname, "://localhost")) { UseShellExecute = true });
    }

    // ------------------------------------------------------------- service

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        await Run(async () => await Task.Run(ServiceInstaller.RestartAsync), "Service restarted");
        await Task.Delay(1500);
        Shell.CurrentPage.OnActivated();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (!Dialogs.Confirm("Uninstall the OwlSamba service?",
                "Real-time protection will stop. Existing block rules stay in the firewall and your data is kept in ProgramData.",
                "UNINSTALL", danger: true))
            return;
        await Run(() => Task.Run(ServiceInstaller.UninstallAsync), "Service uninstalled");
    }

    partial void OnTrayAutostartChanged(bool value)
    {
        _ = Task.Run(() => ServiceInstaller.SetTrayAutostartAsync(value));
    }

    // ------------------------------------------------------------- data

    private static string ListSep => CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? "semicolon" : "comma";

    [RelayCommand]
    private async Task ExportAsync(string kind)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"owlsamba-{kind}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        await Run(() => Api.Download($"api/export/{kind}?days=3650&sep={ListSep}", dlg.FileName), "Exported: " + Path.GetFileName(dlg.FileName));
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"owlsamba-backup-{DateTime.Now:yyyyMMdd-HHmm}.db",
            Filter = "OwlSamba database (*.db)|*.db",
        };
        if (dlg.ShowDialog() != true) return;
        await Run(() => Api.Download("api/backup", dlg.FileName), "Backup saved");
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var dlg = new OpenFileDialog { Filter = "OwlSamba database (*.db)|*.db|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!Dialogs.Confirm("Restore this backup?",
                "ALL current data (IPs, bans, whitelist and settings) will be replaced. A safety copy is taken first.",
                "RESTORE", danger: true))
            return;
        if (await Run(() => Api.UploadRestore(dlg.FileName), "Backup restored"))
            await SafeLoadAsync();
    }

    [RelayCommand]
    private void BrowseLegacy()
    {
        var dlg = new OpenFolderDialog { Title = "Folder of the previous OwlSamba version (with banned_ips.json, .env or DataBase.db)" };
        if (dlg.ShowDialog() == true) MigratePath = dlg.FolderName;
    }

    [RelayCommand]
    private async Task MigrateAsync()
    {
        if (string.IsNullOrWhiteSpace(MigratePath)) { BrowseLegacy(); if (string.IsNullOrWhiteSpace(MigratePath)) return; }
        await Run(async () =>
        {
            var r = await Api.Post<MigrationReport>("api/migrate", new MigrateRequest(MigratePath));
            MigrationResult =
                $"{r.FilesProcessed.Count} file(s) · {r.IpsImported} IPs · {r.BansImported} bans · {r.AttemptsImported} attempts · " +
                $"{r.WhitelistImported} whitelist · {r.LegacyRulesRemoved} v1 rules replaced" +
                (r.SettingsImported ? " · settings imported" : "") +
                (r.Warnings.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", r.Warnings) : "");
            Shell.Toast("Migration completed", "ok");
        });
        await SafeLoadAsync();
    }

    [RelayCommand] private void OpenDataFolder() => OpenFolder(Paths.DataDir);
    [RelayCommand] private void OpenLogsFolder() => OpenFolder(Paths.LogsDir);

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { /* ignore */ }
    }

    public override void OnLiveEvent(LiveEvent e)
    {
        if (e.Type == "changed" && e.Message is "settings" or "restore" or "migration" && !Dirty && Shell.CurrentPage == this)
            _ = SafeLoadAsync();
    }
}
