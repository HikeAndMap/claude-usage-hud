using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// "Check for updates" from the tray menu: compares this build's version with the latest GitHub release of the
/// project (published automatically by .github/workflows/release.yml, which stamps the version into the exe)
/// and, on request, replaces the running exe with the release asset. Only ever talks to this one repository;
/// the download URL is required to be a release-download URL of it, and the file is checked against the size
/// and SHA-256 digest GitHub reports for the asset before it replaces anything.
/// </summary>
public static class UpdateChecker
{
    private const string Repo = "HikeAndMap/claude-usage-hud";
    private const string ExpectedUrlPrefix = $"https://github.com/{Repo}/releases/download/";

    // Generous timeout: it covers the ~110MB download (headers-only mode below means it effectively bounds
    // connect + headers, but a stalled body is still caught by the cancellation token passed to the copy).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeUsageHUD-update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256);

    /// <summary>This build's version as stamped by the release workflow (-p:Version). A local dev build isn't
    /// stamped, so it reports 1.0.0.0 and never offers itself an "update".</summary>
    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Returns the latest release, or throws with a message fit to show the user.</summary>
    public static async Task<ReleaseInfo> GetLatestAsync()
    {
        using HttpResponseMessage response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub answered HTTP {(int)response.StatusCode}.");

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out Version? version))
        {
            throw new InvalidOperationException($"Couldn't read the version from release tag \"{tag}\".");
        }

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "ClaudeUsageHUD.exe") continue;

            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            return new ReleaseInfo(version, tag, root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "",
                url, asset.GetProperty("size").GetInt64(), sha);
        }
        throw new InvalidOperationException($"Release {tag} has no ClaudeUsageHUD.exe attached.");
    }

    /// <summary>Downloads the release exe next to the running one, verifies it, and swaps it in. Windows lets a
    /// running exe be renamed but not overwritten, so: running exe → .old, new exe → its name; the caller then
    /// relaunches and exits. Throws with a user-readable message on any failure, leaving the original in place.</summary>
    public static async Task InstallAsync(ReleaseInfo release)
    {
        if (!release.DownloadUrl.StartsWith(ExpectedUrlPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to download from an unexpected address.");
        }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running exe.");
        string staged = exe + ".update";
        string old = exe + ".old";

        try
        {
            using (HttpResponseMessage response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Download failed: HTTP {(int)response.StatusCode}.");
                await using Stream source = await response.Content.ReadAsStreamAsync();
                await using FileStream target = File.Create(staged);
                await source.CopyToAsync(target);
            }

            if (new FileInfo(staged).Length != release.Size)
            {
                throw new InvalidOperationException("Downloaded file is the wrong size - not installing it.");
            }
            if (release.Sha256 != null)
            {
                using FileStream check = File.OpenRead(staged);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Downloaded file failed its checksum - not installing it.");
                }
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(staged, exe);
            }
            catch
            {
                File.Move(old, exe); // put the original back rather than leave no exe at all
                throw;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"No permission to write to {Path.GetDirectoryName(exe)}.");
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>Removes the previous exe left behind by the last update. Best effort - at the moment the new
    /// instance starts the old process may still be exiting, in which case the next launch gets it.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch (Exception) { /* still locked, or not ours to delete - try again next launch */ }
    }
}
