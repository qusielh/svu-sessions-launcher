using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// In-memory implementation used for design-time, demos and offline testing.
/// It returns realistic sample data without touching the network.
/// </summary>
public sealed class MockWebDemoService : IWebDemoService
{
    private static readonly ProgramInfo[] Programs =
    [
        new(1, "IT", "Information Technology"),
        new(2, "BA", "Business Administration"),
        new(3, "ED", "Education")
    ];

    private static readonly Dictionary<int, CourseInfo[]> Courses = new()
    {
        [1] =
        [
            new(101, "IT101", "Introduction to Programming"),
            new(102, "IT201", "Data Structures"),
            new(103, "IT301", "Databases")
        ],
        [2] =
        [
            new(201, "BA101", "Principles of Management"),
            new(202, "BA201", "Marketing")
        ],
        [3] =
        [
            new(301, "ED101", "Foundations of Education")
        ]
    };

    public async Task<AuthResult> CheckUserPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        await Task.Delay(600, ct);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail("login.error.empty");
        }

        return AuthResult.Ok(username);
    }

    public async Task<int> IsUserTrainerAsync(CancellationToken ct = default)
    {
        await Task.Delay(150, ct);
        return 0;
    }

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default)
    {
        await Task.Delay(250, ct);
        return Programs;
    }

    public async Task<IReadOnlyList<CourseInfo>> GetCoursesAsync(int programId, CancellationToken ct = default)
    {
        await Task.Delay(250, ct);
        return Courses.TryGetValue(programId, out var list) ? list : [];
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int programId, int courseId, int option = 0, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        var today = DateTime.Today;
        return
        [
            new SessionInfo
            {
                SessionId = 1001 + courseId,
                SessionName = $"Lecture 1 - Course {courseId}",
                StudentUrl = "https://sessions.svuonline.org/session/1001",
                Status = "available",
                StartDate = today.AddHours(9),
                LaunchedDate = today.ToString("yyyy-MM-dd"),
                ServerNumber = 1,
                TutorId = 55,
                TutorName = "Dr. Ahmad",
                ClassId = courseId
            },
            new SessionInfo
            {
                SessionId = 1002 + courseId,
                SessionName = $"Lab - Course {courseId}",
                StudentUrl = "https://sessions.svuonline.org/session/1002",
                Status = "inprogress",
                StartDate = today.AddHours(11),
                LaunchedDate = today.ToString("yyyy-MM-dd"),
                ServerNumber = 2,
                TutorId = 56,
                TutorName = "Eng. Lina",
                ClassId = courseId
            },
            new SessionInfo
            {
                SessionId = 1003 + courseId,
                SessionName = $"Tutorial - Course {courseId}",
                StudentUrl = string.Empty,
                Status = "finished",
                StartDate = today.AddDays(-2).AddHours(10),
                LaunchedDate = today.AddDays(-2).ToString("yyyy-MM-dd"),
                ServerNumber = 1,
                TutorId = 55,
                TutorName = "Dr. Ahmad",
                ClassId = courseId
            }
        ];
    }

    public async Task<IReadOnlyList<SessionInfo>> GetTodaySessionsAsync(int option = 0, CancellationToken ct = default)
    {
        await Task.Delay(350, ct);
        var sessions = await GetSessionsAsync(1, 101, option, ct);
        return sessions.Count > 0 ? sessions : [];
    }

    public Task DiagnosticsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<int> GetMeetingLaunchCountAsync(string launchedDate, int serverNumber, CancellationToken ct = default)
    {
        await Task.Delay(150, ct);
        return 3;
    }
}