namespace OwlSamba.Core;

/// <summary>
/// All tunables. Persisted as JSON inside the SQLite settings table and editable live
/// from the desktop UI or the web dashboard.
/// </summary>
public sealed record OwlSettings
{
    // --- Detection -----------------------------------------------------------------
    /// <summary>Failed attempts inside <see cref="BurstWindowSeconds"/> that trigger a temporary ban.</summary>
    public int BurstThreshold { get; init; } = 3;
    public int BurstWindowSeconds { get; init; } = 60;
    public int TempBanMinutes { get; init; } = 15;

    /// <summary>Accumulated attempts that trigger a permanent ban.</summary>
    public int PermanentBanThreshold { get; init; } = 10;
    /// <summary>How far back attempts accumulate toward the permanent ban. 0 = forever.</summary>
    public int AccumulationWindowHours { get; init; } = 24;

    /// <summary>When false the app only monitors and alerts (no firewall rules).</summary>
    public bool EnableBanning { get; init; } = true;
    public bool KillSmbSessionsOnBan { get; init; } = true;

    /// <summary>Raise an alert when an IP with recent failures logs on successfully.</summary>
    public bool AlertOnSuccessAfterFailures { get; init; } = true;
    public bool BanOnSuccessAfterFailures { get; init; }
    public int SuccessAfterFailuresLookbackMinutes { get; init; } = 60;

    // --- Event sources -------------------------------------------------------------
    public bool MonitorSecurityLog { get; init; } = true;
    public bool MonitorSmbServerLog { get; init; } = true;
    public bool AutoEnableAuditPolicy { get; init; } = true;

    // --- Integrations --------------------------------------------------------------
    public bool DiscordEnabled { get; init; }
    public string DiscordWebhookUrl { get; init; } = "";
    public bool DiscordNotifyTempBans { get; init; } = true;
    public bool DiscordNotifyPermanentBans { get; init; } = true;
    public bool DiscordNotifySuspiciousLogons { get; init; } = true;

    public bool GeoIpEnabled { get; init; } = true;

    // --- Web dashboard -------------------------------------------------------------
    public bool WebDashboardEnabled { get; init; } = true;
    public int WebDashboardPort { get; init; } = 8443;
    public bool WebDashboardHttps { get; init; } = true;
    public string WebDashboardUser { get; init; } = "admin";
    /// <summary>PBKDF2 hash ("pbkdf2$iter$salt$hash") or a migrated bcrypt hash.</summary>
    public string WebDashboardPasswordHash { get; init; } = "";

    // --- Data ----------------------------------------------------------------------
    public int RetentionDays { get; init; } = 180;
    public string HostnameOverride { get; init; } = "";

    public OwlSettings Normalize() => this with
    {
        BurstThreshold = Math.Clamp(BurstThreshold, 1, 1000),
        BurstWindowSeconds = Math.Clamp(BurstWindowSeconds, 1, 86_400),
        TempBanMinutes = Math.Clamp(TempBanMinutes, 1, 525_600),
        PermanentBanThreshold = Math.Clamp(PermanentBanThreshold, 1, 100_000),
        AccumulationWindowHours = Math.Clamp(AccumulationWindowHours, 0, 87_600),
        SuccessAfterFailuresLookbackMinutes = Math.Clamp(SuccessAfterFailuresLookbackMinutes, 1, 10_080),
        WebDashboardPort = Math.Clamp(WebDashboardPort, 1, 65_535),
        RetentionDays = Math.Clamp(RetentionDays, 1, 3650),
        DiscordWebhookUrl = DiscordWebhookUrl?.Trim() ?? "",
        WebDashboardUser = string.IsNullOrWhiteSpace(WebDashboardUser) ? "admin" : WebDashboardUser.Trim(),
        HostnameOverride = HostnameOverride?.Trim() ?? "",
        WebDashboardPasswordHash = WebDashboardPasswordHash ?? "",
    };

    /// <summary>Copy safe to send to clients (no password hash).</summary>
    public OwlSettings Redacted() => this with { WebDashboardPasswordHash = "" };

    public string Hostname => string.IsNullOrWhiteSpace(HostnameOverride) ? Environment.MachineName : HostnameOverride;
}
