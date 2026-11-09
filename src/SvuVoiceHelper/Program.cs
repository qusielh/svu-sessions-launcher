using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SvuVoiceHelper;

/// <summary>
/// SvuVoiceHelper — a tiny 32-bit process that decodes incoming tutor voice (DSP Group
/// TrueSpeech, WAVE_FORMAT 0x0022) to PCM and plays it. It exists because the TrueSpeech ACM
/// codec ships only as 32-bit (SysWOW64\tssoft32.acm) while the main launcher is 64-bit.
///
/// Protocol (stdin): a stream of records [uint32 little-endian length][type byte][payload],
/// where the type byte is 0 for an audio frame and 1 for an ASCII command line
/// ("VOL &lt;muted&gt; &lt;gain&gt;", gain 0..4). The audio frame bytes are a full Linktivity voice frame:
///   [u16 count][u16 strLen][node (strLen-4-1 bytes + NUL)] [em]
///   em = ["10 04"][u16 innerLen][00 02][10 zero][b[0..5]=voice hdr incl. tag][256 TrueSpeech]
/// The 256-byte TrueSpeech payload begins at frame offset strLen + 21 (it is the final 256
/// bytes of the frame). A bare payload (already-stripped codec bytes) is also accepted.
/// The helper never opens any capture device — it is decode/playback only.
/// Volume and mute are applied through the helper's own Core Audio session, which acts on
/// audio that is already playing; software gain is only used above 100% (session volume
/// cannot exceed 1.0). The launcher delivers commands on a dedicated control pipe
/// (<c>--control &lt;name&gt;</c>) so they never queue behind audio on stdin.
///
/// Test modes:
///   --wav &lt;out.wav&gt;  decode stdin records to a WAV file instead of playing.
///   --file &lt;log&gt;     read captured frames from a session-debug.log and (play or --wav).
///   --probe           print every render session's pid, volume and mute state.
/// </summary>
internal static class Program
{
    private const int TotalBlocksSafetyCap = 4096;

    /// <summary>Record type byte: the payload is a raw audio frame.</summary>
    private const byte RecordFrame = 0;

    /// <summary>Record type byte: the payload is an ASCII command line.</summary>
    private const byte RecordCommand = 1;

