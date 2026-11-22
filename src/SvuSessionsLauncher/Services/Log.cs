using System.IO;
using System.Text.RegularExpressions;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Minimal file logger used for diagnosing live-service problems. Every line
/// is sanitized before it reaches disk: URL query strings, password-shaped
/// values and XML password elements are removed, so a log can be shared for
/// support without handing over credentials. The file is rotated once it
/// reaches a small size cap instead of growing forever.
/// </summary>
public static class Log
{
    private const int MaxMessageLength = 8_000;
    private const long MaxLogBytes = 1_000_000;

    private static readonly object Gate = new();

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Covers "password=...", "password: ...", "password":"..." and the same
    // shapes for pwd/secret/token/api-key. The value may be quoted or bare.
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)[""']?\b(pass(?:word)?|pwd|secret|token|api[_-]?key)\b[""']?(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s,;)]+)",
        RegexOptions.Compiled);

    private static readonly Regex SecretElementPattern = new(
        @"(?is)(<pass(?:word)?\b[^>]*>)(.*?)(</pass(?:word)?>)",
        RegexOptions.Compiled);

    // The WebDemo service uses "Not Found <user>" as its unknown-account
    // pseudo-row. Keep that diagnostic shape but never persist the account.
    private static readonly Regex NotFoundUserPattern = new(
        @"(?i)\bnot\s+found\s+[^\s,;]+",
        RegexOptions.Compiled);

    public static void Write(string message)
    {
        try
        {
            var safe = Sanitize(message);
            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    AppPaths.LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {safe}\r\n");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>
    /// Removes the parts of a message that should never be persisted. Public
    /// for tests and for reviewers who want to check the exact transformation.
    /// </summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var text = SecretElementPattern.Replace(message, "$1***$3");
        text = SecretAssignmentPattern.Replace(text, "$1$2***");
        text = NotFoundUserPattern.Replace(text, "Not Found <redacted>");
        text = UrlPattern.Replace(text, match => StripQuery(match.Value));

        if (text.Length > MaxMessageLength)
        {
            text = text[..MaxMessageLength] + "\u2026";
        }

        return text;
    }

    private static string StripQuery(string url)
    {
        int query = url.IndexOf('?');
        return query < 0 ? url : url[..query];
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        File.Delete(AppPaths.PreviousLogFile);
        File.Move(AppPaths.LogFile, AppPaths.PreviousLogFile);
    }
}
