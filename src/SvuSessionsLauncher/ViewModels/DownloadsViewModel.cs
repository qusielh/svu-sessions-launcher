using System.Collections.ObjectModel;
using System.IO;
using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Services;

namespace SvuSessionsLauncher.ViewModels;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Done,
    Failed,
    Cancelled
}

/// <summary>One selectable session row in the downloads list.</summary>
public sealed class DownloadRow : ObservableObject
{
    public DownloadRow(SvuSession session) => Session = session;

    public SvuSession Session { get; }

    public string Order => Session.Order;
    public string Course => Session.Course;
    public string Class => Session.Class;
    public string Term => Session.Term;
    public string Tutor => Session.Tutor;
    public string Date => Session.Date;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private DownloadStatus _status;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private string _note = "";
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string StatusText => Status switch
    {
        DownloadStatus.Downloading => Loc.Instance["download.status.downloading"],
        DownloadStatus.Done => Loc.Instance["download.status.done"],
        DownloadStatus.Failed => Loc.Instance["download.status.failed"],
        DownloadStatus.Cancelled => Loc.Instance["download.status.cancelled"],
        _ => Loc.Instance["download.status.queued"]
    };

    public void RefreshLocalization() => OnPropertyChanged(nameof(StatusText));
}

/// <summary>
/// Backs the "Download sessions" window: it walks the university's WebForms
/// filters, lists the matching sessions, and streams the chosen recordings to
/// disk (resuming after dropped connections).
/// </summary>
public sealed class DownloadsViewModel : ObservableObject, IDisposable
{
    public const string AllClassesValue = "__ALL__";

    private readonly SvuDownloadClient _client = new();
    private readonly AppSettings _settings;
    private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cascadeCts;
    private CancellationTokenSource? _downloadCts;
    private Task _cascade = Task.CompletedTask;
    private bool _suppress;
    private bool _disposed;

    public DownloadsViewModel(AppSettings settings)
    {
        _settings = settings;
        _saveDirectory = string.IsNullOrWhiteSpace(settings.DownloadDirectory)
            ? ResolveDefaultDirectory()
            : settings.DownloadDirectory;
        _downloadInParallel = settings.DownloadParallel;

        DownloadCommand = new RelayCommand(DownloadAsync, () => !IsDownloading && SelectedCount > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsDownloading);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => Sessions.Count > 0 && !IsDownloading);
        ClearSelectionCommand = new RelayCommand(() => SetAll(false), () => SelectedCount > 0 && !IsDownloading);

