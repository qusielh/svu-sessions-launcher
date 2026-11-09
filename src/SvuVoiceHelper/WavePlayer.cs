using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SvuVoiceHelper;

/// <summary>
/// Minimal 8 kHz / 16-bit / mono waveOut player. Accepts PCM buffers and writes them to the
/// device as buffers free up, keeping latency low.
/// </summary>
internal sealed class WavePlayer : IDisposable
{
    private const int SessionAttemptsMax = 12;

    private readonly IntPtr _hwo;
    private readonly ConcurrentQueue<Buffer> _free = new();
    private readonly List<Buffer> _all = new();
    private readonly object _sync = new();
    private SessionVolume? _session;
    private float _gain = 1f;
    private float _level;
    private bool _muted;
    private bool _attaching;
    private int _sessionAttempts;
    private long _nextSessionAttempt;
    private volatile bool _closed;

    private sealed class Buffer
    {
        public IntPtr Data;
        public int Capacity;
        public Native.WAVEHDR Header;
        public bool Prepared;
        public bool Queued;

        // Unscaled PCM, kept so gain can be baked into each buffer once.
        public byte[]? Raw;
        public int Length;
    }

    public WavePlayer()
    {
        var fmt = new Native.WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = 8000,
            nAvgBytesPerSec = 16000,
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0
        };
        int rc = Native.waveOutOpen(out _hwo, Native.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0)
            throw new InvalidOperationException($"waveOutOpen failed: {rc}.");

        // The stream now exists, so our audio session should exist too. Controlling
        // that session makes volume/mute changes instantaneous (the mixer applies
        // them to audio that is already playing). WinMM can take a moment to
        // register the session, so this is retried from Play/SetVolume below.
        TryAttachSession(force: true);

