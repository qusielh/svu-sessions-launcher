using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// Every window state change is the same dissolve: freeze the current pixels on
/// a topmost layered window, apply the new state underneath in a single step,
/// then fade the frozen frame out. The window never animates its geometry, so
/// the page and the WebView2 resize exactly once and the visible motion is pure
/// opacity, which stays smooth even while the content reflows.
///
/// <para>
/// The same path covers maximize, restore, minimize, restore-from-minimize, and
/// the chat window's first appearance and close. The native
/// <see cref="WindowState"/> is authoritative and changes immediately, so the
/// taskbar, Aero Snap, Win+Up/Down, and the system menu all agree with what is
/// on screen. DWM transitions are disabled for the window's whole lifetime so
/// the OS never replays its own animation underneath the dissolve.
/// </para>
/// </summary>
internal static class WindowAnimator
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSysCommand = 0x0112;
    private const int WmEnterSizeMove = 0x0231;

    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScRestore = 0xF120;

    private const int MonitorDefaultToNearest = 2;

    private const int DwmwaTransitionsForcedisabled = 3;

    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int WpfRestoreToMaximized = 0x0002;

    private const double FadeMilliseconds = 120.0;

    // State changes repaint the window at its new size. Keep the frozen frame
    // fully opaque until the new layout has had time to paint underneath it,
    // otherwise the stale surface shows through as a scaled flash.
    private const double StateHoldMilliseconds = 60.0;

    private static readonly Dictionary<Window, Motion> Motions = new();

    /// <summary>
    /// Handles the messages the windows forward from their WndProc: the
    /// maximized-size clamp, the system commands that change state, and the
    /// start of a user move/resize (which takes over from our transition).
    /// </summary>
    public static void HandleMessage(
        IntPtr hwnd,
        Window window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            if (TryClampMaximizedBounds(hwnd, lParam))
            {
                handled = true;
            }

            return;
        }

        if (message == WmEnterSizeMove)
        {
            Cancel(window);
            return;
        }

        if (message != WmSysCommand)
        {
            return;
        }

        switch (wParam.ToInt32() & 0xFFF0)
        {
            case ScMinimize:
                if (window.WindowState != WindowState.Minimized)
                {
                    handled = true;
                    Minimize(window);
                }

                break;
            case ScMaximize:
                if (window.WindowState != WindowState.Maximized)
                {
                    handled = true;
                    Maximize(window);
                }

                break;
            case ScRestore:
                if (window.WindowState != WindowState.Normal)
                {
                    handled = true;
                    Restore(window);
                }

                break;
        }
    }

    /// <summary>
    /// Called before a native resize loop starts. A transition is finished
    /// first so the resize begins from the live window; a minimized window
    /// cannot be resized, so that case is refused.
    /// </summary>
    public static bool TryCancelForResize(Window window)
    {
        if (!Motions.TryGetValue(window, out var motion))
        {
            return true;
        }

        if (motion.Goal == WindowGoal.Minimized)
        {
            return false;
        }

        Finish(motion);
        return true;
    }

    /// <summary>
    /// Disables the native DWM transitions for a window for its whole lifetime.
    /// The dissolve replaces every state transition, and leaving the native
    /// maximize animation enabled lets Windows scale the old frame up before
    /// the frozen frame is composed.
    /// </summary>
    public static void Attach(Window window)
    {
        var hwnd = Handle(window);
        if (hwnd != IntPtr.Zero)
        {
            DisableNativeTransitions(hwnd);
        }
    }

    public static void ToggleMaximize(Window window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            Restore(window);
            return;
        }

        Maximize(window);
    }

    public static void Maximize(Window window)
    {
        if (Motions.TryGetValue(window, out var motion))
        {
            if (motion.Goal == WindowGoal.Maximized)
            {
                return;
            }

            Finish(motion);
        }

        if (window.WindowState == WindowState.Maximized)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            StartRestore(window, forceMaximized: true);
            return;
        }

        StartMaximize(window);
    }

    public static void Restore(Window window)
    {
        if (Motions.TryGetValue(window, out var motion))
        {
            if (motion.Goal == WindowGoal.Normal)
            {
                return;
            }

            Finish(motion);
        }

        if (window.WindowState == WindowState.Minimized)
        {
            StartRestore(window, forceMaximized: false);
            return;
        }

        if (window.WindowState != WindowState.Maximized)
        {
            return;
        }

        StartRestoreFromMaximized(window);
    }

    public static void Minimize(Window window)
    {
        if (Motions.TryGetValue(window, out var motion))
        {
            if (motion.Goal == WindowGoal.Minimized)
            {
                return;
            }

            Finish(motion);
        }

        if (window.WindowState == WindowState.Minimized)
        {
            return;
        }

        StartMinimize(window);
    }

    /// <summary>
    /// Reveals a hidden window with the same dissolve as a state change: freeze
    /// the screen at the destination, show the window at full opacity
    /// underneath, hold until it has painted, then fade the frozen frame out.
    /// </summary>
    public static void Open(Window window, Rect target, Action show)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero)
        {
            show();
            return;
        }

        if (Motions.TryGetValue(window, out var active))
        {
            Finish(active);
        }

        var area = ToDeviceRect(window, target);
        var snapshot = WindowSnapshot.CaptureScreen(area);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            show();
            return;
        }

        var motion = Begin(window, hwnd, WindowGoal.Normal, overlay, snapshot);
        try
        {
            show();
        }
        catch
        {
            Finish(motion);
            throw;
        }

        StartFade(motion, StateHoldMilliseconds);
    }

    /// <summary>
    /// Closes a visible window with the same dissolve as a state change: freeze
    /// its pixels, hide it underneath, then fade the frozen frame out. The
    /// window closes once the fade finishes, so a coordinated close can run
    /// alongside another window's fade.
    /// </summary>
    /// <param name="captureCovered">
    /// Print the window's own surface when it is not the active window. Safe for
    /// WPF-only windows; a browser surface may print blank, so the chat opts
    /// out and closes directly when it is covered.
    /// </param>
    public static Task FadeOutAndClose(Window window, Action close, bool captureCovered = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A hidden or minimized window has no visible surface to dissolve.
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
        {
            close();
            done.TrySetResult();
            return done.Task;
        }

        // Screen pixels are exact only while this window is in front. A covered
        // window would otherwise freeze whatever is covering it, so only the
        // opted-in surfaces fall back to printing themselves.
        if (!window.IsActive && !captureCovered)
        {
            close();
            done.TrySetResult();
            return done.Task;
        }

        if (Motions.TryGetValue(window, out var active))
        {
            Finish(active);
        }

        var hwnd = Handle(window);
        var snapshot = window.IsActive ? Capture(window) : WindowSnapshot.Capture(window);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            close();
            done.TrySetResult();
            return done.Task;
        }

        var motion = new Motion(window, hwnd, WindowGoal.Closing, overlay, snapshot);
        motion.Overlay.Faded = () =>
        {
            Finish(motion);
            try
            {
                close();
            }
            finally
            {
                done.TrySetResult();
            }
        };

        Motions[window] = motion;
        window.Hide();
        StartFade(motion);
        return done.Task;
    }

    private static void StartMaximize(Window window)
    {
        var hwnd = Handle(window);
        var current = hwnd == IntPtr.Zero ? Rect.Empty : GetBounds(hwnd);
        if (current.Width <= 0 || current.Height <= 0)
        {
            window.WindowState = WindowState.Maximized;
            return;
        }

        // Capture the whole target area, not just the old window rect, so the
        // maximized layout does not pop in around the fading snapshot.
        var area = current;
        if (TryGetMonitorRects(hwnd, out var work, out _))
        {
            area = Rect.Union(current, work);
        }

        var snapshot = CaptureArea(window, area);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            window.WindowState = WindowState.Maximized;
            return;
        }

        var motion = Begin(window, hwnd, WindowGoal.Maximized, overlay, snapshot);
        // Hold the old frame while the maximized layout paints, then dissolve.
        // Fading immediately would reveal the stale surface at the new size.
        StartFade(motion, StateHoldMilliseconds);
        window.WindowState = WindowState.Maximized;
    }

    private static void StartRestoreFromMaximized(Window window)
    {
        var hwnd = Handle(window);
        var current = hwnd == IntPtr.Zero ? Rect.Empty : GetBounds(hwnd);
        if (current.Width <= 0 || current.Height <= 0)
        {
            window.WindowState = WindowState.Normal;
            return;
        }

        var normal = GetNormalBounds(hwnd, current);
        if (normal.Width <= 0 || normal.Height <= 0)
        {
            window.WindowState = WindowState.Normal;
            return;
        }

        var snapshot = Capture(window);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            window.WindowState = WindowState.Normal;
            ClearRestoreToMaximized(hwnd);
            SyncProperties(window, normal);
            return;
        }

        var motion = Begin(window, hwnd, WindowGoal.Normal, overlay, snapshot);
        StartFade(motion, StateHoldMilliseconds);
        window.WindowState = WindowState.Normal;
        ClearRestoreToMaximized(hwnd);
        SyncProperties(window, normal);
    }

    private static void StartMinimize(Window window)
    {
        var hwnd = Handle(window);
        var current = hwnd == IntPtr.Zero ? Rect.Empty : GetBounds(hwnd);
        if (current.Width <= 0 || current.Height <= 0)
        {
            window.WindowState = WindowState.Minimized;
            return;
        }

        var wasMaximized = window.WindowState == WindowState.Maximized;
        var snapshot = Capture(window);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            window.WindowState = WindowState.Minimized;
            SetRestoreToMaximized(hwnd, wasMaximized);
            return;
        }

        var motion = Begin(window, hwnd, WindowGoal.Minimized, overlay, snapshot);
        window.WindowState = WindowState.Minimized;
        SetRestoreToMaximized(hwnd, wasMaximized);
        StartFade(motion);
    }

    private static void StartRestore(Window window, bool forceMaximized)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var placement = GetPlacement(hwnd);
        var normal = placement.length > 0
            ? ToRect(placement.rcNormalPosition)
            : ToDeviceRect(window, window.RestoreBounds);

        if (normal.Width <= 0 || normal.Height <= 0)
        {
            window.WindowState = WindowState.Normal;
            if (forceMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            ClearRestoreToMaximized(hwnd);
            return;
        }

        var restoreMaximized = forceMaximized || (placement.flags & WpfRestoreToMaximized) != 0;

        // Capture what is on screen now (the desktop, or whatever covers the
        // window), so the restored window can dissolve in over it. The window
        // itself is minimized, so a screen copy is the only meaningful frame.
        var area = normal;
        if (restoreMaximized && TryGetMonitorRects(hwnd, out var work, out _))
        {
            area = Rect.Union(normal, work);
        }

        var snapshot = WindowSnapshot.CaptureScreen(area);
        var overlay = snapshot is null ? null : LayeredOverlay.Create(snapshot);
        if (overlay is null || snapshot is null)
        {
            snapshot?.Dispose();
            RestoreDirectly(window, hwnd, restoreMaximized);
            return;
        }

        var goal = restoreMaximized ? WindowGoal.Maximized : WindowGoal.Normal;
        var motion = Begin(window, hwnd, goal, overlay, snapshot);
        RestoreWindowUnderOverlay(window, hwnd, normal, restoreMaximized);
        StartFade(motion, StateHoldMilliseconds);
    }

    private static void RestoreDirectly(Window window, IntPtr hwnd, bool maximized)
    {
        window.WindowState = WindowState.Normal;
        if (maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        ClearRestoreToMaximized(hwnd);
    }

    private static void RestoreWindowUnderOverlay(
        Window window,
        IntPtr hwnd,
        Rect normal,
        bool maximized)
    {
        window.WindowState = WindowState.Normal;
        ForceNormal(hwnd, normal);
        SyncProperties(window, normal);

        if (maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        ClearRestoreToMaximized(hwnd);
        window.Activate();
    }

    private static Motion Begin(
        Window window,
        IntPtr hwnd,
        WindowGoal goal,
        LayeredOverlay overlay,
        WindowSnapshot snapshot)
    {
        var motion = new Motion(window, hwnd, goal, overlay, snapshot)
        {
            Closed = (_, _) => Cancel(window)
        };

        motion.Overlay.Faded = () => Finish(motion);
        Motions[window] = motion;
        window.Closed += motion.Closed;
        return motion;
    }

    private static void StartFade(Motion motion, double holdMilliseconds = 0) =>
        motion.Overlay.StartFade(FadeMilliseconds, holdMilliseconds);

    private static void Finish(Motion motion)
    {
        if (!IsCurrent(motion))
        {
            return;
        }

        Remove(motion);
        motion.Snapshot.Dispose();
    }

    private static void Cancel(Window window)
    {
        if (Motions.TryGetValue(window, out var motion))
        {
            Finish(motion);
        }
    }

    private static void Remove(Motion motion)
    {
        Motions.Remove(motion.Window);
        motion.Window.Closed -= motion.Closed;
        motion.Closed = null;
        motion.Overlay.Dispose();
    }

    private static bool IsCurrent(Motion motion) =>
        Motions.TryGetValue(motion.Window, out var current) && ReferenceEquals(current, motion);

    private static WindowSnapshot? Capture(Window window)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // Copy what is actually on screen, even when another window covers
        // this one. The dissolve then starts from the exact pixels the user
        // sees, and a covered window never flashes its hidden content.
        return WindowSnapshot.CaptureScreen(window) ?? WindowSnapshot.Capture(window);
    }

    private static WindowSnapshot? CaptureArea(Window window, Rect area)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        return WindowSnapshot.CaptureScreen(area) ?? WindowSnapshot.Capture(window);
    }

    private static bool TryClampMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            return false;
        }

        if (!TryGetMonitorRects(hwnd, out var work, out var monitor))
        {
            return false;
        }

        // WindowChrome would otherwise maximize a borderless window to the
        // monitor rect plus its resize border, leaving the title bar and page
        // edges off-screen. Clamp to the work area instead.
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.ptMaxPosition.X = (int)work.Left - (int)monitor.Left;
        mmi.ptMaxPosition.Y = (int)work.Top - (int)monitor.Top;
        mmi.ptMaxSize.X = (int)work.Width;
        mmi.ptMaxSize.Y = (int)work.Height;
        Marshal.StructureToPtr(mmi, lParam, true);
        return true;
    }

    private static bool TryGetMonitorRects(IntPtr hwnd, out Rect work, out Rect monitor)
    {
        work = Rect.Empty;
        monitor = Rect.Empty;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var handle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
        {
            return false;
        }

        work = ToRect(info.rcWork);
        monitor = ToRect(info.rcMonitor);
        return work.Width > 0 && work.Height > 0;
    }

    private static Rect GetNormalBounds(IntPtr hwnd, Rect fallback)
    {
        var placement = GetPlacement(hwnd);
        if (placement.length == 0)
        {
            return fallback;
        }

        var normal = ToRect(placement.rcNormalPosition);
        return normal.Width > 0 && normal.Height > 0 ? normal : fallback;
    }

    private static void ForceNormal(IntPtr hwnd, Rect normal)
    {
        var placement = GetPlacement(hwnd);
        if (placement.length == 0)
        {
            return;
        }

        placement.showCmd = SwShowNormal;
        placement.rcNormalPosition = ToNative(normal);
        SetWindowPlacement(hwnd, ref placement);
    }

    private static void SetRestoreToMaximized(IntPtr hwnd, bool maximized)
    {
        var placement = GetPlacement(hwnd);
        if (placement.length == 0)
        {
            return;
        }

        if (maximized)
        {
            placement.flags |= WpfRestoreToMaximized;
            placement.showCmd = SwShowMinimized;
        }
        else
        {
            placement.flags &= ~WpfRestoreToMaximized;
        }

        SetWindowPlacement(hwnd, ref placement);
    }

    private static void ClearRestoreToMaximized(IntPtr hwnd)
    {
        var placement = GetPlacement(hwnd);
        if (placement.length == 0)
        {
            return;
        }

        placement.flags &= ~WpfRestoreToMaximized;
        SetWindowPlacement(hwnd, ref placement);
    }

    private static WindowPlacement GetPlacement(IntPtr hwnd)
    {
        var placement = new WindowPlacement { length = Marshal.SizeOf<WindowPlacement>() };
        return GetWindowPlacement(hwnd, ref placement) ? placement : default;
    }

    private static Rect GetBounds(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var rect) ? ToRect(rect) : Rect.Empty;

    private static void SyncProperties(Window window, Rect deviceRect)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var left = deviceRect.X / dpi.DpiScaleX;
        var top = deviceRect.Y / dpi.DpiScaleY;
        var width = deviceRect.Width / dpi.DpiScaleX;
        var height = deviceRect.Height / dpi.DpiScaleY;

        if (Math.Abs(window.Left - left) > 0.5)
        {
            window.Left = left;
        }

        if (Math.Abs(window.Top - top) > 0.5)
        {
            window.Top = top;
        }

        if (Math.Abs(window.Width - width) > 0.5)
        {
            window.Width = width;
        }

        if (Math.Abs(window.Height - height) > 0.5)
        {
            window.Height = height;
        }
    }

    private static Rect ToDeviceRect(Window window, Rect dipRect)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(
            dipRect.X * dpi.DpiScaleX,
            dipRect.Y * dpi.DpiScaleY,
            dipRect.Width * dpi.DpiScaleX,
            dipRect.Height * dpi.DpiScaleY);
    }

    private static void DisableNativeTransitions(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var disabled = 1;
            DwmSetWindowAttribute(hwnd, DwmwaTransitionsForcedisabled, ref disabled, sizeof(int));
        }
        catch
        {
            // DWM is optional; the fade still runs without it.
        }
    }

    private static IntPtr Handle(Window window)
    {
        try
        {
            return new WindowInteropHelper(window).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static Rect ToRect(NativeRect rect) => new(
        rect.Left,
        rect.Top,
        Math.Max(0, rect.Right - rect.Left),
        Math.Max(0, rect.Bottom - rect.Top));

    private static NativeRect ToNative(Rect rect) => new()
    {
        Left = (int)Math.Round(rect.Left),
        Top = (int)Math.Round(rect.Top),
        Right = (int)Math.Round(rect.Right),
        Bottom = (int)Math.Round(rect.Bottom)
    };

    private enum WindowGoal
    {
        Normal,
        Maximized,
        Minimized,
        Closing
    }

    private sealed class Motion
    {
        public Motion(
            Window window,
            IntPtr hwnd,
            WindowGoal goal,
            LayeredOverlay overlay,
            WindowSnapshot snapshot)
        {
            Window = window;
            Hwnd = hwnd;
            Goal = goal;
            Overlay = overlay;
            Snapshot = snapshot;
        }

        public Window Window { get; }

        public IntPtr Hwnd { get; }

        public WindowGoal Goal { get; }

        public LayeredOverlay Overlay { get; }

        public WindowSnapshot Snapshot { get; }

        public EventHandler? Closed { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int length;
        public int flags;
        public int showCmd;
        public NativePoint ptMinPosition;
        public NativePoint ptMaxPosition;
        public NativeRect rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
