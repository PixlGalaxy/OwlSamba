using Microsoft.Extensions.Logging.Abstractions;
using OwlSamba.Core;
using OwlSamba.Detection;
using OwlSamba.Migration;

namespace OwlSamba.Tests;

public class ParsingTests
{
    private const string Event4625 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System><EventID>4625</EventID><EventRecordID>1234</EventRecordID></System>
          <EventData>
            <Data Name="SubjectUserSid">S-1-0-0</Data>
            <Data Name="TargetUserName">administrator</Data>
            <Data Name="TargetDomainName">CORP</Data>
            <Data Name="Status">0xc000006d</Data>
            <Data Name="SubStatus">0xc000006a</Data>
            <Data Name="LogonType">3</Data>
            <Data Name="WorkstationName">KALI</Data>
            <Data Name="IpAddress">::ffff:203.0.113.9</Data>
            <Data Name="IpPort">51234</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void Parses_4625()
    {
        var e = EventParsing.ParseSecurity4625(Event4625, 1000)!;
        Assert.Equal("203.0.113.9", e.Ip);
        Assert.Equal("CORP\\administrator", e.User);
        Assert.Equal("KALI", e.Workstation);
        Assert.Equal("0xc000006a", e.Status);
        Assert.Equal(3, e.LogonType);
        Assert.Equal(Sources.Security, e.Source);
    }

    [Fact]
    public void Ignores_4625_without_ip()
    {
        Assert.Null(EventParsing.ParseSecurity4625(Event4625.Replace("::ffff:203.0.113.9", "-"), 1000));
    }

    [Fact]
    public void Parses_551_with_sockaddr()
    {
        // SOCKADDR_IN: family 2, port 0xC35A, 192.168.1.10
        var xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventID>551</EventID></System>
              <EventData>
                <Data Name="SessionGUID">{00000000-0000-0000-0000-000000000000}</Data>
                <Data Name="Status">3221225581</Data>
                <Data Name="ClientAddressLength">16</Data>
                <Data Name="ClientAddress">0200C35AC0A8010A0000000000000000</Data>
                <Data Name="UserName">guest</Data>
                <Data Name="ClientName">\\192.168.1.10</Data>
              </EventData>
            </Event>
            """;
        var e = EventParsing.ParseSmb551(xml, 1000)!;
        Assert.Equal("192.168.1.10", e.Ip);
        Assert.Equal("guest", e.User);
        Assert.Equal("0xc000006d", e.Status);
        Assert.Equal(Sources.SmbServer, e.Source);
    }

    [Fact]
    public void Parses_ipv6_sockaddr()
    {
        // SOCKADDR_IN6: family 23 (0x17), port, flowinfo, 2001:db8::1
        var hex = "1700C35A00000000" + "20010DB8000000000000000000000001" + "00000000";
        Assert.Equal("2001:db8::1", EventParsing.ParseSockAddrHex(hex));
    }

    [Fact]
    public void Ignores_machine_accounts_on_success()
    {
        var xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><EventData>
              <Data Name="TargetUserName">SRV01$</Data><Data Name="LogonType">3</Data><Data Name="IpAddress">10.0.0.5</Data>
            </EventData></Event>
            """;
        Assert.Null(EventParsing.ParseSecurity4624(xml, 1));
    }

    [Theory]
    [InlineData("192.168.1.77", "192.168.1.0/24", true)]
    [InlineData("192.168.2.1", "192.168.1.0/24", false)]
    [InlineData("10.200.3.4", "10.0.0.0/8", true)]
    [InlineData("172.20.0.1", "172.16.0.0/12", true)]
    [InlineData("172.32.0.1", "172.16.0.0/12", false)]
    [InlineData("2001:db8::5", "2001:db8::/32", true)]
    [InlineData("1.2.3.4", "0.0.0.0/0", true)]
    public void Cidr_matching(string ip, string cidr, bool expected) => Assert.Equal(expected, IpUtil.InCidr(ip, cidr));

    [Theory]
    [InlineData("1.2.3.4", "ip")]
    [InlineData("10.0.0.0/8", "cidr")]
    [InlineData("nas.example.com", "domain")]
    [InlineData("10.0.0.0/99", null)]
    [InlineData("not a host!", null)]
    public void Whitelist_classification(string value, string? kind) => Assert.Equal(kind, IpUtil.ClassifyWhitelistValue(value));
}