        Loc.LanguageChanged += OnLanguageChanged;
        _ = InitializeAsync();
    }

    public RelayCommand DownloadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public ObservableCollection<SvuOption> Terms { get; } = new();
    public ObservableCollection<SvuOption> Programs { get; } = new();
    public ObservableCollection<SvuOption> Courses { get; } = new();
    public ObservableCollection<SvuOption> Tutors { get; } = new();
    public ObservableCollection<SvuOption> Classes { get; } = new();
    public ObservableCollection<DownloadRow> Sessions { get; } = new();

    private string _saveDirectory;
    public string SaveDirectory
    {
        get => _saveDirectory;
        private set => SetProperty(ref _saveDirectory, value);
    }

    private SvuOption? _selectedTerm;
    public SvuOption? SelectedTerm
    {
        get => _selectedTerm;
        set
        {
            if (SetProperty(ref _selectedTerm, value))
            {
                RaiseEnablement();
                if (!_suppress)
                {
                    _cascade = OnTermChosenAsync();
                }
            }
        }
    }

    private SvuOption? _selectedProgram;
    public SvuOption? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetProperty(ref _selectedProgram, value))
            {
                RaiseEnablement();
                if (!_suppress)
                {
                    _cascade = OnProgramChosenAsync();
                }
            }
        }
    }

    private SvuOption? _selectedCourse;
    public SvuOption? SelectedCourse
    {
        get => _selectedCourse;
        set
        {
            if (SetProperty(ref _selectedCourse, value))
            {
                RaiseEnablement();
                if (!_suppress)
                {
                    _cascade = OnCourseChosenAsync();
                }
            }
        }
    }

    private SvuOption? _selectedTutor;
    public SvuOption? SelectedTutor
    {
        get => _selectedTutor;
        set
        {
            if (SetProperty(ref _selectedTutor, value))
            {
                RaiseEnablement();
                if (!_suppress)
                {
                    _cascade = OnTutorChosenAsync();
                }
            }
        }
    }

    private SvuOption? _selectedClass;
    public SvuOption? SelectedClass
    {
        get => _selectedClass;
        set
        {
            if (SetProperty(ref _selectedClass, value))
            {
                RaiseEnablement();
                if (!_suppress)
                {
                    _cascade = OnClassChosenAsync();
                }
            }
        }
    }

    // ---- Cascading enablement -------------------------------------------------
    // Each box unlocks only once the previous one holds a real (non-placeholder)
    // pick, so the user can never pick from a level that has nothing to offer.

    public bool IsTermEnabled => true;
    public bool IsProgramEnabled => !IsPlaceholder(_selectedTerm);
    public bool IsCourseEnabled => !IsPlaceholder(_selectedProgram);
    public bool IsTutorEnabled => !IsPlaceholder(_selectedCourse);
    public bool IsClassEnabled => !IsPlaceholder(_selectedTutor);

    // ---- Per-level loading (drives the inline spinner in each box) -------------

    private bool _isLoadingTerms;
    public bool IsLoadingTerms
    {
        get => _isLoadingTerms;
        private set
        {
            if (SetProperty(ref _isLoadingTerms, value))
            {
                OnPropertyChanged(nameof(ShowTermPlaceholder));
            }
        }
    }

    private bool _isLoadingPrograms;
    public bool IsLoadingPrograms
    {
        get => _isLoadingPrograms;
        private set
        {
            if (SetProperty(ref _isLoadingPrograms, value))
            {
                OnPropertyChanged(nameof(ShowProgramPlaceholder));
            }
        }
    }

    private bool _isLoadingCourses;
    public bool IsLoadingCourses
    {
        get => _isLoadingCourses;
        private set
        {
            if (SetProperty(ref _isLoadingCourses, value))
            {
                OnPropertyChanged(nameof(ShowCoursePlaceholder));
            }
        }
    }

    private bool _isLoadingTutors;
    public bool IsLoadingTutors
    {
        get => _isLoadingTutors;
        private set
        {
            if (SetProperty(ref _isLoadingTutors, value))
            {
                OnPropertyChanged(nameof(ShowTutorPlaceholder));
            }
        }
    }

    private bool _isLoadingClasses;
    public bool IsLoadingClasses
    {
        get => _isLoadingClasses;
        private set
        {
            if (SetProperty(ref _isLoadingClasses, value))
            {
                OnPropertyChanged(nameof(ShowClassPlaceholder));
            }
        }
    }

    // ---- Closed-box placeholders ---------------------------------------------
    // Shown when nothing is selected yet and the level is not mid-load.

    public bool ShowTermPlaceholder => IsPlaceholder(_selectedTerm) && !IsLoadingTerms;
    public bool ShowProgramPlaceholder => IsPlaceholder(_selectedProgram) && !IsLoadingPrograms;
    public bool ShowCoursePlaceholder => IsPlaceholder(_selectedCourse) && !IsLoadingCourses;
    public bool ShowTutorPlaceholder => IsPlaceholder(_selectedTutor) && !IsLoadingTutors;
    public bool ShowClassPlaceholder => IsPlaceholder(_selectedClass) && !IsLoadingClasses;

    /// <summary>Raises the enablement flags after any selection changes.</summary>
    private void RaiseEnablement()
    {
        OnPropertyChanged(nameof(IsTermEnabled));
        OnPropertyChanged(nameof(IsProgramEnabled));
        OnPropertyChanged(nameof(IsCourseEnabled));
        OnPropertyChanged(nameof(IsTutorEnabled));
        OnPropertyChanged(nameof(IsClassEnabled));
        OnPropertyChanged(nameof(ShowTermPlaceholder));
        OnPropertyChanged(nameof(ShowProgramPlaceholder));
        OnPropertyChanged(nameof(ShowCoursePlaceholder));
        OnPropertyChanged(nameof(ShowTutorPlaceholder));
        OnPropertyChanged(nameof(ShowClassPlaceholder));
    }

    private bool _isLoadingSessions;
    public bool IsLoadingSessions
    {
        get => _isLoadingSessions;
        private set
        {
            if (SetProperty(ref _isLoadingSessions, value))
            {
                OnPropertyChanged(nameof(ShowSpinner));
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(ShowHint));
            }
        }
    }

    private bool _sessionsLoaded;
    public bool SessionsLoaded
    {
        get => _sessionsLoaded;
        private set
        {
            if (SetProperty(ref _sessionsLoaded, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(ShowHint));
            }
        }
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(ShowHint));
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private bool _downloadInParallel;
    public bool DownloadInParallel
    {
        get => _downloadInParallel;
        set
        {
            if (!SetProperty(ref _downloadInParallel, value))
            {
                return;
            }

            _settings.DownloadParallel = value;
            _settings.Save();
        }
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int SelectedCount => Sessions.Count(row => row.IsSelected);

    public string SelectedCountText => Loc.Instance.Format("download.selected", SelectedCount);

    public bool ShowSpinner => IsLoadingSessions;
    public bool ShowEmpty => SessionsLoaded && Sessions.Count == 0 && !IsLoadingSessions && !HasError;
    public bool ShowHint => !SessionsLoaded && Sessions.Count == 0 && !IsLoadingSessions && !HasError;
    public bool ShowHeader => Sessions.Count > 0;
    public bool ShowList => Sessions.Count > 0;

    public void SetSaveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        SaveDirectory = directory;
        _settings.DownloadDirectory = directory;
        _settings.Save();
    }

    private static string ResolveDefaultDirectory()
    {
        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    private async Task InitializeAsync()
    {
        IsLoadingTerms = true;
        try
        {
            await RunAsync(async ct =>
            {
                var page = await _client.OpenAsync(ct);
                SetOptions(Terms, page.Dropdowns.GetValueOrDefault(SvuDownloadClient.TermControl), set => SelectedTerm = set);
            }, session: false);
        }
        finally
        {
            IsLoadingTerms = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (_disposed || string.IsNullOrEmpty(_settings.DownloadTerm))
        {
            return;
        }

        SelectIfPresent(Terms, _settings.DownloadTerm, set => SelectedTerm = set);
        await _cascade;
        if (SelectedTerm is null)
        {
            return;
        }

        SelectIfPresent(Programs, _settings.DownloadProgram, set => SelectedProgram = set);
        await _cascade;
        if (SelectedProgram is null)
        {
            return;
        }

        SelectIfPresent(Courses, _settings.DownloadCourse, set => SelectedCourse = set);
        await _cascade;
        if (SelectedCourse is null)
        {
            return;
        }

        SelectIfPresent(Tutors, _settings.DownloadTutor, set => SelectedTutor = set);
        await _cascade;
        if (SelectedTutor is null)
        {
            return;
        }

        SelectIfPresent(Classes, _settings.DownloadClass, set => SelectedClass = set);
    }

    private void SelectIfPresent(ObservableCollection<SvuOption> collection, string value, Action<SvuOption?> select)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var option = collection.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.Ordinal));
        if (option != null)
        {
            select(option);
        }
    }

    private async Task OnTermChosenAsync()
    {
        ClearDownstream(1);
        ResetSessions();

        if (_selectedTerm is not { } term || IsPlaceholder(term))
        {
            return;
        }

        IsLoadingPrograms = true;
        try
        {
            await RunAsync(async ct =>
            {
                var page = await _client.ChooseAsync(SvuDownloadClient.TermControl, term.Value, ct);
                SetOptions(Programs, page.Dropdowns.GetValueOrDefault(SvuDownloadClient.ProgramControl), set => SelectedProgram = set);
            }, session: false);
        }
        finally
        {
            IsLoadingPrograms = false;
        }
    }

    private async Task OnProgramChosenAsync()
    {
        ClearDownstream(2);
        ResetSessions();

        if (_selectedProgram is not { } program || IsPlaceholder(program))
        {
            return;
        }

        IsLoadingCourses = true;
        try
        {
            await RunAsync(async ct =>
            {
                var page = await _client.ChooseAsync(SvuDownloadClient.ProgramControl, program.Value, ct);
                SetOptions(Courses, page.Dropdowns.GetValueOrDefault(SvuDownloadClient.CourseControl), set => SelectedCourse = set);
            }, session: false);
        }
        finally
        {
            IsLoadingCourses = false;
        }
    }

    private async Task OnCourseChosenAsync()
    {
        ClearDownstream(3);
        ResetSessions();

        if (_selectedCourse is not { } course || IsPlaceholder(course))
        {
            return;
        }

        IsLoadingTutors = true;
        try
        {
            await RunAsync(async ct =>
            {
                var page = await _client.ChooseAsync(SvuDownloadClient.CourseControl, course.Value, ct);
                SetOptions(Tutors, page.Dropdowns.GetValueOrDefault(SvuDownloadClient.TutorControl), set => SelectedTutor = set);
            }, session: false);
        }
        finally
        {
            IsLoadingTutors = false;
        }
    }

    private async Task OnTutorChosenAsync()
    {
        ClearDownstream(4);
        ResetSessions();

        if (_selectedTutor is not { } tutor || IsPlaceholder(tutor))
        {
            return;
        }

        IsLoadingClasses = true;
        try
        {
            await RunAsync(async ct =>
            {
                var page = await _client.ChooseAsync(SvuDownloadClient.TutorControl, tutor.Value, ct);
                var options = page.Dropdowns.GetValueOrDefault(SvuDownloadClient.ClassControl);
                SetOptions(Classes, options, set => SelectedClass = set);
                AppendAllClassesOption();
            }, session: false);
        }
        finally
        {
            IsLoadingClasses = false;
        }
    }

    private async Task OnClassChosenAsync()
    {
        ResetSessions();

        if (_selectedClass is not { } chosen || IsPlaceholder(chosen))
        {
            return;
        }

        if (chosen.Value == AllClassesValue)
        {
            await RunAsync(FetchAllClassesAsync, session: true);
            return;
        }

        await RunAsync(async ct =>
        {
            var page = await _client.ChooseAsync(SvuDownloadClient.ClassControl, chosen.Value, ct);
            PopulateSessions(page.HasSessionsGrid ? page.Sessions : new List<SvuSession>(), chosen.Value);
        }, session: true);
    }

    private async Task FetchAllClassesAsync(CancellationToken cancellationToken)
    {
        var classes = Classes.Where(o => o.Value != AllClassesValue && !IsPlaceholder(o)).ToList();
        var collected = new List<SvuSession>();

        for (int i = 0; i < classes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = Loc.Instance.Format("download.fanoutClass", i + 1, classes.Count);

            var page = await _client.ChooseAsync(SvuDownloadClient.ClassControl, classes[i].Value, cancellationToken);
            if (page.HasSessionsGrid && page.Sessions.Count > 0)
            {
                foreach (var session in page.Sessions)
                {
                    session.ClassValue = classes[i].Value;
                    collected.Add(session);
                }
            }
        }

        PopulateSessions(collected, null);
    }

    private void PopulateSessions(IEnumerable<SvuSession> sessions, string? classValue)
    {
        _suppress = true;
        try
        {
            foreach (var row in Sessions)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }

            Sessions.Clear();

            foreach (var session in sessions)
            {
                if (classValue != null && string.IsNullOrEmpty(session.ClassValue))
                {
                    session.ClassValue = classValue;
                }

                var row = new DownloadRow(session);
                row.PropertyChanged += OnRowPropertyChanged;
                Sessions.Add(row);
            }
        }
        finally
        {
            _suppress = false;
        }

        SessionsLoaded = true;
        StatusMessage = "";
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowHeader));
        OnSelectionChanged();
    }

    private void ResetSessions()
    {
        _suppress = true;
        try
        {
            foreach (var row in Sessions)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }

            Sessions.Clear();
        }
        finally
        {
            _suppress = false;
        }

        SessionsLoaded = false;
        StatusMessage = "";
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowHeader));
        OnSelectionChanged();
    }

    private void ClearDownstream(int fromLevel)
    {
        _suppress = true;
        try
        {
            if (fromLevel <= 1)
            {
                Programs.Clear();
                SelectedProgram = null;
            }

            if (fromLevel <= 2)
            {
                Courses.Clear();
                SelectedCourse = null;
            }

            if (fromLevel <= 3)
            {
                Tutors.Clear();
                SelectedTutor = null;
            }

            if (fromLevel <= 4)
            {
                Classes.Clear();
                SelectedClass = null;
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SetOptions(ObservableCollection<SvuOption> collection, List<SvuOption>? options, Action<SvuOption?>? select)
    {
        _suppress = true;
        try
        {
            collection.Clear();
            if (options != null)
            {
                foreach (var option in options)
                {
                    // Drop the server's own blank/"0" placeholder rows: the closed
                    // box shows its own hint, and the open list stays free of empties.
                    if (IsPlaceholder(option))
                    {
                        continue;
                    }

                    collection.Add(option);
                }
            }

            select?.Invoke(null);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void AppendAllClassesOption()
    {
        if (Classes.Any(o => o.Value == AllClassesValue))
        {
            return;
        }

        // The real classes are already populated; add the combined "all classes"
        // entry so one tutor's full catalogue can be fetched in a single list.
        Classes.Add(new SvuOption(AllClassesValue, Loc.Instance["download.allClasses"]));
    }

    // The WebForms page labels its own "please choose" rows ("Select Program",
    // "Select Course", ...). They usually carry value "0", but a populated
    // postback can give them another sentinel value, so match the label as well.
    private static readonly HashSet<string> PlaceholderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "select program", "select programme", "select course", "select term",
        "select tutor", "select class", "select session",
        "choose", "choose program", "choose programme", "choose course", "choose term",
        "choose tutor", "choose class", "choose session",
        "\u0627\u062e\u062a\u0631",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0628\u0631\u0646\u0627\u0645\u062c",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0645\u0642\u0631\u0631",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0641\u0635\u0644",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0645\u062f\u0631\u0633",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0645\u062d\u0627\u0636\u0631",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u0634\u0639\u0628\u0629",
        "\u0627\u062e\u062a\u0631 \u0627\u0644\u062c\u0644\u0633\u0629"
    };

    private static bool IsPlaceholder(SvuOption? option)
    {
        if (option is null)
        {
            return true;
        }

        var value = option.Value.Trim();
        if (value.Length == 0 || value == "0" || value == "-1")
        {
            return true;
        }

        var label = option.Label.Trim().TrimEnd('.', '\u2026').Trim();
        return label.Length == 0 || PlaceholderLabels.Contains(label);
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool session)
    {
        _cascadeCts?.Cancel();
        _cascadeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _cascadeCts = cts;

        if (session)
        {
            IsLoadingSessions = true;
            StatusMessage = "";
        }

        HasError = false;
        ErrorMessage = "";

        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, or the window closed.
        }
        catch (Exception exception)
        {
            // A failed request is not an empty result: surface the error instead
            // of the "no recordings" label.
            HasError = true;
            ErrorMessage = Loc.Instance.Format("download.error", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cascadeCts, cts))
            {
                if (session)
                {
                    IsLoadingSessions = false;
                }
            }
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadRow.IsSelected) && !_suppress)
        {
            OnSelectionChanged();
        }
    }

    private void OnLanguageChanged()
    {
        RelabelAll();

        foreach (var row in Sessions)
        {
            row.RefreshLocalization();
        }

        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void RelabelAll()
    {
        RelabelColumn(Terms, () => SelectedTerm, value => SelectedTerm = value);
        RelabelColumn(Programs, () => SelectedProgram, value => SelectedProgram = value);
        RelabelColumn(Courses, () => SelectedCourse, value => SelectedCourse = value);
        RelabelColumn(Tutors, () => SelectedTutor, value => SelectedTutor = value);
        RelabelColumn(Classes, () => SelectedClass, value => SelectedClass = value);
    }

    private void RelabelColumn(
        ObservableCollection<SvuOption> collection, Func<SvuOption?> get, Action<SvuOption?> set)
    {
        _suppress = true;
        try
        {
            for (int i = 0; i < collection.Count; i++)
            {
                var option = collection[i];
                if (option.Value == AllClassesValue)
                {
                    collection[i] = option with { Label = Loc.Instance["download.allClasses"] };
                }
            }

            var selected = get();
            if (selected != null && selected.Value == AllClassesValue)
            {
                set(collection.FirstOrDefault(o => o.Value == selected.Value));
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        RaiseCommandStates();
    }

    private void SetAll(bool selected)
    {
        _suppress = true;
        try
        {
            foreach (var row in Sessions)
            {
                row.IsSelected = selected;
            }
        }
        finally
        {
            _suppress = false;
        }

        OnSelectionChanged();
    }

    private void Cancel()
    {
        _downloadCts?.Cancel();
        StatusMessage = Loc.Instance["download.cancelling"];
    }

    private async Task DownloadAsync()
    {
        var rows = Sessions.Where(row => row.IsSelected).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        _usedNames.Clear();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        IsDownloading = true;
        int done = 0;
        int failed = 0;

        try
        {
            Log.Write($"download start: {rows.Count} file(s), mode={(DownloadInParallel ? "parallel" : "queue")}");

            if (DownloadInParallel)
            {
                (done, failed) = await DownloadParallelAsync(rows, token);
            }
            else
            {
                (done, failed) = await DownloadQueuedAsync(rows, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; rows already marked.
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;

            StatusMessage = failed > 0
                ? Loc.Instance.Format("download.doneSome", done, failed)
                : Loc.Instance.Format("download.done", done);
            Log.Write($"download end: {done} done, {failed} failed");
        }
    }

    private async Task<(int Done, int Failed)> DownloadQueuedAsync(
        List<DownloadRow> rows, CancellationToken cancellationToken)
    {
        int done = 0;
        int failed = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkDownloading(row);

            try
            {
                var request = await PrepareDownloadAsync(row, cancellationToken).ConfigureAwait(true);
                if (request is null)
                {
                    MarkFailed(row, Loc.Instance["download.error.noLink"]);
                    failed++;
                    continue;
                }

                var outcome = await StartFileDownloadAsync(request, cancellationToken).ConfigureAwait(true);
                if (outcome == DownloadOutcome.Completed)
                {
                    done++;
                }
                else if (outcome == DownloadOutcome.Failed)
                {
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                row.Status = DownloadStatus.Cancelled;
                throw;
            }
            catch (Exception ex)
            {
                MarkFailed(row, ex.Message);
                failed++;
            }
        }

        return (done, failed);
    }

    /// <summary>
    /// Resolves every file link first (the WebForms grid shares one form state,
    /// so this part must stay sequential), then streams all selected files at
    /// once. Each file has its own response stream and progress updates.
    /// </summary>
    private async Task<(int Done, int Failed)> DownloadParallelAsync(
        List<DownloadRow> rows, CancellationToken cancellationToken)
    {
        int failed = 0;
        var requests = new List<DownloadRequest>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkDownloading(row);

            try
            {
                var request = await PrepareDownloadAsync(row, cancellationToken).ConfigureAwait(true);
                if (request is null)
                {
                    MarkFailed(row, Loc.Instance["download.error.noLink"]);
                    failed++;
                }
                else
                {
                    requests.Add(request);
                }
            }
            catch (OperationCanceledException)
            {
                row.Status = DownloadStatus.Cancelled;
                throw;
            }
            catch (Exception ex)
            {
                MarkFailed(row, ex.Message);
                failed++;
            }
        }

        if (requests.Count == 0)
        {
            return (0, failed);
        }

        var outcomes = await Task.WhenAll(requests.Select(request =>
            StartFileDownloadAsync(request, cancellationToken))).ConfigureAwait(true);

        int done = outcomes.Count(outcome => outcome == DownloadOutcome.Completed);
        failed += outcomes.Count(outcome => outcome == DownloadOutcome.Failed);
        return (done, failed);
    }

    private async Task<DownloadRequest?> PrepareDownloadAsync(
        DownloadRow row, CancellationToken cancellationToken)
    {
        string classValue = row.Session.ClassValue;
        if (!string.IsNullOrEmpty(classValue) &&
            _client.ValueOf(SvuDownloadClient.ClassControl) != classValue)
        {
            await _client.ChooseAsync(SvuDownloadClient.ClassControl, classValue, cancellationToken)
                .ConfigureAwait(true);
        }

        var page = await _client.SelectRowAsync(row.Session.Index, cancellationToken).ConfigureAwait(true);
        string? url = page.FileLinks.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string fileName = BuildFileName(row.Session, url);
        row.Note = fileName;
        return new DownloadRequest(row, url, Path.Combine(SaveDirectory, fileName), fileName);
    }

    private async Task<DownloadOutcome> StartFileDownloadAsync(
        DownloadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                request.Row.Progress = report.Total > 0 ? (double)report.Received / report.Total : 0;
            });

            var outcome = await _client
                .DownloadAsync(request.Url, request.Destination, progress, cancellationToken)
                .ConfigureAwait(true);

            switch (outcome)
            {
                case DownloadOutcome.Completed:
                    request.Row.Status = DownloadStatus.Done;
                    request.Row.Progress = 1;
                    request.Row.Note = request.FileName;
                    break;
                case DownloadOutcome.Cancelled:
                    request.Row.Status = DownloadStatus.Cancelled;
                    break;
                default:
                    MarkFailed(request.Row, Loc.Instance["download.error.file"]);
                    break;
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            request.Row.Status = DownloadStatus.Cancelled;
            return DownloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            MarkFailed(request.Row, ex.Message);
            return DownloadOutcome.Failed;
        }
    }

    private static void MarkDownloading(DownloadRow row)
    {
        row.Status = DownloadStatus.Downloading;
        row.Progress = 0;
        row.Note = "";
    }

    private static void MarkFailed(DownloadRow row, string reason)
    {
        row.Status = DownloadStatus.Failed;
        row.Note = string.IsNullOrWhiteSpace(reason) ? Loc.Instance["download.status.failed"] : reason;
        Log.Write($"download failed for {row.Course} / {row.Order}: {row.Note}");
    }

    private sealed record DownloadRequest(DownloadRow Row, string Url, string Destination, string FileName);

    private string BuildFileName(SvuSession session, string url)
    {
        string stem = string.Join("_", new[]
        {
            Sanitize(session.Order), Sanitize(session.Course), Sanitize(session.Class), Sanitize(session.Term)
        }.Where(part => part.Length > 0));

        if (stem.Length == 0)
        {
            stem = Sanitize(Path.GetFileNameWithoutExtension(url));
        }

        if (stem.Length == 0)
        {
            stem = "session";
        }

        string candidate = stem + ".lrec";
        int suffix = 2;
        while (!_usedNames.Add(candidate))
        {
            candidate = $"{stem} ({suffix++}).lrec";
        }

        return candidate;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private void RaiseCommandStates()
    {
        DownloadCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    private static string ValueOrEmpty(SvuOption? option) =>
        option == null || IsPlaceholder(option) ? string.Empty : option.Value;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loc.LanguageChanged -= OnLanguageChanged;

        _cascadeCts?.Cancel();
        _cascadeCts?.Dispose();
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _client.Dispose();
    }
}
