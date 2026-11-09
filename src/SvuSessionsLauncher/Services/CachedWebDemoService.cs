using System.IO;
using System.Text.Json;
using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Keeps the programme/course catalog local so the filter lists never wait on
/// the network: memory first, then a JSON file under %LOCALAPPDATA%, and only
/// then the service. A background refresh updates the file for the next run,
/// and a polite prefetch fills in courses for programmes not seen yet.
/// </summary>
public sealed class CachedWebDemoService : IWebDemoService
{
    private readonly IWebDemoService _inner;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly List<ProgramInfo> _programs = [];
    private readonly Dictionary<int, List<CourseInfo>> _courses = [];
    private bool _loaded;
    private bool _refreshAttempted;
    private bool _prefetchStarted;

    public CachedWebDemoService(IWebDemoService inner, string? path = null)
    {
        _inner = inner;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SvuSessionsLauncher",
            "catalog.json");
    }

    public Task<AuthResult> CheckUserPasswordAsync(string username, string password, CancellationToken ct = default)
        => _inner.CheckUserPasswordAsync(username, password, ct);

    public Task<int> IsUserTrainerAsync(CancellationToken ct = default)
        => _inner.IsUserTrainerAsync(ct);

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (_programs.Count > 0)
            {
                if (!_refreshAttempted)
                {
                    _refreshAttempted = true;
                    _ = RefreshProgramsAsync();
                }

                return Task.FromResult<IReadOnlyList<ProgramInfo>>(_programs.ToArray());
            }
        }

        return FetchProgramsAsync(ct);
    }

    public Task<IReadOnlyList<CourseInfo>> GetCoursesAsync(int programId, CancellationToken ct = default)
    {
        EnsureLoaded();
        lock (_gate)
        {
            if (_courses.TryGetValue(programId, out var cached))
            {
                return Task.FromResult<IReadOnlyList<CourseInfo>>(cached.ToArray());
            }
        }

        return FetchCoursesAsync(programId, ct);
    }

    public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int programId, int courseId, int option = 0, CancellationToken ct = default)
        => _inner.GetSessionsAsync(programId, courseId, option, ct);

    public Task<IReadOnlyList<SessionInfo>> GetTodaySessionsAsync(int option = 0, CancellationToken ct = default)
        => _inner.GetTodaySessionsAsync(option, ct);

    public Task<int> GetMeetingLaunchCountAsync(string launchedDate, int serverNumber, CancellationToken ct = default)
        => _inner.GetMeetingLaunchCountAsync(launchedDate, serverNumber, ct);

    public Task DiagnosticsAsync(CancellationToken ct = default)
        => _inner.DiagnosticsAsync(ct);

    // ------------------------------------------------------------- internals

    private async Task<IReadOnlyList<ProgramInfo>> FetchProgramsAsync(CancellationToken ct)
    {
        var programs = await _inner.GetProgramsAsync(ct).ConfigureAwait(false);
        if (programs.Count == 0)
        {
            return programs;
        }

        lock (_gate)
        {
            _refreshAttempted = true;
            _programs.Clear();
            _programs.AddRange(programs);
        }

        Save();
        StartPrefetch();
        return programs;
    }

    private async Task RefreshProgramsAsync()
    {
        try
        {
            var programs = await _inner.GetProgramsAsync().ConfigureAwait(false);
            if (programs.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                _programs.Clear();
                _programs.AddRange(programs);
            }

            Save();
            StartPrefetch();
        }
        catch
        {
            // A failed refresh keeps the cached catalog; the next run tries again.
        }
    }

    private async Task<IReadOnlyList<CourseInfo>> FetchCoursesAsync(int programId, CancellationToken ct)
    {
        var courses = await _inner.GetCoursesAsync(programId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _courses[programId] = courses.ToList();
        }

        Save();
        return courses;
    }

    private void StartPrefetch()
    {
        lock (_gate)
        {
            if (_prefetchStarted)
            {
                return;
            }

            _prefetchStarted = true;
        }

        _ = Task.Run(PrefetchCoursesAsync);
    }

    /// <summary>
    /// Fills courses for programmes the user has not opened yet, one call at a
    /// time with a small gap, so later switches are instant. Stops on the first
    /// failure: a struggling server should not be hammered.
    /// </summary>
    private async Task PrefetchCoursesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        int[] ids;
        lock (_gate)
        {
            // Only prefetch a single saved/favourite programme to avoid
            // hammering the server with ~70 concurrent-like sequential calls.
            ids = _programs.Where(p => !p.IsAll).Select(p => p.Id).Take(1).ToArray();
        }

        foreach (int id in ids)
        {
            lock (_gate)
            {
                if (_courses.ContainsKey(id))
                {
                    continue;
                }
            }

            try
            {
                var courses = await _inner.GetCoursesAsync(id).ConfigureAwait(false);
                lock (_gate)
                {
                    _courses[id] = courses.ToList();
                }

                Save();
            }
            catch
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(_path));
                if (file is null)
                {
                    return;
                }

                _programs.Clear();
                _programs.AddRange(file.Programs.Select(ToProgram));
                _courses.Clear();
                foreach (var (id, list) in file.Courses)
                {
                    _courses[id] = list.Select(ToCourse).ToList();
                }
            }
            catch
            {
                // A corrupt cache is simply ignored; the next fetch rewrites it.
            }
        }
    }

    private void Save()
    {
        lock (_saveGate)
        {
            try
            {
                CatalogFile file;
                lock (_gate)
                {
                    file = new CatalogFile
                    {
                        Programs = _programs
                            .Select(p => new CatalogEntry { Id = p.Id, Code = p.Code, Name = p.Name })
                            .ToList(),
                        Courses = _courses.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value
                                .Select(c => new CatalogEntry { Id = c.Id, Code = c.Code, Name = c.Name })
                                .ToList())
                    };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(file));
                File.Move(temp, _path, true);
            }
            catch
            {
                // Caching must never break the data path.
            }
        }
    }

    private static ProgramInfo ToProgram(CatalogEntry entry) =>
        new(entry.Id, entry.Code ?? string.Empty, entry.Name);

    private static CourseInfo ToCourse(CatalogEntry entry) =>
        new(entry.Id, entry.Code ?? string.Empty, entry.Name);

    private sealed class CatalogFile
    {
        public List<CatalogEntry> Programs { get; set; } = [];
        public Dictionary<int, List<CatalogEntry>> Courses { get; set; } = [];
    }

    private sealed class CatalogEntry
    {
        public int Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
    }
}
