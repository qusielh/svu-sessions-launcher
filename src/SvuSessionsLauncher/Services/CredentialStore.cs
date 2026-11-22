using System.IO;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Stores the "remember my password" secret. Implementations must encrypt at
/// rest; passwords are never written in plain text.
/// </summary>
public interface ICredentialStore
{
    void Save(string username, string password);
    string? Load(string username);
    void Remove(string username);
}

/// <summary>
/// Windows DPAPI-backed credential store. Secrets are encrypted with
/// <see cref="System.Security.Cryptography.ProtectedData"/> scoped to the
/// current user, so another account on the same machine cannot read them.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = "SVU.Sessions.Launcher.v2"u8.ToArray();

    private static string StorePath => AppPaths.CredentialsFile;

    public void Save(string username, string password)
    {
        if (string.IsNullOrEmpty(username))
        {
            return;
        }

        var table = ReadAll();
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(password), Entropy,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        table[username] = Convert.ToBase64String(protectedBytes);
        WriteAll(table);
    }

    public string? Load(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }

        var table = ReadAll();
        if (!table.TryGetValue(username, out var encoded))
        {
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(encoded);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes, Entropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public void Remove(string username)
    {
        var table = ReadAll();
        if (table.Remove(username))
        {
            WriteAll(table);
        }
    }

    private static Dictionary<string, string> ReadAll()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Fall through to an empty table.
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteAll(Dictionary<string, string> table)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(table);
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
