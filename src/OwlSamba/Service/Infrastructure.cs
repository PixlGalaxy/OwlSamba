using System.Threading.Channels;
using OwlSamba.Core;
using OwlSamba.Data;

namespace OwlSamba.Service;

/// <summary>Holds the live settings; every change is persisted and broadcast.</summary>
public sealed class SettingsProvider
{
    private readonly Repository _repo;
    private OwlSettings _current;

    public SettingsProvider(Repository repo)
    {
        _repo = repo;
        _current = repo.GetSettings();
        if (!repo.HasSettings()) repo.SaveSettings(_current);
    }

    public OwlSettings Current => Volatile.Read(ref _current);

    public event Action<OwlSettings, OwlSettings>? Changed;

    public OwlSettings Update(Func<OwlSettings, OwlSettings> change)
    {
        OwlSettings old, updated;
        lock (_repo)
        {
            old = _current;
            updated = change(old).Normalize();
            _repo.SaveSettings(updated);
            Volatile.Write(ref _current, updated);
        }
        Changed?.Invoke(old, updated);
        return updated;
    }

    /// <summary>Re-reads settings from the DB (after a restore or a migration).</summary>
    public void Reload()
    {
        var old = _current;
        Volatile.Write(ref _current, _repo.GetSettings());
        Changed?.Invoke(old, _current);
    }
}

/// <summary>Fan-out of real-time events to every connected client (desktop UI + web dashboards).</summary>
public sealed class LiveHub
{
    private readonly object _gate = new();
    private readonly List<Channel<LiveEvent>> _subscribers = new();

    public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

    public void Publish(LiveEvent e)
    {
        lock (_gate)
            foreach (var s in _subscribers)
                s.Writer.TryWrite(e);
    }

    public (ChannelReader<LiveEvent> reader, IDisposable subscription) Subscribe()
    {
        var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate) _subscribers.Add(channel);
        return (channel.Reader, new Unsubscriber(() =>
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }));
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}

public static class Clock
{
    public static long NowMs(this TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();
}
