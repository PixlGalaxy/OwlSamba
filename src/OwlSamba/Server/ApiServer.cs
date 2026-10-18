using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Detection;
using OwlSamba.Integrations;
using OwlSamba.Migration;
using OwlSamba.Platform;
using OwlSamba.Service;

namespace OwlSamba.Server;

/// <summary>
/// Hosts two Kestrel servers sharing the same API:
///  • local  — 127.0.0.1:{random}, authenticated with the key in local-endpoint.json (desktop UI)
///  • web    — 0.0.0.0:{WebDashboardPort} over HTTPS with a login (remote dashboard); restartable
/// </summary>
public sealed class ApiServer : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly SettingsProvider _settings;
    private readonly IFirewall _firewall;
    private readonly ILogger<ApiServer> _log;
    private readonly SessionStore _sessions = new();
    private readonly SemaphoreSlim _webGate = new(1, 1);
    private WebApplication? _local;
    private WebApplication? _web;
    private LocalEndpoint? _endpoint;

    public string? WebUrl { get; private set; }
    public bool WebRunning => _web != null;

    public ApiServer(IServiceProvider sp, SettingsProvider settings, IFirewall firewall, ILogger<ApiServer> log)
    {
        _sp = sp;
        _settings = settings;
        _firewall = firewall;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _endpoint = LocalEndpoint.CreateAndWrite();
        _local = Build(web: false, b => b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, _endpoint.Port)));
        await _local.StartAsync(ct);
        _log.LogInformation("Local API listening on 127.0.0.1:{Port}", _endpoint.Port);

        await ApplyWebSettingsAsync();
        _settings.Changed += (old, now) =>
        {
            if (old.WebDashboardEnabled != now.WebDashboardEnabled || old.WebDashboardPort != now.WebDashboardPort ||
                old.WebDashboardHttps != now.WebDashboardHttps)
                _ = ApplyWebSettingsAsync();
            if (old.WebDashboardPasswordHash != now.WebDashboardPasswordHash || old.WebDashboardUser != now.WebDashboardUser)
                _sessions.RevokeAll();
        };
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopWebAsync();
        if (_local != null) await _local.StopAsync(ct);
    }

    private async Task StopWebAsync()
    {
        if (_web == null) return;
        try { await _web.StopAsync(); await _web.DisposeAsync(); } catch { /* ignore */ }
        _web = null;
        WebUrl = null;
    }

    private async Task ApplyWebSettingsAsync()
    {
        await _webGate.WaitAsync();
        try
        {
            await StopWebAsync();
            var s = _settings.Current;
            if (!s.WebDashboardEnabled)
            {
                _firewall.SetDashboardRule(null);
                return;
            }
            try
            {
                var cert = s.WebDashboardHttps ? DashboardCertificate.LoadOrCreate(s.Hostname) : null;
                void Endpoint(ListenOptions o)
                {
                    o.Protocols = HttpProtocols.Http1AndHttp2;
                    if (cert != null) o.UseHttps(cert);
                }
                var web = Build(web: true, b => b.WebHost.ConfigureKestrel(k =>
                {
                    if (Demo.Enabled) k.Listen(IPAddress.Loopback, s.WebDashboardPort, Endpoint); // no firewall prompt in demos
                    else k.ListenAnyIP(s.WebDashboardPort, Endpoint);
                }));
                await web.StartAsync();
                _web = web;
                WebUrl = $"{(cert != null ? "https" : "http")}://{s.Hostname}:{s.WebDashboardPort}";
                _firewall.SetDashboardRule(s.WebDashboardPort);
                _log.LogInformation("Web dashboard listening on {Url}", WebUrl);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Web dashboard could not start on port {Port}", s.WebDashboardPort);
            }
        }
        finally
        {
            _webGate.Release();
        }
    }

    // ===================================================================== app

    private WebApplication Build(bool web, Action<WebApplicationBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "OwlSamba",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024; // database restores
        });
        configure(builder);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (web)
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; connect-src 'self'; frame-ancestors 'none'";
            await next();
        });

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path is "/api/auth/login" or "/api/auth/state")
            {
                await next();
                return;
            }
            var actor = Authenticate(ctx, web);
            if (actor == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Not authenticated" });
                return;
            }
            // CSRF: cookie-authenticated writes must carry a custom header (impossible cross-origin without CORS).
            if (web && !HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-OwlSamba"] != "1")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            ctx.Items["actor"] = actor;
            await next();
        });

        if (web)
        {
            var files = new ManifestEmbeddedFileProvider(typeof(ApiServer).Assembly, "Server/wwwroot");
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        MapApi(app, web);
        return app;
    }

    private string? Authenticate(HttpContext ctx, bool web)
    {
        if (!web)
        {
            var key = ctx.Request.Headers["X-Owl-Key"].ToString();
            if (_endpoint != null && key.Length > 0 &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(_endpoint.Key)) &&
                ctx.Connection.RemoteIpAddress is { } r && IPAddress.IsLoopback(r))
                return "desktop";
            return null;
        }
        var user = _sessions.Validate(ctx.Request.Cookies[SessionStore.CookieName]);
        return user == null ? null : "web:" + user;
    }

    private static string Actor(HttpContext ctx) => ctx.Items["actor"] as string ?? "?";

    private void MapApi(WebApplication app, bool web)
    {
        var repo = _sp.GetRequiredService<Repository>();
        var engine = _sp.GetRequiredService<BruteForceEngine>();
        var hub = _sp.GetRequiredService<LiveHub>();
        var time = _sp.GetRequiredService<TimeProvider>();
        var monitor = _sp.GetRequiredService<EventLogMonitor>();
        var whitelist = _sp.GetRequiredService<WhitelistMatcher>();
        var importer = _sp.GetRequiredService<LegacyImporter>();
        var discord = _sp.GetRequiredService<DiscordNotifier>();
        var status = _sp.GetRequiredService<StatusService>();

        static IResult Bad(string message) => Results.BadRequest(new { error = message });

        // ------------------------------------------------------------ auth (web)
        app.MapGet("/api/auth/state", (HttpContext ctx) =>
        {
            var user = web ? _sessions.Validate(ctx.Request.Cookies[SessionStore.CookieName]) : "desktop";
            return Results.Ok(new
            {
                authenticated = user != null,
                user,
                host = _settings.Current.Hostname,
                version = AppInfo.Version,
                passwordSet = !string.IsNullOrEmpty(_settings.Current.WebDashboardPasswordHash),
            });
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx, LoginRequest req) =>
        {
            var s = _settings.Current;
            if (string.IsNullOrEmpty(s.WebDashboardPasswordHash))
                return Results.Json(new { error = "Set a dashboard password from the desktop app first (Settings → Web dashboard)." },
                    statusCode: StatusCodes.Status403Forbidden);

            var ok = string.Equals(req.Username?.Trim(), s.WebDashboardUser, StringComparison.OrdinalIgnoreCase)
                     & PasswordHasher.Verify(req.Password ?? "", s.WebDashboardPasswordHash);
            var ip = IpUtil.Normalize(ctx.Connection.RemoteIpAddress?.ToString()) ?? "-";
            if (!ok)
            {
                // Failed dashboard logins feed the same brute-force engine as SMB.
                if (ip != "-")
                    await engine.ProcessFailureAsync(new FailedLogon(ip, time.NowMs(), req.Username ?? "-", "web dashboard", Sources.Web));
                await Task.Delay(Random.Shared.Next(400, 900));
                return Results.Json(new { error = "Wrong username or password" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (PasswordHasher.NeedsRehash(s.WebDashboardPasswordHash))
            {
                var rehash = PasswordHasher.Hash(req.Password!);
                _settings.Update(x => x with { WebDashboardPasswordHash = rehash });
            }
            var token = _sessions.Create(s.WebDashboardUser);
            ctx.Response.Cookies.Append(SessionStore.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromHours(12),
            });
            repo.Audit("web:" + s.WebDashboardUser, "login", ip, null, time.NowMs());
            return Results.Ok(new { user = s.WebDashboardUser });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            _sessions.Revoke(ctx.Request.Cookies[SessionStore.CookieName]);
            ctx.Response.Cookies.Delete(SessionStore.CookieName);
            return Results.Ok();
        });

        // ------------------------------------------------------------ overview
        app.MapGet("/api/status", () => Results.Ok(status.Build(WebRunning, WebUrl)));
        app.MapGet("/api/stats", () => Results.Ok(repo.Stats(time.NowMs())));
        app.MapGet("/api/attempts", (int? limit, string? search, long? beforeId) =>
            Results.Ok(repo.RecentAttempts(limit ?? 200, search, beforeId)));

        // ------------------------------------------------------------ IPs
        app.MapGet("/api/ips", (string? q, string? status, string? sort, bool? desc, int? page, int? pageSize) =>
        {
            var now = time.NowMs();
            var result = repo.SearchIps(q, status ?? "all", sort ?? "last_seen", desc ?? true, page ?? 0, pageSize ?? 100, now);
            foreach (var r in result.Items) r.Whitelisted = whitelist.IsWhitelisted(r.Ip);
            return Results.Ok(result);
        });

        app.MapGet("/api/ips/{ip}", (string ip) =>
        {
            var norm = IpUtil.Normalize(ip);
            if (norm == null) return Bad("Invalid IP");
            var detail = repo.GetIpDetail(norm);
            if (detail == null) return Results.NotFound(new { error = "No records for that IP" });
            detail.Record.Whitelisted = whitelist.IsWhitelisted(norm);
            return Results.Ok(detail);
        });

        app.MapPost("/api/ips/notes", (HttpContext ctx, NoteRequest req) =>
        {
            var norm = IpUtil.Normalize(req.Ip);
            if (norm == null) return Bad("Invalid IP");
            repo.SetNotes(norm, string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim());
            repo.Audit(Actor(ctx), "note", norm, req.Notes, time.NowMs());
            return Results.Ok();
        });

        // ------------------------------------------------------------ bans
        app.MapGet("/api/bans/active", () => Results.Ok(repo.ActiveBans(time.NowMs())));
        app.MapGet("/api/bans/history", (int? limit) => Results.Ok(repo.BanHistory(limit ?? 500)));

        app.MapPost("/api/bans", async (HttpContext ctx, BanRequest req) =>
        {
            var norm = IpUtil.Normalize(req.Ip);
            if (norm == null) return Bad("Invalid IP");
            if (whitelist.IsWhitelisted(norm)) return Bad("That IP is whitelisted (or belongs to this machine). Remove it from the whitelist first.");
            return Results.Ok(await engine.BanAsync(norm, req.Minutes, req.Reason, Actor(ctx)));
        });

        app.MapPost("/api/unban", async (HttpContext ctx, UnbanRequest req) =>
        {
            if (IpUtil.Normalize(req.Ip) == null) return Bad("Invalid IP");
            var lifted = await engine.UnbanAsync(req.Ip, req.ResetCounters, Actor(ctx));
            return Results.Ok(new { lifted });
        });

        app.MapPost("/api/firewall/reconcile", async () =>
        {
            var (added, removed) = await engine.ReconcileFirewallAsync();
            return Results.Ok(new { added, removed });
        });

        // ------------------------------------------------------------ whitelist
        app.MapGet("/api/whitelist", () => Results.Ok(repo.Whitelist()));

        app.MapPost("/api/whitelist", async (HttpContext ctx, WhitelistRequest req) =>
        {
            var value = req.Value?.Trim() ?? "";
            var kind = IpUtil.ClassifyWhitelistValue(value);
            if (kind == null) return Bad("Invalid value: use an IP, a CIDR range (192.168.1.0/24) or a domain name.");
            if (kind == "ip") value = IpUtil.Normalize(value)!;
            repo.AddWhitelist(value, kind, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), time.NowMs());
            repo.Audit(Actor(ctx), "whitelist+", value, req.Note, time.NowMs());
            await whitelist.RefreshAsync();
            // Whitelisting a banned IP lifts the ban.
            if (kind == "ip" && repo.GetIp(value)?.IsBannedAt(time.NowMs()) == true)
                await engine.UnbanAsync(value, true, Actor(ctx));
            hub.Publish(new LiveEvent { Type = "changed", At = time.NowMs(), Message = "whitelist" });
            return Results.Ok();
        });

        app.MapDelete("/api/whitelist/{id:long}", async (HttpContext ctx, long id) =>
        {
            var entry = repo.Whitelist().FirstOrDefault(w => w.Id == id);
            if (entry == null || !repo.RemoveWhitelist(id)) return Results.NotFound();
            repo.Audit(Actor(ctx), "whitelist-", entry.Value, null, time.NowMs());
            await whitelist.RefreshAsync();
            hub.Publish(new LiveEvent { Type = "changed", At = time.NowMs(), Message = "whitelist" });
            return Results.Ok();
        });

        // ------------------------------------------------------------ settings
        app.MapGet("/api/settings", () => Results.Ok(_settings.Current.Redacted()));

        app.MapPut("/api/settings", async (HttpContext ctx, OwlSettings incoming) =>
        {
            var old = _settings.Current;
            var updated = _settings.Update(_ => incoming with { WebDashboardPasswordHash = old.WebDashboardPasswordHash });
            repo.Audit(Actor(ctx), "settings", null, DescribeChanges(old, updated), time.NowMs());
            if (old.MonitorSecurityLog != updated.MonitorSecurityLog || old.MonitorSmbServerLog != updated.MonitorSmbServerLog ||
                old.AlertOnSuccessAfterFailures != updated.AlertOnSuccessAfterFailures || old.BanOnSuccessAfterFailures != updated.BanOnSuccessAfterFailures)
                await monitor.RestartAsync();
            if (!old.AutoEnableAuditPolicy && updated.AutoEnableAuditPolicy)
                await AuditPolicy.EnsureLogonFailureAuditingAsync();
            hub.Publish(new LiveEvent { Type = "changed", At = time.NowMs(), Message = "settings" });
            return Results.Ok(updated.Redacted());
        });

        app.MapPost("/api/settings/password", (HttpContext ctx, PasswordRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username)) return Bad("Username is required");
            if (req.Password is not { Length: >= 10 }) return Bad("The password must be at least 10 characters long");
            var hash = PasswordHasher.Hash(req.Password);
            _settings.Update(s => s with { WebDashboardUser = req.Username.Trim(), WebDashboardPasswordHash = hash });
            repo.Audit(Actor(ctx), "web password", req.Username.Trim(), null, time.NowMs());
            return Results.Ok();
        });

        app.MapPost("/api/discord/test", async (HttpContext ctx) =>
        {
            string? url = null;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
            }
            var (ok, message) = await discord.SendTestAsync(url);
            return ok ? Results.Ok(new { message }) : Bad(message);
        });

        app.MapGet("/api/audit", (int? limit) => Results.Ok(repo.AuditLog(limit ?? 300)));

        // ------------------------------------------------------------ data
        app.MapGet("/api/export/{kind}", (HttpContext ctx, string kind, int? days, string? sep) =>
            CsvExport.Write(ctx, repo, kind, days ?? 30, sep == "semicolon" ? ';' : ',', time.NowMs()));

        app.MapGet("/api/backup", (HttpContext ctx) =>
        {
            var file = Path.Combine(Paths.BackupsDir, $"owlsamba-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            repo.Db.BackupTo(file);
            repo.Audit(Actor(ctx), "backup", file, null, time.NowMs());
            return Results.File(file, "application/octet-stream", Path.GetFileName(file));
        });

        app.MapPost("/api/restore", async (HttpContext ctx) =>
        {
            var temp = Path.Combine(Paths.BackupsDir, $"restore-{Guid.NewGuid():N}.db");
            try
            {
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var f = form.Files.FirstOrDefault();
                    if (f == null) return Bad("Missing file");
                    await using var fs = File.Create(temp);
                    await f.CopyToAsync(fs);
                }
                else
                {
                    await using var fs = File.Create(temp);
                    await ctx.Request.Body.CopyToAsync(fs);
                }
                // Safety net: snapshot current DB before overwriting it.
                repo.Db.BackupTo(Path.Combine(Paths.BackupsDir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
                repo.Db.RestoreFrom(temp);
                _settings.Reload();
                await whitelist.RefreshAsync();
                await engine.ReconcileFirewallAsync();
                repo.Audit(Actor(ctx), "restore", null, null, time.NowMs());
                hub.Publish(new LiveEvent { Type = "changed", At = time.NowMs(), Message = "restore" });
                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Bad(ex.Message);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        });

        app.MapPost("/api/migrate", async (HttpContext ctx, MigrateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path)) return Bad("Path is required");
            var report = importer.Import(req.Path.Trim(), req.ReplaceLegacyFirewallRules, force: false);
            await whitelist.RefreshAsync();
            hub.Publish(new LiveEvent { Type = "changed", At = time.NowMs(), Message = "migration" });
            return Results.Ok(report);
        });

        // ------------------------------------------------------------ real-time stream (SSE)
        app.MapGet("/api/stream", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var (reader, subscription) = hub.Subscribe();
            using (subscription)
            {
                var ct = ctx.RequestAborted;
                await ctx.Response.WriteAsync("retry: 2000\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        keepAlive.CancelAfter(TimeSpan.FromSeconds(15));
                        bool hasData;
                        try { hasData = await reader.WaitToReadAsync(keepAlive.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            await ctx.Response.WriteAsync(": ping\n\n", ct);
                            await ctx.Response.Body.FlushAsync(ct);
                            continue;
                        }
                        if (!hasData) break;
                        while (reader.TryRead(out var e))
                            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, json)}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) { }
            }
        });
    }

    private static string DescribeChanges(OwlSettings a, OwlSettings b)
    {
        var changes = new List<string>();
        foreach (var p in typeof(OwlSettings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(OwlSettings.WebDashboardPasswordHash)))
        {
            var va = p.GetValue(a);
            var vb = p.GetValue(b);
            if (!Equals(va, vb))
                changes.Add(p.Name.Contains("Webhook") ? $"{p.Name}: (changed)" : $"{p.Name}: {va} → {vb}");
        }
        return changes.Count == 0 ? "no changes" : string.Join("; ", changes);
    }
}

