using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SvuSessionsLauncher.Helpers;
using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Models;
using SvuSessionsLauncher.Services;
using SvuSessionsLauncher.Theming;
using SvuSessionsLauncher.ViewModels;

namespace SvuSessionsLauncher.Views;

/// <summary>
/// WebView2 host for the studio main window. The existing <see cref="MainViewModel"/>
/// remains the single source of truth: this window projects it as JSON state and
/// turns page actions back into view-model commands.
/// </summary>
public partial class MainStudioWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> SessionProjectionProperties = new(StringComparer.Ordinal)
    {
        nameof(MainViewModel.TutorFilter),
        nameof(MainViewModel.ClassFilter),
        nameof(MainViewModel.SelectedProgram),
        nameof(MainViewModel.SelectedCourse),
        nameof(MainViewModel.SelectedMode),
        nameof(MainViewModel.SortColumn),
        nameof(MainViewModel.SortDescending),
        nameof(MainViewModel.SessionCount),
        nameof(MainViewModel.ShowSessionsEmpty),
        nameof(MainViewModel.HasSessionLoadError),
        nameof(MainViewModel.ShowNoMatches)
    };

    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _pushTimer;
    private bool _webReady;
    private bool _subscribed;
    private bool _disposed;
    private bool _revealed;
    private double _targetLeft;
    private double _targetTop;
    private DispatcherTimer? _revealTimer;
    private DispatcherTimer? _topmostTimer;
    private string? _sortColumn;
    private int _sortStage;
    private string? _sessionsJson;
    private int _sessionsRevision;
    private int _lastSentSessionsRevision = -1;
    private bool _sessionsDirty = true;

    private DownloadsViewModel? _downloadsVm;
    private readonly HashSet<DownloadRow> _hookedDownloadRows = new();
    private bool _downloadsOpen;
    private string? _downloadsJson;
    private int _downloadsRevision;
    private int _lastSentDownloadsRevision = -1;
    private bool _downloadsDirty;

    /// <summary>Completes when the studio page has painted and accepted initial state.</summary>
    public Task PageReady => _pageReady.Task;

    public MainStudioWindow()
    {
        InitializeComponent();
        WindowActivation.Attach(this);

        SetWebBackground();

        // Load the page off-screen, then reveal the finished surface. This
        // avoids showing the native shell while the browser is still painting.
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetCenteredTarget();
        Left = SystemParameters.VirtualScreenLeft - Width - 200;
        Top = SystemParameters.VirtualScreenTop - Height - 200;

        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer?.Stop();
            Reveal();
        };
        _revealTimer.Start();

        _pushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            PushState();
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Loc.LanguageChanged += OnLanguageChanged;
        DisplayOptions.Changed += OnDisplayOptionsChanged;
        ThemeManager.Changed += OnThemeChanged;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowAnimator.Minimize(this);

    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowAnimator.ToggleMaximize(this);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
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
            Log.Write($"studio: resize grip state failed: {ex.Message}");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowAnimator.Attach(this);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        WindowAnimator.HandleMessage(hwnd, this, msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        Subscribe(vm);
        SetWebBackground();

        try
        {
            await Web.EnsureCoreWebView2Async(await WebViewRuntime.Environment);
        }
        catch (Exception ex)
        {
            Log.Write($"studio: webview init failed: {ex}");
            return;
        }

        if (_disposed || Web.CoreWebView2 is not { } core)
        {
            return;
        }

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        Web.ZoomFactor = 1.0;

        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += (_, args) => args.Handled = true;

        var page = new Uri(Path.Combine(
            AppContext.BaseDirectory, "Assets", "studio", "index.html")).AbsoluteUri;
        core.Navigate(page + "?v=20261122h");

        await vm.InitializeAsync();
        SchedulePush();
    }

    private void Subscribe(MainViewModel vm)
    {
        if (_subscribed)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (vm.SessionsView is INotifyCollectionChanged view)
        {
            view.CollectionChanged += OnSessionsChanged;
        }

        _subscribed = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _pageReady.TrySetResult();
        _pushTimer.Stop();
        _revealTimer?.Stop();
        _revealTimer = null;
        _topmostTimer?.Stop();
        _topmostTimer = null;
        Activated -= OnRevealActivated;

        Loc.LanguageChanged -= OnLanguageChanged;
        DisplayOptions.Changed -= OnDisplayOptionsChanged;
        ThemeManager.Changed -= OnThemeChanged;

        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;

            if (vm.SessionsView is INotifyCollectionChanged view)
            {
                view.CollectionChanged -= OnSessionsChanged;
            }

            vm.Dispose();
        }

        if (Web.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessage;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
        }

        DisposeDownloads();
    }

    private void SetWebBackground()
    {
        var color = Application.Current?.TryFindResource("Brush.Background") is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0xF2, 0xF5, 0xFA);

        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
    }

    private void SetCenteredTarget()
    {
        var work = SystemParameters.WorkArea;
        _targetLeft = Math.Max(SystemParameters.VirtualScreenLeft + 12,
            Math.Min(
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width - 12,
                work.Left + ((work.Width - Width) / 2)));
        _targetTop = Math.Max(SystemParameters.VirtualScreenTop + 12,
            Math.Min(
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height - 12,
                work.Top + ((work.Height - Height) / 2)));
    }

    private void Reveal()
    {
        if (_revealed || _disposed)
        {
            return;
        }

        _revealed = true;
        _revealTimer?.Stop();
        Left = _targetLeft;
        Top = _targetTop;

        // Closing the modal sign-in window can hand foreground back to another
        // application. Raise the finished studio until it is activated (or
        // briefly, if activation is refused) so it opens in front naturally.
        Topmost = true;
        Activated -= OnRevealActivated;
        Activated += OnRevealActivated;

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

    private void OnRevealActivated(object? sender, EventArgs e)
    {
        Activated -= OnRevealActivated;
        _topmostTimer?.Stop();
        Topmost = false;
    }

    private void OnTopmostTimedOut(object? sender, EventArgs e)
    {
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
            Log.Write($"studio: foreground failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Log.Write($"studio: navigation failed ({e.WebErrorStatus})");
            return;
        }

        _webReady = true;
        _lastSentSessionsRevision = -1;
        SyncResizeGrips();
        Web.Focus();
        PushState();
    }

    private void OnLanguageChanged()
    {
        MarkSessionsDirty();
        if (_downloadsVm is not null)
        {
            _downloadsDirty = true;
        }

        SchedulePush();
    }

    private void OnDisplayOptionsChanged()
    {
        MarkSessionsDirty();
        SchedulePush();
    }

    private void OnThemeChanged()
    {
        SetWebBackground();
        SchedulePush();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && SessionProjectionProperties.Contains(name))
        {
            MarkSessionsDirty();
        }

        SchedulePush();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkSessionsDirty();
        SchedulePush();
    }

    private void MarkSessionsDirty() => _sessionsDirty = true;

    private void SchedulePush()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SchedulePush);
            return;
        }

        _pushTimer.Stop();
        _pushTimer.Start();
    }

    private void PushState()
    {
        if (_disposed || !_webReady || Web.CoreWebView2 is not { } core)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            var sessionsJson = GetSessionsJson(vm);
            if (_lastSentSessionsRevision != _sessionsRevision)
            {
                core.PostWebMessageAsJson(
                    "{\"type\":\"sessions\",\"revision\":"
                    + _sessionsRevision.ToString(CultureInfo.InvariantCulture)
                    + ",\"sessions\":" + sessionsJson + "}");
                _lastSentSessionsRevision = _sessionsRevision;
            }

            if (_downloadsOpen && _downloadsVm is not null
                && (_downloadsDirty || _lastSentDownloadsRevision != _downloadsRevision))
            {
                core.PostWebMessageAsJson(GetDownloadsJson());
                _lastSentDownloadsRevision = _downloadsRevision;
            }

            core.PostWebMessageAsJson(JsonSerializer.Serialize(BuildState(vm), JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"studio: state push failed: {ex.Message}");
        }
    }

    private void PushTheme(MainViewModel vm)
    {
        if (_disposed || !_webReady || Web.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "theme", theme = BuildTheme(vm) }, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"studio: theme preview failed: {ex.Message}");
        }
    }

    private string GetSessionsJson(MainViewModel vm)
    {
        if (!_sessionsDirty && _sessionsJson is not null)
        {
            return _sessionsJson;
        }

        var sessions = vm.SessionsView
            .Cast<SessionInfo>()
            .Select(s => new
            {
                key = Key(s),
                token = s.SessionToken,
                classToken = s.ClassToken,
                course = s.CourseLabel,
                program = s.ProgramCode,
                name = s.SessionName,
                tutor = s.TutorName,
                server = s.ServerNumber.ToString(CultureInfo.InvariantCulture),
                start = FormatDate(s.StartDate),
                status = StatusKey(s),
                statusText = Loc.Instance[StatusCode.ToLocKey(StatusCode.FromRaw(s.Status))],
                canLaunch = s.CanLaunch
            })
            .ToList();

        _sessionsJson = JsonSerializer.Serialize(sessions, JsonOptions);
        _sessionsRevision++;
        _sessionsDirty = false;
        return _sessionsJson;
    }

    private string GetDownloadsJson()
    {
        if (!_downloadsDirty && _downloadsJson is not null)
        {
            return _downloadsJson;
        }

        _downloadsDirty = false;
        _downloadsRevision++;

        _downloadsJson = JsonSerializer.Serialize(new
        {
            type = "downloads",
            revision = _downloadsRevision,
            downloads = _downloadsVm is null ? null : BuildDownloads(_downloadsVm)
        }, JsonOptions);

        return _downloadsJson;
    }

    private static object BuildDownloads(DownloadsViewModel downloads) => new
    {
        saveDirectory = downloads.SaveDirectory,
        terms = OptionList(downloads.Terms),
        programs = OptionList(downloads.Programs),
        courses = OptionList(downloads.Courses),
        tutors = OptionList(downloads.Tutors),
        classes = OptionList(downloads.Classes),
        selected = new
        {
            term = downloads.SelectedTerm?.Value ?? "",
            program = downloads.SelectedProgram?.Value ?? "",
            course = downloads.SelectedCourse?.Value ?? "",
            tutor = downloads.SelectedTutor?.Value ?? "",
            @class = downloads.SelectedClass?.Value ?? ""
        },
        enabled = new
        {
            program = downloads.IsProgramEnabled,
            course = downloads.IsCourseEnabled,
            tutor = downloads.IsTutorEnabled,
            @class = downloads.IsClassEnabled
        },
        loading = new
        {
            terms = downloads.IsLoadingTerms,
            programs = downloads.IsLoadingPrograms,
            courses = downloads.IsLoadingCourses,
            tutors = downloads.IsLoadingTutors,
            classes = downloads.IsLoadingClasses,
            sessions = downloads.IsLoadingSessions
        },
        placeholders = new
        {
            term = downloads.ShowTermPlaceholder,
            program = downloads.ShowProgramPlaceholder,
            course = downloads.ShowCoursePlaceholder,
            tutor = downloads.ShowTutorPlaceholder,
            @class = downloads.ShowClassPlaceholder
        },
        show = new
        {
            spinner = downloads.ShowSpinner,
            empty = downloads.ShowEmpty,
            hint = downloads.ShowHint,
            header = downloads.ShowHeader,
            list = downloads.ShowList
        },
        hasError = downloads.HasError,
        errorMessage = downloads.ErrorMessage,
        isDownloading = downloads.IsDownloading,
        downloadInParallel = downloads.DownloadInParallel,
        canChangeMode = !downloads.IsDownloading,
        statusMessage = downloads.StatusMessage,
        selectedCount = downloads.SelectedCount,
        selectedCountText = downloads.SelectedCountText,
        canDownload = downloads.DownloadCommand.CanExecute(null),
        canCancel = downloads.CancelCommand.CanExecute(null),
        canSelectAll = downloads.SelectAllCommand.CanExecute(null),
        canClear = downloads.ClearSelectionCommand.CanExecute(null),
        sessions = downloads.Sessions.Select((row, index) => new
        {
            index,
            order = row.Order,
            course = row.Course,
            @class = row.Class,
            term = row.Term,
            tutor = row.Tutor,
            date = row.Date,
            selected = row.IsSelected,
            status = row.Status.ToString().ToLowerInvariant(),
            statusText = row.StatusText,
            progress = Math.Round(row.Progress, 3),
            note = row.Note
        }).ToList()
    };

    private static List<object> OptionList(IEnumerable<SvuOption> options) =>
        options.Select(option => (object)new { value = option.Value, label = option.Label }).ToList();

    private object BuildState(MainViewModel vm)
    {
        return new
        {
            type = "state",
            sessionRevision = _sessionsRevision,
            language = new
            {
                code = Loc.Instance.Language,
                dir = Loc.Instance.IsRightToLeft ? "rtl" : "ltr",
                font = string.Equals(Loc.Instance.Language, "ar", StringComparison.OrdinalIgnoreCase)
                    ? "Cairo"
                    : "Rubik"
            },
            theme = BuildTheme(vm),
            appearance = BuildAppearance(vm),
            strings = BuildStrings(),
            header = new
            {
                eyebrow = Loc.Instance["app.subtitle"],
                title = Loc.Instance["app.title"],
                welcome = vm.WelcomeText,
                dataSource = vm.DataSourceText,
                role = vm.RoleText,
                term = vm.CurrentTerm,
                termLabel = vm.TermLabel,
                hasTerm = vm.HasTerm,
                count = vm.SessionCount,
                showCount = vm.ShowSessionCount
            },
            body = new
            {
                loading = vm.IsLoadingSessions,
                empty = vm.ShowSessionsEmpty,
                error = vm.HasSessionLoadError,
                noMatches = vm.ShowNoMatches,
                busy = vm.IsBusy,
                active = vm.IsSessionActive,
                statusMessage = vm.StatusMessage,
                mic = vm.MicStatusText,
                timeFormat = vm.TimeFormatText
            },
            filters = new
            {
                tutor = vm.TutorFilter,
                classFilter = vm.ClassFilter,
                program = vm.SelectedProgram?.Id,
                course = vm.SelectedCourse?.Id,
                mode = vm.SelectedMode?.Option,
                usesProgramCourse = vm.UsesFilters,
                courseEnabled = vm.IsCourseEnabled,
                programs = vm.Programs.Select(p => new { id = p.Id, label = p.Display }).ToList(),
                courses = vm.Courses.Select(c => new { id = c.Id, label = c.Display }).ToList(),
                modes = vm.Modes.Select(m => new { option = m.Option, label = m.Display }).ToList()
            },
            sort = new
            {
                column = vm.SortColumn,
                descending = vm.SortDescending
            }
        };
    }

    private static object BuildAppearance(MainViewModel vm)
    {
        var accent = vm.SelectedAccent;

        return new
        {
            dark = vm.IsDarkTheme,
            accentHex = ThemeManager.Hex(accent.Primary),
            ink = vm.InkStyle,
            preset = vm.SelectedPresetOption?.Key,
            presets = vm.PresetOptions.Select(p =>
            {
                var palette = ThemeManager.GetPalette(p.Preset.Theme);
                var accent = ThemeManager.Resolve(p.Preset.Accent);

                return new
                {
                    key = p.Key,
                    label = p.Label,
                    themeLabel = p.ThemeLabel,
                    dark = p.Preset.Theme == Theming.AppTheme.Dark,
                    swatch = BrushCss(p.Swatch),
                    accent = ThemeManager.Hex(accent.Primary),
                    surfaceAlt = CssColor(palette.SurfaceAlt),
                    text = CssColor(palette.Text),
                    muted = CssColor(palette.Muted),
                    border = CssColor(palette.Border),
                    borderStrong = CssColor(palette.BorderStrong)
                };
            }).ToList()
        };
    }

    private static object BuildTheme(MainViewModel vm)
    {
        var dark = vm.IsDarkTheme;
        var theme = dark ? AppTheme.Dark : AppTheme.Light;
        var accent = vm.SelectedAccent;
        var headerStops = ThemeManager.ResolveHeaderStops(accent, vm.HeaderStyle);

        return new
        {
            dark,
            bg = Brush("Brush.Background"),
            surface = Brush("Brush.Surface"),
            surfaceAlt = Brush("Brush.SurfaceAlt"),
            border = Brush("Brush.Border"),
            borderStrong = Brush("Brush.BorderStrong"),
            text = Brush("Brush.Text"),
            muted = Brush("Brush.Muted"),
            primary = CssColor(accent.Primary),
            primaryHover = CssColor(accent.Hover),
            primaryPressed = CssColor(accent.Pressed),
            onPrimary = CssColor(ThemeManager.ResolveOnPrimary(accent, vm.InkStyle)),
            primarySoft = CssColor(ThemeManager.PrimarySoft(accent, theme)),
            selection = CssColor(ThemeManager.Selection(accent, theme)),
            danger = Brush("Brush.Danger"),
            accentEdge = CssColor(ThemeManager.AccentEdge(accent, theme)),
            headerA = CssColor(headerStops[0]),
            headerB = CssColor(headerStops[1]),
            headerC = CssColor(headerStops[2]),
            okBg = Brush("Brush.StatusAvailableBg"),
            okFg = Brush("Brush.StatusAvailableFg"),
            progressBg = Brush("Brush.StatusProgressBg"),
            progressFg = Brush("Brush.StatusProgressFg"),
            doneBg = Brush("Brush.StatusFinishedBg"),
            doneFg = Brush("Brush.StatusFinishedFg"),
            cancelBg = Brush("Brush.StatusCancelledBg"),
            cancelFg = Brush("Brush.StatusCancelledFg")
        };
    }

    private static string BrushCss(Brush? brush)
    {
        if (brush is SolidColorBrush solid)
        {
            return CssColor(solid.Color);
        }

        if (brush is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2)
        {
            var stops = gradient.GradientStops;
            var middle = stops[stops.Count / 2];
            return "linear-gradient(135deg, "
                + CssColor(stops[0].Color) + " 0%, "
                + CssColor(middle.Color) + " 55%, "
                + CssColor(stops[stops.Count - 1].Color) + " 100%)";
        }

        return "transparent";
    }

    private static string CssColor(Color color)
    {
        var alpha = color.A / 255.0;
        return FormattableString.Invariant(
            $"rgba({color.R}, {color.G}, {color.B}, {alpha:0.###})");
    }

    private static string Brush(string key)
    {
        if (Application.Current?.TryFindResource(key) is not SolidColorBrush brush)
        {
            return "transparent";
        }

        return CssColor(brush.Color);
    }

    private static Dictionary<string, string> BuildStrings()
    {
        var keys = new[]
        {
            "app.title", "app.subtitle", "main.title", "main.sessions", "main.program", "main.course",
            "main.options", "main.refresh", "main.refreshing", "main.launch", "main.launching",
            "main.filterTutor", "main.filterClass", "select.search", "select.empty",
            "main.emptySessions", "main.loadingSessions",
            "main.noMatches", "main.loadError", "common.retry", "main.selectSession", "main.about", "main.logout", "main.exit",
            "main.appearance", "main.clockFormat", "main.term", "main.sessionCount",
            "main.style", "main.presets", "main.theme", "main.themeLight", "main.themeDark",
            "main.globalColor", "main.colorHint", "main.labelInk",
            "main.inkAuto", "main.inkBlack", "main.inkWhite", "main.showMore", "main.showLess",
            "about.title", "about.close", "about.body", "about.support",
            "col.session", "col.course", "col.class", "col.server", "col.start", "col.tutor",
            "diag.button",
            "download.title", "download.subtitle", "download.saveTo", "download.changeFolder",
            "download.select", "download.term", "download.program", "download.course",
            "download.tutor", "download.class", "download.colTerm", "download.colDate",
            "download.loading", "download.empty", "download.hint", "download.selectAll",
            "download.clear", "download.download", "download.cancel", "download.cancelling",
            "download.modeQueue", "download.modeParallel",
            "download.status.queued", "download.status.downloading", "download.status.done",
            "download.status.failed", "download.status.cancelled",
            "download.retrying", "download.error", "download.error.noLink", "download.error.file",
            "download.done", "download.doneSome", "download.allClasses", "download.fanoutClass"
        };

        var strings = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            strings[key] = Loc.Instance[key];
        }

        return strings;
    }

    private static string Key(SessionInfo session) =>
        $"{session.SessionId}:{session.SessionName}:{session.ClassId}";

    private static string StatusKey(SessionInfo session) =>
        StatusCode.FromRaw(session.Status) switch
        {
            SessionStatus.Available => "available",
            SessionStatus.InProgress => "progress",
            SessionStatus.Finished => "finished",
            SessionStatus.Cancelled => "cancelled",
            _ => "unknown"
        };

    private static string FormatDate(DateTime? date) =>
        date is null
            ? string.Empty
            : date.Value.ToString(
                DisplayOptions.Use24HourClock ? "ddd dd MMM, HH:mm" : "ddd dd MMM, hh:mm tt",
                Loc.Instance.Culture);

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var action = Text(root, "action");

            switch (action)
            {
                case "ready":
                    _webReady = true;
                    _lastSentSessionsRevision = -1;
                    PushState();
                    _pageReady.TrySetResult();
                    break;
                case "rendered":
                    Reveal();
                    break;
                case "resize":
                    WindowResizer.Start(this, Text(root, "edge"));
                    break;
                case "select":
                    SelectSession(vm, Text(root, "key"));
                    break;
                case "launch":
                    if (vm.LaunchCommand.CanExecute(null))
                    {
                        vm.LaunchCommand.Execute(null);
                    }

                    break;
                case "refresh":
                    if (vm.RefreshCommand.CanExecute(null))
                    {
                        vm.RefreshCommand.Execute(null);
                    }

                    break;
                case "sort":
                    CycleSort(vm, Text(root, "column"));
                    break;
                case "filter":
                    vm.TutorFilter = Text(root, "tutor") ?? string.Empty;
                    vm.ClassFilter = Text(root, "classFilter") ?? string.Empty;
                    break;
                case "program":
                    vm.SelectedProgram = vm.Programs.FirstOrDefault(p => p.Id == Number(root, "id"));
                    break;
                case "course":
                    vm.SelectedCourse = vm.Courses.FirstOrDefault(c => c.Id == Number(root, "id"));
                    break;
                case "mode":
                    vm.SelectedMode = vm.Modes.FirstOrDefault(m => m.Option == Number(root, "option"));
                    break;
                case "language":
                    Loc.Instance.Language = Text(root, "code") == "ar" ? "ar" : "en";
                    break;
                case "signOut":
                    vm.SignOutCommand.Execute(null);
                    break;
                case "exit":
                    vm.ExitCommand.Execute(null);
                    break;
                case "diagnostics":
                    vm.DiagnosticsCommand.Execute(null);
                    break;
                case "toggleTime":
                    vm.ToggleTimeCommand.Execute(null);
                    break;
                case "appearanceTheme":
                    vm.IsDarkTheme = Flag(root, "value");
                    break;
                case "appearanceColor":
                    var colorHex = Text(root, "value");
                    // Older cached pages do not send "final"; treat those as a
                    // one-shot commit so the picker still works after an update.
                    var finalColor = !root.TryGetProperty("final", out var finalFlag)
                        || finalFlag.ValueKind == JsonValueKind.True;
                    if (!finalColor)
                    {
                        if (vm.SetCustomAccent(colorHex, preview: true))
                        {
                            PushTheme(vm);
                        }

                        break;
                    }

                    vm.SetCustomAccent(colorHex);
                    _pushTimer.Stop();
                    PushState();
                    break;
                case "appearanceInk":
                    vm.InkStyle = Text(root, "value") ?? "auto";
                    break;
                case "appearancePreset":
                    var preset = ThemeManager.Presets.FirstOrDefault(
                        p => string.Equals(p.Key, Text(root, "key"), StringComparison.OrdinalIgnoreCase));
                    if (preset is not null)
                    {
                        vm.ApplyPreset(preset);
                    }

                    break;
                case "downloadsOpen":
                    EnsureDownloads(vm);
                    _downloadsOpen = true;
                    _downloadsDirty = true;
                    _lastSentDownloadsRevision = -1;
                    PushState();
                    break;
                case "downloadsClose":
                    _downloadsOpen = false;
                    break;
                case "downloadFolder":
                    PickDownloadFolder();
                    break;
                case "downloadTerm":
                    SetDownloadSelection("term", Text(root, "value"));
                    break;
                case "downloadProgram":
                    SetDownloadSelection("program", Text(root, "value"));
                    break;
                case "downloadCourse":
                    SetDownloadSelection("course", Text(root, "value"));
                    break;
                case "downloadTutor":
                    SetDownloadSelection("tutor", Text(root, "value"));
                    break;
                case "downloadClass":
                    SetDownloadSelection("class", Text(root, "value"));
                    break;
                case "download-retry":
                    if (_downloadsVm is { } retryDownloads)
                    {
                        await retryDownloads.RetryAsync();
                    }

                    break;
                case "downloadSelect":
                    if (_downloadsVm is { } selectedDownloads
                        && Number(root, "index") is var index
                        && index >= 0
                        && index < selectedDownloads.Sessions.Count)
                    {
                        selectedDownloads.Sessions[index].IsSelected = Flag(root, "value");
                        _downloadsDirty = true;
                        SchedulePush();
                    }

                    break;
                case "downloadSelectAll":
                    if (_downloadsVm?.SelectAllCommand.CanExecute(null) == true)
                    {
                        _downloadsVm.SelectAllCommand.Execute(null);
                    }

                    break;
                case "downloadClear":
                    if (_downloadsVm?.ClearSelectionCommand.CanExecute(null) == true)
                    {
                        _downloadsVm.ClearSelectionCommand.Execute(null);
                    }

                    break;
                case "downloadMode":
                    if (_downloadsVm is { IsDownloading: false } modeDownloads)
                    {
                        modeDownloads.DownloadInParallel =
                            string.Equals(Text(root, "value"), "parallel", StringComparison.OrdinalIgnoreCase);
                        _downloadsDirty = true;
                        SchedulePush();
                    }

                    break;
                case "downloadStart":
                    if (_downloadsVm?.DownloadCommand.CanExecute(null) == true)
                    {
                        _downloadsVm.DownloadCommand.Execute(null);
                    }

                    break;
                case "downloadCancel":
                    if (_downloadsVm?.CancelCommand.CanExecute(null) == true)
                    {
                        _downloadsVm.CancelCommand.Execute(null);
                    }

                    break;
                case "openRepo":
                    OpenRepository();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"studio: bridge message failed: {ex.Message}");
        }
    }

    private void SelectSession(MainViewModel vm, string? key)
    {
        vm.SelectedSession = key is null
            ? null
            : vm.Sessions.FirstOrDefault(s => Key(s) == key);
    }

    private void CycleSort(MainViewModel vm, string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        if (!string.Equals(column, _sortColumn, StringComparison.Ordinal))
        {
            _sortColumn = column;
            _sortStage = 0;
        }

        _sortStage = _sortStage % 3 + 1;

        vm.ApplySort(
            _sortStage == 3 ? null : column,
            _sortStage == 2 ? ListSortDirection.Descending : ListSortDirection.Ascending);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : int.MinValue;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void EnsureDownloads(MainViewModel vm)
    {
        if (_downloadsVm is not null)
        {
            return;
        }

        _downloadsVm = new DownloadsViewModel(vm.Settings);
        _downloadsVm.PropertyChanged += OnDownloadChanged;
        _downloadsVm.Sessions.CollectionChanged += OnDownloadSessionsChanged;
        _downloadsVm.Terms.CollectionChanged += OnDownloadOptionsChanged;
        _downloadsVm.Programs.CollectionChanged += OnDownloadOptionsChanged;
        _downloadsVm.Courses.CollectionChanged += OnDownloadOptionsChanged;
        _downloadsVm.Tutors.CollectionChanged += OnDownloadOptionsChanged;
        _downloadsVm.Classes.CollectionChanged += OnDownloadOptionsChanged;
        SyncDownloadRows();
        _downloadsDirty = true;
    }

    private void SyncDownloadRows()
    {
        if (_downloadsVm is null)
        {
            return;
        }

        var current = new HashSet<DownloadRow>(_downloadsVm.Sessions);
        foreach (var row in _hookedDownloadRows.ToList())
        {
            if (current.Contains(row))
            {
                continue;
            }

            row.PropertyChanged -= OnDownloadRowChanged;
            _hookedDownloadRows.Remove(row);
        }

        foreach (var row in current)
        {
            if (!_hookedDownloadRows.Add(row))
            {
                continue;
            }

            row.PropertyChanged += OnDownloadRowChanged;
        }
    }

    private void SetDownloadSelection(string level, string? value)
    {
        if (_downloadsVm is not { } downloads)
        {
            return;
        }

        SvuOption? Find(IEnumerable<SvuOption> options) =>
            string.IsNullOrEmpty(value)
                ? null
                : options.FirstOrDefault(option => option.Value == value);

        switch (level)
        {
            case "term":
                downloads.SelectedTerm = Find(downloads.Terms);
                break;
            case "program":
                downloads.SelectedProgram = Find(downloads.Programs);
                break;
            case "course":
                downloads.SelectedCourse = Find(downloads.Courses);
                break;
            case "tutor":
                downloads.SelectedTutor = Find(downloads.Tutors);
                break;
            case "class":
                downloads.SelectedClass = Find(downloads.Classes);
                break;
        }

        _downloadsDirty = true;
        SchedulePush();
    }

    private void PickDownloadFolder()
    {
        if (_downloadsVm is not { } downloads)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = Loc.Instance["download.changeFolder"],
            InitialDirectory = downloads.SaveDirectory
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            downloads.SetSaveDirectory(dialog.FolderName);
            _downloadsDirty = true;
            SchedulePush();
        }
    }

    private void OnDownloadChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_downloadsOpen)
        {
            return;
        }

        _downloadsDirty = true;
        SchedulePush();
    }

    private void OnDownloadRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_downloadsOpen)
        {
            return;
        }

        _downloadsDirty = true;
        SchedulePush();
    }

    private void OnDownloadSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncDownloadRows();
        if (!_downloadsOpen)
        {
            return;
        }

        _downloadsDirty = true;
        SchedulePush();
    }

    private void OnDownloadOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_downloadsOpen)
        {
            return;
        }

        _downloadsDirty = true;
        SchedulePush();
    }

    private void DisposeDownloads()
    {
        if (_downloadsVm is null)
        {
            return;
        }

        _downloadsVm.PropertyChanged -= OnDownloadChanged;
        _downloadsVm.Sessions.CollectionChanged -= OnDownloadSessionsChanged;
        _downloadsVm.Terms.CollectionChanged -= OnDownloadOptionsChanged;
        _downloadsVm.Programs.CollectionChanged -= OnDownloadOptionsChanged;
        _downloadsVm.Courses.CollectionChanged -= OnDownloadOptionsChanged;
        _downloadsVm.Tutors.CollectionChanged -= OnDownloadOptionsChanged;
        _downloadsVm.Classes.CollectionChanged -= OnDownloadOptionsChanged;

        foreach (var row in _hookedDownloadRows)
        {
            row.PropertyChanged -= OnDownloadRowChanged;
        }

        _hookedDownloadRows.Clear();
        _downloadsVm.Dispose();
        _downloadsVm = null;
    }

    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/qusielh/svu-sessions-launcher",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Open repository failed: {ex.Message}");
        }
    }
}






