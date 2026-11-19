using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SvuSessionsLauncher.Helpers;
using SvuSessionsLauncher.Services;

namespace SvuSessionsLauncher.Views;

public partial class ViewerWindow : Window, INotifyPropertyChanged, IDisposable
{
    /// <summary>Height of our single title bar; also the WindowChrome caption height.</summary>
    private const double TitleBarHeight = 32.0;

    private readonly ISessionConnection? _bridge;
    private readonly DispatcherTimer _micGuard;
    private bool _hasNoFrame = true;
    private bool _shown;
    private bool _audioMuted;
    private float _audioVolume = 1.0f; // 1.0 = 100%
    private bool _audioReady;
    private int _framePixelWidth;
    private int _framePixelHeight;
    private WriteableBitmap? _bitmap;
    private bool _renderQueued;
    private ViewportFrame? _pendingFrame;
    private readonly object _frameGate = new();
    private bool _allowClose;
    private bool _disposed;
    private double _scale = 1.0;
    private double _zoom = 1.0;
    private double _requestedScale = 1.0;
    private bool _fitToWindow = true;
    private bool _updatingViewport;
    private Task? _fadeTask;

    public ViewerWindow(ISessionConnection? bridge = null)
    {
        _bridge = bridge;
        InitializeComponent();
        WindowActivation.Attach(this);
        SizeChanged += (_, _) =>
        {
            UpdateScaleAvailability();
            UpdateViewportLayout();
        };
        DataContext = this;

        // The microphone is permanently muted. Re-assert it on a timer because
        // Windows or another application can hand the capture device back on.
        _micGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _micGuard.Tick += (_, _) => ApplyMicMute();
        Loaded += (_, _) => ApplyMicMute();
        _micGuard.Start();

        // Set the initial audio UI without firing the change handlers (setting
        // Slider.Value in code would otherwise touch the engine on open).
        _audioMuted = false;
        _audioVolume = 1.0f;
        AudioMuteToggle.IsChecked = false;
        AudioVolumeSlider.Value = 100.0;
        UpdateAudioIcon();
        ApplyMicMute();

        if (_bridge != null)
        {
            _bridge.ViewportReceived += OnViewportReceived;
            _bridge.Exited += OnBridgeExited;
            _bridge.SharingChanged += OnSharingChanged;
        }

        _audioReady = true;
    }

    public bool HasNoFrame
    {
        get => _hasNoFrame;
        set
        {
            _hasNoFrame = value;
            OnPropertyChanged();
        }
    }

    private void OnViewportReceived(ViewportFrame frame)
    {
        // Called on the network reader thread. Do not block it: stash the newest frame and ask
        // the UI thread to render once. If frames arrive faster than the UI drains them, older
        // ones are dropped rather than queued — the viewer always shows the latest frame, which
        // is exactly what a live screen share wants, and the reader thread is never throttled by
        // rendering.
        lock (_frameGate)
        {
            _pendingFrame = frame;
            if (_renderQueued)
            {
                return;
            }

            _renderQueued = true;
        }

        Dispatcher.BeginInvoke(new Action(RenderPendingFrame), DispatcherPriority.Render);
    }

    private void RenderPendingFrame()
    {
        ViewportFrame? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderQueued = false;
        }

        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        try
        {
            int width = frame.Width;
            int height = frame.Height;
            int stride = width * 4;
            bool sizeChanged = _framePixelWidth != width || _framePixelHeight != height;

            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                ViewportImage.Source = _bitmap;
            }

            _bitmap.Lock();
            try
            {
                _bitmap.WritePixels(
                    new Int32Rect(0, 0, width, height),
                    frame.Data,
                    stride,
                    0);
            }
            finally
            {
                _bitmap.Unlock();
            }

            HasNoFrame = false;

            _framePixelWidth = width;
            _framePixelHeight = height;

