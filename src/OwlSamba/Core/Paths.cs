namespace OwlSamba.Core;

public static class AppInfo
{
    public const string Name = "OwlSamba";
    public const string ServiceName = "OwlSamba";
    public const string ServiceDisplayName = "OwlSamba SMB Protection";
    public const string FirewallRulePrefix = "OwlSamba Block ";
    public const string FirewallGroup = "OwlSamba";
    public const string TrayTaskName = "OwlSamba Tray";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
}

public static class Paths
{
    /// <summary>OWLSAMBA_DATA_DIR redirects all state (development / demo runs without elevation).</summary>
    public static string? DevDataDir => Environment.GetEnvironmentVariable("OWLSAMBA_DATA_DIR") is { Length: > 0 } d ? d : null;

    public static string DataDir { get; private set; } =
        DevDataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.Name);

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name);

    public static string InstalledExe => Path.Combine(InstallDir, "OwlSamba.exe");

    public static string Database => Path.Combine(DataDir, "owlsamba.db");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string LocalEndpointFile => Path.Combine(DataDir, "local-endpoint.json");
    public static string CertificateFile => Path.Combine(DataDir, "dashboard.pfx");

    public static string CurrentExe =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OwlSamba.exe");

    /// <summary>Used by tests to redirect all state to a temp folder.</summary>
    internal static void OverrideDataDir(string dir) => DataDir = dir;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BackupsDir);
    }
}
