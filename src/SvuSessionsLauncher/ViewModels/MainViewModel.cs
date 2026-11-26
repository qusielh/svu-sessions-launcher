using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Models;
using SvuSessionsLauncher.Services;

namespace SvuSessionsLauncher.ViewModels;

/// <summary>A header "radiant" option shown in the appearance popup.</summary>
public sealed record HeaderOption(string Key, string Label, Brush Swatch, bool IsAuto);

/// <summary>A whole-look preset tile shown at the top of the appearance popup.</summary>
public sealed record PresetOption(string Key, string Label, string ThemeLabel, Brush Swatch, Theming.AppearancePreset Preset);

/// <summary>Backs the main session-browser window.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IWebDemoService _service;
    private readonly ISessionLauncher _launcher;
    private readonly AppSettings _settings;
    private readonly string _user;

    private ProgramInfo? _selectedProgram;
    private CourseInfo? _selectedCourse;
    private SessionInfo? _selectedSession;
    private SessionMode? _selectedMode;
    private bool _isBusy;
    private bool _isLoadingSessions;
    private bool _sessionActive;
    private bool _restoringFilters;
    private int _sessionLoadVersion;
    private CancellationTokenSource? _sessionLoadCts;
    private int _courseLoadVersion;
    private CancellationTokenSource? _courseLoadCts;
    private string? _statusKey;
    private object? _statusArg;
    private bool? _micMuted;

    private IReadOnlyList<HeaderOption> _headerOptions = [];
    private IReadOnlyList<PresetOption> _presetOptions = [];

    public MainViewModel(IWebDemoService service, ISessionLauncher launcher, AppSettings settings, string user)
    {
        _service = service;
        _launcher = launcher;
        _settings = settings;
        _user = user;

        RefreshCommand = new RelayCommand(RefreshAsync, () => !IsBusy);
        LaunchCommand = new RelayCommand(
            LaunchAsync, () => SelectedSession?.CanLaunch == true && !IsBusy && !IsSessionActive);
        SignOutCommand = new RelayCommand(() => SignOut?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        DiagnosticsCommand = new RelayCommand(DiagnosticsAsync, () => !IsBusy);
        ToggleTimeCommand = new RelayCommand(ToggleTimeFormat);

        _selectedMode = Modes[0];
        BuildPresetOptions();
        BuildHeaderOptions();
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = MatchesFilters;

        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>True while either filter holds a term.</summary>
    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(_tutorFilter) || !string.IsNullOrWhiteSpace(_classFilter);

    private bool MatchesFilters(object item)
    {
        if (item is not SessionInfo session)
        {
            return false;
        }

        var tutor = _tutorFilter.Trim();
        if (tutor.Length > 0 && !Contains(session.TutorName, tutor))
        {
            return false;
        }

        var cls = _classFilter.Trim();
        if (cls.Length > 0 && !MatchesClass(session, cls))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Matches the class filter against the session's class token. "C1" and "1"
    /// both mean class 1 (exact); any other text is a substring match on the
    /// "C#" token.
    /// </summary>
    private static bool MatchesClass(SessionInfo session, string filter)
    {
        var number = filter.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? filter[1..] : filter;
        if (number.Length > 0 && number.All(char.IsDigit))
        {
            return string.Equals(session.ClassNumber, number, StringComparison.OrdinalIgnoreCase);
        }

        return Contains(session.ClassToken, filter);
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RefreshFilters()
    {
        SessionsView.Refresh();
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    public event EventHandler? SignOut;
    public event EventHandler? ExitRequested;

    /// <summary>The shared, persisted application settings instance.</summary>
    public AppSettings Settings => _settings;

    // ----------------------------------------------------------------- appearance

    /// <summary>True while the dark palette is active.</summary>
    public bool IsDarkTheme
    {
        get => Theming.ThemeManager.Parse(_settings.Theme) == Theming.AppTheme.Dark;
        set
        {
            var theme = value ? Theming.AppTheme.Dark : Theming.AppTheme.Light;
            if (Theming.ThemeManager.Parse(_settings.Theme) == theme)
            {
                return;
            }

            _settings.Theme = Theming.ThemeManager.Key(theme);
            _settings.Save();
            Theming.ThemeManager.Apply(
                theme,
                _settings.Accent,
                _settings.Header,
                _settings.Ink,
                _settings.AccentHex);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPresetOption));
        }
    }

    /// <summary>The accent currently applied.</summary>
    public Theming.Accent SelectedAccent
    {
        get => Theming.ThemeManager.Resolve(_settings.Accent, _settings.AccentHex);
        set
        {
            if (value is null || string.Equals(_settings.Accent, value.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Accent = value.Key;
            if (!string.Equals(value.Key, Theming.ThemeManager.CustomAccent, StringComparison.OrdinalIgnoreCase))
            {
                _settings.AccentHex = string.Empty;
            }

            _settings.Save();
            Theming.ThemeManager.Apply(
                Theming.ThemeManager.Parse(_settings.Theme),
                value.Key,
                _settings.Header,
                _settings.Ink,
                _settings.AccentHex);
            BuildHeaderOptions();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HeaderOptions));
            OnPropertyChanged(nameof(SelectedHeader));
            OnPropertyChanged(nameof(SelectedPresetOption));
        }
    }

    /// <summary>Forced ink on accent surfaces: "auto", "black", or "white".</summary>
    public string InkStyle
    {
        get => _settings.Ink?.ToLowerInvariant() switch
        {
            "black" => "black",
            "white" => "white",
            _ => "auto",
        };
        set
        {
            var key = value?.ToLowerInvariant() switch
            {
                "black" => "black",
                "white" => "white",
                _ => "auto",
            };
            if (string.Equals(InkStyle, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Ink = key;
            _settings.Save();
            Theming.ThemeManager.Apply(
                Theming.ThemeManager.Parse(_settings.Theme),
                _settings.Accent,
                _settings.Header,
                key,
                _settings.AccentHex);
            OnPropertyChanged();
        }
    }

    /// <summary>Applies a user-picked global colour and makes the header follow it.</summary>
    public void SetCustomAccent(string? hex) => SetCustomAccent(hex, preview: false);

    /// <summary>
    /// Updates a custom accent. Preview updates stay in memory so the colour can
    /// track the picker without saving or repainting the WPF palette every frame;
    /// the commit applies, saves and notifies once the picker closes.
    /// </summary>
    public bool SetCustomAccent(string? hex, bool preview)
    {
        if (!Theming.ThemeManager.TryParseColor(hex, out var color))
        {
            return false;
        }

        var normalized = Theming.ThemeManager.Hex(color);
        var unchanged = string.Equals(_settings.Accent, Theming.ThemeManager.CustomAccent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_settings.AccentHex, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_settings.Header, Theming.ThemeManager.AutoHeader, StringComparison.OrdinalIgnoreCase);

        if (preview)
        {
            if (unchanged)
            {
                return false;
            }

            _settings.Accent = Theming.ThemeManager.CustomAccent;
            _settings.AccentHex = normalized;
            _settings.Header = Theming.ThemeManager.AutoHeader;
            Theming.ThemeManager.PreviewAccent(
                _settings.Accent,
                _settings.Header,
                _settings.Ink,
                _settings.AccentHex);
            return true;
        }

        _settings.Accent = Theming.ThemeManager.CustomAccent;
        _settings.AccentHex = normalized;
        _settings.Header = Theming.ThemeManager.AutoHeader;
        _settings.Save();

        Theming.ThemeManager.Apply(
            Theming.ThemeManager.Parse(_settings.Theme),
            _settings.Accent,
            _settings.Header,
            _settings.Ink,
            _settings.AccentHex);
        BuildHeaderOptions();

        OnPropertyChanged(nameof(SelectedAccent));
        OnPropertyChanged(nameof(HeaderStyle));
        OnPropertyChanged(nameof(SelectedHeader));
        OnPropertyChanged(nameof(HeaderOptions));
        OnPropertyChanged(nameof(SelectedPresetOption));
        return !unchanged;
    }

    /// <summary>Whole-look preset tiles shown at the top of the appearance popup.</summary>
    public IReadOnlyList<PresetOption> PresetOptions => _presetOptions;

    /// <summary>The preset tile matching the current theme+accent+header, if any.</summary>
    public PresetOption? SelectedPresetOption =>
        _presetOptions.FirstOrDefault(o =>
            o.Preset.Theme == Theming.ThemeManager.Parse(_settings.Theme)
            && string.Equals(o.Preset.Accent, _settings.Accent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Preset.Header, _settings.Header, StringComparison.OrdinalIgnoreCase));

    /// <summary>Header "radiant" options (Auto + named colours).</summary>
    public IReadOnlyList<HeaderOption> HeaderOptions => _headerOptions;

    /// <summary>The header radiant currently applied.</summary>
    public HeaderOption SelectedHeader =>
        _headerOptions.FirstOrDefault(h => string.Equals(h.Key, _settings.Header, StringComparison.OrdinalIgnoreCase))
        ?? _headerOptions[0];

    /// <summary>Persisted header radiant key.</summary>
    public string HeaderStyle
    {
        get => _settings.Header;
        set
        {
            var key = string.IsNullOrWhiteSpace(value) ? Theming.ThemeManager.AutoHeader : value;
            if (string.Equals(_settings.Header, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Header = key;
            _settings.Save();
            Theming.ThemeManager.Apply(
                Theming.ThemeManager.Parse(_settings.Theme),
                _settings.Accent,
                key,
                _settings.Ink,
                _settings.AccentHex);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedHeader));
            OnPropertyChanged(nameof(SelectedPresetOption));
        }
    }

    /// <summary>Applies a whole-look preset (theme + accent + header) in one step.</summary>
    public void ApplyPreset(Theming.AppearancePreset preset)
    {
        _settings.Theme = Theming.ThemeManager.Key(preset.Theme);
        _settings.Accent = preset.Accent;
        _settings.AccentHex = string.Empty;
        _settings.Header = preset.Header;
        _settings.Save();

        Theming.ThemeManager.Apply(
            preset.Theme,
            preset.Accent,
            preset.Header,
            _settings.Ink);
        BuildHeaderOptions();

        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(SelectedAccent));
        OnPropertyChanged(nameof(HeaderStyle));
        OnPropertyChanged(nameof(SelectedHeader));
        OnPropertyChanged(nameof(HeaderOptions));
        OnPropertyChanged(nameof(SelectedPresetOption));
    }

    /// <summary>Rebuilds the header swatches ("Auto" tracks the accent).</summary>
    private void BuildHeaderOptions()
    {
        var accent = Theming.ThemeManager.Resolve(_settings.Accent, _settings.AccentHex);
        var list = new List<HeaderOption>
        {
            new(
                Theming.ThemeManager.AutoHeader,
                "Auto",
                Theming.ThemeManager.Gradient(accent.Hover, accent.Primary, accent.Pressed),
                IsAuto: true),
        };

        foreach (var choice in Theming.ThemeManager.HeaderChoices)
        {
            var stops = Theming.ThemeManager.ResolveHeaderStops((string?)null, choice.Key);
            list.Add(new HeaderOption(
                choice.Key,
                choice.Label,
                Theming.ThemeManager.Gradient(stops[0], stops[1], stops[2]),
                IsAuto: false));
        }

        _headerOptions = list;
    }

    /// <summary>Builds the preset tiles once (labels + header-gradient swatches).</summary>
    private void BuildPresetOptions()
    {
        var list = new List<PresetOption>();
        foreach (var preset in Theming.ThemeManager.Presets)
        {
            var stops = Theming.ThemeManager.ResolveHeaderStops(preset.Accent, preset.Header);
            var themeLabel = preset.Theme == Theming.AppTheme.Dark
                ? Loc.Instance["main.themeDark"]
                : Loc.Instance["main.themeLight"];
            list.Add(new PresetOption(
                preset.Key,
                preset.Label,
                themeLabel,
                Theming.ThemeManager.Gradient(stops[0], stops[1], stops[2]),
                preset));
        }

        _presetOptions = list;
    }

    /// <summary>Raised when a session engine has started and its UI should open.</summary>
    public event EventHandler<ISessionConnection>? SessionStarted;

    public ObservableCollection<ProgramInfo> Programs { get; } = [];
    public ObservableCollection<CourseInfo> Courses { get; } = [];
    public RangeObservableCollection<SessionInfo> Sessions { get; } = [];

    /// <summary>
    /// Filtered projection of <see cref="Sessions"/> bound by the list. The raw
    /// collection stays intact (counts, notifications) while the search box
    /// narrows what is shown.
    /// </summary>
    public ICollectionView SessionsView { get; }

    private string _tutorFilter = string.Empty;
    private string _classFilter = string.Empty;

    /// <summary>
    /// Tutor filter. Matches a partial tutor name (case-insensitive substring),
    /// so "khal" finds "Dr. Khaled".
    /// </summary>
    public string TutorFilter
    {
        get => _tutorFilter;
        set
        {
            if (SetProperty(ref _tutorFilter, value))
            {
                RefreshFilters();
            }
        }
    }

    /// <summary>
    /// Class filter. Matches the class token embedded in the session name
    /// (e.g. "C1"/"1" finds class 1). Careful: this filters by class, never by
    /// the session number (the trailing "S#").
    /// </summary>
    public string ClassFilter
    {
        get => _classFilter;
        set
        {
            if (SetProperty(ref _classFilter, value))
            {
                RefreshFilters();
            }
        }
    }

    /// <summary>
    /// Fetch modes matching the original launcher's student "Options" list.
    /// The numeric <see cref="SessionMode.Option"/> is exactly what the old
    /// launcher sent: 1 = own classes, 2 = all classes of my courses,
    /// 3 = by programme and course. Only mode 3 uses the programme/course
    /// dropdowns; modes 1 and 2 are role-based and ignore them.
    /// </summary>
    public ObservableCollection<SessionMode> Modes { get; } =
    [
        new(Option: 1, UseProgramCourse: false, LocKey: "opt.myClasses"),
        new(Option: 2, UseProgramCourse: false, LocKey: "opt.myCourses"),
        new(Option: 3, UseProgramCourse: true, LocKey: "opt.byProgram")
    ];

    /// <summary>What to show in the sessions list. Defaults to "my classes only".</summary>
    public SessionMode? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(UsesFilters));
                OnPropertyChanged(nameof(IsCourseEnabled));
                if (!_restoringFilters)
                {
                    SaveFilterPicks();
                    _ = LoadSessionsAsync();
                }
            }
        }
    }

    /// <summary>True when the selected mode actually filters by programme/course.</summary>
    public bool UsesFilters => SelectedMode?.UseProgramCourse == true;

    /// <summary>
    /// The course dropdown is only usable once a real (non-"all") programme is
    /// chosen. With "All programmes" selected there is nothing to narrow by, so
    /// the course box is greyed out.
    /// </summary>
    public bool IsCourseEnabled => UsesFilters && SelectedProgram is { IsAll: false };

    public RelayCommand RefreshCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand DiagnosticsCommand { get; }
    public RelayCommand ToggleTimeCommand { get; }

    public string User => _user;

    /// <summary>Localised label for the data source next to the header badges.</summary>
    public string DataSourceText => Loc.Instance["dataSource.live"];

    public string WelcomeText => Loc.Instance.Format("main.welcome", _user);

    public string RoleText => Loc.Instance["role.student"];

    public ProgramInfo? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetProperty(ref _selectedProgram, value))
            {
                OnPropertyChanged(nameof(IsCourseEnabled));
                if (!_restoringFilters)
                {
                    SaveFilterPicks();
                    _ = LoadCoursesAsync();
                }
            }
        }
    }

    public CourseInfo? SelectedCourse
    {
        get => _selectedCourse;
        set
        {
            if (SetProperty(ref _selectedCourse, value))
            {
                if (!_restoringFilters)
                {
                    SaveFilterPicks();
                    _ = LoadSessionsAsync();
                }
            }
        }
    }

    public SessionInfo? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                OnPropertyChanged(nameof(HasSelectedSession));
                LaunchCommand.RaiseCanExecuteChanged();
                UpdateSessionHint();
            }
        }
    }

    public bool HasSelectedSession => SelectedSession is not null;

    /// <summary>
    /// Selection-aware status-bar copy. Picking a session whose slot is already
    /// over tells the user they can still join it (matching the original
    /// launcher); switching back to a live session restores the usual count.
    /// </summary>
    private void UpdateSessionHint()
    {
        var isHint = _statusKey is "main.endedJoinable" or "main.cancelledJoinable";
        if (_selectedSession is null)
        {
            if (isHint)
            {
                SetStatus(null);
            }

            return;
        }

        if (_selectedSession.HasEnded)
        {
            SetStatus(StatusCode.FromRaw(_selectedSession.Status) is SessionStatus.Cancelled
                ? "main.cancelledJoinable"
                : "main.endedJoinable");
        }
        else if (isHint)
        {
            SetStatus(Sessions.Count == 0 ? null : "main.sessionCount", Sessions.Count);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RefreshCommand.RaiseCanExecuteChanged();
                LaunchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// True while the user is inside a live session. Until the session (the chat
    /// window) is closed, joining is locked so two sessions can never run at
    /// once. Set by the host as the session window opens and closes.
    /// </summary>
    public bool IsSessionActive
    {
        get => _sessionActive;
        set
        {
            if (SetProperty(ref _sessionActive, value))
            {
                LaunchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True while the sessions list is being fetched. Drives the in-list
    /// spinner so the empty message is never shown mid-request.
    /// </summary>
    public bool IsLoadingSessions
    {
        get => _isLoadingSessions;
        private set
        {
            if (SetProperty(ref _isLoadingSessions, value))
            {
                OnPropertyChanged(nameof(ShowSessionsEmpty));
                OnPropertyChanged(nameof(ShowNoMatches));
                OnPropertyChanged(nameof(ShowSessionCount));
            }
        }
    }

    public string? StatusMessage =>
        _statusKey is null
            ? null
            : _statusArg is null
                ? Loc.Instance[_statusKey]
                : Loc.Instance.Format(_statusKey, _statusArg);

    private void SetStatus(string? key, object? arg = null)
    {
        _statusKey = key;
        _statusArg = arg;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasSessionLoadError));
        OnPropertyChanged(nameof(ShowSessionsEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    /// <summary>
    /// Maps a failed service call to the honest status key: a dropped
    /// connection is not a server fault, so the status bar says so.
    /// </summary>
    private static string FailureStatus(WebDemoServiceException ex) =>
        string.Equals(ex.Message, "login.error.network", StringComparison.Ordinal)
            ? "login.error.network"
            : "service.error";

    /// <summary>
    /// Read-only microphone state shown in the status bar. The microphone is
    /// always forced off; this tells the user whether that succeeded.
    /// </summary>
    public string? MicStatusText => _micMuted switch
    {
        true => Loc.Instance["mic.muted"],
        false => Loc.Instance["mic.on"],
        _ => Loc.Instance["mic.unknown"]
    };

    /// <summary>Label for the clock-format toggle showing the active format.</summary>
    public string TimeFormatText => DisplayOptions.Use24HourClock ? "24h" : "12h";

    public int SessionCount => Sessions.Count;

    private bool _sessionsLoaded;

    /// <summary>
    /// Shows the count chip only after a fetch has finished, so the user never
    /// sees a misleading "0" while the list is still loading.
    /// </summary>
    public bool ShowSessionCount => _sessionsLoaded && !IsLoadingSessions;

    /// <summary>
    /// True when the last fetch failed, so the list can show an error with a
    /// Retry action instead of the misleading "no sessions" empty state.
    /// </summary>
    public bool HasSessionLoadError =>
        Sessions.Count == 0
        && (_statusKey == "service.error" || _statusKey == "login.error.network");

    /// <summary>Shows the "no sessions" hint only once a fetch has finished empty.</summary>
    public bool ShowSessionsEmpty => !IsLoadingSessions && !HasSessionLoadError && Sessions.Count == 0;

    /// <summary>Shows the "no matches" hint when a search hides every row.</summary>
    public bool ShowNoMatches =>
        !IsLoadingSessions && !HasSessionLoadError && Sessions.Count > 0 && HasFilters && SessionsView.IsEmpty;

    private void RefreshSessionDerivedState()
    {
        OnPropertyChanged(nameof(SessionCount));
        OnPropertyChanged(nameof(HasSessionLoadError));
        OnPropertyChanged(nameof(ShowSessionsEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
        UpdateCurrentTerm();
    }

    /// <summary>
    /// Remembers the mode/programme/course picks so the next sign-in restores the
    /// same view. Saved on every change; the ids survive across runs.
    /// </summary>
    private void SaveFilterPicks()
    {
        if (_restoringFilters)
        {
            return;
        }

        _settings.MainModeOption = SelectedMode?.Option ?? 1;
        _settings.MainProgramId = SelectedProgram?.Id ?? 0;
        _settings.MainCourseId = SelectedCourse?.Id ?? 0;
        _settings.Save();
    }

    private string _currentTerm = string.Empty;

    /// <summary>The term token most sessions share (e.g. "F25"); empty when unknown.</summary>
    public string CurrentTerm => _currentTerm;

    /// <summary>True when a term token could be derived and should be shown.</summary>
    public bool HasTerm => _currentTerm.Length > 0;

    /// <summary>Localised "Term" label shown before the token in the header badge.</summary>
    public string TermLabel => Loc.Instance["main.term"];

    /// <summary>
    /// Picks the term token shared by most of the loaded sessions (the term is
    /// constant per semester, so this is effectively "the current term").
    /// </summary>
    private void UpdateCurrentTerm()
    {
        var term = Sessions
            .Select(s => s.TermToken)
            .Where(t => t.Length > 0)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault() ?? string.Empty;

        if (!string.Equals(term, _currentTerm, StringComparison.Ordinal))
        {
            _currentTerm = term;
            OnPropertyChanged(nameof(CurrentTerm));
            OnPropertyChanged(nameof(HasTerm));
        }
    }

    /// <summary>
    /// Applies (or clears) the list sort. Only one column sorts at a time:
    /// applying a new column replaces the previous sort entirely. A null/empty
    /// column restores the natural load order. Sorting is a projection: the
    /// underlying collection (and therefore the counts) is untouched.
    /// </summary>
    public void ApplySort(string? column, ListSortDirection direction)
    {
        _sortColumn = string.IsNullOrEmpty(column) ? null : column;
        _sortDescending = _sortColumn is not null && direction == ListSortDirection.Descending;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));

        if (SessionsView is ListCollectionView listView)
        {
            listView.CustomSort = _sortColumn is null
                ? null
                : new SessionComparer(_sortColumn, direction);
        }
    }

    private string? _sortColumn;
    private bool _sortDescending;

    /// <summary>Column currently sorted, or null for the default order.</summary>
    public string? SortColumn => _sortColumn;

    /// <summary>True when the active sort is descending (Z→A / newest first).</summary>
    public bool SortDescending => _sortDescending;

    /// <summary>Releases the static-event subscription when the window closes.</summary>
    public void Dispose()
    {
        _sessionLoadCts?.Cancel();
        _courseLoadCts?.Cancel();
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Loads programmes once the window is ready.</summary>
    public async Task InitializeAsync()
    {
        EnsureMicMuted();

        IsBusy = true;
        SetStatus("main.refreshing");
        try
        {
            var programs = await _service.GetProgramsAsync();
            Programs.Clear();
            Programs.Add(new ProgramInfo(0, string.Empty, IsAll: true));
            foreach (var program in programs)
            {
                // The service occasionally returns a blank programme row; skip it
                // so the filter never shows an empty entry.
                if (string.IsNullOrWhiteSpace(program.Code) && string.IsNullOrWhiteSpace(program.Name))
                {
                    continue;
                }

                Programs.Add(program);
            }

            // Restore the last mode/programme picks, falling back to the original
            // launcher's defaults (All programmes, personal sessions). Suppress
            // the property-change loads here: they would fetch the schedule once
            // from the old picks and again after the courses arrive.
            _restoringFilters = true;
            try
            {
                SelectedMode = Modes.FirstOrDefault(m => m.Option == _settings.MainModeOption) ?? Modes[0];
                SelectedProgram = Programs.FirstOrDefault(p => p.Id == _settings.MainProgramId) ?? Programs[0];
            }
            finally
            {
                _restoringFilters = false;
            }

            await LoadCoursesAsync();
        }
        catch (WebDemoServiceException ex)
        {
            Log.Write($"Initialize failed: {ex}");
            SetStatus(FailureStatus(ex));
        }
        catch (Exception ex)
        {
            Log.Write($"Initialize failed: {ex}");
            SetStatus("login.error.network");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        await LoadSessionsAsync();
    }

    private async Task LoadCoursesAsync()
    {
        var version = ++_courseLoadVersion;
        _courseLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _courseLoadCts = cts;

        // A course change replaces the schedule. Cancel any fetch already in
        // flight so it cannot publish its rows behind the new selection.
        _sessionLoadVersion++;
        _sessionLoadCts?.Cancel();
        _sessionLoadCts = null;

        Courses.Clear();
        Sessions.Clear();
        SelectedSession = null;
        RefreshSessionDerivedState();

        if (SelectedProgram is null)
        {
            SetStatus(null);
            IsLoadingSessions = false;
            return;
        }

        var programId = SelectedProgram.Id;
        IsBusy = true;
        IsLoadingSessions = true;
        try
        {
            Courses.Add(new CourseInfo(0, string.Empty, IsAll: true));
            if (programId != 0)
            {
                var courses = await _service.GetCoursesAsync(programId, cts.Token);
                if (version != _courseLoadVersion)
                {
                    return;
                }

                foreach (var course in courses)
                {
                    Courses.Add(course);
                }
            }

            if (version != _courseLoadVersion)
            {
                return;
            }

            // Set the remembered course without triggering its property setter;
            // the explicit load below is the only schedule fetch for this change.
            _restoringFilters = true;
            try
            {
                SelectedCourse = Courses.FirstOrDefault(c => c.Id == _settings.MainCourseId) ?? Courses[0];
            }
            finally
            {
                _restoringFilters = false;
            }

            await LoadSessionsAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer course selection owns the state now.
        }
        catch (WebDemoServiceException ex)
        {
            if (version == _courseLoadVersion)
            {
                Log.Write($"Load courses failed: {ex}");
                SetStatus(FailureStatus(ex));
                IsLoadingSessions = false;
            }
        }
        catch (Exception ex)
        {
            if (version == _courseLoadVersion)
            {
                Log.Write($"Load courses failed: {ex}");
                SetStatus("login.error.network");
                IsLoadingSessions = false;
            }
        }
        finally
        {
            if (version == _courseLoadVersion)
            {
                _courseLoadCts = null;
                IsBusy = false;
            }
        }
    }

    private async Task LoadSessionsAsync()
    {
        var version = ++_sessionLoadVersion;
        _sessionLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sessionLoadCts = cts;

        IsLoadingSessions = true;
        Sessions.Clear();
        SelectedSession = null;
        RefreshSessionDerivedState();

        var mode = SelectedMode ?? Modes[0];
        IsBusy = true;
        SetStatus("main.loadingSessions");
        try
        {
            // Modes 1 and 2 are role-based; only mode 3 filters by the
            // programme and course chosen in the dropdowns.
            var programId = mode.UseProgramCourse ? SelectedProgram?.Id ?? 0 : 0;
            var courseId = mode.UseProgramCourse ? SelectedCourse?.Id ?? 0 : 0;

            // "All programmes" with option 3 used to return the full schedule; keep
            // it as-is but fall back to option 2 if the first attempt times out so
            // the list never dead-ends on a slow server.
            int option = mode.Option;
            IReadOnlyList<SessionInfo> sessions;
            try
            {
                sessions = await _service.GetSessionsAsync(programId, courseId, option, cts.Token);
            }
            catch (WebDemoServiceException ex) when (mode.Option == 3 && programId == 0 &&
                                                     !cts.IsCancellationRequested &&
                                                     ex.InnerException is TaskCanceledException)
            {
                Log.Write($"Load sessions timeout for opt3 all-programmes; falling back to opt2: {ex}");
                if (version != _sessionLoadVersion)
                {
                    return;
                }

                sessions = await _service.GetSessionsAsync(0, 0, 2, cts.Token);
            }

            if (version != _sessionLoadVersion)
            {
                return;
            }

            // Bulk-append so the bound list lays out once for the whole batch
            // instead of once per session (keeps the UI smooth on large terms).
            var loaded = new List<SessionInfo>(sessions.Count);
            foreach (var session in sessions)
            {
                loaded.Add(session);
            }

            Sessions.AddRange(loaded);
            RefreshSessionDerivedState();
            if (Sessions.Count == 0)
            {
                SetStatus("main.noSessions");
            }
            else
            {
                SetStatus("main.sessionCount", Sessions.Count);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer selection; leave the loading state to it.
        }
        catch (WebDemoServiceException ex)
        {
            if (version != _sessionLoadVersion)
            {
                return;
            }

            Log.Write($"Load sessions failed (mode={mode.LocKey}): {ex}");
            SetStatus(FailureStatus(ex));
        }
        catch (Exception ex)
        {
            if (version != _sessionLoadVersion)
            {
                return;
            }

            Log.Write($"Load sessions failed: {ex}");
            SetStatus("login.error.network");
        }
        finally
        {
            if (version == _sessionLoadVersion)
            {
                _sessionLoadCts = null;
                IsBusy = false;
                _sessionsLoaded = true;
                OnPropertyChanged(nameof(ShowSessionCount));
                IsLoadingSessions = false;
            }
        }
    }

    /// <summary>
    /// Leaves a short notice in the status bar when a live session could not be
    /// recovered and the chat windows had to close.
    /// </summary>
    public void NotifySessionEnded() => SetStatus("session.endedNetwork");

    /// <summary>Runs the live parameter sweep and records every raw reply.</summary>
    private async Task DiagnosticsAsync()
    {
        IsBusy = true;
        SetStatus("diag.running");
        try
        {
            await _service.DiagnosticsAsync();
            SetStatus("diag.done", SoapResponsesPath());
        }
        catch (Exception ex)
        {
            Log.Write($"Diagnostics failed: {ex}");
            SetStatus("diag.failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SoapResponsesPath() => AppPaths.ResponsesDirectory;

    /// <summary>
    /// The student client never transmits microphone audio; the mic is reported
    /// muted as a policy/state value. OS-level device muting is not relied on
    /// for that guarantee and is left untouched here for now.
    /// </summary>
    private void EnsureMicMuted()
    {
        _micMuted = true;
        OnPropertyChanged(nameof(MicStatusText));
    }

    /// <summary>Flips between the 24-hour and 12-hour clock and persists the choice.</summary>
    private void ToggleTimeFormat()
    {
        DisplayOptions.Use24HourClock = !DisplayOptions.Use24HourClock;
        _settings.Use24HourClock = DisplayOptions.Use24HourClock;
        _settings.Save();
        OnPropertyChanged(nameof(TimeFormatText));
    }

    /// <summary>
    /// Re-reads every view-model string derived from <see cref="Loc"/> so the
    /// whole window flips language at once (the XAML <c>[key]</c> bindings update
    /// on their own; these are the ones computed in code).
    /// </summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WelcomeText));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(DataSourceText));
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(TimeFormatText));
        OnPropertyChanged(nameof(TermLabel));

        // Combo boxes render the item's Display string; re-read it in place so
        // the selection translates without the user having to re-pick.
        foreach (var program in Programs)
        {
            program.RaiseDisplayChanged();
        }

        foreach (var course in Courses)
        {
            course.RaiseDisplayChanged();
        }

        foreach (var mode in Modes)
        {
            mode.RaiseDisplayChanged();
        }
    }

    private async Task LaunchAsync()
    {
        if (SelectedSession is null || SelectedCourse is null)
        {
            return;
        }

        var session = SelectedSession;
        var course = SelectedCourse;

        Log.Write($"LaunchAsync enter session={session.SessionId} course={course.Code}");

        IsBusy = true;
        SetStatus("main.launching");
        EnsureMicMuted();
        try
        {
            var request = new SessionLaunchRequest(
                session,
                _user,
                LinkId: session.SessionId.ToString(),
                SessionParameter: course.Code);
            var bridge = await _launcher.LaunchAsync(request);
            if (bridge is not null)
            {
                SessionStarted?.Invoke(this, bridge);
            }

            Log.Write("LaunchAsync session finished");
            SetStatus("main.launch");
        }
        catch (SessionLaunchException ex)
        {
            Log.Write($"LaunchAsync launch error: {ex.Message}");
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"LaunchAsync failed: {ex.GetType().Name}: {ex.Message}");
            SetStatus("login.error.network");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A selectable fetch mode for the sessions list.</summary>
/// <param name="Option">Numeric <c>option</c> passed to the service (1, 2 or 3).</param>
/// <param name="UseProgramCourse">True when the mode filters by programme/course.</param>
/// <param name="LocKey">Localisation key for the mode's label.</param>
public sealed class SessionMode : INotifyPropertyChanged
{
    public SessionMode(int Option, bool UseProgramCourse, string LocKey)
    {
        this.Option = Option;
        this.UseProgramCourse = UseProgramCourse;
        this.LocKey = LocKey;
    }

    public int Option { get; }
    public bool UseProgramCourse { get; }
    public string LocKey { get; }

    public string Display => Loc.Instance[LocKey];

    public override string ToString() => Display;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the localised label after a language switch.</summary>
    public void RaiseDisplayChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}

/// <summary>
/// Sorts session rows for the clicked column. Class and session compare
/// numerically (C1 before C2 before C10); course and tutor compare as text
/// (ignoring case); start compares as a date. Ties fall back to the session
/// name so the order is always stable.
/// </summary>
public sealed class SessionComparer : IComparer
{
    private readonly string _column;
    private readonly ListSortDirection _direction;
    private readonly StringComparer _text = StringComparer.CurrentCultureIgnoreCase;

    public SessionComparer(string column, ListSortDirection direction)
    {
        _column = column;
        _direction = direction;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not SessionInfo a || y is not SessionInfo b)
        {
            return 0;
        }

        var result = _column switch
        {
            "course" => _text.Compare(a.CourseLabel, b.CourseLabel),
            "class" => CompareNumeric(a.ClassNumber, b.ClassNumber),
            "session" => CompareNumeric(a.SessionNumber, b.SessionNumber),
            "start" => CompareStart(a.StartDate, b.StartDate),
            "tutor" => _text.Compare(a.TutorName, b.TutorName),
            _ => 0
        };

        if (result == 0)
        {
            result = _text.Compare(a.SessionName, b.SessionName);
        }

        return _direction == ListSortDirection.Descending ? -result : result;
    }

    private static int CompareNumeric(string left, string right)
    {
        var hasLeft = int.TryParse(left, out var l);
        var hasRight = int.TryParse(right, out var r);
        if (hasLeft && hasRight)
        {
            return l.CompareTo(r);
        }

        // Rows whose number could not be parsed sort to the end, in text order.
        if (hasLeft != hasRight)
        {
            return hasLeft ? -1 : 1;
        }

        return 0;
    }

    private static int CompareStart(DateTime? left, DateTime? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        return DateTime.Compare(left.Value, right.Value);
    }
}
