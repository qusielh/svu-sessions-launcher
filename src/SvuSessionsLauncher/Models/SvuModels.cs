using System.ComponentModel;
using SvuSessionsLauncher.Localization;

namespace SvuSessionsLauncher.Models;
/// <summary>
/// A programme a student is enrolled in. Mirrors the fields returned by the
/// WebDemo <c>getPrograms</c> method (programid, programcode).
/// </summary>
public sealed class ProgramInfo : INotifyPropertyChanged
{
    public ProgramInfo(int Id, string Code, string? Name = null, bool IsAll = false)
    {
        this.Id = Id;
        this.Code = Code;
        this.Name = Name;
        this.IsAll = IsAll;
    }

    public int Id { get; }
    public string Code { get; }
    public string? Name { get; }
    public bool IsAll { get; }

    public string Display =>
        IsAll ? Loc.Instance["main.allPrograms"]
        : string.IsNullOrWhiteSpace(Name) ? Code
        : string.IsNullOrWhiteSpace(Code) ? Name
        : $"{Name} ({Code})";

    public override string ToString() => Display;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the localised label after a language switch.</summary>
    public void RaiseDisplayChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}

/// <summary>
/// A course belonging to a programme. Mirrors <c>getCourses</c>
/// (courseid, coursecode).
/// </summary>
public sealed class CourseInfo : INotifyPropertyChanged
{
    public CourseInfo(int Id, string Code, string? Name = null, bool IsAll = false)
    {
        this.Id = Id;
        this.Code = Code;
        this.Name = Name;
        this.IsAll = IsAll;
    }

    public int Id { get; }
    public string Code { get; }
    public string? Name { get; }
    public bool IsAll { get; }

    public string Display =>
        IsAll ? Loc.Instance["main.allCourses"]
        : string.IsNullOrWhiteSpace(Name) ? Code
        : string.IsNullOrWhiteSpace(Code) ? Name
        : $"{Name} ({Code})";

    public override string ToString() => Display;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the localised label after a language switch.</summary>
    public void RaiseDisplayChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}

