using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OwlSamba.Core;
using OwlSamba.Server;

namespace OwlSamba.UI;

public sealed class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Talks to the protection service over the local loopback API.</summary>
public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient? _http;
    private LocalEndpoint? _endpoint;
    private readonly object _gate = new();

    private HttpClient Http
    {
        get
        {
            lock (_gate)
            {
                var current = LocalEndpoint.TryRead();
                if (current == null) throw new ApiException("The OwlSamba service is not running.");
                if (_http == null || current != _endpoint)
                {
                    _http?.Dispose();
                    _endpoint = current;
                    _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                    {
                        BaseAddress = new Uri($"http://127.0.0.1:{current.Port}/"),
                        Timeout = TimeSpan.FromSeconds(60),
                    };
                    _http.DefaultRequestHeaders.Add("X-Owl-Key", current.Key);
                }
                return _http;
            }
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
        {
            string message = $"Error {(int)resp.StatusCode}";
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) message = e.GetString() ?? message;
            }
            catch { /* body wasn't JSON */ }
            throw new ApiException(message, resp.StatusCode);
        }
        if (typeof(T) == typeof(bool)) return (T)(object)true;
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<HttpResponseMessage> Send(Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        try { return await call(Http); }
        catch (HttpRequestException ex) { throw new ApiException("Could not connect to the service: " + ex.Message); }
        catch (TaskCanceledException) { throw new ApiException("The service did not respond in time."); }
    }

    public async Task<T> Get<T>(string path) => await Read<T>(await Send(h => h.GetAsync(path)));
    public async Task<T> Post<T>(string path, object? body) => await Read<T>(await Send(h => h.PostAsJsonAsync(path, body, Json)));
    public async Task<T> Put<T>(string path, object body) => await Read<T>(await Send(h => h.PutAsJsonAsync(path, body, Json)));
    public async Task Delete(string path) => await Read<bool>(await Send(h => h.DeleteAsync(path)));

    public async Task Download(string path, string destination)
    {
        var resp = await Send(h => h.GetAsync(path, HttpCompletionOption.ResponseHeadersRead));
        if (!resp.IsSuccessStatusCode) await Read<bool>(resp);
        await using var fs = File.Create(destination);
        await resp.Content.CopyToAsync(fs);
    }

    public async Task UploadRestore(string file)
    {
        await using var fs = File.OpenRead(file);
        var content = new StreamContent(fs);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        await Read<bool>(await Send(h => h.PostAsync("api/restore", content)));
    }

    /// <summary>
    /// Keeps a Server-Sent Events connection open, reconnecting forever.
    /// <paramref name="onConnection"/> reports connection state changes.
    /// </summary>
    public async Task RunStreamAsync(Action<LiveEvent> onEvent, Action<bool> onConnection, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "api/stream");
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                onConnection(true);
                delay = TimeSpan.FromSeconds(1);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    // Server pings every 15s; a missing ping means a dead connection.
                    var lineTask = reader.ReadLineAsync(ct).AsTask();
                    var done = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(40), ct));
                    if (done != lineTask) break;
                    var line = await lineTask;
                    if (line == null) break;
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    var e = JsonSerializer.Deserialize<LiveEvent>(line.AsSpan(6), Json);
                    if (e != null) onEvent(e);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* reconnect below */ }

            onConnection(false);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 5));
        }
    }

    public void Dispose() => _http?.Dispose();
}
