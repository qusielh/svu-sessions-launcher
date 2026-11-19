using System;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Wraps a session connection in a silent retry loop so a failed join never
/// reaches the user as a dead connection. It opens the connection, waits for its
/// <c>{"t":"hello"}</c> (the page's join signal), and if the connection ends or
/// stays silent past a per-attempt timeout, discards it and tries again up to a
/// fixed number of attempts. The UI (<see cref="ISessionConnection"/>) sees one
/// stable connection throughout, so the chat window opens immediately and
/// simply keeps its loader up while attempts run.
///
/// <para>Events from whichever connection is current are re-emitted unchanged.
/// Two synthetic events are added for the page:</para>
/// <list type="bullet">
///   <item><c>{"t":"retrying","attempt":N,"max":M}</c> at the start of each attempt.</item>
///   <item><c>{"t":"joinFailed","max":M}</c> once every attempt has failed, at
///   which point the page shows "check your internet" + Retry and the user's
///   Retry re-enters the loop via <see cref="RetryAsync"/>.</item>
/// </list>
///
/// <para><see cref="Exited"/> is deliberately suppressed while an attempt is
/// still trying: only a real end (connection lost <em>after</em> joining, or the
/// whole thing is disposed) is surfaced.</para>
/// </summary>
public sealed class RetryingSessionConnection : ISessionConnection
{
    private const int PerAttemptTimeoutMs = 30000;

    private readonly Func<ISessionConnection> _startConnection;
    private readonly Func<bool> _isOffline;
    private readonly int _maxAttempts;
    private readonly object _gate = new();

    private ISessionConnection? _inner;
    private Timer? _guard;
    private int _attempt;
    private int _failures;
    private bool _joined;
    private bool _everJoined;
    private bool _gaveUp;
    private bool _disposed;

    public RetryingSessionConnection(
        Func<ISessionConnection> startConnection,
        int maxAttempts,
        Func<bool>? isOffline = null)
    {
        _startConnection = startConnection ?? throw new ArgumentNullException(nameof(startConnection));
        _isOffline = isOffline ?? DefaultIsOffline;
        _maxAttempts = Math.Max(1, maxAttempts);
        BeginAttempt();
    }

