using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using SvuSessionsLauncher.Linktivity;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// The student side of the session, implemented entirely in-process on top of
/// <see cref="SessionEngine"/>. It adapts the engine's peer/chat/viewport/voice
/// events into the JSON shapes the chat web UI consumes (<c>hello</c>, <c>peers</c>,
/// <c>chat</c>).
/// </summary>
public sealed class LinktivitySessionConnection : ISessionConnection
{
    private readonly SessionEngine _engine = new();
    private readonly ViewportProtocol _viewport = new();
    private readonly VoicePlayback _voice = new();
    private readonly string _user;
    private readonly object _gate = new();
    private bool _hello;
    private bool _failed;
    private bool _disposed;
    private bool _sharing;
    private bool _voiceMuted;
    private float _voiceVolume = 1f;

    public LinktivitySessionConnection(string host, int port, int type, string sessionId, string user,
        int width, int height)
    {
        _user = user ?? string.Empty;

        _engine.SelfJoined += OnSelfJoined;
        _engine.PeerAdded += _ => EmitPeers();
        _engine.PeerUpdated += _ => EmitPeers();
        _engine.PeerRemoved += _ => EmitPeers();
        _engine.ChatReceived += OnChat;
        _engine.ViewportReceived += OnViewportData;
        _engine.VoiceFrameReceived += OnVoiceFrame;
        _engine.Failed += OnEngineFailed;

        Task.Run(() =>
        {
            bool joined;
            try
            {
                joined = _engine.Join(host, port, type, sessionId, _user, width, height, false);
            }
            catch (Exception exception)
            {
                Log.Write($"linktivity join threw: {exception.Message}");
                joined = false;
            }

            Log.Write($"linktivity join result={joined} {host}:{port} session={sessionId}");

            if (!joined)
            {
                FailAttempt();
            }
        });
    }

    public event Action<string>? EventReceived;
    public event Action<int>? Exited;
    public event Action<ViewportFrame>? ViewportReceived;
    public event Action<bool>? SharingChanged;

    public bool HasExited
    {
        get { lock (_gate) { return _disposed; } }
    }

    /// <summary>
    /// PID whose audio session carries the tutor voice. The launcher itself renders no session
    /// audio in-process; the 32-bit decode/play helper does, so we surface the helper's PID so
    /// the viewer's per-application mute/volume acts on the right session.
    /// </summary>
    public int ProcessId => _voice.ProcessId;