        // Four buffers (one decoded frame each, ≈ 30 ms, so ≈ 120 ms peak) absorb
        // jitter without putting a noticeable delay between the slider and the
        // ear. A starved queue drops instead of stalling the stdin reader.
        for (int i = 0; i < 4; i++)
        {
            var b = new Buffer { Data = Marshal.AllocHGlobal(8192), Capacity = 8192 };
            _all.Add(b);
            _free.Enqueue(b);
        }
    }

    /// <summary>
    /// Queue one PCM buffer for playback. Never blocks for long: real-time audio must be
    /// dropped rather than stall the reader (a blocked reader stalls the whole session).
    /// </summary>
    public void Play(byte[] pcm, int length)
    {
        if (length <= 0 || _closed) return;
        TryAttachSession(force: false);
        Buffer? buf = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_closed)
        {
            Reclaim();
            if (_free.TryDequeue(out buf)) break;
            Thread.Sleep(1);
            if (sw.ElapsedMilliseconds > 40) return; // drop the frame, keep the pipe moving
        }
        if (buf == null || _closed) return;

        if (length > buf.Capacity) length = buf.Capacity;
        if (buf.Raw is null || buf.Raw.Length < length) buf.Raw = new byte[buf.Capacity];
        Array.Copy(pcm, buf.Raw, length);
        buf.Length = length;
        WriteScaled(buf);

        buf.Header = new Native.WAVEHDR { lpData = buf.Data, dwBufferLength = (uint)length };
        if (Native.waveOutPrepareHeader(_hwo, ref buf.Header, (uint)Marshal.SizeOf<Native.WAVEHDR>()) == 0)
            buf.Prepared = true;
        if (Native.waveOutWrite(_hwo, ref buf.Header, (uint)Marshal.SizeOf<Native.WAVEHDR>()) == 0)
        {
            buf.Queued = true;
        }
        else
        {
            _free.Enqueue(buf);
        }
    }

    /// <summary>
    /// Applies the current gain/mute to future frames and, when the Core Audio
    /// session is available, to audio that is already playing. The session path
    /// makes 0–100% and mute instantaneous; everything above 100% is baked into
    /// new samples because a session master volume caps at 1.0.
    /// </summary>
    public void SetVolume(float gain, bool muted)
    {
        if (_closed) return;

        bool wasMuted;
        float targetGain;
        lock (_sync)
        {
            wasMuted = _muted;
            _gain = gain < 0f ? 0f : gain > 4f ? 4f : gain;
            _muted = muted;
            targetGain = _gain;
        }

        TryAttachSession(force: false);

        SessionVolume? session;
        lock (_sync)
        {
            session = _session;
        }

        if (session is null)
        {
            return;
        }

        float targetLevel = muted ? 0f : Math.Min(targetGain, 1f);
        if (muted && !wasMuted)
        {
            RampLevel(session, _level, 0f);
            session.SetMuted(true);
            _level = 0f;
        }
        else if (!muted && wasMuted)
        {
            session.SetMuted(false);
            session.SetLevel(0f);
            _level = 0f;
            RampLevel(session, 0f, targetLevel);
        }
        else if (!muted)
        {
            session.SetLevel(targetLevel);
            _level = targetLevel;
        }
        else
        {
            session.SetLevel(0f);
            _level = 0f;
        }
    }

    private static void RampLevel(SessionVolume session, float from, float to)
    {
        const int steps = 5;
        for (int i = 1; i <= steps; i++)
        {
            session.SetLevel(from + (to - from) * i / steps);
            Thread.Sleep(5);
        }
    }

    private void WriteScaled(Buffer buf)
    {
        int length = buf.Length;
        byte[] raw = buf.Raw!;
        float gain;
        lock (_sync)
        {
            // With a session the mixer owns 0–1 and mute; without one the samples
            // must carry the whole setting so silence still works.
            gain = _session is not null
                ? (_gain > 1f ? _gain : 1f)
                : (_muted ? 0f : _gain);
        }

        if (gain <= 0f)
        {
            for (int i = 0; i < length; i++)
            {
                Marshal.WriteByte(buf.Data, i, 0);
            }

            return;
        }

        if (Math.Abs(gain - 1f) < 0.0001f)
        {
            for (int i = 0; i < length; i++)
            {
                Marshal.WriteByte(buf.Data, i, raw[i]);
            }

            return;
        }

        for (int i = 0; i + 1 < length; i += 2)
        {
            short s = (short)(raw[i] | (raw[i + 1] << 8));
            double scaled = s * (double)gain;
            if (gain > 1f)
            {
                scaled = SoftLimit(scaled);
            }

            int v = (int)Math.Round(scaled);
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            Marshal.WriteByte(buf.Data, i, (byte)(v & 0xFF));
            Marshal.WriteByte(buf.Data, i + 1, (byte)((v >> 8) & 0xFF));
        }
    }

    /// <summary>
    /// Rounds off peaks above <c>0.73 FS</c> instead of clipping them, so boosting
    /// past 100% stays loud without turning loud speech into square-wave distortion.
    /// The curve is linear up to the knee and asymptotes to ±32767 above it.
    /// </summary>
    private static double SoftLimit(double value)
    {
        const double knee = 24000.0;
        const double range = 32767.0 - knee;
        if (value > knee)
        {
            return knee + range * Math.Tanh((value - knee) / range);
        }

        if (value < -knee)
        {
            return -knee - range * Math.Tanh((-value - knee) / range);
        }

        return value;
    }

    /// <summary>
    /// Attaches this process's Core Audio session, retrying briefly because WinMM
    /// can create the session a moment after <c>waveOutOpen</c>. Applying the
    /// current state on attach also overwrites any mixer volume/mute Windows
    /// persisted from a previous run.
    /// </summary>
    private void TryAttachSession(bool force)
    {
        lock (_sync)
        {
            if (_session is not null || _attaching || _sessionAttempts >= SessionAttemptsMax)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (!force && now < _nextSessionAttempt)
            {
                return;
            }

            _nextSessionAttempt = now + 750;
            _sessionAttempts++;
            _attaching = true;
        }

        SessionVolume? session = SessionVolume.TryCreate();
        lock (_sync)
        {
            _attaching = false;
            if (session is null || _session is not null)
            {
                return;
            }

            _session = session;
            float level = _muted ? 0f : Math.Min(_gain, 1f);
            session.SetLevel(level);
            session.SetMuted(_muted);
            _level = level;
        }

        Console.Error.WriteLine(force
            ? "SvuVoiceHelper: session volume ready"
            : "SvuVoiceHelper: session volume attached after retry");
    }

    private void Reclaim()
    {
        foreach (var b in _all)
        {
            if (b.Queued && (b.Header.dwFlags & Native.WHDR_DONE) != 0)
            {
                Native.waveOutUnprepareHeader(_hwo, ref b.Header, (uint)Marshal.SizeOf<Native.WAVEHDR>());
                b.Prepared = false;
                b.Queued = false;
                b.Header = default;
                _free.Enqueue(b);
            }
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { Native.waveOutReset(_hwo); } catch { }
        Thread.Sleep(50);
        foreach (var b in _all)
        {
            if (b.Prepared)
                Native.waveOutUnprepareHeader(_hwo, ref b.Header, (uint)Marshal.SizeOf<Native.WAVEHDR>());
            if (b.Data != IntPtr.Zero) Marshal.FreeHGlobal(b.Data);
        }
        Native.waveOutClose(_hwo);
    }
}
