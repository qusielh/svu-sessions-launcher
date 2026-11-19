using System.IO;

namespace SvuSessionsLauncher.Services;

/// <summary>Minimal file logger used for diagnosing live-service problems.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SVU Sessions Launcher");
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}