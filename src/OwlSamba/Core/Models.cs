namespace OwlSamba.Core;

// Timestamps are Unix epoch milliseconds (UTC) everywhere: DB, API, SSE and UI.

public static class Sources
{
    public const string Security = "security";   // Security log, event 4625
    public const string SmbServer = "smb";       // Microsoft-Windows-SMBServer/Security, event 551
    public const string Web = "web";             // failed logins against the OwlSamba web dashboard
}

public static class BanKinds
{
    public const string Temporary = "temp";
    public const string Permanent = "perm";
    public const string Manual = "manual";
}

/// <summary>A single failed authentication observed by one of the event sources.</summary>
public sealed record FailedLogon(
    string Ip,
    long OccurredAt,
    string User,
    string Workstation,
    string Source,
    string? Status = null,
    int? LogonType = null);

public sealed class IpRecord
{
    public string Ip { get; set; } = "";
    public long FirstSeen { get; set; }
    public long LastSeen { get; set; }
    public long TotalAttempts { get; set; }
    public string? LastUser { get; set; }
    public string? LastWorkstation { get; set; }
    public string? BanKind { get; set; }
    public long? BanStartedAt { get; set; }
    public long? BanUntil { get; set; }
    public string? BanReason { get; set; }
    public long BanCount { get; set; }
    public long CounterResetAt { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? Isp { get; set; }
    public long? GeoAt { get; set; }
    public string? Notes { get; set; }
    public long? SuspiciousAt { get; set; }

    // Filled by queries / API, not stored.
    public long RecentAttempts { get; set; }
    public bool Whitelisted { get; set; }

    public bool IsBannedAt(long now) => BanKind != null && (BanUntil == null || BanUntil > now);
    public bool IsPermanent => BanKind != null && BanUntil == null;
}

public sealed class AttemptRecord
{
    public long Id { get; set; }
    public string Ip { get; set; } = "";
    public long OccurredAt { get; set; }
    public string? User { get; set; }
    public string? Workstation { get; set; }
    public string Source { get; set; } = "";
    public string? Status { get; set; }
    public int? LogonType { get; set; }
    public bool Whitelisted { get; set; }
    public string? CountryCode { get; set; }
}

public sealed class BanRecord
{
    public long Id { get; set; }
    public string Ip { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Reason { get; set; }
    public long StartedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public long? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public string? CreatedBy { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public long TotalAttempts { get; set; }
}

public sealed class WhitelistEntry
{
    public long Id { get; set; }
    public string Value { get; set; } = "";
    public string Kind { get; set; } = "ip";   // ip | cidr | domain
    public string? Note { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public long At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Details { get; set; }
}

public sealed class TopItem
{
    public string Key { get; set; } = "";
    public long Count { get; set; }
    public string? CountryCode { get; set; }
}

public sealed class DashboardStats
{
    public long Attempts24h { get; set; }
    public long AttemptsTotal { get; set; }
    public long UniqueIps24h { get; set; }
    public long ActiveTempBans { get; set; }
    public long PermanentBans { get; set; }
    public long Bans24h { get; set; }
    public long? LastAttemptAt { get; set; }
    /// <summary>24 buckets, oldest first; the last bucket is the current hour.</summary>
    public long[] Hourly { get; set; } = new long[24];
    public long HourlyStart { get; set; }
    public List<TopItem> TopIps { get; set; } = new();
    public List<TopItem> TopUsers { get; set; } = new();
}

public sealed class SourceStatus
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public string? Error { get; set; }
    public long EventsSeen { get; set; }
}

public sealed class ServiceStatus
{
    public string Version { get; set; } = AppInfo.Version;
    public string Hostname { get; set; } = "";
    public long StartedAt { get; set; }
    public bool BanningEnabled { get; set; }
    public bool FirewallAvailable { get; set; }
    public bool FirewallAllProfilesOn { get; set; }
    public string? FirewallError { get; set; }
    public bool AuditPolicyOk { get; set; }
    public string? AuditPolicyInfo { get; set; }
    public long? LastEventAt { get; set; }
    public long EventsProcessed { get; set; }
    public List<SourceStatus> Sources { get; set; } = new();
    public bool WebDashboardRunning { get; set; }
    public string? WebDashboardUrl { get; set; }
}

public sealed class IpDetail
{
    public IpRecord Record { get; set; } = new();
    public List<AttemptRecord> Attempts { get; set; } = new();
    public List<BanRecord> Bans { get; set; } = new();
}

public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public long Total { get; set; }
}

public sealed record BanRequest(string Ip, int? Minutes, string? Reason);
public sealed record UnbanRequest(string Ip, bool ResetCounters = true);
public sealed record WhitelistRequest(string Value, string? Note);
public sealed record NoteRequest(string Ip, string? Notes);
public sealed record LoginRequest(string Username, string Password);
public sealed record PasswordRequest(string Username, string Password);
public sealed record MigrateRequest(string Path, bool ReplaceLegacyFirewallRules = true);

public sealed class MigrationReport
{
    public List<string> FilesProcessed { get; set; } = new();
    public int IpsImported { get; set; }
    public int BansImported { get; set; }
    public int AttemptsImported { get; set; }
    public int WhitelistImported { get; set; }
    public bool SettingsImported { get; set; }
    public int LegacyRulesRemoved { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Envelope pushed over the real-time stream (Server-Sent Events).</summary>
public sealed class LiveEvent
{
    public string Type { get; set; } = "";   // attempt | ban | unban | alert | changed
    public long At { get; set; }
    public string? Ip { get; set; }
    public string? Message { get; set; }
    public AttemptRecord? Attempt { get; set; }
    public BanRecord? Ban { get; set; }
}
