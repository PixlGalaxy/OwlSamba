using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OwlSamba.Core;
using OwlSamba.Data;
using OwlSamba.Service;

namespace OwlSamba.Detection;

/// <summary>
/// Subscribes to the Windows event log with EventLogWatcher: Windows pushes each event to us the
/// moment it is written (no polling), so a failed SMB logon reaches the engine in milliseconds.
///
/// On start, events written while the service was stopped are replayed from the last processed
/// EventRecordID (or the last 24h on first run), then the live subscription takes over without
/// gaps or duplicates.
/// </summary>
public sealed class EventLogMonitor : IAsyncDisposable
{
    private sealed record Subscription(string Key, string Display, string Channel, string XPath, Func<string, long, object?> Parse);

    private sealed class Runner
    {
        public required Subscription Sub;
        public EventLogWatcher? Watcher;
        public readonly object Gate = new();
        public bool CatchingUp = true;
        public readonly List<(long id, object? item)> Buffer = new();
        public long MaxCatchUpId;
        public long LastId;
        public long Seen;
        public string? Error;
    }

    private readonly BruteForceEngine _engine;
    private readonly Repository _repo;
    private readonly SettingsProvider _settings;
    private readonly ILogger<EventLogMonitor> _log;
    private readonly Channel<QueueItem> _queue = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Runner> _runners = new();
    private CancellationTokenSource? _cts;
    private Task? _consumer;
    private Task? _bookmarkFlusher;

    public EventLogMonitor(BruteForceEngine engine, Repository repo, SettingsProvider settings, ILogger<EventLogMonitor> log)
    {
        _engine = engine;
        _repo = repo;
        _settings = settings;
        _log = log;
    }

    private static IEnumerable<Subscription> Subscriptions(OwlSettings s)
    {
        if (s.MonitorSecurityLog)
            yield return new("security4625", "Security · 4625 (failed logon)", "Security",
                "*[System[(EventID=4625)]]", (xml, t) => EventParsing.ParseSecurity4625(xml, t));
        if (s.MonitorSmbServerLog)
            yield return new("smb551", "SMBServer · 551 (SMB authentication failure)", "Microsoft-Windows-SMBServer/Security",
                "*[System[(EventID=551)]]", (xml, t) => EventParsing.ParseSmb551(xml, t));
        if (s.MonitorSecurityLog && (s.AlertOnSuccessAfterFailures || s.BanOnSuccessAfterFailures))
            yield return new("security4624", "Security · 4624 (successful network logon)", "Security",
                "*[System[(EventID=4624)] and EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]",
                (xml, t) => EventParsing.ParseSecurity4624(xml, t));
    }

