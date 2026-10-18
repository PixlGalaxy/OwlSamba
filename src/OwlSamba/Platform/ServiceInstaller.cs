using System.ServiceProcess;
using OwlSamba.Core;

namespace OwlSamba.Platform;

public static class ServiceInstaller
{
    public static bool IsInstalled()
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(AppInfo.ServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    public static ServiceControllerStatus? Status()
    {
        if (!IsInstalled()) return null;
        try
        {
            using var sc = new ServiceController(AppInfo.ServiceName);
            return sc.Status;
        }
        catch { return null; }
    }

    public static bool RunningFromInstallDir() =>
        string.Equals(Path.GetFullPath(Paths.CurrentExe), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies this exe to Program Files, registers it as an auto-start service with crash recovery,
    /// creates a Start menu shortcut and starts it. Running it again upgrades the installed copy.
    /// </summary>
    public static async Task<string> InstallAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Stopping previous service…");
        if (IsInstalled()) await StopAsync();

        if (!RunningFromInstallDir())
        {
            progress?.Report("Copying OwlSamba.exe to Program Files…");
            Directory.CreateDirectory(Paths.InstallDir);
            // The service process may need a moment to release the file after stopping.
            for (int i = 0; ; i++)
            {
                try { File.Copy(Paths.CurrentExe, Paths.InstalledExe, overwrite: true); break; }
                catch (IOException) when (i < 20) { await Task.Delay(500); }
            }
        }

        var bin = $"\"{Paths.InstalledExe}\" --service";
        progress?.Report("Registering Windows service…");
        if (!IsInstalled())
            await Sc("create", AppInfo.ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", AppInfo.ServiceDisplayName);
        else
            await Sc("config", AppInfo.ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", AppInfo.ServiceDisplayName);
        await Sc("description", AppInfo.ServiceName, "Real-time SMB brute-force protection (OwlSamba).");
        await Sc("failure", AppInfo.ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000/restart/10000");
        await Sc("failureflag", AppInfo.ServiceName, "1");

        CreateStartMenuShortcut();

        progress?.Report("Starting service…");
        await StartAsync();
        return Paths.InstalledExe;
    }

    public static async Task UninstallAsync()
    {
        if (IsInstalled())
        {
            await StopAsync();
            await Sc("delete", AppInfo.ServiceName);
        }
        await SetTrayAutostartAsync(false);
        try { File.Delete(StartMenuShortcutPath); } catch { /* ignore */ }
    }

    public static async Task StartAsync()
    {
        using var sc = new ServiceController(AppInfo.ServiceName);
        sc.Refresh();
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
        sc.Start();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)));
    }

    public static async Task StopAsync()
    {
        using var sc = new ServiceController(AppInfo.ServiceName);
        sc.Refresh();
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)));
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    // ------------------------------------------------------------- tray autostart

    /// <summary>
    /// The UI needs admin rights, and HKCU\Run cannot elevate at logon, so a scheduled task
    /// with "highest privileges" is used instead.
    /// </summary>
    public static async Task SetTrayAutostartAsync(bool enabled)
    {
        var exe = File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Paths.CurrentExe;
        if (enabled)
            await ProcessRunner.RunAsync("schtasks.exe",
                ["/create", "/tn", AppInfo.TrayTaskName, "/tr", $"\"{exe}\" --tray", "/sc", "onlogon", "/rl", "highest", "/f"],
                TimeSpan.FromSeconds(15));
        else
            await ProcessRunner.RunAsync("schtasks.exe", ["/delete", "/tn", AppInfo.TrayTaskName, "/f"], TimeSpan.FromSeconds(15));
    }

    public static async Task<bool> IsTrayAutostartEnabledAsync()
    {
        var (code, _) = await ProcessRunner.RunAsync("schtasks.exe", ["/query", "/tn", AppInfo.TrayTaskName], TimeSpan.FromSeconds(10));
        return code == 0;
    }

    // ------------------------------------------------------------- helpers

    private static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "OwlSamba.lnk");

    private static void CreateStartMenuShortcut()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(StartMenuShortcutPath);
            link.TargetPath = Paths.InstalledExe;
            link.WorkingDirectory = Paths.InstallDir;
            link.Description = "OwlSamba — SMB protection";
            link.IconLocation = Paths.InstalledExe + ",0";
            link.Save();
        }
        catch { /* shortcut is a convenience only */ }
    }

    private static async Task Sc(params string[] args)
    {
        var (code, output) = await ProcessRunner.RunAsync("sc.exe", args, TimeSpan.FromSeconds(30));
        if (code != 0)
            throw new InvalidOperationException($"sc.exe {args[0]} failed ({code}): {output.Trim()}");
    }
}
