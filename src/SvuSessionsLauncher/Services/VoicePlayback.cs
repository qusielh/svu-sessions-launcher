using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Plays the incoming tutor voice channel.
///
/// <para>The voice stream is DSP Group TrueSpeech (WAVE_FORMAT 0x0022), which Windows ships
/// only as a 32-bit ACM codec (<c>SysWOW64\tssoft32.acm</c>). This launcher runs 64-bit, so it
/// cannot decode the stream in-process. Instead it launches the bundled 32-bit
/// <c>SvuVoiceHelper.exe</c> as a child process and forwards each voice frame to it over the
/// helper's standard input as a length-prefixed record (<c>[uint32 length][frame bytes]</c>).
/// The helper extracts the 256-byte TrueSpeech payload, decodes it with the OS codec, and plays
/// it through <c>waveOut</c>.</para>
///
/// <para>The helper is decode/playback only — it never opens a capture device, so no microphone
/// is ever engaged. Volume and mute are applied by the helper to its own Core Audio
/// session, which changes audio that is already playing; commands travel on a dedicated
/// pipe so they never wait behind queued frames.</para>
/// </summary>
public sealed class VoicePlayback : IDisposable
{
    /// <summary>Record type byte: the payload is a raw audio frame.</summary>
    private const byte RecordFrame = 0;

    /// <summary>Record type byte: the payload is an ASCII command line.</summary>
    private const byte RecordCommand = 1;

    /// <summary>Frames older than this many queued samples are dropped (≈ 15 s of audio).</summary>
    private const int MaxQueuedAudio = 64;

    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly Queue<byte[]> _audio = new();
    private readonly Queue<byte[]> _commands = new();
    private Thread? _writer;
    private bool _stopping;
    private Process? _helper;
    private Stream? _stdin;
    private NamedPipeServerStream? _control;
    private bool _disposed;

    /// <summary>PID of the helper process rendering the tutor audio, or -1 when not running.</summary>
    public int ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _helper is { HasExited: false } ? _helper.Id : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try { return _helper is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>Ensures the helper process is started. Safe to call repeatedly.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_helper is { HasExited: false })
            {
                return;
            }

            try
            {
                string exe = ResolveHelperPath();
                if (exe.Length == 0)
                {
                    Log.Write("voice playback: SvuVoiceHelper.exe not found next to the launcher");
                    return;
                }

                // Control (volume/mute) travels on its own pipe. Sending it on stdin
                // would queue it behind seconds of audio and make the slider feel laggy.
                string controlName = "SvuVoiceCtl-" + Guid.NewGuid().ToString("N");
                var control = new NamedPipeServerStream(
                    controlName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--control \"{controlName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                var process = new Process { StartInfo = info, EnableRaisingEvents = true };
                try
                {
                    process.Start();
                }
                catch
                {
                    control.Dispose();
                    throw;
                }
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Write($"voice helper: {e.Data}");
                    }
                };
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                _helper = process;
                _stdin = process.StandardInput.BaseStream;

                _control?.Dispose();
                _control = control;
                _ = WaitForControlAsync(control);

                // A fresh helper must not be fed audio queued for a dead one.
                lock (_queueGate)
                {
                    _audio.Clear();
                    _stopping = false;
                }

