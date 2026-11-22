using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SvuSessionsLauncher.Helpers;
using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Services;
using SvuSessionsLauncher.Theming;
using SvuSessionsLauncher.ViewModels;

namespace SvuSessionsLauncher.Views;

/// <summary>
/// WebView2 host for the sign-in page. The view-model remains the source of
/// truth; the page only renders it and posts user input back.
/// </summary>
public partial class LoginWindow : Window
{
    private LoginViewModel? _viewModel;
    private bool _webReady;
    private bool _closed;
    private bool _revealed;
    private double _targetLeft;
    private double _targetTop;
    private DispatcherTimer? _revealTimer;
    private DispatcherTimer? _topmostTimer;

    public LoginWindow()
    {
        InitializeComponent();
        WindowActivation.Attach(this);

        // Match the native window and page before the browser surface paints,
        // otherwise the first frame flashes white.
        Web.DefaultBackgroundColor = GetWebViewBackground();

        // Load the page off-screen so the user never sees the native shell or a
        // half-painted form. The window is moved into place once the page has
        // applied its first full state and loaded its fonts.
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetCenteredTarget();
        Left = SystemParameters.VirtualScreenLeft - Width - 200;
        Top = SystemParameters.VirtualScreenTop - Height - 200;

        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer?.Stop();
            Reveal();
        };
        _revealTimer.Start();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public void PlaceOver(Rect? anchor)
    {
        if (anchor is not { Width: > 0, Height: > 0 } bounds)
        {
            SetCenteredTarget();
            return;
        }

        SetTarget(
            bounds.Left + ((bounds.Width - Width) / 2),
            bounds.Top + ((bounds.Height - Height) / 2));
    }

    private void SetCenteredTarget()
    {
        var work = SystemParameters.WorkArea;
        SetTarget(
            work.Left + ((work.Width - Width) / 2),
            work.Top + ((work.Height - Height) / 2));
    }

    private void SetTarget(double left, double top)
    {
        _targetLeft = Math.Max(SystemParameters.VirtualScreenLeft + 12,
            Math.Min(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width - 12, left));
        _targetTop = Math.Max(SystemParameters.VirtualScreenTop + 12,
            Math.Min(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height - 12, top));
    }

    private void Reveal()
    {
        if (_revealed || _closed)
        {
            return;
        }

        _revealed = true;
        _revealTimer?.Stop();

        Left = _targetLeft;
        Top = _targetTop;

        // A sign-out closes the main window, which can leave another app active.
        // Raise the finished sign-in surface above it until it is activated (or
        // briefly, if activation is refused) so it never opens behind the user.
        Topmost = true;
        Activated -= OnFirstActivated;
        Activated += OnFirstActivated;

        _topmostTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Stop();
        _topmostTimer.Tick -= OnTopmostTimedOut;
        _topmostTimer.Tick += OnTopmostTimedOut;
        _topmostTimer.Start();

        BringToFront();
        if (IsActive)
        {
            _topmostTimer?.Stop();
            Topmost = false;
        }
    }

    private void OnTopmostTimedOut(object? sender, EventArgs e)
    {
        _topmostTimer?.Stop();
        Topmost = false;
    }

    private void OnFirstActivated(object? sender, EventArgs e)
    {
        Activated -= OnFirstActivated;
        _topmostTimer?.Stop();
        Topmost = false;
    }

    private void BringToFront()
    {
        try
        {
            Activate();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Login foreground failed: {ex.Message}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }

        try
        {
            await Web.EnsureCoreWebView2Async(await WebViewRuntime.Environment);
        }
        catch (Exception ex)
        {
            Log.Write($"Login WebView2 init failed: {ex}");
            MessageBox.Show(
                this,
                "The embedded browser (WebView2) could not start.\n\nInstall the Microsoft Edge WebView2 Runtime and try again.",
                "SVU Sessions",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        var web = Web.CoreWebView2;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsZoomControlEnabled = false;
        Web.ZoomFactor = 1.0;
        web.WebMessageReceived += OnWebMessage;

        var page = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "login", "index.html")).AbsoluteUri;
        web.Navigate(page + BuildThemeQuery());
    }

