using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using SvuSessionsLauncher.Helpers;
using SvuSessionsLauncher.Services;
using SvuSessionsLauncher.Theming;

namespace SvuSessionsLauncher.Views;

/// <summary>
/// The session's only window. Hosts the reworked chat UI in a WebView2 and owns
/// the session connection through <see cref="ISessionConnection"/>: the
/// connection opens before this window and is closed with it, so closing the
/// chat ends the whole session.
/// </summary>
public partial class ChatWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private ISessionConnection? _bridge;
    private readonly AppSettings _settings;
    private readonly List<string> _pending = new();
    private bool _webReady;
    private bool _prewarming;
    private bool _closing;
    private bool _closed;
    private bool _closeSignaled;
    private bool _allowClose;
    private Task? _fadeTask;

    public ChatWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowActivation.Attach(this);

        // The WebView2 honours a background set before its core initializes, so
        // the first native frame is the app background, not black.
        Web.DefaultBackgroundColor = GetWebViewBackground();

        // The chat has no Owner (so the launcher can be brought above it), which
        // makes CenterOwner meaningless ? centre it on the screen instead.
        // Prewarm() parks a hidden window off-screen; ShowForSession() brings it
        // back at the normal position with the painted loader.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _settings = settings;
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        ThemeManager.Changed += OnThemeChanged;
        ThemeManager.PreviewChanged += OnThemePreviewChanged;
    }

    public bool IsClosed => _closed;

    /// <summary>
    /// True when the window is closing because a live session could not be
    /// recovered, rather than because the user left it.
    /// </summary>
    public bool EndedUnexpectedly { get; private set; }

    /// <summary>
    /// Raised once the live connection has been dropped and the window should
    /// leave the screen. The launcher fades the chat and viewer together.
    /// </summary>
    public event EventHandler? SessionCloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            WindowAnimator.Attach(this);
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }

        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));

            // A prewarmed chat exists only to paint its WebView off-screen. Keep
            // it out of Alt+Tab and the taskbar until a real session claims it.
            if (_prewarming)
            {
                SetPrewarmWindowStyle(true);
            }
        }
        catch
        {
            // Windows 10 has no DWM corner preference; the window stays square.
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        WindowAnimator.HandleMessage(hwnd, this, msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }

    private void SetPrewarmWindowStyle(bool prewarming)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        if (prewarming)
        {
            style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        }
        else
        {
            style &= ~(WsExToolWindow | WsExNoActivate);
        }

        SetWindowLong(handle, GwlExStyle, style);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowAnimator.Minimize(this);

    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowAnimator.ToggleMaximize(this);

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        await SignalSessionCloseAsync(true);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        ApplyMaximizedVisualState(WindowState == WindowState.Maximized);
        SyncResizeGrips();
    }

    private void ApplyMaximizedVisualState(bool maximized)
    {
        // While maximized the top edge must be caption, not a resize border:
        // WindowChrome would otherwise report HTTOP for the first 6px and a
        // double-click at y=0 would not restore the window.
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.ResizeBorderThickness = maximized ? new Thickness(0) : new Thickness(6);
        }

        MaximizeGlyph.Data = Geometry.Parse(maximized
            ? "M 2,0 L 9,0 L 9,7 M 0,2 L 7,2 L 7,9 L 0,9 Z"
            : "M 0,0 L 9,0 L 9,9 L 0,9 Z");
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private async void SyncResizeGrips()
    {
        if (Web.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            var maximized = WindowState == WindowState.Maximized ? "true" : "false";
            await core.ExecuteScriptAsync(
                "if (window.svuWindowState) window.svuWindowState(" + maximized + ");");
        }
        catch (Exception ex)
        {
            Log.Write($"chat: resize grip state failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the loader page in a hidden, off-screen window so the first frame
    /// shown to the user is the already-painted loader, not a blank browser
    /// surface. The window is hidden again as soon as the page has painted.
    /// </summary>
    public void Prewarm()
    {
        _prewarming = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft - Width - 100;
        Top = SystemParameters.VirtualScreenTop;
        Show();
        BeginPrewarmHide();
    }

    /// <summary>
    /// Attaches the live session and opens the prewarmed window at its final
    /// position. The destination is captured first, then the window is shown at
    /// full opacity underneath and the frozen desktop dissolves away.
    /// </summary>
    public void ShowForSession(ISessionConnection bridge)
    {
        AttachBridge(bridge);
        _prewarming = false;
        ShowActivated = true;
        ShowInTaskbar = true;
        SetPrewarmWindowStyle(false);

        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var target = new Rect(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);

        Opacity = 1;
        BeginAnimation(OpacityProperty, null);

        // Capture the destination before moving the prewarmed window there, then
        // dissolve from the desktop to the already-painted chat surface.
        WindowAnimator.Open(this, target, () =>
        {
            Left = target.Left;
            Top = target.Top;
            if (!IsVisible)
            {
                Show();
            }
        });

        Activate();
    }

    private void AttachBridge(ISessionConnection bridge)
    {
        if (_bridge is not null)
        {
            throw new InvalidOperationException("The chat already has a session connection.");
        }

        _bridge = bridge;
        if (_closed)
        {
            bridge.Dispose();
            return;
        }

        bridge.EventReceived += OnEngineEvent;
        bridge.Exited += OnEngineExited;
    }

    private async void BeginPrewarmHide()
    {
        // Give the page enough time to paint the loader before hiding it. The
        // hidden window keeps its WebView surface; ShowForSession brings that
        // same painted surface back with the normal window show transition.
        await Task.Delay(1800);
        if (!_prewarming || _closing)
        {
            return;
        }

        _prewarming = false;
        Hide();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Reuse the app-wide environment (started at launch) so the chat
            // page — and its loader — paints without a cold start.
            await Web.EnsureCoreWebView2Async(await WebViewRuntime.Environment);
        }
        catch (Exception ex)
        {
            Log.Write($"WebView2 init failed: {ex}");
            if (_prewarming)
            {
                CloseSafely();
                return;
            }

            MessageBox.Show(
                this,
                "The embedded browser (WebView2) could not start.\n\nInstall the Microsoft Edge WebView2 Runtime and try again.",
                "SVU Sessions",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            CloseSafely();
            return;
        }

        var web = Web.CoreWebView2;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsZoomControlEnabled = false;
        Web.ZoomFactor = 1.0;

        Web.DefaultBackgroundColor = GetWebViewBackground();

        // File URLs skip WebView2's virtual-host network path, which adds ~2 s
        // before NavigationCompleted on this machine.
        var page = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "chat", "index.html")).AbsoluteUri;
        web.WebMessageReceived += OnWebMessage;
        web.NavigationStarting += OnNavigationStarting;
        web.NewWindowRequested += OnNewWindowRequested;
        web.Navigate(page + BuildThemeQuery());
    }

    private void MarkWebReady()
    {
        if (_webReady)
        {
            return;
        }

        _webReady = true;
        foreach (var json in _pending)
        {
            Post(json);
        }

        _pending.Clear();

        // A preview can land while the page is still loading (before its
        // setAppTheme hook exists). Re-apply the current look now that it is
        // ready so no drag is lost.
        PushThemeToPage();
        SyncResizeGrips();
        RevealPage();
    }

    /// <summary>
    /// Releases the page from its background-only holding state as soon as it is
    /// ready. This normally happens while the window is prewarmed and hidden, so
    /// the surface is fully composed before the dissolve ever shows it.
    /// </summary>
    private void RevealPage()
    {
        try
        {
            Web.CoreWebView2?.ExecuteScriptAsync(
                "document.documentElement.classList.remove('chat-preparing')");
        }
        catch (Exception ex)
        {
            Log.Write($"chat reveal failed: {ex.Message}");
        }
    }

    private void OnEngineEvent(string json)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_webReady)
            {
                _pending.Add(json);
                return;
            }

            Post(json);
        });
    }

    private void Post(string json)
    {
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Log.Write($"web post failed: {ex.Message}");
        }
    }

    private void OnEngineExited(int code)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_closeSignaled || _closed)
            {
                return;
            }

            EndedUnexpectedly = true;
            await SignalSessionCloseAsync(false);
        });
    }

    /// <summary>
    /// Drops the session connection (and with it the audio helper), then asks
    /// the launcher to fade both windows out together. Falls back to closing
    /// this window directly when no host has subscribed.
    /// </summary>
    private async Task SignalSessionCloseAsync(bool notifyServer)
    {
        if (_closeSignaled || _closed)
        {
            return;
        }

        _closeSignaled = true;
        _closing = true;

        if (notifyServer && _bridge is not null)
        {
            try
            {
                await _bridge.LeaveAsync();
            }
            catch (Exception ex)
            {
                Log.Write($"session leave failed: {ex.Message}");
            }
        }

        ReleaseBridge();

        var handler = SessionCloseRequested;
        if (handler is not null)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        await FadeOutAndCloseAsync();
    }

    private void ReleaseBridge()
    {
        var bridge = _bridge;
        _bridge = null;
        if (bridge is null)
        {
            return;
        }

        bridge.EventReceived -= OnEngineEvent;
        bridge.Exited -= OnEngineExited;
        bridge.Dispose();
    }

    /// <summary>
    /// Dissolves the already-painted window surface out before closing it. The
    /// launcher calls this alongside the viewer fade.
    /// </summary>
    public Task FadeOutAndCloseAsync()
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        if (_fadeTask is not null)
        {
            return _fadeTask;
        }

        _closing = true;
        _fadeTask = WindowAnimator.FadeOutAndClose(this, () =>
        {
            _allowClose = true;
            CloseSafely();
        });
        return _fadeTask;
    }

    /// <summary>
    /// Closes the window unless WPF is already tearing it down. A queued fade
    /// can finish while the window is in its closing pass, and calling
    /// <see cref="Window.Close"/> then throws; there is nothing left to do.
    /// </summary>
    private void CloseSafely()
    {
        if (_closed)
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

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type = null;
        string? text = null;
        string? edge = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t))
            {
                type = t.GetString();
            }

            if (root.TryGetProperty("text", out var x))
            {
                text = x.GetString();
            }

            if (root.TryGetProperty("edge", out var edgeValue))
            {
                edge = edgeValue.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"web message parse failed: {ex.Message}");
            return;
        }

        switch (type)
        {
            case "ready":
                MarkWebReady();
                break;
            case "resize":
                WindowResizer.Start(this, edge);
                break;
            case "joined":
                // The page owns its own loader: it plays the in-page SVU loader
                // until the engine's hello arrives, then fades it out itself.
                // Nothing for the host to reveal here anymore.
                break;
            case "send":
                if (_bridge is not null && !string.IsNullOrEmpty(text))
                {
                    await _bridge.SendPublicAsync(text);
                }

                break;
            case "leave":
                Log.Write("chat leave requested");
                await SignalSessionCloseAsync(true);
                break;
            case "ping":
                if (_bridge is not null)
                {
                    await _bridge.PingAsync();
                }

                break;
            case "retry":
                // The page's "Retry" after a failed join re-enters the launch
                // loop with a fresh connection; the page returns to its loader
                // as soon as the first "retrying" event arrives.
                if (_bridge is not null)
                {
                    await _bridge.RetryAsync();
                }

                break;
        }
    }

    /// <summary>
    /// Query string that carries the launcher's appearance into the chat page so
    /// it matches the app: light/dark plus the chosen accent and header
    /// "radiant". Colours are bare hex (no leading '#') so nothing needs
    /// URL-encoding.
    /// </summary>
    private string BuildThemeQuery()
    {
        var theme = ThemeManager.Parse(_settings.Theme) == AppTheme.Dark ? "dark" : "light";
        var accent = ThemeManager.Resolve(_settings.Accent, _settings.AccentHex);
        var stops = ThemeManager.ResolveHeaderStops(accent, _settings.Header);
        var q = new System.Text.StringBuilder("?theme=").Append(theme);
        q.Append("&accent=").Append(Hex(accent.Primary));
        q.Append("&accentHover=").Append(Hex(accent.Hover));
        q.Append("&accentStrong=").Append(Hex(accent.Pressed));
        q.Append("&hd0=").Append(Hex(stops[0]));
        q.Append("&hd1=").Append(Hex(stops[1]));
        q.Append("&hd2=").Append(Hex(stops[2]));
        // Bust WebView2's file cache so a rebuilt page never reuses the old splash logic.
        q.Append("&v=20261122c");
        return q.ToString();
    }

    private static string Hex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Backs the WebView before its page paints. Read from the live palette so
    /// the join window shares the launcher's current theme from the first frame
    /// instead of a hard-coded colour.
    /// </summary>
    private static System.Drawing.Color GetWebViewBackground()
    {
        if (Application.Current?.Resources["Brush.ChatBackground"] is SolidColorBrush brush)
        {
            var c = brush.Color;
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        return System.Drawing.Color.FromArgb(0xEE, 0xF1, 0xF4);
    }

    /// <summary>JavaScript that re-tints the already-loaded chat page in place.</summary>
    private string BuildThemeScript()
    {
        var theme = ThemeManager.Parse(_settings.Theme) == AppTheme.Dark ? "dark" : "light";
        var accent = ThemeManager.Resolve(_settings.Accent, _settings.AccentHex);
        var stops = ThemeManager.ResolveHeaderStops(accent, _settings.Header);
        return "window.setAppTheme({"
               + $"theme:'{theme}',"
               + $"accent:'{Hex(accent.Primary)}',"
               + $"accentHover:'{Hex(accent.Hover)}',"
               + $"accentStrong:'{Hex(accent.Pressed)}',"
               + $"hd0:'{Hex(stops[0])}',"
               + $"hd1:'{Hex(stops[1])}',"
               + $"hd2:'{Hex(stops[2])}'"
               + "})";
    }

    private void OnThemeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                Web.DefaultBackgroundColor = GetWebViewBackground();
                PushThemeToPage();
            }
            catch
            {
                // Ignore theme update failures.
            }
        });
    }

    /// <summary>
    /// A colour-picker drag only previews the accent. The WPF palette stays
    /// untouched, but the chat page still needs the new tint immediately, even
    /// while this window sits behind the studio.
    /// </summary>
    private void OnThemePreviewChanged()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                PushThemeToPage();
            }
            catch
            {
                // Ignore theme update failures.
            }
        });
    }

    private void PushThemeToPage()
    {
        if (Web.CoreWebView2 != null)
        {
            Web.CoreWebView2.ExecuteScriptAsync(BuildThemeScript());
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsExternal(e.Uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (IsExternal(e.Uri))
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        }
    }

    /// <summary>True for web links the chat page itself should not navigate to.</summary>
    private static bool IsExternal(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return true;
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"open link failed: {ex.Message}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (!_closing)
            {
                _ = SignalSessionCloseAsync(true);
            }

            return;
        }

        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _closed = true;
        StateChanged -= OnWindowStateChanged;
        ThemeManager.Changed -= OnThemeChanged;
        ThemeManager.PreviewChanged -= OnThemePreviewChanged;
        ReleaseBridge();
    }
}

