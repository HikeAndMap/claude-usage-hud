using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>One parsed usage snapshot from the most recent assistant message in the active session's transcript.</summary>
public sealed record UsageSnapshot(string Model, long PromptTokens, DateTimeOffset Timestamp);

/// <summary>
/// Reads Claude Code's own local session transcripts (~/.claude/projects/&lt;project&gt;/&lt;session-id&gt;.jsonl,
/// one JSON object per line, one line per message) to derive the current context-window usage - no private
/// API, no stored credentials, just the same files Claude Code itself already writes. The "active" session is
/// taken to be whichever .jsonl file across every project was most recently modified - confirmed empirically
/// (2026-09-06) to reliably identify the actual live session, since only the session currently receiving
/// messages is being appended to.
/// </summary>
public static class SessionUsageReader
{
    private static readonly string ProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    // Only the tail of a session file ever matters (we just want the LAST usage entry), and these files can
    // grow to tens of megabytes over a long session - re-reading the whole file every poll tick would be
    // wasteful. This is generous enough that the last complete JSONL line is virtually always inside it
    // (a single line rarely exceeds a few hundred KB even with large tool outputs embedded).
    private const int TailBytesToRead = 512 * 1024;

    public static string? FindActiveSessionFile()
    {
        if (!Directory.Exists(ProjectsRoot)) return null;

        string? latestPath = null;
        DateTime latestTime = DateTime.MinValue;

        foreach (string path in Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime > latestTime)
            {
                latestTime = writeTime;
                latestPath = path;
            }
        }

        return latestPath;
    }

    /// <summary>
    /// Reads just the tail of the given transcript file and returns the most recent line that carries a
    /// message.usage object - null if the file has no such line within the tail window (e.g. a brand-new
    /// session with only a user message so far).
    /// </summary>
    public static UsageSnapshot? ReadLatestUsage(string sessionFilePath)
    {
        string text;
        using (var stream = new FileStream(sessionFilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            long start = Math.Max(0, stream.Length - TailBytesToRead);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }

        string[] lines = text.Split('\n');
        // Walk backwards - the first (from the end) line with a usage object is the most recent one.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            UsageSnapshot? snapshot = TryParseUsageLine(line);
            if (snapshot != null) return snapshot;
        }

        return null;
    }

    private static UsageSnapshot? TryParseUsageLine(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("message", out JsonElement message)) return null;
            if (!message.TryGetProperty("usage", out JsonElement usage)) return null;
            if (!message.TryGetProperty("model", out JsonElement modelElement)) return null;

            long inputTokens = usage.TryGetProperty("input_tokens", out JsonElement it) ? it.GetInt64() : 0;
            long cacheCreation = usage.TryGetProperty("cache_creation_input_tokens", out JsonElement cc) ? cc.GetInt64() : 0;
            long cacheRead = usage.TryGetProperty("cache_read_input_tokens", out JsonElement cr) ? cr.GetInt64() : 0;

            DateTimeOffset timestamp = root.TryGetProperty("timestamp", out JsonElement ts) && ts.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(ts.GetString()!)
                : DateTimeOffset.UtcNow;

            return new UsageSnapshot(modelElement.GetString() ?? "unknown", inputTokens + cacheCreation + cacheRead, timestamp);
        }
        catch (JsonException)
        {
            // A truncated first line inside the tail window (we sliced mid-line) or a non-usage line
            // (e.g. a plain user/tool_result message) - not an error, just skip it.
            return null;
        }
    }
}
