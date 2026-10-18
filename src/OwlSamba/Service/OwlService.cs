using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Detection;
using OwlSamba.Integrations;
using OwlSamba.Migration;
using OwlSamba.Platform;
using OwlSamba.Server;
using Serilog;

namespace OwlSamba.Service;

public sealed class StatusService
{
    private readonly BruteForceEngine _engine;
    private readonly EventLogMonitor _monitor;
    private readonly IFirewall _firewall;
    private readonly SettingsProvider _settings;
    private readonly long _startedAt;

    public (bool? ok, string info) AuditPolicyState { get; set; } = (null, "Not checked yet");

    public StatusService(BruteForceEngine engine, EventLogMonitor monitor, IFirewall firewall, SettingsProvider settings, TimeProvider time)
    {
        _engine = engine;
        _monitor = monitor;
        _firewall = firewall;
        _settings = settings;
        _startedAt = time.NowMs();
    }

    public ServiceStatus Build(bool webRunning, string? webUrl)
    {
        var (available, allOn, error) = _firewall.Health();
        var s = _settings.Current;
        return new ServiceStatus
        {
            Hostname = s.Hostname,
            StartedAt = _startedAt,
            BanningEnabled = s.EnableBanning,
            FirewallAvailable = available,
            FirewallAllProfilesOn = allOn,
            FirewallError = error,
            AuditPolicyOk = AuditPolicyState.ok != false,
            AuditPolicyInfo = AuditPolicyState.info,
            LastEventAt = _engine.LastEventAt,
            EventsProcessed = _engine.EventsProcessed,
            Sources = _monitor.Status(),
            WebDashboardRunning = webRunning,
            WebDashboardUrl = webUrl,
        };
    }
}

/// <summary>Orchestrates startup and the periodic jobs of the protection service.</summary>
public sealed class OwlService : BackgroundService
{
    private readonly Repository _repo;
    private readonly SettingsProvider _settings;
    private readonly WhitelistMatcher _whitelist;
    private readonly BruteForceEngine _engine;
    private readonly EventLogMonitor _monitor;
    private readonly LegacyImporter _importer;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private readonly ILogger<OwlService> _log;

    public OwlService(Repository repo, SettingsProvider settings, WhitelistMatcher whitelist, BruteForceEngine engine,
        EventLogMonitor monitor, LegacyImporter importer, StatusService status, TimeProvider time, ILogger<OwlService> log)
    {
        _repo = repo;
        _settings = settings;
        _whitelist = whitelist;
        _engine = engine;
        _monitor = monitor;
        _importer = importer;
        _status = status;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("OwlSamba {Version} starting on {Host}", AppInfo.Version, _settings.Current.Hostname);

        if (_settings.Current.AutoEnableAuditPolicy)
        {
            var (ok, info) = await AuditPolicy.EnsureLogonFailureAuditingAsync();
            if (!ok) _log.LogWarning("{Info}", info);
        }
        _status.AuditPolicyState = AuditPolicy.Check();

        AutoMigrateOnFirstRun();
        await _whitelist.RefreshAsync(ct);
        await _engine.ExpireDueBansAsync();
        await _engine.ReconcileFirewallAsync();
        await _monitor.StartAsync();
        _log.LogInformation("Real-time protection active");

        var nextWhitelistRefresh = DateTime.UtcNow.AddMinutes(10);
        var nextMaintenance = DateTime.UtcNow.AddMinutes(2);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await _engine.ExpireDueBansAsync();

                    if (DateTime.UtcNow >= nextWhitelistRefresh)
                    {
                        nextWhitelistRefresh = DateTime.UtcNow.AddMinutes(10);
                        await _whitelist.RefreshAsync(ct);
                        _status.AuditPolicyState = AuditPolicy.Check();
                    }
                    if (DateTime.UtcNow >= nextMaintenance)
                    {
                        nextMaintenance = DateTime.UtcNow.AddHours(24);
                        Maintenance();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Periodic job failed");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _monitor.StopAsync();
            _log.LogInformation("OwlSamba stopped");
        }
    }

