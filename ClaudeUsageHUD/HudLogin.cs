using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// The HUD's own Claude sign-in (OAuth authorization-code flow with PKCE, loopback redirect), so it has a login
/// that carries the user:profile scope the usage endpoint needs AND that nothing else shares - which is what
/// makes renewing it here safe (a refresh can rotate the refresh token and sign out any other client holding
/// the same one). Tokens live only in the HUD's own DPAPI-encrypted file; this never reads or writes Claude
/// Code's or the desktop app's credential stores. Endpoints, client id and scope names are undocumented and
/// taken from community tooling, so every failure is logged (status and error body, never a token) and
/// surfaced to the user rather than assumed.
/// </summary>
public static class HudLogin
{
    // Verified 2026-10-04 against the constants inside the bundled claude.exe 2.1.286 (CLAUDE_AI_AUTHORIZE_URL /
    // TOKEN_URL / CLIENT_ID). The older claude.ai/oauth/authorize + console.anthropic.com/v1/oauth/token pair
    // from community tooling is stale - the authorize page answered it with "invalid request format".
    private const string AuthorizeEndpoint = "https://claude.com/cai/oauth/authorize";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string Scopes = "user:profile user:inference";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "hud-login.bin");

    private sealed record Tokens(string AccessToken, string RefreshToken, long ExpiresAtMs);

    public static bool IsSignedIn => File.Exists(StorePath);

    public static void SignOut()
    {
        try { File.Delete(StorePath); }
        catch (Exception) { /* nothing useful to do */ }
    }

    /// <summary>Returns a valid access token (renewing it first if it expires within 2 minutes), or null if not
    /// signed in or the renewal failed - the caller then falls back to other token sources.</summary>
    public static async Task<string?> GetAccessTokenAsync()
    {
        Tokens? tokens = Load();
        if (tokens == null) return null;
        if (DateTimeOffset.FromUnixTimeMilliseconds(tokens.ExpiresAtMs) > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return tokens.AccessToken;
        }

        _grantRejected = false;
        Tokens? renewed = await ExchangeAsync(new
        {
            grant_type = "refresh_token",
            refresh_token = tokens.RefreshToken,
            client_id = ClientId,
        }, tokens.RefreshToken, "refresh");
        if (renewed == null)
        {
            if (_grantRejected)
            {
                // The refresh token is dead for good ("invalid_grant ... Refresh token expired", seen 2026-11-04
                // after ~a month of working renewals and a two-day gap with no successful refresh) - retrying
                // every minute can't help and only spams the log, so drop it and ask the user to sign in again.
                SignOut();
                SignInExpired?.Invoke();
            }
            return null;
        }
        Save(renewed);
        return renewed.AccessToken;
    }

    /// <summary>Raised (on the UI thread) when the stored login can no longer be renewed and was discarded -
    /// only a fresh browser sign-in fixes it, so the tray offers one.</summary>
    public static event Action? SignInExpired;

    private static bool _grantRejected;

    /// <summary>Opens the browser for the user to authorize, catches the redirect on a loopback port, and stores
    /// the resulting tokens. Returns null on success, else a short reason to show the user.</summary>
    public static async Task<string?> SignInAsync()
    {
        try
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            // 32 bytes like Claude Code (43 chars) - an earlier 16-byte (22-char) state was one of the few
            // differences left from its authorize URL while the page still answered "invalid request format".
            string state = Base64Url(RandomNumberGenerator.GetBytes(32));

            int port = FreePort();
            string redirect = $"http://localhost:{port}/callback";
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();

            // Claude Code (g5n() in claude.exe 2.1.286) sends code=true even with a loopback redirect, and the
            // authorize page rejects the request as "invalid request format" without it.
            string url = AuthorizeEndpoint + "?" + string.Join("&", new (string, string)[]
            {
                ("code", "true"),
                ("client_id", ClientId),
                ("response_type", "code"),
                ("redirect_uri", redirect),
                ("scope", Scopes),
                ("code_challenge", challenge),
                ("code_challenge_method", "S256"),
                ("state", state),
            }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            Task<HttpListenerContext> pending = listener.GetContextAsync();
            if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMinutes(5))) != pending)
            {
                return "timed out waiting for authorization in the browser";
            }
            HttpListenerContext ctx = await pending;

            string? code = ctx.Request.QueryString["code"];
            string? returnedState = ctx.Request.QueryString["state"];
            string? error = ctx.Request.QueryString["error"];
            await RespondAsync(ctx, error == null && code != null
                ? "Signed in - you can close this tab and return to the HUD."
                : "Sign-in was not completed - you can close this tab.");

            if (error != null) return $"authorization was not granted ({error})";
            if (code == null || returnedState != state) return "unexpected response from the browser";

            Tokens? tokens = await ExchangeAsync(new
            {
                grant_type = "authorization_code",
                code,
                redirect_uri = redirect,
                client_id = ClientId,
                code_verifier = verifier,
                state,
            }, null, "sign-in");
            if (tokens == null) return "token exchange failed - see weekly-diagnostic.log";

            Save(tokens);
            return null;
        }
        catch (Exception ex)
        {
            PlanUsageReader.LogDiagnostic($"hud sign-in: {ex.GetType().Name}: {ex.Message}");
            return ex.GetType().Name;
        }
    }

    private static async Task<Tokens?> ExchangeAsync(object body, string? previousRefreshToken, string what)
    {
        try
        {
            using HttpResponseMessage response = await Http.PostAsJsonAsync(TokenEndpoint, body);
            string text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // Error bodies describe the problem; they don't echo the credentials we sent.
                PlanUsageReader.LogDiagnostic($"hud {what}: HTTP {(int)response.StatusCode}; body={(text.Length <= 300 ? text : text[..300] + "...")}");
                _grantRejected = text.Contains("invalid_grant", StringComparison.Ordinal);
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            string? access = doc.RootElement.TryGetProperty("access_token", out JsonElement a) ? a.GetString() : null;
            string? refresh = doc.RootElement.TryGetProperty("refresh_token", out JsonElement r) ? r.GetString() : previousRefreshToken;
            long expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt64(out long s) ? s : 3600;
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                PlanUsageReader.LogDiagnostic($"hud {what}: 200 but response lacked access_token/refresh_token");
                return null;
            }
            return new Tokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            PlanUsageReader.LogDiagnostic($"hud {what}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Tokens? Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Tokens>(plain);
        }
        catch (Exception)
        {
            // Corrupt, or encrypted under another Windows account - same as being signed out.
            return null;
        }
    }

    private static void Save(Tokens tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        byte[] protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, protectedBytes);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task RespondAsync(HttpListenerContext ctx, string message)
    {
        byte[] page = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Claude Usage HUD</title><body style=\"font-family:Segoe UI,sans-serif;padding:2em\"><p>{message}</p>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = page.Length;
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
