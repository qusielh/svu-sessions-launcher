using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SvuVoiceHelper;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ACMSTREAMHEADER
    {
        public uint cbStruct;
        public uint fdwStatus;
        public IntPtr dwUser;
        public IntPtr pbSrc;
        public uint cbSrcLength;
        public uint cbSrcLengthUsed;
        public IntPtr dwSrcUser;
        public IntPtr pbDst;
        public uint cbDstLength;
        public uint cbDstLengthUsed;
        public IntPtr dwDstUser;
        public uint dwReserved1, dwReserved2, dwReserved3, dwReserved4, dwReserved5,
                    dwReserved6, dwReserved7, dwReserved8, dwReserved9, dwReserved10;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    public delegate int AcmDriverEnumProc(IntPtr hadid, IntPtr dwInstance, uint fdwSupport);

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ACMFORMATDETAILSA
    {
        public uint cbStruct;
        public uint dwFormatIndex;
        public uint dwFormatTag;
        public uint fdwSupport;
        public IntPtr pwfx;
        public uint cbwfx;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szFormat;
    }

    public delegate int AcmFormatEnumProc(IntPtr had, ref ACMFORMATDETAILSA pafd, IntPtr dwInstance, uint fdwSupport);

    public const uint ACM_METRIC_MAX_SIZE_FORMAT = 0x32;
    public const uint ACM_STREAMOPENF_NONREALTIME = 0x4;
    public const uint ACM_STREAMCONVERTF_BLOCKALIGN = 0x4;
    public const uint ACM_STREAMCONVERTF_END = 0x2;
    public const uint WAVE_FORMAT_PCM = 1;
    public const uint WAVE_MAPPER = 0xFFFFFFFF;
    public const uint WHDR_DONE = 0x1;
    public const uint WHDR_INQUEUE = 0x10;

    [DllImport("msacm32.dll")] public static extern int acmDriverEnum(AcmDriverEnumProc fnCallback, IntPtr dwInstance, uint fdwEnum);
    [DllImport("msacm32.dll")] public static extern int acmDriverOpen(out IntPtr phad, IntPtr hadid, uint fdwOpen);
    [DllImport("msacm32.dll")] public static extern int acmDriverClose(IntPtr had, uint fdwClose);
    [DllImport("msacm32.dll")] public static extern int acmFormatEnumA(IntPtr had, ref ACMFORMATDETAILSA pafd, AcmFormatEnumProc fnCallback, IntPtr dwInstance, uint fdwEnum);
    [DllImport("msacm32.dll")] public static extern int acmStreamOpen(out IntPtr phas, IntPtr had, IntPtr pwfxSrc, ref WAVEFORMATEX pwfxDst, IntPtr pwfltr, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("msacm32.dll")] public static extern int acmStreamPrepareHeader(IntPtr has, ref ACMSTREAMHEADER pash, uint fdwPrepare);
    [DllImport("msacm32.dll")] public static extern int acmStreamConvert(IntPtr has, ref ACMSTREAMHEADER pash, uint fdwConvert);
    [DllImport("msacm32.dll")] public static extern int acmStreamUnprepareHeader(IntPtr has, ref ACMSTREAMHEADER pash, uint fdwUnprepare);
    [DllImport("msacm32.dll")] public static extern int acmStreamClose(IntPtr has, uint fdwClose);

    [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
    [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
    [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
    [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
    [DllImport("winmm.dll")] public static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME pmmt, uint cbmmt);

    [StructLayout(LayoutKind.Sequential)]
    public struct MMTIME { public uint wType; public uint val; }
}
