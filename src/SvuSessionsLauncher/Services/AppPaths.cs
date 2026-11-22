using System.IO;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// The one place that knows where the launcher writes files. Settings, the log
/// and diagnostic dumps live under roaming app data; the catalog cache and the
/// DPAPI credential blob live under local app data. Keeping every path here
/// stops a feature from silently inventing a second folder with a slightly
/// different name.
/// </summary>
public static class AppPaths
{
    private const string FolderName = "SVU Sessions Launcher";

    /// <summary><c>%APPDATA%\SVU Sessions Launcher</c>.</summary>
    public static string RoamingDirectory { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName));

    /// <summary><c>%LOCALAPPDATA%\SVU Sessions Launcher</c>.</summary>
    public static string LocalDirectory { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName));

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");
    public static string LogFile => Path.Combine(RoamingDirectory, "error.log");
    public static string PreviousLogFile => Path.Combine(RoamingDirectory, "error.previous.log");
    public static string ResponsesDirectory => Ensure(Path.Combine(RoamingDirectory, "responses"));
    public static string CredentialsFile => Path.Combine(LocalDirectory, "credentials.dat");
    public static string CatalogFile => Path.Combine(LocalDirectory, "catalog.json");

    static AppPaths()
    {
        MigrateLegacyCatalog();
    }

    /// <summary>
    /// Earlier builds kept the catalog under a folder without spaces. Copy it
    /// once so an offline first run still has the programme list; the source is
    /// left alone because it is only a cache.
    /// </summary>
    private static void MigrateLegacyCatalog()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SvuSessionsLauncher",
                "catalog.json");
            if (File.Exists(legacy) && !File.Exists(CatalogFile))
            {
                File.Copy(legacy, CatalogFile);
            }
        }
        catch
        {
            // A missing cache is harmless: the service refreshes it.
        }
    }

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
