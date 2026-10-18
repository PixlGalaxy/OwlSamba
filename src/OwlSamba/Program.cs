using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using OwlSamba.Core;
using OwlSamba.Platform;
using OwlSamba.Service;

namespace OwlSamba;

/// <summary>
/// One exe, several roles:
///   OwlSamba.exe              desktop UI (tray + window)
///   OwlSamba.exe --tray       desktop UI started hidden in the tray
///   OwlSamba.exe --service    protection service (used by the SCM)
///   OwlSamba.exe --console    protection service in a console window (debugging)
///   OwlSamba.exe --install    install / upgrade the service and exit
///   OwlSamba.exe --uninstall  remove the service and exit
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var flags = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));

        if (flags.Contains("--service"))
        {
            ServiceHost.Build(args, asWindowsService: true).Run();
            return 0;
        }

        if (flags.Contains("--console"))
        {
            AttachOrAllocConsole();
            Console.WriteLine($"OwlSamba {AppInfo.Version} — console mode (Ctrl+C to exit)");
            ServiceHost.Build(args, asWindowsService: false).Run();
            return 0;
        }

        if (flags.Contains("--install") || flags.Contains("--uninstall"))
        {
            AttachOrAllocConsole();
            try
            {
                if (flags.Contains("--install"))
                {
                    var path = ServiceInstaller.InstallAsync(new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
                    Console.WriteLine($"Service installed and running: {path}");
                }
                else
                {
                    ServiceInstaller.UninstallAsync().GetAwaiter().GetResult();
                    Console.WriteLine("Service removed.");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        return UI.App.RunDesktop(startHidden: flags.Contains("--tray"));
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    private static void AttachOrAllocConsole()
    {
        if (!Console.IsOutputRedirected && !AttachConsole(-1)) AllocConsole();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console attached */ }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }
}
