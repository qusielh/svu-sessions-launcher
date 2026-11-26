using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Abstraction over the WebDemo back-end. The published service is an
/// RPC/encoded SOAP 1.1 endpoint; this interface keeps the UI independent of
/// the transport.
/// </summary>
public interface IWebDemoService
{
    /// <summary>Validates a student's credentials. Mirrors <c>checkUserPass</c>.</summary>
    Task<AuthResult> CheckUserPasswordAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Non-zero when the signed-in user is a trainer/tutor. Mirrors <c>isUserTrainer</c>.</summary>
    Task<int> IsUserTrainerAsync(CancellationToken ct = default);

    /// <summary>Returns every programme. Mirrors <c>getPrograms</c>.</summary>
    Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default);

    /// <summary>Returns the courses in a programme. Mirrors <c>getCourses</c>.</summary>
    Task<IReadOnlyList<CourseInfo>> GetCoursesAsync(int programId, CancellationToken ct = default);

    /// <summary>Returns the sessions for a programme/course. Mirrors <c>getSessionsByProgCourse</c>.</summary>
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int programId, int courseId, int option = 0, CancellationToken ct = default);

    /// <summary>Returns the signed-in user's sessions for today. Mirrors <c>getTodaySessions</c>.</summary>
    Task<IReadOnlyList<SessionInfo>> GetTodaySessionsAsync(int option = 0, CancellationToken ct = default);

    /// <summary>Reports how many times a meeting has been launched. Mirrors <c>getMeetingLaunchCount</c>.</summary>
    Task<int> GetMeetingLaunchCountAsync(string launchedDate, int serverNumber, CancellationToken ct = default);

    /// <summary>
    /// Runs a parameter sweep against the live service and records the raw
    /// replies, so the exact contract can be reverse-engineered from real data.
    /// </summary>
    Task DiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>Thrown when the back-end reports an error or is unreachable.</summary>
public sealed class WebDemoServiceException : Exception
{
    public WebDemoServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
