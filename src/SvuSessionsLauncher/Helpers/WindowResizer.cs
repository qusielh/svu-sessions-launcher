using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// Starts the native resize loop from an in-page edge grip. WebView2 renders
/// in a separate process, so its child HWND cannot be subclassed to let edge
/// clicks fall through to the WindowChrome border. Instead the page reports
/// which edge was grabbed and the host asks Windows to resize exactly as it
/// would from a normal window border.
/// </summary>
internal static class WindowResizer
{
    private const int WmNcLButtonDown = 0x00A1;

    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    public static void Start(Window window, string? edge)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            return;
        }

        // A fade is finished first so the resize starts from the live window;
        // a minimized window cannot be resized.
        if (!WindowAnimator.TryCancelForResize(window))
        {
            return;
        }

        var hit = edge switch
        {
            "left" => HtLeft,
            "right" => HtRight,
            "bottom" => HtBottom,
            "bottom-left" => HtBottomLeft,
            "bottom-right" => HtBottomRight,
            _ => 0
        };

        if (hit == 0)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var point))
        {
            return;
        }

        var lParam = new IntPtr((point.Y << 16) | (point.X & 0xFFFF));
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, new IntPtr(hit), lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
