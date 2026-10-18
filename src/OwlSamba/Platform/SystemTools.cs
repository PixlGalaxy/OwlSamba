using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;

namespace OwlSamba.Platform;

public interface ISmbSessionKiller
{
    Task CloseSessionsAsync(string ip);
}

/// <summary>Closes open SMB sessions of an IP so a ban also kicks out an attacker that already got in.</summary>
public sealed class SmbSessionKiller : ISmbSessionKiller
{
    private readonly ILogger<SmbSessionKiller> _log;
    public SmbSessionKiller(ILogger<SmbSessionKiller> log) => _log = log;

    public async Task CloseSessionsAsync(string ip)
    {
        // Only canonical IPs reach the command line (no injection possible).
        var safe = IpUtil.Normalize(ip);
        if (safe == null) return;
        var script =
            $"$ip='{safe}'; Get-SmbSession -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.ClientComputerName -eq $ip -or $_.ClientComputerName -eq \"[$ip]\" } | " +
            "ForEach-Object { Close-SmbSession -SessionId $_.SessionId -Force -ErrorAction SilentlyContinue; $_.SessionId }";
        var (code, output) = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], TimeSpan.FromSeconds(20));
        var closed = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (code == 0 && closed > 0)
            _log.LogWarning("Closed {Count} open SMB session(s) from {Ip}", closed, safe);
    }
}

public static class AuditPolicy
{
    // "Logon" subcategory GUID (language independent).
    private const string LogonSubcategory = "{0CCE9215-69AE-11D9-BED3-505054503030}";

    /// <summary>Makes sure failed logons are audited, otherwise Windows never writes event 4625.</summary>
    public static async Task<(bool ok, string info)> EnsureLogonFailureAuditingAsync()
    {
        var (code, output) = await ProcessRunner.RunAsync("auditpol.exe",
            ["/set", $"/subcategory:{LogonSubcategory}", "/failure:enable"], TimeSpan.FromSeconds(15));
        return code == 0
            ? (true, "Failed logon auditing is enabled")
            : (false, "Could not enable auditing (auditpol): " + output.Trim());
    }

    private const uint POLICY_AUDIT_EVENT_FAILURE = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIT_POLICY_INFORMATION
    {
        public Guid AuditSubCategoryGuid;
        public uint AuditingInformation;
        public Guid AuditCategoryGuid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AuditQuerySystemPolicy(Guid[] subCategoryGuids, uint policyCount, out IntPtr auditPolicy);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    /// <summary>Reads the effective policy through the Win32 API (output of auditpol is localized).</summary>
    public static (bool? ok, string info) Check()
    {
        try
        {
            if (!AuditQuerySystemPolicy([Guid.Parse(LogonSubcategory)], 1, out var ptr))
                return (null, $"Could not read the audit policy (error {Marshal.GetLastWin32Error()})");
            try
            {
                var info = Marshal.PtrToStructure<AUDIT_POLICY_INFORMATION>(ptr);
                var ok = (info.AuditingInformation & POLICY_AUDIT_EVENT_FAILURE) != 0;
                return (ok, ok
                    ? "Failed logon auditing is enabled"
                    : "Failed logon auditing is disabled: Windows will not write event 4625");
            }
            finally { AuditFree(ptr); }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}

public static class ProcessRunner
{
    public static async Task<(int code, string output)> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* already gone */ }
                return (-1, "timeout");
            }
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