public class MigrationTests
{
    [Fact]
    public void Imports_both_json_formats_env_and_replaces_bans()
    {
        using var h = new TestHarness();
        var legacy = Path.Combine(h.Dir, "legacy");
        Directory.CreateDirectory(legacy);

        // Original script format (v0)
        File.WriteAllText(Path.Combine(legacy, "IP_History.json"), """
            {
              "198.51.100.1": { "Attempts": 14, "LastAttempt": "Wed Feb 05 20:46:06 2025", "Workstation": "WIN-X", "LastUser": "admin", "Banned": true },
              "198.51.100.2": { "Attempts": 2,  "LastAttempt": "Wed Feb 05 21:00:00 2025", "Workstation": "-", "LastUser": "guest", "Banned": false }
            }
            """);
        // Later export format (v1)
        File.WriteAllText(Path.Combine(legacy, "banned_ips.json"), """
            { "198.51.100.3": { "ip": "198.51.100.3", "attempts": 11, "last_attempt": "2025-06-01T10:00:00", "workstation": "-", "last_user": "root", "banned": 1, "banned_time": "2025-06-01T10:00:01", "manual": 0 } }
            """);
        File.WriteAllText(Path.Combine(legacy, ".env"), """
            DASHBOARD_USER=fabrizio
            DASHBOARD_PASSWORD=change_me_securely
            DASHBOARD_PASSWORD_HASH=$2b$12$abcdefghijklmnopqrstuuG0jJ5c3pS1Q8cX1y8m1m6m0p2b7xN2e
            THRESHOLD=12
            WHITELIST_IPS="127.0.0.1, 192.168.0.0/24"
            WHITELIST_DOMAINS="nas.example.com"
            BAN_IPS=True
            """);

        var importer = new LegacyImporter(h.Repo, h.Firewall, h.Settings, h.Time, NullLogger<LegacyImporter>.Instance);
        var report = importer.Import(legacy, replaceLegacyRules: true);

        Assert.DoesNotContain(report.Warnings, w => !w.Contains("already imported"));
        Assert.Equal(3, report.IpsImported);
        Assert.Equal(2, report.BansImported);
        Assert.Equal(3, report.WhitelistImported);
        Assert.True(h.Blocked("198.51.100.1"));
        Assert.True(h.Blocked("198.51.100.3"));
        Assert.False(h.Blocked("198.51.100.2"));
        Assert.Equal(14, h.Repo.GetIp("198.51.100.1")!.TotalAttempts);
        Assert.Equal(12, h.Settings.Current.PermanentBanThreshold);
        Assert.Equal("fabrizio", h.Settings.Current.WebDashboardUser);
        Assert.StartsWith("$2b$", h.Settings.Current.WebDashboardPasswordHash);

        // Second run is idempotent.
        var again = importer.Import(legacy, replaceLegacyRules: false);
        Assert.Equal(0, again.IpsImported);
        Assert.Equal(0, again.BansImported);
    }

    [Fact]
    public void Imports_v1_sqlite_database()
    {
        using var h = new TestHarness();
        var legacyDb = Path.Combine(h.Dir, "DataBase.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDb};Pooling=False"))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE bans (ip TEXT PRIMARY KEY, attempts INTEGER, last_attempt TEXT, workstation TEXT, last_user TEXT, banned INTEGER, banned_time TEXT, manual INTEGER);
                CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, ip TEXT, occurred_at TEXT, workstation TEXT, user TEXT);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO bans VALUES ('203.0.113.77', 25, '2025-09-01T08:00:00', 'X', 'sa', 1, '2025-09-01T08:00:00', 0);
                INSERT INTO events(ip, occurred_at, workstation, user) VALUES ('203.0.113.77', '2025-09-01T07:59:00', 'X', 'sa'), ('203.0.113.77', '2025-09-01T08:00:00', 'X', 'sa');
                INSERT INTO settings VALUES ('threshold', '13'), ('ban_ips', 'True'), ('whitelist_ips', '127.0.0.1');
                """;
            cmd.ExecuteNonQuery();
        }

        var importer = new LegacyImporter(h.Repo, h.Firewall, h.Settings, h.Time, NullLogger<LegacyImporter>.Instance);
        var report = importer.Import(legacyDb, replaceLegacyRules: false);

        Assert.Equal(1, report.BansImported);
        Assert.Equal(2, report.AttemptsImported);
        Assert.Equal(13, h.Settings.Current.PermanentBanThreshold);
        Assert.True(h.Blocked("203.0.113.77"));
    }

    [Theory]
    [InlineData("Wed Feb 05 20:46:06 2025")]
    [InlineData("02/05/25 20:46:06")]
    [InlineData("2025-02-05T20:46:06")]
    [InlineData("2025-02-05 20:46:06")]
    public void Parses_legacy_dates(string value)
    {
        var ms = LegacyImporter.ParseLegacyDate(value)!.Value;
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        Assert.Equal((2025, 2, 5, 20, 46), (local.Year, local.Month, local.Day, local.Hour, local.Minute));
    }

    [Fact]
    public void Password_hashes_roundtrip_and_accept_bcrypt()
    {
        var h = PasswordHasher.Hash("correct horse battery");
        Assert.True(PasswordHasher.Verify("correct horse battery", h));
        Assert.False(PasswordHasher.Verify("wrong", h));
        var bcrypt = BCrypt.Net.BCrypt.HashPassword("legacy-pass", 4);
        Assert.True(PasswordHasher.Verify("legacy-pass", bcrypt));
        Assert.True(PasswordHasher.NeedsRehash(bcrypt));
    }
}
