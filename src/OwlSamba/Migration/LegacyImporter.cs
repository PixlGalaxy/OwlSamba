using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Platform;
using OwlSamba.Service;

namespace OwlSamba.Migration;

/// <summary>
/// Imports data from OwlSamba v1 (Python):
///  • banned_ips.json / IP_History.json — both the original script format
///    { "ip": { "Attempts", "LastAttempt", "Workstation", "LastUser", "Banned" } }
///    and the later export format { "ip": { "ip", "attempts", "last_attempt", "banned", "banned_time", ... } }
///  • DataBase.db — tables bans / events / settings
///  • .env / config.env — threshold, whitelist, Discord webhook, dashboard credentials
///  • legacy firewall rules SMB_block_* / SMB_block_V2_* (replaced by OwlSamba rules)
/// Each file is fingerprinted so importing the same file twice is a no-op.
/// </summary>
public sealed class LegacyImporter
{
    private const string ImportedBy = "v1 migration";

    private readonly Repository _repo;
    private readonly IFirewall _firewall;
    private readonly SettingsProvider _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<LegacyImporter> _log;

    public LegacyImporter(Repository repo, IFirewall firewall, SettingsProvider settings, TimeProvider time, ILogger<LegacyImporter> log)
    {
        _repo = repo;
        _firewall = firewall;
        _settings = settings;
        _time = time;
        _log = log;
    }

    /// <summary>Folders checked automatically on first start.</summary>
    public static IEnumerable<string> DefaultSearchFolders()
    {
        var exeDir = Path.GetDirectoryName(Paths.CurrentExe)!;
        yield return exeDir;
        yield return Path.Combine(exeDir, "backend");
        yield return Path.Combine(Paths.DataDir, "import");
    }

