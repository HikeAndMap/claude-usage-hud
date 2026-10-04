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

    private static string ClaudeLoginPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>The HUD's own sign-in (HudLogin - renewed by the HUD, never shared) wins; then the read-only
    /// fallbacks in <see cref="Resolve"/>.</summary>
    public static async Task<(string? Token, string Source)> ResolveAsync()
    {
        string? own = await HudLogin.GetAccessTokenAsync();
        if (own != null) return (own, "HUD sign-in");

        string? token = Resolve(out string source);
        return (token, source);
    }

    /// <summary>Picks the token to call the usage endpoint with: Claude Code's own login first (its token has
    /// the user:profile scope the endpoint requires, which a setup-token one lacks - seen as HTTP 403 on
    /// 2026-10-04), else the token pasted via the tray menu. Read fresh every call because Claude Code renews
    /// that file in place; this app only ever READS it - never refreshes the token, since refreshing can rotate
    /// the refresh token and sign Claude Code itself out. An expired file token is skipped (Claude Code renews
    /// it the next time it runs) rather than sent to fail. Nothing from the file is ever logged.</summary>
    public static string? Resolve(out string source)
    {
        try
        {
            if (File.Exists(ClaudeLoginPath))
            {
                using FileStream stream = new(ClaudeLoginPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("claudeAiOauth", out System.Text.Json.JsonElement oauth)
                    && oauth.TryGetProperty("accessToken", out System.Text.Json.JsonElement tokenEl)
                    && tokenEl.GetString() is { Length: > 0 } fileToken)
                {
                    bool expired = oauth.TryGetProperty("expiresAt", out System.Text.Json.JsonElement exp)
                        && exp.TryGetInt64(out long expMs)
                        && DateTimeOffset.FromUnixTimeMilliseconds(expMs) <= DateTimeOffset.UtcNow;
                    if (!expired)
                    {
                        source = "Claude login";
                        return fileToken;
                    }
                    PlanUsageReader.LogDiagnostic("claude login token in credentials file is expired; waiting for Claude Code to renew it");
                }
                else
                {
                    PlanUsageReader.LogDiagnostic("credentials file has no claudeAiOauth.accessToken");
                }
            }
        }
        catch (Exception ex)
        {
            PlanUsageReader.LogDiagnostic($"could not read credentials file: {ex.GetType().Name}");
        }

        string? pasted = Load();
        source = "pasted token";
        return pasted;
    }

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
