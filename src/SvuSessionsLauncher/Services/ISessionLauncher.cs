using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Opens a session connection for the caller. Kept behind an interface so the
/// UI does not depend on how the connection is established. Returns the live
/// <see cref="ISessionConnection"/> so the caller can host the session UI and end
/// the session by closing it.
/// </summary>
public interface ISessionLauncher
{
    Task<ISessionConnection?> LaunchAsync(SessionLaunchRequest request, CancellationToken ct = default);
}

/// <summary>Raised when the session connection cannot be established.</summary>
public sealed class SessionLaunchException : Exception
{
    public SessionLaunchException(string message, Exception? inner = null) : base(message, inner) { }
}
