using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using OwlSamba.Core;

namespace OwlSamba.Server;

/// <summary>Where the desktop UI finds the local API and the key that authenticates it.</summary>
public sealed record LocalEndpoint(int Port, string Key)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LocalEndpoint? TryRead()
    {
        try
        {
            return File.Exists(Paths.LocalEndpointFile)
                ? JsonSerializer.Deserialize<LocalEndpoint>(File.ReadAllText(Paths.LocalEndpointFile), Json)
                : null;
        }
        catch { return null; }
    }

    /// <summary>Generates a fresh key + free loopback port and writes them readable by Administrators/SYSTEM only.</summary>
    public static LocalEndpoint CreateAndWrite()
    {
        var endpoint = new LocalEndpoint(FreeLoopbackPort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Paths.EnsureCreated();
        File.WriteAllText(Paths.LocalEndpointFile, JsonSerializer.Serialize(endpoint, Json));
        RestrictToAdmins(Paths.LocalEndpointFile);
        return endpoint;
    }

    private static int FreeLoopbackPort()
    {
        // Prefer a stable port, fall back to any free one.
        foreach (var candidate in new[] { 47650, 47651, 47652, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, candidate);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free loopback port");
    }

    /// <summary>
    /// ProgramData lets standard users create files in subfolders by default. Lock the data folder down to
    /// Administrators + SYSTEM so nobody can plant a certificate, endpoint file or database before the service does.
    /// </summary>
    public static void SecureDataDirectory(string dir)
    {
        if (Paths.DevDataDir != null) return;
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch { /* not elevated (e.g. --console without admin) */ }
    }

    public static void RestrictToAdmins(string file)
    {
        if (Paths.DevDataDir != null) return; // dev runs: UI and service run unelevated
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        catch { /* not fatal (e.g. FAT volumes) */ }
    }
}

/// <summary>Cookie sessions for the web dashboard.</summary>
public sealed class SessionStore
{
    public const string CookieName = "owl_session";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, (string user, DateTimeOffset expires)> _sessions = new();

    public string Create(string user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[token] = (user, DateTimeOffset.UtcNow + Lifetime);
        foreach (var kv in _sessions.Where(kv => kv.Value.expires < DateTimeOffset.UtcNow).ToList())
            _sessions.TryRemove(kv.Key, out _);
        return token;
    }

    public string? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var s)) return null;
        if (s.expires < DateTimeOffset.UtcNow) { _sessions.TryRemove(token, out _); return null; }
        return s.user;
    }

    public void Revoke(string? token)
    {
        if (token != null) _sessions.TryRemove(token, out _);
    }

    public void RevokeAll() => _sessions.Clear();
}

public static class DashboardCertificate
{
    /// <summary>Loads (or creates) the self-signed certificate used by the HTTPS dashboard.</summary>
    public static X509Certificate2 LoadOrCreate(string hostname)
    {
        if (File.Exists(Paths.CertificateFile))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(Paths.CertificateFile, null,
                    X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
            }
            catch { /* regenerate */ }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=OwlSamba {hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        File.WriteAllBytes(Paths.CertificateFile, cert.Export(X509ContentType.Pfx));
        LocalEndpoint.RestrictToAdmins(Paths.CertificateFile);
        return X509CertificateLoader.LoadPkcs12FromFile(Paths.CertificateFile, null,
            X509KeyStorageFlags.Exportable);
    }
}
