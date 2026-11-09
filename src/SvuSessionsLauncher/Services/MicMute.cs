using System.Runtime.InteropServices;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Forces the default recording (capture) device to a muted/off state using the
/// Windows Core Audio API, mirroring the original launcher's "mic mute" feature.
/// There is deliberately no UI control for this: the microphone is always kept
/// off, and the caller only reads back the resulting state to display it.
/// </summary>
internal static class MicMute
{
    /// <summary>
    /// Attempts to mute the default capture device.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the device is now muted, <c>false</c> when it could not be
    /// muted, and <c>null</c> when the audio endpoint could not be reached.
    /// </returns>
    public static bool? SetMuted(bool mute)
    {
        var volume = GetEndpointVolume();
        if (volume is null)
        {
            return null;
        }

        try
        {
            Marshal.ThrowExceptionForHR(volume.SetMute(mute, Guid.Empty));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            return muted;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    /// <summary>Reads the current mute state of the default capture device.</summary>
    public static bool? IsMuted()
    {
        var volume = GetEndpointVolume();
        if (volume is null)
        {
            return null;
        }

        try
        {
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            return muted;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device));

            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out var endpoint));
            return (IAudioEndpointVolume)endpoint;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    internal enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [Flags]
    internal enum CLSCTX : uint
    {
        INPROC_SERVER = 0x1,
        INPROC_HANDLER = 0x2,
        LOCAL_SERVER = 0x4,
        REMOTE_SERVER = 0x10,
        ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out int count);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(int channel, float level, Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(int channel, float level, Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(int channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(int channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out int step, out int stepCount);

        [PreserveSig]
        int VolumeStepUp(Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out int hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float min, out float max, out float increment);
    }
}
