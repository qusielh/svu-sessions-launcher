using System;
using System.Threading.Tasks;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// A live session connection: the narrow surface the chat and viewer UI depend
/// on. <see cref="LinktivitySessionConnection"/> implements it in-process, and
/// <see cref="RetryingSessionConnection"/> adds join retries around it.
/// </summary>
public interface ISessionConnection : IDisposable
{
    /// <summary>Raised for every JSON event from the connection.</summary>
    event Action<string>? EventReceived;

    /// <summary>Raised when the connection ends.</summary>
    event Action<int>? Exited;

    /// <summary>Raised for each received screen-share frame.</summary>
    event Action<ViewportFrame>? ViewportReceived;

    /// <summary>Raised when the tutor starts (<c>true</c>) or stops (<c>false</c>) screen-sharing.</summary>
    event Action<bool>? SharingChanged;

    /// <summary>True once the connection has ended.</summary>
    bool HasExited { get; }

    /// <summary>OS process id of the voice helper (used for per-process audio), or <c>-1</c>.</summary>
    int ProcessId { get; }

    Task SendPublicAsync(string text);

    Task LeaveAsync();

    Task PingAsync();

    Task SendVoiceMuteAsync(bool muted);

    Task SendVoiceVolumeAsync(float volume);

    /// <summary>
    /// Re-attempts the connection after a join failure. The in-process client
    /// treats this as a no-op; <see cref="RetryingSessionConnection"/> drives the
    /// actual reconnect.
    /// </summary>
    Task RetryAsync();

    /// <summary>Terminates the connection, ending the whole session.</summary>
    void Kill();
}

/// <summary>
/// A decoded screen-share frame. The pixel buffer is raw BGRA32 (premultiplied-free,
/// straight alpha), row-major, top-down, with a stride of <c>Width * 4</c> bytes — ready to be
/// blitted straight into a <c>WriteableBitmap</c> with no intermediate image encode/decode.
/// </summary>
public sealed class ViewportFrame
{
    public int Width { get; init; }

    public int Height { get; init; }

    public byte[] Data { get; init; } = Array.Empty<byte>();
}
