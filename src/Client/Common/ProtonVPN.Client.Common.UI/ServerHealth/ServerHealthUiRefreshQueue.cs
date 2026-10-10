using System;
using System.Threading;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

// Throttle list rebuilds, not measurements. A fast full pass must not rebuild the catalogue per reply.
public sealed class ServerHealthUiRefreshQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<Action> _dispatch;
    private readonly Action _refresh;
    private readonly Timer _timer;
    private bool _pending;
    private bool _disposed;

    public ServerHealthUiRefreshQueue(Action<Action> dispatch, Action refresh)
    {
        _dispatch = dispatch;
        _refresh = refresh;
        _timer = new(_ => Dispatch(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Request()
    {
        lock (_sync)
        {
            if (_disposed || _pending) { return; }
            _pending = true;
            _timer.Change(200, Timeout.Infinite);
        }
    }

    private void Dispatch()
    {
        lock (_sync) { if (_disposed) { return; } }
        _dispatch(() =>
        {
            lock (_sync)
            {
                if (_disposed) { return; }
                _pending = false;
            }
            _refresh();
        });
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _timer.Dispose(); }
    }
}
