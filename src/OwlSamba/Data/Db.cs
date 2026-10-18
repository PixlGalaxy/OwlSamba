using Dapper;
using Microsoft.Data.Sqlite;

namespace OwlSamba.Data;

/// <summary>SQLite connection factory + schema management.</summary>
public sealed class Db
{
    public const int SchemaVersion = 1;

    static Db() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public string Path { get; }
    private readonly string _connectionString;

    public Db(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        return conn;
    }

    public void EnsureSchema()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var conn = Open();
        conn.Execute("PRAGMA journal_mode=WAL;");
        using var tx = conn.BeginTransaction();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS ips (
                ip               TEXT PRIMARY KEY,
                first_seen       INTEGER NOT NULL,
                last_seen        INTEGER NOT NULL,
                total_attempts   INTEGER NOT NULL DEFAULT 0,
                last_user        TEXT,
                last_workstation TEXT,
                ban_kind         TEXT,
                ban_started_at   INTEGER,
                ban_until        INTEGER,
                ban_reason       TEXT,
                ban_count        INTEGER NOT NULL DEFAULT 0,
                counter_reset_at INTEGER NOT NULL DEFAULT 0,
                country          TEXT,
                country_code     TEXT,
                isp              TEXT,
                geo_at           INTEGER,
                notes            TEXT,
                suspicious_at    INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_ips_ban ON ips(ban_kind, ban_until);
            CREATE INDEX IF NOT EXISTS ix_ips_last_seen ON ips(last_seen);

            CREATE TABLE IF NOT EXISTS attempts (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ip          TEXT NOT NULL,
                occurred_at INTEGER NOT NULL,
                user        TEXT,
                workstation TEXT,
                source      TEXT NOT NULL,
                status      TEXT,
                logon_type  INTEGER,
                whitelisted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_attempts_ip_time ON attempts(ip, occurred_at);
            CREATE INDEX IF NOT EXISTS ix_attempts_time ON attempts(occurred_at);

            CREATE TABLE IF NOT EXISTS bans (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ip         TEXT NOT NULL,
                kind       TEXT NOT NULL,
                reason     TEXT,
                started_at INTEGER NOT NULL,
                expires_at INTEGER,
                ended_at   INTEGER,
                end_reason TEXT,
                created_by TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_bans_ip ON bans(ip);
            CREATE INDEX IF NOT EXISTS ix_bans_started ON bans(started_at);

            CREATE TABLE IF NOT EXISTS whitelist (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                value      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                kind       TEXT NOT NULL,
                note       TEXT,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS audit (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      INTEGER NOT NULL,
                actor   TEXT NOT NULL,
                action  TEXT NOT NULL,
                target  TEXT,
                details TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);
            """, transaction: tx);

        var version = conn.ExecuteScalar<long?>("SELECT version FROM schema_version LIMIT 1", transaction: tx);
        if (version == null)
            conn.Execute("INSERT INTO schema_version(version) VALUES (@v)", new { v = SchemaVersion }, tx);
        // Future migrations: if (version < 2) { ... }
        tx.Commit();
    }

    /// <summary>Hot backup using SQLite's online backup API (safe while the service runs).</summary>
    public void BackupTo(string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(destination);
        using var src = Open();
        using var dst = new SqliteConnection($"Data Source={destination};Pooling=False");
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>Replaces the live database contents with a backup file after validating it.</summary>
    public void RestoreFrom(string source)
    {
        using (var check = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
        {
            check.Open();
            var tables = check.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
            foreach (var required in new[] { "ips", "attempts", "bans", "whitelist", "settings" })
                if (!tables.Contains(required))
                    throw new InvalidDataException($"The file is not an OwlSamba backup (table '{required}' is missing).");
            using var dst = Open();
            check.BackupDatabase(dst);
        }
        SqliteConnection.ClearAllPools();
        EnsureSchema();
    }
}