/// <summary>
/// A single online session row. Mirrors the fields returned by
/// <c>getSessionsByProgCourse</c>:
/// sessionid, sessionname, studenturl, status, startdate, launcheddate,
/// servernumber, tutorid, tutorname, classid, tutor.
/// </summary>
public sealed class SessionInfo
{
    public int SessionId { get; init; }
    public string SessionName { get; init; } = string.Empty;
    public string StudentUrl { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? StartDate { get; init; }
    public string LaunchedDate { get; init; } = string.Empty;
    public int ServerNumber { get; init; }
    public int TutorId { get; init; }
    public string TutorName { get; init; } = string.Empty;
    public int ClassId { get; init; }
    public bool IsTutor { get; init; }

    private string[]? _nameParts;

    // Real session names are shaped PROGRAM_COURSE_C<class>_F<term>_S<session>,
    // e.g. "ITE_BOS501_C7_F25_S3". These helpers pull the pieces out so the UI
    // can show them as separate columns/filters instead of one long string.
    private string[] NameParts => _nameParts ??=
        SessionName.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string NamePartAt(int index) => index < NameParts.Length ? NameParts[index] : string.Empty;

    private string FindNameToken(char prefix)
    {
        foreach (var part in NameParts)
        {
            if (part.Length > 1 && char.ToUpperInvariant(part[0]) == prefix && IsAllDigits(part.AsSpan(1)))
            {
                return part;
            }
        }

        return string.Empty;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Programme code parsed from the session name (e.g. "ITE").</summary>
    public string ProgramCode => NamePartAt(0);

    /// <summary>Course code parsed from the session name (e.g. "BOS501").</summary>
    public string CourseCode => NamePartAt(1);

    private string? _classToken;
    private string? _sessionToken;
    private string? _termToken;

    /// <summary>Class token parsed from the session name (e.g. "C7").</summary>
    public string ClassToken => _classToken ??= FindNameToken('C');

    /// <summary>Session token parsed from the session name (e.g. "S3").</summary>
    public string SessionToken => _sessionToken ??= FindNameToken('S');

    /// <summary>Term token parsed from the session name (e.g. "F25"). Constant per term.</summary>
    public string TermToken => _termToken ??= FindNameToken('F');

    /// <summary>Digits of the class token (e.g. "7"); empty when unknown.</summary>
    public string ClassNumber => ClassToken.Length > 1 ? ClassToken[1..] : string.Empty;

    /// <summary>Digits of the session token (e.g. "3"); empty when unknown.</summary>
    public string SessionNumber => SessionToken.Length > 1 ? SessionToken[1..] : string.Empty;

    private SessionStatus? _status;

    private SessionStatus NormalizedStatus => _status ??= StatusCode.FromRaw(Status);

    /// <summary>
    /// Normalised status name ("Available", "InProgress", …) used by the status
    /// chip's style triggers. Kept as a string so the XAML triggers compare
    /// reliably against a literal value.
    /// </summary>
    public string StatusKind => NormalizedStatus.ToString();

    /// <summary>
    /// True when the name follows the structured shape and can be shown as
    /// separate course/class/session cells; otherwise the UI falls back to the
    /// raw name.
    /// </summary>
    public bool HasStructuredName => ClassToken.Length > 0 || SessionToken.Length > 0;

    /// <summary>What to show in a single "course" cell; falls back to the raw name.</summary>
    public string CourseLabel => CourseCode.Length > 0 ? CourseCode : SessionName;

    /// <summary>
    /// A session is launchable whenever an address for it can be resolved. The
    /// service status only describes the timetable: sessions whose time slot has
    /// already passed stay joinable (and re-joinable) exactly like the original
    /// launcher did. A tutor closing the door is a different thing - that is a
    /// runtime event delivered by the session connection, not a list status, and
    /// it still ends the session for everyone.
    /// </summary>
    public bool CanLaunch => SessionId > 0 || !string.IsNullOrWhiteSpace(StudentUrl);

    /// <summary>True when the session's scheduled time slot is already over.</summary>
    public bool HasEnded => NormalizedStatus is SessionStatus.Finished or SessionStatus.Cancelled;
}

/// <summary>
/// Normalised session status. The raw values coming back from the service are
/// mapped here so the UI can present localised, human-readable labels.
/// </summary>
public enum SessionStatus
{
    Unknown = 0,
    Available,
    InProgress,
    Finished,
    Cancelled
}

public static class StatusCode
{
    public static SessionStatus FromRaw(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" => SessionStatus.Unknown,
            "available" or "open" or "ready" or "planned" or "scheduled" or "1" => SessionStatus.Available,
            "inprogress" or "in_progress" or "started" or "active" or "live" or "2" => SessionStatus.InProgress,
            "finished" or "ended" or "closed" or "done" or "3" => SessionStatus.Finished,
            "cancelled" or "canceled" or "4" => SessionStatus.Cancelled,
            _ => SessionStatus.Unknown
        };
    }

    /// <summary>Localisation key for the given status.</summary>
    public static string ToLocKey(SessionStatus status) => status switch
    {
        SessionStatus.Available => "status.available",
        SessionStatus.InProgress => "status.inProgress",
        SessionStatus.Finished => "status.finished",
        SessionStatus.Cancelled => "status.cancelled",
        _ => "status.unknown"
    };
}

/// <summary>Result of a credential check against the service.</summary>
public sealed record AuthResult(bool Success, string? DisplayName = null, string? Message = null)
{
    public static AuthResult Ok(string? displayName = null) => new(true, displayName);
    public static AuthResult Fail(string? message = null) => new(false, null, message);
}

/// <summary>Everything needed to open a Linktivity student session.</summary>
public sealed record SessionLaunchRequest(
    SessionInfo Session,
    string Username,
    string LinkId,
    string SessionParameter);