    private static int Main(string[] args)
    {
        string? wavOut = null;
        string? fileIn = null;
        string? controlName = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--probe": SessionVolume.Probe(); return 0;
                case "--wav": if (i + 1 < args.Length) wavOut = args[++i]; break;
                case "--file": if (i + 1 < args.Length) fileIn = args[++i]; break;
                case "--control": if (i + 1 < args.Length) controlName = args[++i]; break;
            }
        }

        try
        {
            using var decoder = new TrueSpeechDecoder();
            IntPtr pDst = Marshal.AllocHGlobal(65536);
            try
            {
                if (fileIn != null)
                    return RunFile(fileIn, wavOut, decoder, pDst);
                return RunStdin(wavOut, controlName, decoder, pDst);
            }
            finally { Marshal.FreeHGlobal(pDst); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SvuVoiceHelper: " + ex.Message);
            return 1;
        }
    }

    private static int RunStdin(string? wavOut, string? controlName, TrueSpeechDecoder decoder, IntPtr pDst)
    {
        Stream sink = Console.OpenStandardInput();
        FileStream? wav = null;
        BinaryWriter? bw = null;
        WavePlayer? player = null;
        if (wavOut != null) { wav = OpenWav(wavOut); bw = new BinaryWriter(wav); }
        else player = new WavePlayer();

        byte[] lenBuf = new byte[4];
        long pcmTotal = 0;
        // Current volume/mute, shared with the control pipe (which is how the
        // launcher delivers changes; stdin only carries audio in normal use).
        var volState = new VolState { Gain = 1.0f, Muted = false };

        if (controlName != null && player != null)
        {
            try
            {
                var control = new NamedPipeClientStream(".", controlName, PipeDirection.In);
                control.Connect(4000);
                StartControlReader(control, volState, player);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SvuVoiceHelper: control pipe: " + ex.Message);
            }
        }

        try
        {
            while (true)
            {
                if (!ReadExact(sink, lenBuf, 4)) break;
                uint len = BitConverter.ToUInt32(lenBuf, 0);
                if (len == 0 || len > 1 << 20) break;
                byte[] payload = new byte[len];
                if (!ReadExact(sink, payload, (int)len)) break;

                if (payload[0] == RecordCommand)
                {
                    string command = System.Text.Encoding.ASCII.GetString(payload, 1, payload.Length - 1).Trim();
                    ApplyCommand(command, volState, player);
                    continue;
                }

                bool muted;
                float gain;
                lock (volState)
                {
                    muted = volState.Muted;
                    gain = volState.Gain;
                }

                PlayPayload(payload, 1, payload.Length - 1, decoder, pDst, player, bw, ref pcmTotal, muted, gain);
            }
        }
        finally
        {
            if (bw != null) { FinalizeWav(wav!, bw); wav!.Dispose(); }
            player?.Dispose();
        }
        return 0;
    }

    private static int RunFile(string logPath, string? wavOut, TrueSpeechDecoder decoder, IntPtr pDst)
    {
        FileStream? wav = null;
        BinaryWriter? bw = null;
        WavePlayer? player = null;
        if (wavOut != null) { wav = OpenWav(wavOut); bw = new BinaryWriter(wav); }
        else player = new WavePlayer();

        long pcmTotal = 0;
        int frames = 0;
        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                var m = Regex.Match(line, @"voice full len=(\d+) hex=([0-9A-Fa-f]+)");
                if (!m.Success) continue;
                int len = int.Parse(m.Groups[1].Value);
                if (len != 315 && len != 319) continue;
                byte[] full = HexToBytes(m.Groups[2].Value);
                PlayPayload(full, 0, full.Length, decoder, pDst, player, bw, ref pcmTotal, false, 1.0f);
                frames++;
            }
        }
        finally
        {
            if (bw != null) { FinalizeWav(wav!, bw); wav!.Dispose(); }
            player?.Dispose();
        }
        Console.Error.WriteLine($"SvuVoiceHelper: {frames} frames, {pcmTotal} PCM bytes.");
        return 0;
    }

    private static void PlayPayload(byte[] buffer, int offset, int count, TrueSpeechDecoder decoder, IntPtr pDst,
        WavePlayer? player, BinaryWriter? bw, ref long pcmTotal, bool muted, float gain)
    {
        byte[] payload = ExtractCodecPayload(buffer, offset, count);
        for (int off = 0; off + TrueSpeechDecoder.CompressedBlockSize <= payload.Length; off += TrueSpeechDecoder.CompressedBlockSize)
        {
            int used = decoder.DecodeBlock(payload.AsSpan(off, TrueSpeechDecoder.CompressedBlockSize), pDst, 65536);
            if (used <= 0) continue;
            pcmTotal += used;
            byte[] pcm = new byte[used];
            Marshal.Copy(pDst, pcm, 0, used);
            if (bw != null)
            {
                // File output is a straight capture: bake the gain in.
                if (muted || gain <= 0f)
                {
                    Array.Clear(pcm, 0, pcm.Length);
                }
                else if (Math.Abs(gain - 1.0f) > 0.0001f)
                {
                    ApplyGain(pcm, gain);
                }

                bw.Write(pcm);
            }
            else if (player != null)
            {
                // Live playback keeps the unscaled PCM; the player applies the
                // current gain state (session volume, plus software above 100%).
                player.Play(pcm, pcm.Length);
            }
        }
    }

    private static void ApplyGain(byte[] pcm, float gain)
    {
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            int v = (int)(s * gain);
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            pcm[i] = (byte)(v & 0xFF);
            pcm[i + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    private static void ApplyCommand(string command, VolState state, WavePlayer? player)
    {
        if (!command.StartsWith("VOL ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string[] parts = command.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return;
        }

        bool.TryParse(parts[1], out bool muted);
        float gain;
        lock (state)
        {
            gain = state.Gain;
        }

        if (parts.Length >= 3
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            gain = parsed < 0f ? 0f : parsed > 4f ? 4f : parsed;
        }

        lock (state)
        {
            state.Muted = muted;
            state.Gain = gain;
        }

        // Applies to audio already playing (session volume) and future frames.
        player?.SetVolume(gain, muted);
    }

    /// <summary>
    /// Reads volume/mute commands from the launcher's control pipe. This is separate from
    /// stdin on purpose: stdin can hold seconds of queued audio, and a command written
    /// behind it would only be seen after that audio had been accepted.
    /// </summary>
    private static void StartControlReader(Stream control, VolState state, WavePlayer player)
    {
        var thread = new System.Threading.Thread(() =>
        {
            byte[] lenBuf = new byte[4];
            try
            {
                while (true)
                {
                    if (!ReadExact(control, lenBuf, 4)) break;
                    uint len = BitConverter.ToUInt32(lenBuf, 0);
                    if (len == 0 || len > 1 << 16) break;
                    byte[] payload = new byte[len];
                    if (!ReadExact(control, payload, (int)len)) break;
                    if (payload[0] != RecordCommand) continue;
                    string command = System.Text.Encoding.ASCII.GetString(payload, 1, payload.Length - 1).Trim();
                    ApplyCommand(command, state, player);
                }
            }
            catch
            {
                // Launcher went away; stdin reaching EOF will end playback.
            }
            finally
            {
                try { control.Dispose(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "voice-control"
        };
        thread.Start();
    }

    private sealed class VolState
    {
        public float Gain = 1.0f;
        public bool Muted = false;
    }

    /// <summary>
    /// Returns the TrueSpeech codec bytes from a voice frame. The frame is
    /// `[u16 count][u16 strLen][node...][em]` where `em` is the inner message: it begins with the
    /// "10 04" magic, then a 16-byte block that carries a 3-byte size at <c>em[2]</c>. After that
    /// 16-byte block comes a 5-byte voice header and then 256 bytes of TrueSpeech. The codec
    /// payload is therefore the final 256 bytes of the frame, i.e. starting at <c>strLen + 21</c>.
    /// Frames are validated against exactly the field values the player's own decoder requires;
    /// malformed frames return empty (and are skipped). Input that is not a frame at all is
    /// returned unchanged (bare-payload mode).
    /// </summary>
    private static byte[] ExtractCodecPayload(byte[] frame, int offset, int count)
    {
        if (count < 0 || offset + count > frame.Length)
        {
            return Array.Empty<byte>();
        }
        // A voice frame must be at least `strLen + 16 + 261` bytes; `em[16..]` is the 261-byte
        // em body. The smallest real strLen is 37 (33-char node + NUL + 4), so require enough bytes.
        if (count >= 4 + 261)
        {
            ushort count16 = (ushort)(frame[offset] | (frame[offset + 1] << 8));
            int strLen = frame[offset + 2] | (frame[offset + 3] << 8);
            int emAt = offset + strLen;
            int bodyAt = emAt + 16;                 // start of the 261-byte em body
            int payloadStart = offset + strLen + 21; // == bodyAt + 5
            bool looksLikeFrame =
                count16 == 1 &&
                strLen >= 5 && emAt + 21 <= frame.Length &&
                frame[emAt] == 0x10 && frame[emAt + 1] == 0x04;
            if (looksLikeFrame)
            {
                int esize = frame[emAt + 2] | (frame[emAt + 3] << 8) | (frame[emAt + 4] << 16);
                // The player requires the em body to be exactly 261 bytes (esize - 11 == 261).
                if (esize - 11 != 261 || bodyAt + 261 > frame.Length)
                    return Array.Empty<byte>();      // malformed: skip rather than mis-slice
                byte[] payload = new byte[frame.Length - payloadStart];
                Array.Copy(frame, payloadStart, payload, 0, payload.Length);
                return payload;
            }
        }
        // Bare-payload mode: the caller already handed us the codec bytes.
        byte[] raw = new byte[count];
        Array.Copy(frame, offset, raw, 0, count);
        return raw;
    }

    private static FileStream OpenWav(string path)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var bw = new BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0); // size placeholder
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write(8000);
        bw.Write(16000);
        bw.Write((ushort)2);
        bw.Write((ushort)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(0); // size placeholder
        bw.Flush();
        return fs;
    }

    private static void FinalizeWav(FileStream fs, BinaryWriter bw)
    {
        long dataLen = fs.Position - 44;
        fs.Position = 4; bw.Write((uint)(dataLen + 36));
        fs.Position = 40; bw.Write((uint)dataLen);
        bw.Flush();
    }

    private static bool ReadExact(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] HexToBytes(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }


}

