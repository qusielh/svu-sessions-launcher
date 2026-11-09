using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SvuSessionsLauncher.Helpers;

internal static class WindowActivation
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void Attach(Window window)
    {
        var restoreToFront = false;

        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                restoreToFront = IsForeground(window);
                return;
            }

            if (window.WindowState == WindowState.Normal && restoreToFront && window.IsVisible)
            {
                restoreToFront = false;
                BringToFront(window);
            }
        };

        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                if (restoreToFront && window.ShowActivated)
                {
                    restoreToFront = false;
                    BringToFront(window);
                }
                else
                {
                    // Show() uses the flag captured on hide; restore it so a
                    // later hide can capture the window's current state again.
                    restoreToFront = false;
                    window.ShowActivated = true;
                }

                return;
            }

            restoreToFront = window.ShowActivated && IsForeground(window);
            window.ShowActivated = restoreToFront;
        };
    }

    private static bool IsForeground(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && (window.IsActive || GetForegroundWindow() == handle);
    }

    private static void BringToFront(Window window)
    {
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            {
                return;
            }

            window.Activate();
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
            }
        }), DispatcherPriority.Input);
    }
}