    private void AttachViewModel(LoginViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_webReady)
        {
            SendState();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t))
            {
                type = t.GetString();
            }

            switch (type)
            {
                case "drag":
                    BeginWindowDrag();
                    return;
                case "minimize":
                    WindowAnimator.Minimize(this);
                    return;
                case "close":
                    Close();
                    return;
            }

            if (_viewModel is null)
            {
                return;
            }

            switch (type)
            {
                case "ready":
                    _webReady = true;
                    SendState();
                    break;
                case "rendered":
                    Reveal();
                    break;
                case "username":
                    _viewModel.Username = GetString(root, "value") ?? string.Empty;
                    break;
                case "password":
                    _viewModel.Password = GetString(root, "value") ?? string.Empty;
                    break;
                case "savePassword":
                    _viewModel.SavePassword = GetBool(root, "value");
                    break;
                case "autoSignIn":
                    _viewModel.AutoSignIn = GetBool(root, "value");
                    break;
                case "showPassword":
                    _viewModel.ShowPassword = GetBool(root, "value");
                    break;
                case "signIn":
                    _viewModel.SignInCommand.Execute(null);
                    break;
                case "language":
                    Loc.Instance.Language = GetString(root, "code") == "en" ? "en" : "ar";
                    SendState();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Login message failed ({type ?? "unknown"}): {ex.Message}");
        }
    }

    private void SendState()
    {
        if (!_webReady || _viewModel is null || _closed)
        {
            return;
        }

        var payload = new
        {
            type = "login",
            username = _viewModel.Username,
            password = _viewModel.Password,
            savePassword = _viewModel.SavePassword,
            autoSignIn = _viewModel.AutoSignIn,
            showPassword = _viewModel.ShowPassword,
            busy = _viewModel.IsBusy,
            canSignIn = _viewModel.CanSignIn,
            error = _viewModel.ErrorMessage ?? string.Empty,
            hasError = _viewModel.HasError,
            language = Loc.Instance.Language,
            strings = BuildStrings()
        };

        Post(JsonSerializer.Serialize(payload));
    }

    private void Post(string json)
    {
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Log.Write($"Login web post failed: {ex.Message}");
        }
    }

    private static Dictionary<string, string> BuildStrings()
    {
        var keys = new[]
        {
            "app.title", "app.subtitle",
            "login.title", "login.subtitle", "login.username", "login.password",
            "login.autoSignIn", "login.privacy", "login.copyright",
            "login.username.placeholder", "login.password.placeholder",
            "login.savePassword", "login.signIn", "login.signingIn", "login.showPassword"
        };

        var strings = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            strings[key] = Loc.Instance[key];
        }

        return strings;
    }

    private string BuildThemeQuery()
    {
        var settings = _viewModel?.Settings;
        var theme = ThemeManager.Parse(settings?.Theme) == AppTheme.Dark ? "dark" : "light";
        var accent = ThemeManager.Resolve(settings?.Accent, settings?.AccentHex);
        var stops = ThemeManager.ResolveHeaderStops(accent, settings?.Header);
        var ink = ThemeManager.ResolveOnPrimary(accent, settings?.Ink);
        return $"?theme={theme}&accent={Hex(accent.Primary)}&accentHover={Hex(accent.Hover)}" +
               $"&accentStrong={Hex(accent.Pressed)}&ink={Hex(ink)}&hd0={Hex(stops[0])}&hd1={Hex(stops[1])}" +
               $"&hd2={Hex(stops[2])}&v=20261122c";
    }

    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void BeginWindowDrag()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            ReleaseCapture();
            SendMessage(handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write($"Login drag failed: {ex.Message}");
        }
    }

    private static string Hex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static System.Drawing.Color GetWebViewBackground()
    {
        if (Application.Current?.TryFindResource("Brush.Background") is SolidColorBrush brush)
        {
            var c = brush.Color;
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        return System.Drawing.Color.FromArgb(0xEE, 0xF1, 0xF4);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // The sign-in window uses the same dissolve for minimize and restore as
        // the session windows; without the hook the taskbar would replay the
        // native animation.
        WindowAnimator.Attach(this);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        WindowAnimator.HandleMessage(hwnd, this, msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _revealTimer?.Stop();
        _revealTimer = null;
        _topmostTimer?.Stop();
        _topmostTimer = null;
        Activated -= OnFirstActivated;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        if (Web.CoreWebView2 is not null)
        {
            Web.CoreWebView2.WebMessageReceived -= OnWebMessage;
        }
    }
}
