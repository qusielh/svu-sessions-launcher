using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// A frozen copy of a screen region, including the WebView2 surface when the
/// region is the window itself. The bitmap lives in a device context that the
/// overlay presents directly, so the snapshot never round-trips through WPF.
/// </summary>
internal sealed class WindowSnapshot : IDisposable
{
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    private IntPtr _bitmap;
    private IntPtr _dc;
    private IntPtr _previous;

    private WindowSnapshot(Rect bounds, IntPtr dc, IntPtr bitmap, IntPtr previous)
    {
        Bounds = bounds;
        Width = (int)bounds.Width;
        Height = (int)bounds.Height;
        _dc = dc;
        _bitmap = bitmap;
        _previous = previous;
    }

    /// <summary>The screen rect these pixels were captured from, in device pixels.</summary>
    public Rect Bounds { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The device context the snapshot is selected into.</summary>
    public IntPtr Dc => _dc;

    /// <summary>
    /// Copies the window as DWM composites it. <c>PW_RENDERFULLCONTENT</c> is
    /// what makes the cross-process browser child appear. This is the fallback
    /// when a screen copy is unavailable.
    /// </summary>
    public static WindowSnapshot? Capture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero || !TryGetWindowBounds(hwnd, out var bounds)
            ? null
            : Capture(hwnd, bounds, print: true);
    }

    /// <summary>
    /// Copies the visible pixels under the window's rect straight from the
    /// screen. Much faster than <see cref="Capture"/> and exact for a window
    /// that is in front.
    /// </summary>
    public static WindowSnapshot? CaptureScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero || !TryGetWindowBounds(hwnd, out var bounds)
            ? null
            : CaptureScreen(bounds);
    }

    /// <summary>
    /// Copies an arbitrary screen region. Maximize uses this with the union of
    /// the current window and the monitor work area, so the whole area dissolves
    /// at once instead of the old rect fading inside a new layout that popped in
    /// around it.
    /// </summary>
    public static WindowSnapshot? CaptureScreen(Rect bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return null;
        }

        return Capture(IntPtr.Zero, bounds, print: false);
    }

    private static WindowSnapshot? Capture(IntPtr hwnd, Rect bounds, bool print)
    {
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var dc = CreateCompatibleDC(screen);
        if (dc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var info = new BitmapInfo
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };

        var bitmap = CreateDIBSection(dc, ref info, 0, out _, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var previous = SelectObject(dc, bitmap);

        var copied = print
            ? PrintWindow(hwnd, dc, PwRenderFullContent)
            : BitBlt(
                dc,
                0,
                0,
                width,
                height,
                screen,
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                SrcCopy | CaptureBlt);

        ReleaseDC(IntPtr.Zero, screen);

        if (!copied)
        {
            SelectObject(dc, previous);
            DeleteObject(bitmap);
            DeleteDC(dc);
            return null;
        }

        return new WindowSnapshot(
            new Rect(bounds.X, bounds.Y, width, height),
            dc,
            bitmap,
            previous);
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        bounds = ToRect(rect);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public void Dispose()
    {
        if (_dc == IntPtr.Zero)
        {
            return;
        }

        SelectObject(_dc, _previous);
        DeleteObject(_bitmap);
        DeleteDC(_dc);
        _dc = IntPtr.Zero;
    }

    private static Rect ToRect(NativeRect rect) => new(
        rect.Left,
        rect.Top,
        Math.Max(0, rect.Right - rect.Left),
        Math.Max(0, rect.Bottom - rect.Top));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo info,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