    public static List<string> FindLegacyFiles(string path)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];
        var names = new[] { "banned_ips.json", "IP_History.json", "DataBase.db", ".env", "config.env" };
        var found = new List<string>();
        foreach (var dir in new[] { path, Path.Combine(path, "backend") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var n in names)
            {
                var f = Path.Combine(dir, n);
                if (File.Exists(f)) found.Add(f);
            }
        }
        return found;
    }

    public MigrationReport Import(string path, bool replaceLegacyRules, bool force = false)
    {
        var report = new MigrationReport();
        var files = FindLegacyFiles(path);
        if (files.Count == 0)
        {
            report.Warnings.Add($"No OwlSamba v1 files found in '{path}'.");
        }

        var bannedIps = new HashSet<string>();
        // Settings files first so whitelist entries apply before bans are evaluated.
        foreach (var file in files.OrderBy(f => f.EndsWith(".env", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var fingerprintKey = "imported." + Fingerprint(file);
            if (!force && _repo.GetValue(fingerprintKey) != null)
            {
                report.Warnings.Add($"{Path.GetFileName(file)} was already imported (skipped).");
                continue;
            }
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var name = Path.GetFileName(file).ToLowerInvariant();
                if (ext == ".json") ImportJson(file, report, bannedIps);
                else if (ext == ".db") ImportDatabase(file, report, bannedIps);
                else if (name is ".env" or "config.env") ImportEnv(file, report);
                report.FilesProcessed.Add(file);
                _repo.SetValue(fingerprintKey, _time.NowMs().ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Import of {File} failed", file);
                report.Warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (replaceLegacyRules)
        {
            var legacy = _firewall.RemoveLegacyRules();
            report.LegacyRulesRemoved = legacy.Count;
            bannedIps.UnionWith(legacy); // anything v1 had blocked stays blocked
        }

        var now = _time.NowMs();
        foreach (var ip in bannedIps)
        {
            var existing = _repo.GetIp(ip);
            if (existing?.IsBannedAt(now) == true) continue;
            _repo.SetBan(ip, BanKinds.Manual, now, null, "Imported from OwlSamba v1", ImportedBy);
            _firewall.Block(ip, "OwlSamba · Imported from OwlSamba v1");
            report.BansImported++;
        }

        _repo.Audit(ImportedBy, "migration", path,
            $"{report.IpsImported} IPs, {report.BansImported} bans, {report.AttemptsImported} attempts, " +
            $"{report.WhitelistImported} whitelist, v1 rules removed: {report.LegacyRulesRemoved}", now);
        _log.LogInformation("Legacy import from {Path}: {Ips} IPs, {Bans} bans, {Attempts} attempts",
            path, report.IpsImported, report.BansImported, report.AttemptsImported);
        if (report.SettingsImported) _settings.Reload();
        return report;
    }

    // ------------------------------------------------------------------ JSON

    private void ImportJson(string file, MigrationReport report, HashSet<string> bannedIps)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unrecognized JSON format");

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var v = prop.Value;
            var ip = IpUtil.Normalize(Str(v, "ip") ?? prop.Name);
            if (ip == null) continue;

            var attempts = Long(v, "attempts") ?? 0;
            var last = ParseLegacyDate(Str(v, "last_attempt", "lastattempt")) ?? _time.NowMs();
            var user = Str(v, "last_user", "lastuser", "user");
            var ws = Str(v, "workstation");
            _repo.ImportIp(ip, last, last, attempts, user, ws);
            report.IpsImported++;
            if (Bool(v, "banned")) bannedIps.Add(ip);
        }
    }

    private static JsonElement? Prop(JsonElement obj, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
            foreach (var n in names)
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement obj, params string[] names) => Prop(obj, names) is { } e
        ? e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), _ => null }
        : null;

    private static long? Long(JsonElement obj, params string[] names) => Prop(obj, names) is { } e
        ? e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(e.GetString(), out var n) => n,
            _ => null,
        }
        : null;

    private static bool Bool(JsonElement obj, params string[] names) => Prop(obj, names) is { } e && e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => e.TryGetInt64(out var n) && n != 0,
        JsonValueKind.String => e.GetString() is "1" or "true" or "True" or "TRUE",
        _ => false,
    };

    // ------------------------------------------------------------------ SQLite (v1)

    private void ImportDatabase(string file, MigrationReport report, HashSet<string> bannedIps)
    {
        if (Path.GetFullPath(file).Equals(Path.GetFullPath(_repo.Db.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is OwlSamba's current database");

        using var conn = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tables.Contains("bans"))
        {
            foreach (var row in conn.Query("SELECT * FROM bans"))
            {
                var d = new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase);
                var ip = IpUtil.Normalize(d.GetValueOrDefault("ip")?.ToString());
                if (ip == null) continue;
                var last = ParseLegacyDate(d.GetValueOrDefault("last_attempt")?.ToString()) ?? _time.NowMs();
                var attempts = Convert.ToInt64(d.GetValueOrDefault("attempts") ?? 0L, CultureInfo.InvariantCulture);
                _repo.ImportIp(ip, last, last, attempts, d.GetValueOrDefault("last_user")?.ToString(), d.GetValueOrDefault("workstation")?.ToString());
                report.IpsImported++;
                if (Convert.ToInt64(d.GetValueOrDefault("banned") ?? 0L, CultureInfo.InvariantCulture) != 0) bannedIps.Add(ip);
            }
        }

        if (tables.Contains("events"))
        {
            var attempts = new List<FailedLogon>();
            foreach (var row in conn.Query("SELECT * FROM events"))
            {
                var d = new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase);
                var ip = IpUtil.Normalize(d.GetValueOrDefault("ip")?.ToString());
                var at = ParseLegacyDate(d.GetValueOrDefault("occurred_at")?.ToString());
                if (ip == null || at == null) continue;
                attempts.Add(new FailedLogon(ip, at.Value, d.GetValueOrDefault("user")?.ToString() ?? "-",
                    d.GetValueOrDefault("workstation")?.ToString() ?? "-", Sources.Security));
            }
            report.AttemptsImported += _repo.ImportAttempts(attempts);
        }

        if (tables.Contains("settings"))
        {
            var kv = conn.Query<(string key, string? value)>("SELECT key, value FROM settings")
                .ToDictionary(r => r.key.ToUpperInvariant(), r => r.value ?? "");
            ApplySettings(kv, report);
        }
    }

    // ------------------------------------------------------------------ .env

    private void ImportEnv(string file, MigrationReport report)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0 || !kv.ContainsKey(key)) kv[key] = value;
        }
        ApplySettings(kv, report);
    }

    /// <summary>Maps v1 keys (.env names or DB settings names, upper-cased) onto the new settings + whitelist.</summary>
    private void ApplySettings(Dictionary<string, string> kv, MigrationReport report)
    {
        string? Get(params string[] keys) =>
            keys.Select(k => kv.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var now = _time.NowMs();
        foreach (var listKey in new[] { "WHITELIST_IPS", "WHITELIST_DOMAINS" })
        {
            if (Get(listKey) is not { } list) continue;
            foreach (var item in list.Replace("\"", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kind = IpUtil.ClassifyWhitelistValue(item);
                if (kind == null) { report.Warnings.Add($"Invalid whitelist entry ignored: {item}"); continue; }
                if (_repo.AddWhitelist(kind == "ip" ? IpUtil.Normalize(item)! : item, kind, "Imported from v1", now))
                    report.WhitelistImported++;
            }
        }

        var s = _settings.Current;
        var updated = s;
        if (int.TryParse(Get("THRESHOLD"), out var threshold) && threshold > 0)
            updated = updated with { PermanentBanThreshold = threshold };
        if (Get("BAN_IPS") is { } banIps)
            updated = updated with { EnableBanning = banIps.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on" };
        if (Get("DISCORD_WEBHOOK_URL") is { } hook && hook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            updated = updated with { DiscordWebhookUrl = hook, DiscordEnabled = true };
        if (Get("HOSTNAME_OVERRIDE") is { } host && !string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            updated = updated with { HostnameOverride = host };
        if (Get("DASHBOARD_USER") is { } user)
            updated = updated with { WebDashboardUser = user };
        if (Get("DASHBOARD_PASSWORD_HASH") is { } hash && hash.StartsWith("$2", StringComparison.Ordinal))
            updated = updated with { WebDashboardPasswordHash = hash };
        else if (Get("DASHBOARD_PASSWORD") is { } plain && plain is not ("change_me" or "change_me_securely"))
            updated = updated with { WebDashboardPasswordHash = PasswordHasher.Hash(plain) };

        if (updated != s)
        {
            _settings.Update(_ => updated);
            report.SettingsImported = true;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly string[] LegacyDateFormats =
    [
        "ddd MMM dd HH:mm:ss yyyy",   // pywin32 TimeGenerated.Format() with "%a %b %d %H:%M:%S %Y"
        "ddd MMM d HH:mm:ss yyyy",
        "MM/dd/yy HH:mm:ss",          // pywin32 default "%c"
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
    ];

    /// <summary>v1 stored local wall-clock times. Returns Unix ms or null.</summary>
    public static long? ParseLegacyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (DateTime.TryParseExact(value, LegacyDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
        return null;
    }

    private static string Fingerprint(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream))[..32];
    }
}
