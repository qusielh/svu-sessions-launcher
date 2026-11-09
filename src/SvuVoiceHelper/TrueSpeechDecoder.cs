using System;
using System.Runtime.InteropServices;

namespace SvuVoiceHelper;

/// <summary>
/// Decodes DSP Group TrueSpeech (WAVE_FORMAT 0x0022, 8 kHz mono, 32-byte blocks) to
/// 8 kHz / 16-bit / mono PCM using the Windows ACM codec (tssoft32.acm), exactly as the
/// vendor <c>lkvoice.dll</c> does. Decode-only: no capture device is ever opened.
/// </summary>
internal sealed class TrueSpeechDecoder : IDisposable
{
    private IntPtr _stream;
    private readonly IntPtr _pSrcFmt;
    private IntPtr _pSrc;
    private bool _opened;

    public const int CompressedBlockSize = 32;

    public TrueSpeechDecoder()
    {
        if (!TryResolveFormat(out byte[] fmtBytes, out IntPtr driverHandle))
            throw new InvalidOperationException("TrueSpeech ACM codec (tssoft32.acm) is not available.");

        _pSrcFmt = Marshal.AllocHGlobal(fmtBytes.Length);
        Marshal.Copy(fmtBytes, 0, _pSrcFmt, fmtBytes.Length);

        var dst = new Native.WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = 8000,
            nAvgBytesPerSec = 16000,
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0
        };

        int rc = Native.acmStreamOpen(out _stream, driverHandle, _pSrcFmt, ref dst,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.ACM_STREAMOPENF_NONREALTIME);
        if (rc != 0)
        {
            Marshal.FreeHGlobal(_pSrcFmt);
            throw new InvalidOperationException($"acmStreamOpen failed: 0x{rc:X} ({rc}).");
        }
        _opened = true;
        _pSrc = Marshal.AllocHGlobal(CompressedBlockSize);
    }

    /// <summary>Decode one 32-byte compressed block to PCM. Returns PCM byte count (0 on failure).</summary>
    public int DecodeBlock(ReadOnlySpan<byte> block, IntPtr pDst, int dstCapacity)
    {
        byte[] tmp = new byte[CompressedBlockSize];
        block.CopyTo(tmp.AsSpan(0, Math.Min(block.Length, CompressedBlockSize)));
        Marshal.Copy(tmp, 0, _pSrc, CompressedBlockSize);

        var hdr = new Native.ACMSTREAMHEADER
        {
            cbStruct = (uint)Marshal.SizeOf<Native.ACMSTREAMHEADER>(),
            pbSrc = _pSrc,
            cbSrcLength = CompressedBlockSize,
            pbDst = pDst,
            cbDstLength = (uint)dstCapacity
        };

        int rc = Native.acmStreamPrepareHeader(_stream, ref hdr, 0);
        if (rc != 0) return 0;
        rc = Native.acmStreamConvert(_stream, ref hdr, Native.ACM_STREAMCONVERTF_BLOCKALIGN);
        int used = rc == 0 ? (int)hdr.cbDstLengthUsed : 0;
        Native.acmStreamUnprepareHeader(_stream, ref hdr, 0);
        return used;
    }

    private static bool TryResolveFormat(out byte[] fmtBytes, out IntPtr driverHandle)
    {
        byte[]? found = null;
        IntPtr foundHad = IntPtr.Zero;

        Native.acmDriverEnum((IntPtr hadid, IntPtr inst, uint support) =>
        {
            if (found != null) return 1;
            if (Native.acmDriverOpen(out IntPtr had, hadid, 0) != 0) return 1;

            var det = new Native.ACMFORMATDETAILSA
            {
                cbStruct = (uint)Marshal.SizeOf<Native.ACMFORMATDETAILSA>(),
                dwFormatTag = 0
            };
            IntPtr pf = Marshal.AllocHGlobal(1024);
            det.pwfx = pf;
            det.cbwfx = 1024;

            Native.acmFormatEnumA(had, ref det, (IntPtr h, ref Native.ACMFORMATDETAILSA f, IntPtr z, uint s) =>
            {
                if (f.dwFormatTag == 0x0022 && found == null)
                {
                    int n = (int)Math.Min(f.cbwfx, 1024u);
                    byte[] raw = new byte[n];
                    Marshal.Copy(f.pwfx, raw, 0, n);
                    // Keep only WAVEFORMATEX(18) + cbSize extension actually declared.
                    int cb = BitConverter.ToUInt16(raw, 16);
                    int total = 18 + cb;
                    found = new byte[total];
                    Array.Copy(raw, found, total);
                    foundHad = had;
                }
                return 1;
            }, IntPtr.Zero, 0);

            Marshal.FreeHGlobal(pf);
            return 1;
        }, IntPtr.Zero, 0);

        if (found != null)
        {
            fmtBytes = found;
            driverHandle = foundHad;
            return true;
        }
        fmtBytes = Array.Empty<byte>();
        driverHandle = IntPtr.Zero;
        return false;
    }

    public void Dispose()
    {
        if (_opened)
        {
            Native.acmStreamClose(_stream, 0);
            _opened = false;
        }
        if (_pSrcFmt != IntPtr.Zero) Marshal.FreeHGlobal(_pSrcFmt);
        if (_pSrc != IntPtr.Zero) Marshal.FreeHGlobal(_pSrc);
    }
}