    public event Action<string>? EventReceived;
    public event Action<int>? Exited;
    public event Action<ViewportFrame>? ViewportReceived;
    public event Action<bool>? SharingChanged;

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _disposed || (_gaveUp && (_inner is null || _inner.HasExited));
            }
        }
    }

    public int ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _inner?.ProcessId ?? -1;
            }
        }
    }

    public Task SendPublicAsync(string text) => Inner()?.SendPublicAsync(text) ?? Task.CompletedTask;

    public Task LeaveAsync() => Inner()?.LeaveAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Pings only a joined connection. Before <c>hello</c> the inner link exists
    /// but the room is not joined yet; answering then would tell the page the
    /// session is live and re-enable its composer during a rejoin.
    /// </summary>
    public Task PingAsync()
    {
        ISessionConnection? inner;
        lock (_gate)
        {
            if (!_joined)
            {
                return Task.CompletedTask;
            }

            inner = _inner;
        }

        return inner?.PingAsync() ?? Task.CompletedTask;
    }

    public Task SendVoiceMuteAsync(bool muted) => Inner()?.SendVoiceMuteAsync(muted) ?? Task.CompletedTask;

    public Task SendVoiceVolumeAsync(float volume) => Inner()?.SendVoiceVolumeAsync(volume) ?? Task.CompletedTask;

    /// <summary>Re-enters the attempt loop after a reported join failure.</summary>
    public Task RetryAsync()
    {
        lock (_gate)
        {
            if (_disposed || _joined)
            {
                return Task.CompletedTask;
            }

            _attempt = 0;
            _failures = 0;
            _gaveUp = false;
        }

        StopGuard();
        Detach();
        BeginAttempt();
        return Task.CompletedTask;
    }

    public void Kill() => Inner()?.Kill();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopGuard();
        Detach();
    }

    private ISessionConnection? Inner()
    {
        lock (_gate)
        {
            return _inner;
        }
    }

    private void BeginAttempt()
    {
        int attemptNumber;
        lock (_gate)
        {
            if (_disposed || _joined || _gaveUp)
            {
                return;
            }

            _attempt++;
            attemptNumber = _attempt;
        }

        Emit("{\"t\":\"retrying\",\"attempt\":" + attemptNumber + ",\"max\":" + _maxAttempts + "}");
        Task.Run(() => RunAttempt(attemptNumber));
    }

    private void RunAttempt(int attemptNumber)
    {
        ISessionConnection inner;
        try
        {
            inner = _startConnection();
        }
        catch (Exception ex)
        {
            Log.Write($"join attempt {attemptNumber} could not start: {ex.Message}");
            OnAttemptFailed(attemptNumber);
            return;
        }

        lock (_gate)
        {
            if (_disposed || _joined || _gaveUp || attemptNumber != _attempt)
            {
                inner.Dispose();
                return;
            }

            _inner = inner;
        }

        inner.EventReceived += json => OnInnerEvent(attemptNumber, json);
        inner.Exited += code => OnInnerExited(attemptNumber, code);
        inner.ViewportReceived += frame =>
        {
            if (IsCurrent(attemptNumber))
            {
                ViewportReceived?.Invoke(frame);
            }
        };
        inner.SharingChanged += active =>
        {
            if (IsCurrent(attemptNumber))
            {
                SharingChanged?.Invoke(active);
            }
        };

        StartGuard(attemptNumber);
    }

    private void OnInnerEvent(int attemptNumber, string json)
    {
        if (!IsCurrent(attemptNumber))
        {
            return;
        }

        if (!_joined && IsHello(json))
        {
            lock (_gate)
            {
                _joined = true;
                _everJoined = true;
                _failures = 0;
            }

            StopGuard();
        }

        EventReceived?.Invoke(json);
    }

    private void OnInnerExited(int attemptNumber, int code)
    {
        bool dropped;
        lock (_gate)
        {
            dropped = _joined && attemptNumber == _attempt;
            if (dropped)
            {
                // The session was live: a network drop must not end the room.
                // Invalidate this attempt and rejoin silently; the page keeps its
                // roster and transcript and shows its reconnect banner.
                _joined = false;
                _attempt++;
            }
        }

        if (dropped)
        {
            StopGuard();
            Detach();

            // The live stream is gone; hide the viewer instead of leaving the
            // last frame frozen on screen. It reappears when the rejoined
            // connection starts sharing again.
            SharingChanged?.Invoke(false);

            Log.Write($"session dropped after join (code {code}); rejoining");
            ScheduleRejoin(1200);
            return;
        }

        OnAttemptFailed(attemptNumber);
    }

    /// <summary>
    /// Waits out a short network blip (or the whole offline period) before the
    /// next join attempt. Offline time never spends the attempt budget, so a
    /// live room waits for the network instead of being closed.
    /// </summary>
    private void ScheduleRejoin(int delayMs)
    {
        Task.Run(async () =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            await WaitForNetworkAsync();
            BeginAttempt();
        });
    }

    private async Task WaitForNetworkAsync()
    {
        while (!_disposed && IsOffline())
        {
            await Task.Delay(2000);
        }
    }

    private void OnAttemptFailed(int attemptNumber)
    {
        bool retry;
        bool wasLive;

        // Probe outside the lock: a network probe can take a moment and the UI
        // reads Inner()/HasExited from other threads.
        bool offline = IsOffline();
        lock (_gate)
        {
            if (_disposed || _joined || _gaveUp || attemptNumber != _attempt)
            {
                return;
            }

            wasLive = _everJoined;

            if (wasLive && offline)
            {
                // A live room that lost the network is not a failed join: keep
                // the room open and wait for connectivity instead of spending
                // the outage budget and closing the session.
                retry = true;
            }
            else
            {
                // Budget is per outage: a successful join resets it, so a long
                // session that drops repeatedly keeps recovering.
                _failures++;

                // A join that fails while the machine has no network at all
                // cannot be fixed by another silent attempt, so give up at once:
                // the page then shows its "check your internet" card (and Retry)
                // after this single failure instead of waiting out the others.
                retry = !offline && _failures < _maxAttempts;
                if (!retry)
                {
                    _gaveUp = true;
                }
            }
        }

        StopGuard();
        Detach();

        if (retry)
        {
            if (wasLive && offline)
            {
                ScheduleRejoin(0);
            }
            else
            {
                BeginAttempt();
            }
        }
        else if (wasLive)
        {
            // The room was live and could not be recovered: end the session so
            // the windows close cleanly instead of sitting on a dead stream.
            Exited?.Invoke(-1);
        }
        else
        {
            Emit("{\"t\":\"joinFailed\",\"max\":" + _maxAttempts + "}");
        }
    }

    /// <summary>
    /// True when the machine has no usable network interface at all (Wi-Fi off,
    /// cable out). Mirrors the page's own <c>navigator.onLine</c> heuristic;
    /// best effort so a probe failure simply keeps normal retrying.
    /// </summary>
    private bool IsOffline()
    {
        try
        {
            return _isOffline();
        }
        catch
        {
            return false;
        }
    }

    private static bool DefaultIsOffline()
    {
        try
        {
            return !NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }

    private bool IsCurrent(int attemptNumber)
    {
        lock (_gate)
        {
            return !_disposed && attemptNumber == _attempt;
        }
    }

    private void StartGuard(int attemptNumber)
    {
        StopGuard();
        _guard = new Timer(_ => OnAttemptFailed(attemptNumber), null, PerAttemptTimeoutMs, Timeout.Infinite);
    }

    private void StopGuard()
    {
        Timer? guard;
        lock (_gate)
        {
            guard = _guard;
            _guard = null;
        }

        guard?.Dispose();
    }

    private void Detach()
    {
        ISessionConnection? inner;
        lock (_gate)
        {
            inner = _inner;
            _inner = null;
        }

        inner?.Dispose();
    }

    private static bool IsHello(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("t", out var type) && type.GetString() == "hello";
        }
        catch
        {
            return false;
        }
    }

    private void Emit(string json) => EventReceived?.Invoke(json);
}
