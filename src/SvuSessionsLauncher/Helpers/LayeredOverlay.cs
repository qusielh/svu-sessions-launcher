using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace SvuSessionsLauncher.Helpers;

/// <summary>
/// A topmost, no-activate, click-through popup that shows a frozen window image
/// while the real window changes state. The snapshot is presented 1:1 through
/// <c>UpdateLayeredWindow</c> on a dedicated thread paced by DWM, so the fade
/// keeps a steady frame rate even while the UI thread is busy re-laying out the
/// window and the WebView2 child. The snapshot is owned by the animator, so its
/// pixels are never copied.
/// </summary>
internal sealed class LayeredOverlay : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0;

    private readonly WindowSnapshot _snapshot;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _start = new(false);
    private readonly ManualResetEventSlim _stop = new(false);

    private IntPtr _hwnd;
    private double _duration;
    private double _hold;
    private bool _shown;
    private bool _disposed;

    private LayeredOverlay(WindowSnapshot snapshot, Dispatcher dispatcher)
    {
        _snapshot = snapshot;
        _dispatcher = dispatcher;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Window fade"
        };
    }

    /// <summary>Runs on the UI thread once the overlay has faded out or stopped.</summary>
    public Action? Faded { get; set; }

    public static LayeredOverlay? Create(WindowSnapshot snapshot)
    {
        var overlay = new LayeredOverlay(snapshot, Dispatcher.CurrentDispatcher);
        overlay._thread.Start();

        if (!overlay._ready.Wait(TimeSpan.FromSeconds(2)) || overlay._hwnd == IntPtr.Zero)
        {
            overlay.Dispose();
            return null;
        }

        return overlay;
    }

    public void StartFade(double milliseconds, double holdMilliseconds = 0)
    {
        if (_disposed)
        {
            return;
        }

        _duration = Math.Max(1.0, milliseconds);
        _hold = Math.Max(0.0, holdMilliseconds);
        _start.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _start.Set();

        if (_thread.Join(1000))
        {
            _ready.Dispose();
            _start.Dispose();
            _stop.Dispose();
        }
    }

    private void Run()
    {
        try
        {
            var bounds = _snapshot.Bounds;
            var width = Math.Max(1, _snapshot.Width);
            var height = Math.Max(1, _snapshot.Height);
            var x = (int)Math.Round(bounds.X);
            var y = (int)Math.Round(bounds.Y);

            _hwnd = CreateWindowEx(
                WsExLayered | WsExToolWindow | WsExTopmost | WsExTransparent | WsExNoActivate,
                "STATIC",
                string.Empty,
                WsPopup,
                x,
                y,
                width,
                height,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero || !Present(255))
            {
                return;
            }

            // The image is already painted, so the window may change state
            // underneath without a single uncovered frame. Placing it at the
            // top of the topmost band keeps it above a temporarily topmost
            // studio window.
            ShowWindow(_hwnd, SwShowNoActivate);
            SetWindowPos(
                _hwnd,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
            _shown = true;

            // Do not let the window change state until the frozen frame has
            // actually been composed. Otherwise the maximized frame can be
            // presented for one frame before the overlay covers it, which reads
            // as a brief upscaled flash.
            DwmFlush();
            _ready.Set();

            if (!_start.Wait(TimeSpan.FromSeconds(5)) || _stop.IsSet)
            {
                return;
            }

            // Stay fully opaque while the window paints its new layout. The
            // state change happens underneath this frame, so the stale surface
            // must not show through before the new one is ready.
            if (_hold > 0 && _stop.Wait(TimeSpan.FromMilliseconds(_hold)))
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            while (!_stop.IsSet)
            {
                var progress = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / _duration);
                Present((int)Math.Round(255.0 * (1.0 - progress)));

                if (progress >= 1.0)
                {
                    break;
                }

                if (DwmFlush() != 0)
                {
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            _ready.Set();

            if (_shown)
            {
                try
                {
                    _dispatcher.BeginInvoke(() => Faded?.Invoke());
                }
                catch
                {
                    // The dispatcher is already shutting down.
                }
            }
        }
    }

    private bool Present(int alpha)
    {
        var bounds = _snapshot.Bounds;
        var destination = new NativePoint
        {
            X = (int)Math.Round(bounds.X),
            Y = (int)Math.Round(bounds.Y)
        };

        var size = new NativeSize
        {
            CX = Math.Max(1, _snapshot.Width),
            CY = Math.Max(1, _snapshot.Height)
        };

        var source = default(NativePoint);
        var blend = new BlendFunction
        {
            BlendOp = AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = (byte)alpha,
            AlphaFormat = 0
        };

        return UpdateLayeredWindow(
            _hwnd,
            IntPtr.Zero,
            ref destination,
            ref size,
            _snapshot.Dc,
            ref source,
            0,
            ref blend,
            UlwAlpha);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr destinationDc,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDc,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