                StartWriter(_stdin);
                Log.Write($"voice playback: helper started pid={process.Id}");
            }
            catch (Exception exception)
            {
                Log.Write($"voice playback: failed to start helper: {exception.Message}");
                _helper = null;
                _stdin = null;
            }
        }
    }

    /// <summary>Forwards one raw voice frame to the helper for decoding and playback.</summary>
    public void Submit(byte[] frame)
    {
        if (frame is null || frame.Length == 0)
        {
            return;
        }

        WriteRecord(RecordFrame, frame);
    }

    /// <summary>
    /// Sends an ASCII command line to the helper (e.g. <c>"VOL false 1.5"</c>). Commands prefer
    /// the dedicated control pipe so they never wait behind queued audio; the stdin queue is
    /// only a fallback for a helper that has not connected yet.
    /// </summary>
    public void SendCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        byte[] record = BuildRecord(RecordCommand, System.Text.Encoding.ASCII.GetBytes(command));
        if (TryWriteControl(record))
        {
            return;
        }

        if (_helper is not { HasExited: false } || _stdin is null)
        {
            Start();
        }

        Enqueue(record, RecordCommand);
    }

    private async Task WaitForControlAsync(NamedPipeServerStream control)
    {
        try
        {
            await control.WaitForConnectionAsync().ConfigureAwait(false);
        }
        catch
        {
            // Helper exited or the pipe was disposed on shutdown; stdin still works.
        }
    }

    private bool TryWriteControl(byte[] record)
    {
        NamedPipeServerStream? control = _control;
        if (control is null || !control.IsConnected)
        {
            return false;
        }

        try
        {
            control.Write(record, 0, record.Length);
            control.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Frames a record and hands it to the writer thread. Never blocks: the caller is the
    /// network reader thread, and blocking it stalls video, chat and audio all at once.
    /// </summary>
    private void WriteRecord(byte type, byte[] payload)
    {
        if (_disposed)
        {
            return;
        }

        if (_helper is not { HasExited: false } || _stdin is null)
        {
            Start();
        }

        Enqueue(BuildRecord(type, payload), type);
    }

    private static byte[] BuildRecord(byte type, byte[] payload)
    {
        int total = payload.Length + 1;
        var record = new byte[5 + payload.Length];
        record[0] = (byte)(total & 0xFF);
        record[1] = (byte)((total >> 8) & 0xFF);
        record[2] = (byte)((total >> 16) & 0xFF);
        record[3] = (byte)((total >> 24) & 0xFF);
        record[4] = type;
        payload.CopyTo(record, 5);
        return record;
    }

    private void Enqueue(byte[] record, byte type)
    {
        lock (_queueGate)
        {
            if (_stopping)
            {
                return;
            }

            if (type == RecordFrame)
            {
                // Real-time audio: drop the oldest sample instead of growing latency.
                if (_audio.Count >= MaxQueuedAudio)
                {
                    _audio.Dequeue();
                }

                _audio.Enqueue(record);
            }
            else
            {
                // Commands (volume/mute) always jump ahead of queued audio so the
                // slider acts on what the user is hearing right now.
                _commands.Enqueue(record);
            }

            Monitor.Pulse(_queueGate);
        }
    }

    /// <summary>
    /// Starts a writer pinned to one helper's stdin. A restart gets its own writer; a
    /// stale writer whose pipe died exits without touching the replacement helper.
    /// </summary>
    private void StartWriter(Stream stream)
    {
        var thread = new Thread(() => WriterLoop(stream))
        {
            IsBackground = true,
            Name = "voice-writer"
        };
        _writer = thread;
        thread.Start();
    }

    private void WriterLoop(Stream stream)
    {
        while (true)
        {
            byte[]? next;
            lock (_queueGate)
            {
                while (!_stopping && _commands.Count == 0 && _audio.Count == 0)
                {
                    Monitor.Wait(_queueGate, 200);
                }

                if (_stopping)
                {
                    return;
                }

                next = _commands.Count > 0 ? _commands.Dequeue() : _audio.Dequeue();
            }

            try
            {
                stream.Write(next, 0, next.Length);
                stream.Flush();
            }
            catch (Exception exception)
            {
                Log.Write($"voice playback: write failed: {exception.Message}");
                lock (_gate)
                {
                    if (ReferenceEquals(_stdin, stream))
                    {
                        TryShutdownHelper();
                    }
                }

                return;
            }
        }
    }

    /// <summary>
    /// Applies mute/volume to the tutor voice. The value is sent to the helper as a
    /// <c>VOL</c> command on the control pipe; the helper turns it into a Core Audio
    /// session change, which affects audio that is already playing. This method does no
    /// COM work and never blocks on the audio stream, so it is safe to call per slider tick.
    /// </summary>
    public void ApplyVolume(bool muted, float volume)
    {
        float gain = muted ? 0f : volume;
        if (gain < 0f) gain = 0f;
        if (gain > 4f) gain = 4f;
        SendCommand($"VOL {muted.ToString().ToLowerInvariant()} {gain.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static string ResolveHelperPath()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(baseDir, "SvuVoiceHelper.exe");
            return File.Exists(candidate) ? candidate : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void TryShutdownHelper()
    {
        try
        {
            _control?.Dispose();
        }
        catch
        {
        }

        _control = null;

        try
        {
            _stdin?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_helper is { HasExited: false })
            {
                _helper.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            _helper?.Dispose();
        }
        catch
        {
        }

        _stdin = null;
        _helper = null;
    }

    public void Dispose()
    {
        lock (_queueGate)
        {
            _stopping = true;
            _audio.Clear();
            _commands.Clear();
            Monitor.PulseAll(_queueGate);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TryShutdownHelper();
        }
    }
}
