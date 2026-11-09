using System.IO;
using System.Text;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// TEMPORARY DIAGNOSTIC LOGGER for the student-side connection engine.
///
/// <para>Purpose: capture the real Linktivity traffic (framing, control/request
/// pairs, peer field layout, chat send bytes, viewer frames) so the connection
/// behaviour can be understood from a live session instead of guessing.</para>
///
/// <para><b>Removal:</b> this file and every call site are tagged with the
/// <c>SVUDBG</c> marker (and call sites also carry a <c>// SVUDBG</c> comment).
/// To strip the diagnostics from a release build, either set
/// <see cref="Enabled"/> to <c>false</c> (compiles the writes away behind a
/// branch) or search for <c>SVUDBG</c> and delete the file plus those lines.</para>
///
/// <para>Writes to <c>%APPDATA%\SVU Sessions Launcher\session-debug.log</c>.
/// Never throws; logging must not take the session down.</para>
/// </summary>
public static class SvuDebugLog
{
    // SVUDBG: master switch. Flip to false (or delete this file + call sites).
    public static bool Enabled = true;

    private const string FileName = "session-debug.log";

    private static readonly object Gate = new();

    static SvuDebugLog()
    {
        // Start each app run with a clean, timestamped section so one session's
        // traffic is never mixed with a previous run's.
        if (!Enabled)
        {
            return;
        }

        try
        {
            var dir = DirectoryPath();
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(dir, FileName),
                    $"\r\n===== SVUDBG run {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from logging.
        }
    }

    /// <summary>Logs a tagged line. <paramref name="tag"/> groups the source
    /// (e.g. <c>ENG</c>, <c>CONN</c>, <c>SESS</c>, <c>VIEW</c>).</summary>
    public static void Write(string tag, string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var dir = DirectoryPath();
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(dir, FileName),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}\r\n",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from logging.
        }
    }

    private static string DirectoryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVU Sessions Launcher");
}
