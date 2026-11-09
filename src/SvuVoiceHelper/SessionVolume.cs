using System;
using System.Runtime.InteropServices;

namespace SvuVoiceHelper;

/// <summary>
/// Controls the volume/mute of *this* process's audio session through Core Audio.
/// This is the only mechanism that changes already-playing audio instantly: gain
/// applied to decoded PCM can only affect buffers that have not been handed to
/// waveOut yet, so a slider or mute click would otherwise lag by the amount of
/// audio already queued. Values above 100% cannot be expressed by the session
/// volume (it caps at 1.0), so the helper keeps a software gain for that range.
/// </summary>
internal sealed class SessionVolume
{
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;

    private readonly ISimpleAudioVolume _volume;
    private readonly object _sync = new();

    private SessionVolume(ISimpleAudioVolume volume) => _volume = volume;

    private static void Log(string message) =>
        Console.Error.WriteLine("SvuVoiceHelper: session volume: " + message);

    /// <summary>Null when Core Audio is unavailable; callers fall back to software gain.</summary>
    public static SessionVolume? TryCreate()
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? managerObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device);
            if (hr != 0 || device is null)
            {
                Log($"no default render endpoint (hr=0x{hr:X8})");
                return null;
            }

            Guid managerIid = typeof(IAudioSessionManager2).GUID;
            hr = device.Activate(ref managerIid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out IntPtr managerPointer);
            if (hr == 0 && managerPointer != IntPtr.Zero)
            {
                managerObject = Marshal.GetObjectForIUnknown(managerPointer);
                Marshal.Release(managerPointer);
            }

            if (hr != 0 || managerObject is null)
            {
                Log($"session manager activation failed (hr=0x{hr:X8})");
                return null;
            }

            var manager = (IAudioSessionManager2)managerObject;

            // Prefer "this process's default session" (a NULL GUID). WinMM does not
            // always expose one, so fall back to scanning sessions for our PID.
            hr = manager.GetSimpleAudioVolume(IntPtr.Zero, false, out ISimpleAudioVolume? simple);
            if (hr != 0 || simple is null)
            {
                Log($"no default session (hr=0x{hr:X8}); scanning by pid");
                simple = FindOwnSession(manager);
            }

            if (simple is null)
            {
                Log("no session found for this process");
            }

            return simple is null ? null : new SessionVolume(simple);
        }
        catch (Exception ex)
        {
            Log($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (managerObject is not null) Marshal.ReleaseComObject(managerObject);
            if (enumeratorObject is not null) Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    private static ISimpleAudioVolume? FindOwnSession(IAudioSessionManager2 manager)
    {
        if (manager.GetSessionEnumerator(out IntPtr pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        object? enumeratorObject = null;
        try
        {
            enumeratorObject = Marshal.GetObjectForIUnknown(pointer);
            var enumerator = (IAudioSessionEnumerator)enumeratorObject;
            if (enumerator.GetCount(out int count) != 0)
            {
                return null;
            }

            Log($"scanning {count} session(s) for pid {Environment.ProcessId}");

            uint self = (uint)Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                object? controlObject = null;
                try
                {
                    if (enumerator.GetSession(i, out controlObject) != 0 || controlObject is null)
                    {
                        continue;
                    }

                    if (controlObject is IAudioSessionControl2 control
                        && control.GetProcessId(out uint pid) == 0
                        && pid == self)
                    {
                        return controlObject as ISimpleAudioVolume;
                    }
                }
                finally
                {
                    if (controlObject is not null) Marshal.ReleaseComObject(controlObject);
                }
            }

            return null;
        }
        finally
        {
            // The pointer from GetSessionEnumerator is a fresh reference.
            Marshal.Release(pointer);
            if (enumeratorObject is not null) Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    /// <summary>Diagnostic: print every render session's pid, volume and mute state.</summary>
    public static void Probe()
    {
        object? enumeratorObject = new MMDeviceEnumerator();
        IMMDevice? device = null;
        object? managerObject = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0 || device is null)
            {
                return;
            }

            Guid managerIid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerIid, 23, IntPtr.Zero, out IntPtr managerPointer) != 0
                || managerPointer == IntPtr.Zero)
            {
                return;
            }

            managerObject = Marshal.GetObjectForIUnknown(managerPointer);
            Marshal.Release(managerPointer);

            var manager = (IAudioSessionManager2)managerObject;
            if (manager.GetSessionEnumerator(out IntPtr pointer) != 0 || pointer == IntPtr.Zero)
            {
                return;
            }

            object? sessionsObject = Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            try
            {
                var sessions = (IAudioSessionEnumerator)sessionsObject;
                if (sessions.GetCount(out int count) != 0)
                {
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    object? controlObject = null;
                    try
                    {
                        if (sessions.GetSession(i, out controlObject) != 0
                            || controlObject is not IAudioSessionControl2 control
                            || control.GetProcessId(out uint pid) != 0)
                        {
                            continue;
                        }

                        float volume = float.NaN;
                        bool muted = false;
                        if (controlObject is ISimpleAudioVolume simple)
                        {
                            simple.GetMasterVolume(out volume);
                            simple.GetMute(out muted);
                        }

                        Console.Error.WriteLine($"probe pid={pid} volume={volume:F2} muted={muted}");
                    }
                    finally
                    {
                        if (controlObject is not null) Marshal.ReleaseComObject(controlObject);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessionsObject);
            }
        }
        catch (Exception ex)
        {
            Log($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (managerObject is not null) Marshal.ReleaseComObject(managerObject);
            Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_sync)
        {
            try
            {
                Guid none = Guid.Empty;
                _volume.SetMute(muted, ref none);
            }
            catch
            {
                // Never let audio control take the helper down.
            }
        }
    }

    public void SetLevel(float level)
    {
        lock (_sync)
        {
            try
            {
                Guid none = Guid.Empty;
                float master = level < 0f ? 0f : level > 1f ? 1f : level;
                _volume.SetMasterVolume(master, ref none);
            }
            catch
            {
                // Never let audio control take the helper down.
            }
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice(string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, [MarshalAs(UnmanagedType.Bool)] bool allowNew, out ISimpleAudioVolume? volume);

        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IntPtr sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out object? session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