    public List<SourceStatus> Status()
    {
        lock (_runners)
            return _runners.Select(r => new SourceStatus
            {
                Name = r.Sub.Display,
                Enabled = true,
                Active = r.Watcher != null && r.Error == null,
                Error = r.Error,
                EventsSeen = Interlocked.Read(ref r.Seen),
            }).ToList();
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token));
        _bookmarkFlusher = Task.Run(() => FlushBookmarksLoopAsync(_cts.Token));

        foreach (var sub in Subscriptions(_settings.Current))
        {
            var runner = new Runner { Sub = sub };
            lock (_runners) _runners.Add(runner);
            await Task.Run(() => StartRunner(runner));
        }
    }

    /// <summary>Restarts subscriptions (e.g. after the sources were changed in settings).</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private void StartRunner(Runner r)
    {
        try
        {
            // 1) Live subscription first; events are buffered until the replay is done.
            var query = new EventLogQuery(r.Sub.Channel, PathType.LogName, r.Sub.XPath);
            r.Watcher = new EventLogWatcher(query, bookmark: null, readExistingEvents: false);
            r.Watcher.EventRecordWritten += (_, args) => OnEvent(r, args);
            r.Watcher.Enabled = true;

            // 2) Replay what happened while we were not running.
            CatchUp(r);

            // 3) Flush buffered live events that were not part of the replay.
            lock (r.Gate)
            {
                foreach (var (id, item) in r.Buffer)
                    if (id > r.MaxCatchUpId) Enqueue(r, id, item);
                r.Buffer.Clear();
                r.CatchingUp = false;
            }
            _log.LogInformation("Listening to {Source}", r.Sub.Display);
        }
        catch (Exception) when (DisposeWatcher(r)) { /* never: DisposeWatcher returns false */ }
        catch (EventLogNotFoundException)
        {
            r.Error = "This event log does not exist on this system";
            _log.LogWarning("Event channel {Channel} not found; source {Source} disabled", r.Sub.Channel, r.Sub.Key);
        }
        catch (UnauthorizedAccessException)
        {
            r.Error = "Access denied (must run as the service / administrator)";
            _log.LogError("Access denied to {Channel}", r.Sub.Channel);
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
            _log.LogError(ex, "Could not subscribe to {Source}", r.Sub.Display);
        }
    }

    /// <summary>Exception filter helper: releases a half-started subscription so it stops raising errors.</summary>
    private static bool DisposeWatcher(Runner r)
    {
        try { if (r.Watcher != null) { r.Watcher.Enabled = false; r.Watcher.Dispose(); } } catch { /* ignore */ }
        r.Watcher = null;
        return false;
    }

    private void CatchUp(Runner r)
    {
        var bookmarkKey = "bookmark." + r.Sub.Key;
        long.TryParse(_repo.GetValue(bookmarkKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastId);
        var inner = r.Sub.XPath[2..^1]; // strip "*[" and "]"
        var xpath = lastId > 0
            ? $"*[{inner} and System[(EventRecordID > {lastId})]]"
            : $"*[{inner} and System[TimeCreated[timediff(@SystemTime) <= 86400000]]]";

        int replayed = 0;
        using var reader = new EventLogReader(new EventLogQuery(r.Sub.Channel, PathType.LogName, xpath));
        for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                var id = rec.RecordId ?? 0;
                var item = Parse(r, rec);
                Enqueue(r, id, item);
                if (item != null) replayed++;
                r.MaxCatchUpId = Math.Max(r.MaxCatchUpId, id);
            }
        }
        if (replayed > 0)
            _log.LogInformation("Replayed {Count} event(s) from {Source} written while OwlSamba was stopped", replayed, r.Sub.Display);
    }

    private void OnEvent(Runner r, EventRecordWrittenEventArgs args)
    {
        if (args.EventException != null)
        {
            r.Error = args.EventException.Message;
            _log.LogError(args.EventException, "Event subscription error on {Source}", r.Sub.Display);
            return;
        }
        using var rec = args.EventRecord;
        if (rec == null) return;
        r.Error = null;
        var id = rec.RecordId ?? 0;
        var item = Parse(r, rec);
        lock (r.Gate)
        {
            if (r.CatchingUp) { r.Buffer.Add((id, item)); return; }
        }
        Enqueue(r, id, item);
    }

    private object? Parse(Runner r, EventRecord rec)
    {
        try
        {
            var t = rec.TimeCreated is { } tc
                ? new DateTimeOffset(tc.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return r.Sub.Parse(rec.ToXml(), t);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Unparseable event {Id} on {Source}", rec.RecordId, r.Sub.Key);
            return null;
        }
    }

    private sealed record QueueItem(Runner Runner, long RecordId, object? Item);

    /// <summary>Unparseable/irrelevant events are queued too (Item = null) so the bookmark advances in order.</summary>
    private void Enqueue(Runner r, long id, object? item)
    {
        if (item != null) Interlocked.Increment(ref r.Seen);
        _queue.Writer.TryWrite(new QueueItem(r, id, item));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (runner, id, item) in _queue.Reader.ReadAllAsync(ct))
            {
                switch (item)
                {
                    case FailedLogon f: await _engine.ProcessFailureAsync(f); break;
                    case SuccessfulLogon s: await _engine.ProcessSuccessAsync(s); break;
                }
                runner.LastId = Math.Max(runner.LastId, id);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushBookmarksLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                FlushBookmarks();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FlushBookmarks()
    {
        List<Runner> copy;
        lock (_runners) copy = _runners.ToList();
        foreach (var r in copy.Where(r => r.LastId > 0))
        {
            try { _repo.SetValue("bookmark." + r.Sub.Key, r.LastId.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) { _log.LogDebug(ex, "Bookmark flush failed"); }
        }
    }

    public async Task StopAsync()
    {
        List<Runner> copy;
        lock (_runners) { copy = _runners.ToList(); _runners.Clear(); }
        foreach (var r in copy)
        {
            try { if (r.Watcher != null) { r.Watcher.Enabled = false; r.Watcher.Dispose(); } }
            catch { /* ignore */ }
        }
        if (_cts != null)
        {
            // Let the consumer drain what's already queued before stopping.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_queue.Reader.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            _cts.Cancel();
            try { if (_consumer != null) await _consumer; } catch { /* ignore */ }
            try { if (_bookmarkFlusher != null) await _bookmarkFlusher; } catch { /* ignore */ }
            _cts.Dispose();
            _cts = null;
        }
        lock (_runners) _runners.AddRange(copy);
        FlushBookmarks();
        lock (_runners) _runners.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