            // Appear only once screen-sharing actually starts, sized to the shared frame so there
            // are no letterbox bars.
            if (!_shown)
            {
                _shown = true;
                SizeToFrameProvisional();
                Show();
                Dispatcher.BeginInvoke(new Action(SizeToFrameExact), DispatcherPriority.Loaded);
            }
            else if (sizeChanged)
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyScale(_requestedScale)), DispatcherPriority.Loaded);
            }
        }
        catch (Exception)
        {
            // Ignore frame errors; the next frame will retry.
        }
    }

    private void OnBridgeExited(int exitCode)
    {
        Dispatcher.Invoke(CloseForSession);
    }

    /// <summary>
    /// Reacts to the tutor's screen share starting/stopping. We only need the
    /// stop edge: hide the viewer when the broadcast ends (it reappears by
    /// itself when the next frame arrives, i.e. if the tutor shares again). A
    /// start edge needs no action -- frames drive showing the window.
    /// </summary>
    private void OnSharingChanged(bool active)
    {
        Dispatcher.Invoke(() =>
        {
            if (active)
            {
                return;
            }

            if (_shown)
            {
                _shown = false;
                Hide();
            }
        });
    }

    /// <summary>
    /// A best-effort size applied before the window is shown (assumes 1 device
    /// pixel == 1 DIP). The exact size is corrected for DPI and the window
    /// chrome once the window is visible.
    /// </summary>
    private void SizeToFrameProvisional()
    {
        if (_framePixelWidth <= 0 || _framePixelHeight <= 0)
        {
            return;
        }

        var work = SystemParameters.WorkArea;
        var maxWidth = work.Width * 0.9;
        var maxHeight = (work.Height - TitleBarHeight) * 0.9;
        var scale = Math.Min(1.0, Math.Min(maxWidth / _framePixelWidth, maxHeight / _framePixelHeight));

        Width = _framePixelWidth * scale;
        Height = (_framePixelHeight * scale) + TitleBarHeight;
    }

    /// <summary>
    /// Sizes the window so the image area matches the shared frame's exact
    /// aspect (and native pixel size where the frame fits), accounting for the
    /// display DPI and the title bar / window border. The viewport uses
    /// <c>Stretch="Uniform"</c>, so matching the aspect removes the black bars.
    /// </summary>
    private void SizeToFrameExact() => ApplyScale(1.0);

    private void ScaleReset_Click(object sender, RoutedEventArgs e)
    {
        ScaleResetButton.IsChecked = true;
        ApplyScale(1.0);
    }

    private void ScaleDouble_Click(object sender, RoutedEventArgs e)
    {
        ScaleDoubleButton.IsChecked = true;
        ApplyScale(2.0);
    }

    private void ScaleTriple_Click(object sender, RoutedEventArgs e)
    {
        ScaleTripleButton.IsChecked = true;
        ApplyScale(3.0);
    }

    private void ScaleMax_Click(object sender, RoutedEventArgs e)
    {
        ScaleMaxButton.IsChecked = true;
        ApplyScale(double.PositiveInfinity);
    }

    /// <summary>
    /// Applies a scale preset. A preset that fits the work area resizes the
    /// window; a larger one keeps the window at the largest comfortable size and
    /// zooms the image inside it, with scrollbars for panning. This keeps 2x/3x
    /// useful even when the shared frame is already close to screen size.
    /// </summary>
    private void ApplyScale(double requestedScale)
    {
        var maxScale = GetMaxScale();
        if (maxScale <= 0)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var imageWidth = _framePixelWidth / (dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX);
        var imageHeight = _framePixelHeight / (dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY);
        var root = (FrameworkElement)Content;

        // Outer size minus the client grid == the window chrome (border/shadow).
        var chromeWidth = Math.Max(0, ActualWidth - root.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - root.ActualHeight);

        var fits = double.IsPositiveInfinity(requestedScale) || requestedScale <= maxScale + 0.001;
        var windowScale = fits
            ? (double.IsPositiveInfinity(requestedScale) ? maxScale : Math.Max(0.1, requestedScale))
            : maxScale;
        var zoom = fits ? 1.0 : Math.Max(1.0, requestedScale);

        _requestedScale = requestedScale;
        _scale = Math.Max(0.1, windowScale);
        _zoom = zoom;
        _fitToWindow = fits;

        ViewportScroll.HorizontalScrollBarVisibility = fits
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        ViewportScroll.VerticalScrollBarVisibility = fits
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        var newWidth = Math.Round(imageWidth * _scale + chromeWidth);
        var newHeight = Math.Round(imageHeight * _scale + TitleBarHeight + chromeHeight);
        var centreX = Left + ActualWidth / 2;
        var centreY = Top + ActualHeight / 2;

        var work = SystemParameters.WorkArea;
        Width = newWidth;
        Height = newHeight;
        Left = Math.Max(work.Left, Math.Min(work.Right - newWidth, centreX - newWidth / 2));
        Top = Math.Max(work.Top, Math.Min(work.Bottom - newHeight, centreY - newHeight / 2));

        UpdateViewportLayout();
        UpdateScaleAvailability();
    }

    /// <summary>
    /// Lays the image out at either the viewport size (fit) or its zoomed pixel
    /// size. The scroll viewer supplies panning and scrollbars when the zoomed
    /// image is larger than the window.
    /// </summary>
    private void UpdateViewportLayout()
    {
        if (_updatingViewport || _framePixelWidth <= 0 || _framePixelHeight <= 0)
        {
            return;
        }

        var viewportWidth = ViewportScroll.ViewportWidth;
        var viewportHeight = ViewportScroll.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            viewportWidth = ViewportScroll.ActualWidth;
            viewportHeight = ViewportScroll.ActualHeight;
        }

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        _updatingViewport = true;
        try
        {
            if (_fitToWindow)
            {
                ViewportHost.Width = viewportWidth;
                ViewportHost.Height = viewportHeight;
            }
            else
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var imageWidth = _framePixelWidth / (dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX);
                var imageHeight = _framePixelHeight / (dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY);
                ViewportHost.Width = Math.Max(1, imageWidth * _zoom);
                ViewportHost.Height = Math.Max(1, imageHeight * _zoom);
            }

            ViewportImage.Width = double.NaN;
            ViewportImage.Height = double.NaN;
        }
        finally
        {
            _updatingViewport = false;
        }

        Dispatcher.BeginInvoke(new Action(CenterViewport), DispatcherPriority.Loaded);
    }

    private void CenterViewport()
    {
        if (_fitToWindow)
        {
            return;
        }

        var x = Math.Max(0, (ViewportHost.Width - ViewportScroll.ViewportWidth) / 2);
        var y = Math.Max(0, (ViewportHost.Height - ViewportScroll.ViewportHeight) / 2);
        ViewportScroll.ScrollToHorizontalOffset(x);
        ViewportScroll.ScrollToVerticalOffset(y);
    }

    private void ViewportScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow)
        {
            UpdateViewportLayout();
        }
    }

    private double GetMaxScale()
    {
        if (_framePixelWidth <= 0 || _framePixelHeight <= 0 || Content is not FrameworkElement root)
        {
            return 0;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var imageWidth = _framePixelWidth / (dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX);
        var imageHeight = _framePixelHeight / (dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY);

        var chromeWidth = Math.Max(0, ActualWidth - root.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - root.ActualHeight);

        var work = SystemParameters.WorkArea;
        var maxScale = Math.Min(
            (work.Width * 0.9 - chromeWidth) / imageWidth,
            ((work.Height - TitleBarHeight) * 0.9 - chromeHeight) / imageHeight);

        return Math.Max(0.1, maxScale);
    }

    private void UpdateScaleAvailability()
    {
        var maxScale = GetMaxScale();
        if (maxScale <= 0)
        {
            return;
        }

        // 2x/3x are always usable: when the scaled window would not fit, the
        // view zooms inside the largest window and the scroll viewer pans it.
        ScaleDoubleButton.IsEnabled = true;
        ScaleTripleButton.IsEnabled = true;
    }

    private void AudioMuteToggle_Click(object sender, RoutedEventArgs e)
    {
        _audioMuted = AudioMuteToggle.IsChecked == true;
        UpdateAudioIcon();
        ApplyAudioSettings();
    }

    private void AudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_audioReady)
        {
            return;
        }

        _audioVolume = (float)(e.NewValue / 100.0);
        ApplyAudioSettings();
    }

    private void UpdateAudioIcon()
    {
        AudioIcon.Data = (Geometry)FindResource(_audioMuted ? "ViewIcon.VolumeMute" : "ViewIcon.Volume");
        AudioIcon.Stroke = (Brush)FindResource(_audioMuted ? "Brush.Muted" : "Brush.Text");
    }

    private void ApplyAudioSettings()
    {
        if (_bridge is null)
        {
            return;
        }

        // Drive the tutor-audio helper through the connection seam. Both mute
        // and volume are applied so the two controls never fight each other.
        // Fire-and-forget: the UI must not block on audio.
        try
        {
            _ = _bridge.SendVoiceMuteAsync(_audioMuted);
            _ = _bridge.SendVoiceVolumeAsync(_audioVolume);
        }
        catch
        {
            // Ignore if we can't control audio
        }
    }

    /// <summary>
    /// Reflects the microphone state in the title bar. The student client is
    /// **listen-only by construction**: the engine never opens a capture device
    /// and never transmits mic audio (see SessionEngine.OpenVoiceChannel), so
    /// the mic is reported muted as a policy state, not by muting the OS device.
    ///
    /// This is deliberately *not* an OS-level mute: the old launcher shared one
    /// applet for mic + speaker, which is fragile. When/if the microphone is
    /// (re)implemented as a real transmit path, the live state should be sourced
    /// from that path here and the icon bound to it -- this method is the seam.
    /// </summary>
    private void ApplyMicMute()
    {
        // Our own capture state: no transmit path exists yet, so it is always off.
        var muted = true;
        MicIcon.Data = (Geometry)FindResource(muted ? "ViewIcon.MicOff" : "ViewIcon.Mic");
        MicIcon.Stroke = (Brush)FindResource(muted ? "Brush.Muted" : "Brush.Danger");
        MicIcon.ToolTip = muted ? "Microphone is muted" : "Warning: microphone is live";
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Closes the viewer on the launcher's terms -- when the chat/session ends or
    /// the engine exits. This is the only close that is allowed; see
    /// <see cref="OnClosing"/>. Named so the call site makes the intent obvious
    /// (the window has no close button and cannot be closed by hand).
    /// </summary>
    public void CloseForSession()
    {
        _ = FadeOutAndCloseAsync();
    }

    /// <summary>
    /// Fades the video surface out before closing. The screen share is already
    /// stopped by the time the launcher calls this, so there is nothing live to
    /// keep visible.
    /// </summary>
    public Task FadeOutAndCloseAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (_fadeTask is not null)
        {
            return _fadeTask;
        }

        _allowClose = true;
        if (!IsVisible)
        {
            CloseSafely();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource();
        _fadeTask = done.Task;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            CloseSafely();
            done.TrySetResult();
        };
        BeginAnimation(OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>Closes unless WPF is already tearing the window down.</summary>
    private void CloseSafely()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }
    }

    // Windows 11 rounds every top-level window's corners; on a video surface that
    // clips the screen share itself, so the shared image appears to have rounded
    // top corners. Ask DWM to leave the viewer square.
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int preference = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Older Windows has no corner preference; nothing to undo.
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        MaxIcon.Data = (Geometry)FindResource(maximized ? "ViewIcon.Restore" : "ViewIcon.Max");
        MaxButton.ToolTip = maximized ? "Restore" : "Maximise";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _micGuard.Stop();

        if (_bridge != null)
        {
            _bridge.ViewportReceived -= OnViewportReceived;
            _bridge.Exited -= OnBridgeExited;
            _bridge.SharingChanged -= OnSharingChanged;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The viewer is not the user's to close. Ignore every user-initiated
        // close (Alt+F4, the window system menu, taskbar "Close window") so a
        // session that is still sharing keeps its viewer on screen; only the
        // launcher's own CloseForSession() is allowed through.
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        Dispose();
        base.OnClosing(e);
    }
}