    public Task SendPublicAsync(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _engine.SendChat(text);

            // The group server does not relay our own public message back to us
            // in real time (it only appears in the history push on a later
            // join, and even then with a big lag -- observed ~24s). So we show
            // our own line immediately, exactly as the vendor's ChatApplet does
            // before it sends. A live server copy of our own line, if it ever
            // arrives, is suppressed in OnChat to avoid a duplicate.
            Emit(JsonSerializer.Serialize(new
            {
                t = "chat",
                name = _user,
                text,
                host = false,
                @out = true,
                ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)
            }));
        }

        return Task.CompletedTask;
    }

    public Task LeaveAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public Task PingAsync()
    {
        bool alive;
        lock (_gate)
        {
            alive = !_disposed && !_failed;
        }

        if (alive)
        {
            Emit("{\"t\":\"pong\"}");
        }

        return Task.CompletedTask;
    }

    public Task SendVoiceMuteAsync(bool muted)
    {
        _voiceMuted = muted;
        _voice.ApplyVolume(_voiceMuted, _voiceVolume);
        return Task.CompletedTask;
    }

    public Task SendVoiceVolumeAsync(float volume)
    {
        // The title-bar slider runs 0-300% (0..3). Up to 100% the helper's
        // Core Audio session acts instantly; above 100% the extra is baked
        // into the samples. A gentle curve maps the top third to 1x..4x so
        // the last few slider ticks stay useful instead of jumping to a
        // clipped wall of sound.
        float slider = volume < 0f ? 0f : volume > 3f ? 3f : volume;
        _voiceVolume = slider <= 1f
            ? slider
            : 1f + 3f * MathF.Pow((slider - 1f) / 2f, 0.82f);
        _voice.ApplyVolume(_voiceMuted, _voiceVolume);
        return Task.CompletedTask;
    }

    public Task RetryAsync() => Task.CompletedTask;

    public void Kill() => Dispose();

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

        try
        {
            _engine.Dispose();
        }
        catch
        {
            // Already gone.
        }

        try
        {
            _voice.Dispose();
        }
        catch
        {
            // Already gone.
        }
    }

    private void OnSelfJoined(SessionPeer peer)
    {
        Log.Write("linktivity hello");

        lock (_gate)
        {
            _hello = true;
        }

        Emit(JsonSerializer.Serialize(new { t = "ready", role = "Student" }));
        Emit(JsonSerializer.Serialize(new { t = "hello", name = peer.User, host = false }));
        EmitPeers();

        _ = Task.Run(() =>
        {
            try
            {
                _voice.Start();
            }
            catch (Exception exception)
            {
                Log.Write($"voice playback prewarm failed: {exception.Message}");
            }
        });
    }

    private void OnChat(SessionChat chat)
    {
        bool self = string.Equals(chat.User, _user, StringComparison.OrdinalIgnoreCase);

        // Our own line is shown locally the moment we send it (the server does
        // not relay it back in real time). So a *live* copy of our own message
        // would be a duplicate -- skip it. History copies (from the join-time
        // archive) are always shown, so re-joining reconstructs the full
        // conversation including our own past lines.
        if (self && !chat.FromArchive)
        {
            return;
        }

        Emit(JsonSerializer.Serialize(new
        {
            t = "chat",
            name = chat.User,
            text = chat.Text,
            host = false,
            @out = self,
            ts = ToIsoTime(chat.Timestamp)
        }));
    }

    // Chat timestamps arrive as a Java Date.toString() string, e.g.
    // "Sun Oct 18 22:37:39 AST 2026". V8's new Date() cannot parse the
    // timezone abbreviation, so the web UI shows "Invalid Date" for those
    // records (only the numeric "+03:00" style parses). Strip the timezone
    // token, parse the remaining wall-clock fields as local time, and re-emit
    // round-trip ISO-8601 so the UI always formats.
    private static string ToIsoTime(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return "";

        string[] parts = timestamp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string core;
        if (parts.Length >= 7)
        {
            // ddd MMM dd HH:mm:ss <tz> yyyy -> drop the timezone token.
            core = $"{parts[0]} {parts[1]} {parts[2]} {parts[3]} {parts[6]}";
        }
        else if (parts.Length == 6)
        {
            // ddd MMM dd HH:mm:ss yyyy -> already no timezone token.
            core = $"{parts[0]} {parts[1]} {parts[2]} {parts[3]} {parts[5]}";
        }
        else
        {
            return timestamp;
        }

        if (DateTime.TryParseExact(core, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out DateTime local))
        {
            return new DateTimeOffset(local).ToString("o", CultureInfo.InvariantCulture);
        }
        return timestamp;
    }

    /// <summary>
    /// Incoming tutor voice. The frame is forwarded verbatim to the 32-bit decode/play helper,
    /// which extracts and decodes the TrueSpeech payload. No microphone or capture device is
    /// involved — this is listen-only.
    /// </summary>
    private void OnVoiceFrame(byte[] frame)
    {
        try
        {
            _voice.Submit(frame);
        }
        catch (Exception exception)
        {
            Log.Write($"linktivity voice failed: {exception.Message}");
        }
    }

    private void OnViewportData(byte[] stream)
    {
        bool changed;
        try
        {
            changed = _viewport.Apply(stream);
        }
        catch (Exception exception)
        {
            Log.Write($"linktivity viewport failed: {exception.Message}");
            return;
        }

        // Vendor ViewportProtocol.bufferUpdateComplete: after consuming a frame
        // the receiver asks the host for the next one by writing a single
        // UPDATE_REQUEST (type 3). Without this the stream stalls after the
        // initial geometry update.
        _engine.RequestViewportFrame();

        if (_viewport.Stopped)
        {
            RaiseSharing(false);
            return;
        }

        bool haveGeometry = _viewport.Width > 0 && _viewport.Height > 0;
        if (!changed)
        {
            if (haveGeometry)
            {
                RaiseSharing(true);
            }

            return;
        }

        RaiseSharing(true);

        int width = _viewport.Width;
        int height = _viewport.Height;
        byte[] bgra;
        try
        {
            // Hand the raw BGRA32 pixels straight to the viewer, which blits them into a
            // WriteableBitmap. Previously this encoded a full PNG per frame (on the single
            // network reader thread) and the viewer decoded it again on the UI thread — a large,
            // avoidable stall on every frame.
            bgra = _viewport.ToBgra32();
        }
        catch (Exception exception)
        {
            Log.Write($"linktivity viewport copy failed: {exception.Message}");
            return;
        }

        ViewportReceived?.Invoke(new ViewportFrame { Width = width, Height = height, Data = bgra });
    }

    private void RaiseSharing(bool active)
    {
        lock (_gate)
        {
            if (_disposed || _sharing == active)
            {
                return;
            }

            _sharing = active;
        }

        SharingChanged?.Invoke(active);
    }

    private void EmitPeers()
    {
        List<object> hosts = new List<object>();
        List<object> users = new List<object>();

        // The roster includes the local user so the room count stays accurate;
        // the web UI hides the local entry from the member panels.
        SessionPeer? self = _engine.Self;
        string selfConnection = self?.Connection ?? "";
        if (self != null)
        {
            AddMember(hosts, users, self.User.Length > 0 ? self.User : _user, self.Connection, self.Role, (int)MicStatus.Off);
        }

        foreach (SessionPeer peer in _engine.Peers)
        {
            if (self != null && !string.IsNullOrEmpty(selfConnection)
                && string.Equals(peer.Connection, selfConnection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddMember(hosts, users, peer.User, peer.Connection, peer.Role, (int)peer.Mic);
        }

        Emit(JsonSerializer.Serialize(new { t = "peers", hosts, users }));
    }

    private static void AddMember(List<object> hosts, List<object> users, string name, string node, string role, int mic)
    {
        var entry = new { name, node, mic };
        if (string.Equals(role, "Host", StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(entry);
        }
        else
        {
            users.Add(entry);
        }
    }

    private void OnEngineFailed(string reason)
    {
        Log.Write($"linktivity: {reason}");

        bool hello;
        lock (_gate)
        {
            hello = _hello;
        }

        if (!hello)
        {
            FailAttempt();
        }
    }

    private void FailAttempt()
    {
        bool raise;
        lock (_gate)
        {
            if (_hello || _disposed || _failed)
            {
                return;
            }

            _failed = true;
            raise = true;
        }

        if (raise)
        {
            Exited?.Invoke(-1);
        }
    }

    private void Emit(string json)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        EventReceived?.Invoke(json);
    }
}