    private void AutoMigrateOnFirstRun()
    {
        if (_repo.GetValue("migration.autoChecked") != null) return;
        _repo.SetValue("migration.autoChecked", _time.NowMs().ToString());
        foreach (var folder in LegacyImporter.DefaultSearchFolders())
        {
            if (LegacyImporter.FindLegacyFiles(folder).Count == 0) continue;
            _log.LogInformation("Found OwlSamba v1 data in {Folder}; importing", folder);
            var report = _importer.Import(folder, replaceLegacyRules: true);
            foreach (var w in report.Warnings) _log.LogWarning("Migration: {Warning}", w);
        }
    }

    private void Maintenance()
    {
        var s = _settings.Current;
        var purged = _repo.PurgeAttemptsOlderThan(_time.NowMs() - s.RetentionDays * 86_400_000L);
        if (purged > 0) _log.LogInformation("Retention: purged {Count} attempt(s) older than {Days} days", purged, s.RetentionDays);

        // Daily automatic backup, keep the last 7.
        var file = Path.Combine(Paths.BackupsDir, $"auto-{DateTime.Now:yyyyMMdd}.db");
        if (!File.Exists(file)) _repo.Db.BackupTo(file);
        foreach (var old in new DirectoryInfo(Paths.BackupsDir).GetFiles("auto-*.db").OrderByDescending(f => f.Name).Skip(7))
            old.Delete();
    }
}

public static class ServiceHost
{
    /// <summary>Builds the protection host (used both as Windows service and in --console mode).</summary>
    public static IHost Build(string[] args, bool asWindowsService)
    {
        Paths.EnsureCreated();
        LocalEndpoint.SecureDataDirectory(Paths.DataDir);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = AppInfo.Name,
        });
        if (asWindowsService)
            builder.Services.AddWindowsService(o => o.ServiceName = AppInfo.ServiceName);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(Paths.LogsDir, "service-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
        if (!asWindowsService) builder.Logging.AddConsole();

        var s = builder.Services;
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(_ =>
        {
            var db = new Db(Paths.Database);
            db.EnsureSchema();
            return db;
        });
        s.AddSingleton<Repository>();
        s.AddSingleton<SettingsProvider>();
        s.AddSingleton<LiveHub>();
        Demo.Enabled = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        if (Demo.Enabled)
        {
            s.AddSingleton<IFirewall, NullFirewall>();
            s.AddSingleton<ISmbSessionKiller, NoopSessionKiller>();
            s.AddHostedService<DemoSimulator>();
        }
        else
        {
            s.AddSingleton<IFirewall, WindowsFirewall>();
            s.AddSingleton<ISmbSessionKiller, SmbSessionKiller>();
        }
        s.AddSingleton<WhitelistMatcher>();
        s.AddHttpClient("discord", c => c.Timeout = TimeSpan.FromSeconds(15));
        s.AddHttpClient("geo", c => c.Timeout = TimeSpan.FromSeconds(15));
        s.AddSingleton<DiscordNotifier>();
        s.AddSingleton<INotifier>(sp => sp.GetRequiredService<DiscordNotifier>());
        s.AddHostedService(sp => sp.GetRequiredService<DiscordNotifier>());
        s.AddSingleton<GeoIpService>();
        if (Demo.Enabled)
            s.AddSingleton<IGeoQueue, NoopGeoQueue>();
        else
        {
            s.AddSingleton<IGeoQueue>(sp => sp.GetRequiredService<GeoIpService>());
            s.AddHostedService(sp => sp.GetRequiredService<GeoIpService>());
        }
        s.AddSingleton<BruteForceEngine>();
        s.AddSingleton<EventLogMonitor>();
        s.AddSingleton<LegacyImporter>();
        s.AddSingleton<StatusService>();
        s.AddSingleton<ApiServer>();
        s.AddHostedService(sp => sp.GetRequiredService<ApiServer>());
        s.AddHostedService<OwlService>();
        s.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
        return builder.Build();
    }
}
