using System.IO;
using System.Text.Json;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Persisted, non-secret application settings (language, remembered username,
/// endpoints). Secrets such as passwords are kept separately in
/// <see cref="ICredentialStore"/>.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Language { get; set; } = "ar";
    public string? LastUserName { get; set; }
    public bool RememberPassword { get; set; }

    /// <summary>When true the launcher signs in automatically at startup using saved credentials.</summary>
    public bool AutoSignIn { get; set; }

    /// <summary>"light" or "dark".</summary>
    public string Theme { get; set; } = "light";

    /// <summary>Accent palette key (see <c>Theming\ThemeManager</c>), or "custom".</summary>
    public string Accent { get; set; } = "teal";

    /// <summary>User-picked global colour ("#RRGGBB") used when <see cref="Accent"/> is "custom".</summary>
    public string AccentHex { get; set; } = string.Empty;

    /// <summary>
    /// Header "radiant" key: "auto" follows the accent, otherwise a
    /// <c>Theming\ThemeManager.HeaderChoices</c> key.
    /// </summary>
    public string Header { get; set; } = "auto";

    /// <summary>Text/icon ink on accent surfaces: "auto", "black", or "white".</summary>
    public string Ink { get; set; } = "auto";

    /// <summary>When true times are shown on the 24-hour clock, otherwise 12-hour (AM/PM).</summary>
    public bool Use24HourClock { get; set; } = true;

    /// <summary>Base URL of the WebDemo service. Overridable in config.</summary>
    public string ServiceUrl { get; set; } = "https://svuis.svuonline.org/SVUIS/WebDemo_wservice/NewSessions2.php?wsdl";

    /// <summary>When true the app runs against mock data instead of the live service.</summary>
    public bool UseMockService { get; set; } = false;

    /// <summary>Folder recordings are saved to from the downloads window. Empty uses the user's Downloads folder.</summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>When true selected recordings are fetched concurrently instead of one by one.</summary>
    public bool DownloadParallel { get; set; }

    /// <summary>Last downloads-window filter picks, restored on reopen.</summary>
    public string DownloadTerm { get; set; } = string.Empty;
    public string DownloadProgram { get; set; } = string.Empty;
    public string DownloadCourse { get; set; } = string.Empty;
    public string DownloadTutor { get; set; } = string.Empty;
    public string DownloadClass { get; set; } = string.Empty;

    /// <summary>Last main-window filter picks, restored on the next sign-in.</summary>
    public int MainModeOption { get; set; } = 1;
    public int MainProgramId { get; set; }
    public int MainCourseId { get; set; }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SVU Sessions Launcher");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable settings should never block startup.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
