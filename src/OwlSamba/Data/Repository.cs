using System.Text;
using System.Text.Json;
using Dapper;
using OwlSamba.Core;

namespace OwlSamba.Data;

public sealed class Repository
{
    private const string SettingsKey = "settings.v2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Db _db;
    public Repository(Db db) => _db = db;
    public Db Db => _db;

    // ---------------------------------------------------------------- settings / kv

    public OwlSettings GetSettings()
    {
        var raw = GetValue(SettingsKey);
        if (string.IsNullOrEmpty(raw)) return new OwlSettings();
        try { return (JsonSerializer.Deserialize<OwlSettings>(raw, Json) ?? new OwlSettings()).Normalize(); }
        catch (JsonException) { return new OwlSettings(); }
    }

    public void SaveSettings(OwlSettings settings) =>
        SetValue(SettingsKey, JsonSerializer.Serialize(settings.Normalize(), Json));

    public bool HasSettings() => GetValue(SettingsKey) != null;

    public string? GetValue(string key)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key });
    }

    public void SetValue(string key, string? value)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO settings(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            new { key, value });
    }

    // ---------------------------------------------------------------- attempts

    /// <summary>Stores the attempt and updates the per-IP aggregate in one transaction.</summary>
    public (AttemptRecord attempt, IpRecord ip) RecordAttempt(FailedLogon e, bool whitelisted)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var id = c.ExecuteScalar<long>("""
            INSERT INTO attempts(ip, occurred_at, user, workstation, source, status, logon_type, whitelisted)
            VALUES (@Ip, @OccurredAt, @User, @Workstation, @Source, @Status, @LogonType, @whitelisted);
            SELECT last_insert_rowid();
            """, new { e.Ip, e.OccurredAt, e.User, e.Workstation, e.Source, e.Status, e.LogonType, whitelisted }, tx);

        c.Execute("""
            INSERT INTO ips(ip, first_seen, last_seen, total_attempts, last_user, last_workstation)
            VALUES (@Ip, @OccurredAt, @OccurredAt, 1, @User, @Workstation)
            ON CONFLICT(ip) DO UPDATE SET
                total_attempts   = total_attempts + 1,
                first_seen       = MIN(first_seen, excluded.first_seen),
                last_seen        = MAX(last_seen, excluded.last_seen),
                last_user        = CASE WHEN excluded.last_seen >= last_seen THEN excluded.last_user ELSE last_user END,
                last_workstation = CASE WHEN excluded.last_seen >= last_seen THEN excluded.last_workstation ELSE last_workstation END
            """, new { e.Ip, e.OccurredAt, e.User, e.Workstation }, tx);

        var ip = c.QuerySingle<IpRecord>("SELECT * FROM ips WHERE ip=@ip", new { ip = e.Ip }, tx);
        tx.Commit();

        var attempt = new AttemptRecord
        {
            Id = id, Ip = e.Ip, OccurredAt = e.OccurredAt, User = e.User, Workstation = e.Workstation,
            Source = e.Source, Status = e.Status, LogonType = e.LogonType, Whitelisted = whitelisted,
            CountryCode = ip.CountryCode,
        };
        return (attempt, ip);
    }

    /// <summary>Counts non-whitelisted attempts for an IP with since &lt; occurred_at &lt;= until.</summary>
    public long CountAttempts(string ip, long sinceExclusive, long untilInclusive)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM attempts
            WHERE ip=@ip AND whitelisted=0 AND occurred_at > @sinceExclusive AND occurred_at <= @untilInclusive
            """, new { ip, sinceExclusive, untilInclusive });
    }

    public List<AttemptRecord> RecentAttempts(int limit, string? search = null, long? beforeId = null)
    {
        using var c = _db.Open();
        var sql = new StringBuilder("""
            SELECT a.*, i.country_code FROM attempts a LEFT JOIN ips i ON i.ip = a.ip WHERE 1=1
            """);
        if (beforeId != null) sql.Append(" AND a.id < @beforeId");
        if (!string.IsNullOrWhiteSpace(search))
            sql.Append(" AND (a.ip LIKE @like OR a.user LIKE @like OR a.workstation LIKE @like)");
        sql.Append(" ORDER BY a.id DESC LIMIT @limit");
        return c.Query<AttemptRecord>(sql.ToString(),
            new { limit = Math.Clamp(limit, 1, 5000), beforeId, like = $"%{search?.Trim()}%" }).ToList();
    }

    public IEnumerable<AttemptRecord> AttemptsForExport(long from, long to)
    {
        using var c = _db.Open();
        foreach (var a in c.Query<AttemptRecord>("""
                     SELECT a.*, i.country_code FROM attempts a LEFT JOIN ips i ON i.ip=a.ip
                     WHERE a.occurred_at BETWEEN @from AND @to ORDER BY a.occurred_at
                     """, new { from, to }, buffered: false))
            yield return a;
    }

    public bool HadFailuresSince(string ip, long since)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM attempts WHERE ip=@ip AND whitelisted=0 AND occurred_at >= @since)",
            new { ip, since }) == 1;
    }

    public int PurgeAttemptsOlderThan(long cutoff)
    {
        using var c = _db.Open();
        var n = c.Execute("DELETE FROM attempts WHERE occurred_at < @cutoff", new { cutoff });
        c.Execute("DELETE FROM audit WHERE at < @cutoff", new { cutoff });
        return n;
    }

    // ---------------------------------------------------------------- ips & bans

    public IpRecord? GetIp(string ip)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<IpRecord>("SELECT * FROM ips WHERE ip=@ip", new { ip });
    }

    public BanRecord SetBan(string ip, string kind, long startedAt, long? until, string reason, string createdBy)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            INSERT INTO ips(ip, first_seen, last_seen, total_attempts) VALUES (@ip, @startedAt, @startedAt, 0)
            ON CONFLICT(ip) DO NOTHING
            """, new { ip, startedAt }, tx);
        c.Execute("UPDATE bans SET ended_at=@startedAt, end_reason='replaced' WHERE ip=@ip AND ended_at IS NULL",
            new { ip, startedAt }, tx);
        c.Execute("""
            UPDATE ips SET ban_kind=@kind, ban_started_at=@startedAt, ban_until=@until, ban_reason=@reason,
                           ban_count = ban_count + 1
            WHERE ip=@ip
            """, new { ip, kind, startedAt, until, reason }, tx);
        var id = c.ExecuteScalar<long>("""
            INSERT INTO bans(ip, kind, reason, started_at, expires_at, created_by)
            VALUES (@ip, @kind, @reason, @startedAt, @until, @createdBy);
            SELECT last_insert_rowid();
            """, new { ip, kind, reason, startedAt, until, createdBy }, tx);
        var info = c.QuerySingle<IpRecord>("SELECT * FROM ips WHERE ip=@ip", new { ip }, tx);
        tx.Commit();
        return new BanRecord
        {
            Id = id, Ip = ip, Kind = kind, Reason = reason, StartedAt = startedAt, ExpiresAt = until,
            CreatedBy = createdBy, Country = info.Country, CountryCode = info.CountryCode, TotalAttempts = info.TotalAttempts,
        };
    }

    /// <summary>Lifts the ban. With resetCounters, attempts before now no longer count toward bans.</summary>
    public void ClearBan(string ip, long endedAt, string endReason, bool resetCounters)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE ips SET ban_kind=NULL, ban_until=NULL, ban_reason=NULL,
                counter_reset_at = CASE WHEN @resetCounters THEN @endedAt ELSE counter_reset_at END
            WHERE ip=@ip
            """, new { ip, endedAt, resetCounters }, tx);
        c.Execute("UPDATE bans SET ended_at=@endedAt, end_reason=@endReason WHERE ip=@ip AND ended_at IS NULL",
            new { ip, endedAt, endReason }, tx);
        tx.Commit();
    }

    public List<string> ExpiredBans(long now)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT ip FROM ips WHERE ban_kind IS NOT NULL AND ban_until IS NOT NULL AND ban_until <= @now",
            new { now }).ToList();
    }

    public List<string> ActiveBannedIps(long now)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT ip FROM ips WHERE ban_kind IS NOT NULL AND (ban_until IS NULL OR ban_until > @now)",
            new { now }).ToList();
    }

    public List<BanRecord> ActiveBans(long now)
    {
        using var c = _db.Open();
        return c.Query<BanRecord>("""
            SELECT b.*, i.country, i.country_code, i.total_attempts
            FROM ips i JOIN bans b ON b.ip = i.ip AND b.ended_at IS NULL
            WHERE i.ban_kind IS NOT NULL AND (i.ban_until IS NULL OR i.ban_until > @now)
            ORDER BY b.started_at DESC
            """, new { now }).ToList();
    }

    public List<BanRecord> BanHistory(int limit)
    {
        using var c = _db.Open();
        return c.Query<BanRecord>("""
            SELECT b.*, i.country, i.country_code, i.total_attempts
            FROM bans b LEFT JOIN ips i ON i.ip = b.ip ORDER BY b.started_at DESC LIMIT @limit
            """, new { limit = Math.Clamp(limit, 1, 10_000) }).ToList();
    }

    public PagedResult<IpRecord> SearchIps(string? query, string status, string sort, bool desc, int page, int pageSize, long now)
    {
        using var c = _db.Open();
        var where = new StringBuilder(" WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(query))
            where.Append(" AND (ip LIKE @like OR last_user LIKE @like OR last_workstation LIKE @like OR country LIKE @like OR isp LIKE @like OR notes LIKE @like)");
        where.Append(status switch
        {
            "banned" => " AND ban_kind IS NOT NULL AND (ban_until IS NULL OR ban_until > @now)",
            "temp" => " AND ban_kind IS NOT NULL AND ban_until > @now",
            "perm" => " AND ban_kind IS NOT NULL AND ban_until IS NULL",
            "free" => " AND (ban_kind IS NULL OR ban_until <= @now)",
            "suspicious" => " AND suspicious_at IS NOT NULL",
            _ => "",
        });
        var orderBy = sort switch
        {
            "attempts" => "total_attempts",
            "ip" => "ip",
            "first_seen" => "first_seen",
            "country" => "country",
            _ => "last_seen",
        };
        var p = new
        {
            like = $"%{query?.Trim()}%", now,
            limit = Math.Clamp(pageSize, 1, 500),
            offset = Math.Max(0, page) * Math.Clamp(pageSize, 1, 500),
        };
        var total = c.ExecuteScalar<long>("SELECT COUNT(*) FROM ips" + where, p);
        var items = c.Query<IpRecord>(
            $"SELECT * FROM ips{where} ORDER BY {orderBy} {(desc ? "DESC" : "ASC")}, ip LIMIT @limit OFFSET @offset", p).ToList();
        return new PagedResult<IpRecord> { Items = items, Total = total };
    }

    public IEnumerable<IpRecord> AllIps()
    {
        using var c = _db.Open();
        foreach (var r in c.Query<IpRecord>("SELECT * FROM ips ORDER BY last_seen DESC", buffered: false))
            yield return r;
    }

    public IpDetail? GetIpDetail(string ip, int attemptLimit = 200)
    {
        using var c = _db.Open();
        var rec = c.QuerySingleOrDefault<IpRecord>("SELECT * FROM ips WHERE ip=@ip", new { ip });
        if (rec == null) return null;
        return new IpDetail
        {
            Record = rec,
            Attempts = c.Query<AttemptRecord>(
                "SELECT * FROM attempts WHERE ip=@ip ORDER BY occurred_at DESC LIMIT @attemptLimit",
                new { ip, attemptLimit }).ToList(),
            Bans = c.Query<BanRecord>("SELECT * FROM bans WHERE ip=@ip ORDER BY started_at DESC LIMIT 100", new { ip }).ToList(),
        };
    }

    public void SetNotes(string ip, string? notes)
    {
        using var c = _db.Open();
        c.Execute("UPDATE ips SET notes=@notes WHERE ip=@ip", new { ip, notes });
    }

    public void MarkSuspicious(string ip, long at)
    {
        using var c = _db.Open();
        c.Execute("UPDATE ips SET suspicious_at=@at WHERE ip=@ip", new { ip, at });
    }

    public void UpdateGeo(string ip, string? country, string? countryCode, string? isp, long at)
    {
        using var c = _db.Open();
        c.Execute("UPDATE ips SET country=@country, country_code=@countryCode, isp=@isp, geo_at=@at WHERE ip=@ip",
            new { ip, country, countryCode, isp, at });
    }

    public List<string> IpsMissingGeo(int limit)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT ip FROM ips WHERE geo_at IS NULL ORDER BY last_seen DESC LIMIT @limit",
            new { limit }).ToList();
    }

    // ---------------------------------------------------------------- stats

    public DashboardStats Stats(long now)
    {
        using var c = _db.Open();
        var dayAgo = now - 86_400_000L;
        var hourStart = now - now % 3_600_000L - 23 * 3_600_000L;
        var s = new DashboardStats
        {
            Attempts24h = c.ExecuteScalar<long>("SELECT COUNT(*) FROM attempts WHERE occurred_at >= @dayAgo AND whitelisted=0", new { dayAgo }),
            AttemptsTotal = c.ExecuteScalar<long>("SELECT COALESCE(SUM(total_attempts),0) FROM ips"),
            UniqueIps24h = c.ExecuteScalar<long>("SELECT COUNT(DISTINCT ip) FROM attempts WHERE occurred_at >= @dayAgo AND whitelisted=0", new { dayAgo }),
            ActiveTempBans = c.ExecuteScalar<long>("SELECT COUNT(*) FROM ips WHERE ban_kind IS NOT NULL AND ban_until > @now", new { now }),
            PermanentBans = c.ExecuteScalar<long>("SELECT COUNT(*) FROM ips WHERE ban_kind IS NOT NULL AND ban_until IS NULL"),
            Bans24h = c.ExecuteScalar<long>("SELECT COUNT(*) FROM bans WHERE started_at >= @dayAgo", new { dayAgo }),
            LastAttemptAt = c.ExecuteScalar<long?>("SELECT MAX(occurred_at) FROM attempts WHERE whitelisted=0"),
            HourlyStart = hourStart,
        };
        foreach (var row in c.Query<(long bucket, long count)>("""
                     SELECT (occurred_at - @hourStart) / 3600000 AS bucket, COUNT(*) AS count
                     FROM attempts WHERE occurred_at >= @hourStart AND whitelisted=0 GROUP BY bucket
                     """, new { hourStart }))
        {
            if (row.bucket is >= 0 and < 24) s.Hourly[row.bucket] = row.count;
        }
        s.TopIps = c.Query<TopItem>("""
            SELECT a.ip AS key, COUNT(*) AS count, i.country_code FROM attempts a LEFT JOIN ips i ON i.ip=a.ip
            WHERE a.occurred_at >= @dayAgo AND a.whitelisted=0 GROUP BY a.ip ORDER BY count DESC LIMIT 8
            """, new { dayAgo }).ToList();
        s.TopUsers = c.Query<TopItem>("""
            SELECT COALESCE(NULLIF(user,''),'-') AS key, COUNT(*) AS count FROM attempts
            WHERE occurred_at >= @dayAgo AND whitelisted=0 GROUP BY key ORDER BY count DESC LIMIT 8
            """, new { dayAgo }).ToList();
        return s;
    }

    // ---------------------------------------------------------------- whitelist

    public List<WhitelistEntry> Whitelist()
    {
        using var c = _db.Open();
        return c.Query<WhitelistEntry>("SELECT * FROM whitelist ORDER BY kind, value").ToList();
    }

    public bool AddWhitelist(string value, string kind, string? note, long now)
    {
        using var c = _db.Open();
        return c.Execute("""
            INSERT INTO whitelist(value, kind, note, created_at) VALUES (@value, @kind, @note, @now)
            ON CONFLICT(value) DO UPDATE SET note = COALESCE(excluded.note, note)
            """, new { value, kind, note, now }) > 0;
    }

    public bool RemoveWhitelist(long id)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM whitelist WHERE id=@id", new { id }) > 0;
    }

    // ---------------------------------------------------------------- audit

    public void Audit(string actor, string action, string? target, string? details, long now)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO audit(at, actor, action, target, details) VALUES (@now, @actor, @action, @target, @details)",
            new { now, actor, action, target, details });
    }

    public List<AuditEntry> AuditLog(int limit)
    {
        using var c = _db.Open();
        return c.Query<AuditEntry>("SELECT * FROM audit ORDER BY id DESC LIMIT @limit",
            new { limit = Math.Clamp(limit, 1, 5000) }).ToList();
    }

    // ---------------------------------------------------------------- import helpers

    /// <summary>Merges an IP aggregate coming from a legacy source.</summary>
    public void ImportIp(string ip, long firstSeen, long lastSeen, long attempts, string? user, string? workstation)
    {
        using var c = _db.Open();
        c.Execute("""
            INSERT INTO ips(ip, first_seen, last_seen, total_attempts, last_user, last_workstation)
            VALUES (@ip, @firstSeen, @lastSeen, @attempts, @user, @workstation)
            ON CONFLICT(ip) DO UPDATE SET
                first_seen = MIN(first_seen, excluded.first_seen),
                last_seen = MAX(last_seen, excluded.last_seen),
                total_attempts = MAX(total_attempts, excluded.total_attempts),
                last_user = COALESCE(last_user, excluded.last_user),
                last_workstation = COALESCE(last_workstation, excluded.last_workstation)
            """, new { ip, firstSeen, lastSeen, attempts, user, workstation });
    }

    /// <summary>Inserts historical attempts without touching aggregates; skips exact duplicates.</summary>
    public int ImportAttempts(IEnumerable<FailedLogon> attempts)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        int n = 0;
        foreach (var e in attempts)
        {
            n += c.Execute("""
                INSERT INTO attempts(ip, occurred_at, user, workstation, source, status, logon_type, whitelisted)
                SELECT @Ip, @OccurredAt, @User, @Workstation, @Source, @Status, @LogonType, 0
                WHERE NOT EXISTS (SELECT 1 FROM attempts WHERE ip=@Ip AND occurred_at=@OccurredAt)
                """, e, tx);
        }
        tx.Commit();
        return n;
    }
}
