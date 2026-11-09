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

    /// <summary>
    /// Temporary performance probe. Runs <paramref name="action"/> and logs how
    /// many milliseconds it took, including a synchronous flush of queued WPF
    /// layout + render work so the figure reflects what the user actually feels
    /// (not just the collection-code cost). Call sites are marked with a
    /// "PERF PROBE" comment for easy removal once tuning is finished.
    /// </summary>
    public static void Perf(string label, Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();

        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }
        }
        catch
        {
            // Probing must never take the app down.
        }

        Write($"[perf] {label}: {sw.ElapsedMilliseconds} ms");
    }
}