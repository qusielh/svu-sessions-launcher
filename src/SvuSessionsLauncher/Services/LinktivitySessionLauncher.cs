using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Opens a student session with the in-process Linktivity client. There is no
/// bundled Java engine or vendor UI: the connection is created directly and the
/// retry wrapper keeps the join resilient without relaunching a child process.
/// </summary>
public sealed class LinktivitySessionLauncher : ISessionLauncher
{
    private const int MeetingPort = 443;
    private const int MaxJoinAttempts = 3;

    public Task<ISessionConnection?> LaunchAsync(SessionLaunchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var host = MeetingHost(request.Session);
        var width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        var height = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

        Log.Write($"Linktivity student session: {host}:{MeetingPort} session={request.LinkId} {width}x{height}");

        var connection = new RetryingSessionConnection(
            () => new LinktivitySessionConnection(
                host,
                MeetingPort,
                request.LinkId,
                request.Username,
                width,
                height),
            MaxJoinAttempts);

        return Task.FromResult<ISessionConnection?>(connection);
    }

    /// <summary>
    /// The meeting server for a session. Prefer the host advertised in the
    /// student URL (for example <c>class8.svuonline.org</c>); otherwise derive
    /// it from the zero-based <see cref="SessionInfo.ServerNumber"/>.
    /// </summary>
    private static string MeetingHost(SessionInfo session)
    {
        if (!string.IsNullOrWhiteSpace(session.StudentUrl) &&
            Uri.TryCreate(session.StudentUrl, UriKind.Absolute, out var uri) &&
            uri.Host.StartsWith("class", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host;
        }

        return $"class{session.ServerNumber + 1}.svuonline.org";
    }
}
