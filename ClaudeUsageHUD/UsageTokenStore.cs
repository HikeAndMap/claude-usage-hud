using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageHUD;

/// <summary>
/// Holds the OAuth token the user pastes in via the tray menu, encrypted with DPAPI (current-user scope) so
/// it's unreadable to other Windows accounts and never sits in plain text in settings.json. Deliberately a
/// separate file from HudSettings so the secret can't be dragged along by anything that copies or logs settings.
/// </summary>
public static class UsageTokenStore
{
    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "usage-token.bin");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            string token = Encoding.UTF8.GetString(plain);
            return token.Length == 0 ? null : token;
        }
        catch (Exception)
        {
            // Corrupt file, or encrypted under a different Windows account - treat as "no token set".
            return null;
        }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenPath, protectedBytes);
    }

    public static void Clear()
    {
        try { File.Delete(TokenPath); }
        catch (Exception) { /* nothing useful to do - worst case the old token lingers until next Save */ }
    }
}
