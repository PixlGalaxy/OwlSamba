using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Detection;
using OwlSamba.Service;

namespace OwlSamba.Integrations;

/// <summary>Posts ban / alert embeds to a Discord webhook, honoring Discord rate limits.</summary>
public sealed class DiscordNotifier : BackgroundService, INotifier
{
    private const int Yellow = 0xFFD400, Red = 0xE5484D, Orange = 0xF5A524;

    private readonly SettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DiscordNotifier> _log;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public DiscordNotifier(SettingsProvider settings, IHttpClientFactory http, ILogger<DiscordNotifier> log)
    {
        _settings = settings;
        _http = http;
        _log = log;
    }

    private static string Ts(long ms) => $"<t:{ms / 1000}:F>";

    public void BanApplied(BanRecord ban)
    {
        var s = _settings.Current;
        if (!s.DiscordEnabled) return;
        var permanent = ban.ExpiresAt == null;
        if (permanent && !s.DiscordNotifyPermanentBans) return;
        if (!permanent && !s.DiscordNotifyTempBans) return;
        var title = ban.Kind switch
        {
            BanKinds.Permanent => "⛔ Permanent ban",
            BanKinds.Temporary => "⏱️ Temporary ban",
            _ => "🛡️ Manual ban",
        };
        _queue.Writer.TryWrite(Embed(title, permanent ? Red : Yellow,
            ("IP", $"`{ban.Ip}`"),
            ("Country", string.IsNullOrEmpty(ban.Country) ? "—" : ban.Country),
            ("Attempts", ban.TotalAttempts.ToString()),
            ("Until", ban.ExpiresAt is { } e ? Ts(e) : "Permanent"),
            ("Reason", ban.Reason ?? "—")));
    }

    public void SuspiciousLogon(string ip, string user, long at, long recentFailures)
    {
        var s = _settings.Current;
        if (!s.DiscordEnabled || !s.DiscordNotifySuspiciousLogons) return;
        _queue.Writer.TryWrite(Embed("🚨 Successful logon after failed attempts", Orange,
            ("IP", $"`{ip}`"), ("User", user), ("Previous failures", recentFailures.ToString()), ("When", Ts(at))));
    }

    public async Task<(bool ok, string message)> SendTestAsync(string? webhookOverride = null)
    {
        var url = string.IsNullOrWhiteSpace(webhookOverride) ? _settings.Current.DiscordWebhookUrl : webhookOverride.Trim();
        if (!IsValidWebhook(url)) return (false, "Invalid webhook URL");
        var payload = Embed("✅ OwlSamba test", Yellow, ("Host", _settings.Current.Hostname), ("Version", AppInfo.Version));
        try
        {
            var resp = await _http.CreateClient("discord").PostAsJsonAsync(url, payload);
            return resp.IsSuccessStatusCode ? (true, "Message sent") : (false, $"Discord returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool IsValidWebhook(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase));

    private object Embed(string title, int color, params (string name, string value)[] fields) => new
    {
        username = "OwlSamba",
        embeds = new[]
        {
            new
            {
                title,
                color,
                fields = fields.Select(f => new { name = f.name, value = f.value, inline = f.name != "Reason" }).ToArray(),
                footer = new { text = $"OwlSamba · {_settings.Current.Hostname}" },
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
            },
        },
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var payload in _queue.Reader.ReadAllAsync(ct))
        {
            var url = _settings.Current.DiscordWebhookUrl;
            if (!_settings.Current.DiscordEnabled || !IsValidWebhook(url)) continue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var resp = await _http.CreateClient("discord").PostAsJsonAsync(url, payload, ct);
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                        await Task.Delay(wait, ct);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                        _log.LogWarning("Discord webhook returned {Status}", (int)resp.StatusCode);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Discord webhook failed: {Error}", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
        }
    }
}

/// <summary>
/// Country / ISP lookup through ip-api.com's batch endpoint (free tier: 15 batch requests/min,
/// 100 IPs each). Private addresses are labeled locally without any network call.
/// </summary>
public sealed class GeoIpService : BackgroundService, IGeoQueue
{
    private readonly Repository _repo;
    private readonly SettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<GeoIpService> _log;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _inFlight = new();

    public GeoIpService(Repository repo, SettingsProvider settings, IHttpClientFactory http, TimeProvider time, ILogger<GeoIpService> log)
    {
        _repo = repo;
        _settings = settings;
        _http = http;
        _time = time;
        _log = log;
    }

    public void Enqueue(string ip)
    {
        lock (_inFlight)
            if (!_inFlight.Add(ip)) return;
        _queue.Writer.TryWrite(ip);
    }

    private sealed class GeoResult
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
        [JsonPropertyName("isp")] public string? Isp { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        foreach (var ip in _repo.IpsMissingGeo(1000)) Enqueue(ip);

        var batch = new List<string>();
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            if (!await _queue.Reader.WaitToReadAsync(ct)) break;
            while (batch.Count < 100 && _queue.Reader.TryRead(out var ip)) batch.Add(ip);

            var now = _time.NowMs();
            var remote = new List<string>();
            foreach (var ip in batch)
            {
                if (IpUtil.IsPrivate(ip)) _repo.UpdateGeo(ip, "Local network", "LAN", null, now);
                else remote.Add(ip);
            }

            if (remote.Count > 0 && _settings.Current.GeoIpEnabled)
            {
                try
                {
                    var resp = await _http.CreateClient("geo").PostAsJsonAsync(
                        "http://ip-api.com/batch?fields=status,query,country,countryCode,isp", remote, ct);
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        foreach (var ip in remote) _queue.Writer.TryWrite(ip);
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                        continue;
                    }
                    var results = await resp.Content.ReadFromJsonAsync<List<GeoResult>>(ct) ?? new();
                    foreach (var r in results.Where(r => r.Query != null))
                        _repo.UpdateGeo(r.Query!, r.Status == "success" ? r.Country : "Unknown",
                            r.Status == "success" ? r.CountryCode : null, r.Isp, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug("GeoIP lookup failed: {Error}", ex.Message);
                }
            }

            lock (_inFlight) foreach (var ip in batch) _inFlight.Remove(ip);
            // Stay well under the free-tier limit (15 requests/min).
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