public static class CsvExport
{
    public static async Task<IResult> Write(HttpContext ctx, Repository repo, string kind, int days, char sep, long now)
    {
        var name = kind.Replace(".csv", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (name is not ("attempts" or "ips" or "bans")) return Results.NotFound();

        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"owlsamba-{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv\"";
        await using var w = new StreamWriter(ctx.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string F(object? v)
        {
            var s = v switch
            {
                null => "",
                long ms when ms > 946_684_800_000L => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? "",
            };
            // Neutralize spreadsheet formula injection from attacker-controlled fields (user names).
            if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                s = "'" + s;
            return s.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        async Task Row(params object?[] cells) => await w.WriteLineAsync(string.Join(sep, cells.Select(F)));

        switch (name)
        {
            case "attempts":
                await Row("time", "ip", "country", "user", "workstation", "source", "status", "logon_type", "whitelisted");
                foreach (var a in repo.AttemptsForExport(now - days * 86_400_000L, now))
                    await Row(a.OccurredAt, a.Ip, a.CountryCode, a.User, a.Workstation, a.Source, a.Status, a.LogonType, a.Whitelisted ? "yes" : "no");
                break;
            case "ips":
                await Row("ip", "country", "isp", "attempts", "first_seen", "last_seen", "last_user", "status", "banned_until", "reason", "times_banned", "notes");
                foreach (var i in repo.AllIps())
                    await Row(i.Ip, i.Country, i.Isp, i.TotalAttempts, i.FirstSeen, i.LastSeen, i.LastUser,
                        i.IsBannedAt(now) ? (i.IsPermanent ? "permanent" : "temporary") : "free",
                        i.BanUntil, i.BanReason, i.BanCount, i.Notes);
                break;
            case "bans":
                await Row("ip", "kind", "started", "expires", "ended", "end_reason", "reason", "by", "country");
                foreach (var b in repo.BanHistory(10_000))
                    await Row(b.Ip, b.Kind, b.StartedAt, b.ExpiresAt, b.EndedAt, b.EndReason, b.Reason, b.CreatedBy, b.Country);
                break;
        }
        await w.FlushAsync();
        return Results.Empty;
    }
}
