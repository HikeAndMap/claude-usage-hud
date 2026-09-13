using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// Reads the Claude desktop app's own plan-usage history file (%AppData%\Claude\plan-usage-history.json) to get
/// the current weekly and rolling-5-hour usage percentages - the same data backing the desktop app's built-in
/// usage indicator. The file is a JSON object with a "samples" array, each entry shaped like {"t": epochMs,
/// "org": orgId, "u": {"fh": five-hour %, "sd": seven-day/weekly %}}, appended roughly every 15 minutes; the
/// last entry is the current reading. No reset timestamps are stored anywhere in the file (see
/// <see cref="ReadLatestFiveHourUsage"/> for how the 5-hour reset time is estimated instead).
/// </summary>
public static class PlanUsageReader
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");

    // Where we dump forensic detail on the "not found" path below - kept tiny and separate from the main
    // settings file (see HudSettings) so a burst of these never touches user-facing state.
    private static readonly string DiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "weekly-diagnostic.log");

    // Reset the log rather than let it grow forever if this starts happening every poll (every 4s).
    private const long MaxDiagnosticLogBytes = 256 * 1024;

    /// <summary>Returns the most recent weekly usage percentage (0-100), or null if the file is missing, empty,
    /// or every sample in it has no "sd" figure yet.</summary>
    public static int? ReadLatestWeeklyPercent()
    {
        // Deliberately no File.Exists() pre-check: it swallows EVERY exception (permission issues, sharing
        // violations, transient I/O errors - anything, not just "genuinely missing") and reports them all as
        // false, indistinguishable from a real missing file. That hid the actual cause of a persistent "n/a"
        // seen 2026-09-08 through 2026-09-10 (always cleared by restarting the HUD, never by anything changing
        // on disk). Opening the file directly and inspecting the real exception type instead tells us, next
        // time, whether this is genuinely FileNotFoundException or something else File.Exists was masking.
        using FileStream? stream = TryOpen();
        if (stream == null) return null;

        using JsonDocument doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("samples", out JsonElement samples))
        {
            LogDiagnostic("no \"samples\" property on root; raw root=" + Truncate(doc.RootElement.GetRawText(), 500));
            return null;
        }
        if (samples.ValueKind != JsonValueKind.Array)
        {
            LogDiagnostic($"\"samples\" is not an array (ValueKind={samples.ValueKind})");
            return null;
        }

        // Walk backwards - the desktop app appends a new sample's timestamp before it fills in that sample's
        // "u" figures a tick later, so the very last entry can momentarily have an empty "u": {} with no "sd"
        // yet. The most recent entry that actually HAS one is still the right reading to show.
        for (int i = samples.GetArrayLength() - 1; i >= 0; i--)
        {
            JsonElement sample = samples[i];
            if (sample.TryGetProperty("u", out JsonElement usage) && usage.TryGetProperty("sd", out JsonElement sd))
            {
                return sd.GetInt32();
            }
        }

        // Every sample in the file lacks "sd" - this shouldn't happen once the account has any weekly usage
        // recorded at all. Log what we actually saw so a recurrence is diagnosable without needing to catch
        // the HUD showing "n/a" live again.
        int count = samples.GetArrayLength();
        int tailStart = Math.Max(0, count - 5);
        var tail = new List<string> { $"no sample with \"sd\" found; array length={count}" };
        for (int i = tailStart; i < count; i++)
        {
            tail.Add($"  [{i}] {samples[i].GetRawText()}");
        }
        LogDiagnostic(string.Join(Environment.NewLine, tail));
        return null;
    }

    public readonly record struct FiveHourUsage(int Percent, DateTimeOffset? ResetAt);

    /// <summary>Returns the current rolling-5-hour usage percentage plus an estimate of when it resets, or null
    /// if the file has no "fh" figure at all yet. Unlike the weekly figure, no reset timestamp is stored in the
    /// file - Anthropic's 5-hour window resets exactly 5 hours after it started, and empirically (checked
    /// across 2026-08-25 through 2026-09-11) the sample immediately after that start consistently lands within
    /// a few seconds of the true reset moment, often exact to the second. So the start is estimated as the most
    /// recent point "fh" dropped sharply (from one sample to the next) below the current run, and the reset
    /// time is that sample's timestamp + 5 hours. If that projected time has already passed - e.g. after a long
    /// gap with no polling, where we never actually captured the moment of the drop - there isn't enough
    /// information to know the real window start, so no reset estimate is returned rather than guessing.</summary>
    public static FiveHourUsage? ReadLatestFiveHourUsage()
    {
        using FileStream? stream = TryOpen();
        if (stream == null) return null;

        using JsonDocument doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("samples", out JsonElement samples)) return null;
        if (samples.ValueKind != JsonValueKind.Array) return null;

        int count = samples.GetArrayLength();

        int? percent = null;
        int percentIndex = -1;
        for (int i = count - 1; i >= 0; i--)
        {
            if (samples[i].TryGetProperty("u", out JsonElement usage) && usage.TryGetProperty("fh", out JsonElement fh))
            {
                percent = fh.GetInt32();
                percentIndex = i;
                break;
            }
        }
        if (percent == null) return null;

        DateTimeOffset? resetAt = null;
        int? prevFh = null;
        for (int i = 0; i <= percentIndex; i++)
        {
            if (!samples[i].TryGetProperty("u", out JsonElement u) || !u.TryGetProperty("fh", out JsonElement fhEl))
            {
                continue;
            }
            int fhVal = fhEl.GetInt32();
            if (prevFh.HasValue && fhVal < prevFh.Value - 30 && samples[i].TryGetProperty("t", out JsonElement tEl))
            {
                resetAt = DateTimeOffset.FromUnixTimeMilliseconds(tEl.GetInt64()).AddHours(5);
            }
            prevFh = fhVal;
        }

        if (resetAt.HasValue && resetAt.Value <= DateTimeOffset.UtcNow)
        {
            resetAt = null;
        }

        return new FiveHourUsage(percent.Value, resetAt);
    }

    private static FileStream? TryOpen()
    {
        try
        {
            return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 2026-09-11: previously treated as the boring "genuinely missing, nothing to see" case and left
            // unlogged - wrong. Caught this exact exception firing from a long-running HUD process while a
            // completely separate, freshly-started process opened the very same path successfully at the same
            // moment. So this is NOT necessarily "missing" - log everything we can cheaply compare against an
            // external view of the same path, so a recurrence shows whether it's genuinely gone or another
            // instance of one process seeing something different from reality.
            string parentDir = Path.GetDirectoryName(FilePath)!;
            string listing;
            try
            {
                listing = string.Join(", ", Directory.EnumerateFileSystemEntries(parentDir).Select(Path.GetFileName));
            }
            catch (Exception listEx)
            {
                listing = $"<could not list {parentDir}: {listEx.GetType().Name}: {listEx.Message}>";
            }
            LogDiagnostic($"{ex.GetType().Name} opening {FilePath} (HResult={ex.HResult}): {ex.Message} | "
                + $"File.Exists={File.Exists(FilePath)} Directory.Exists(parent)={Directory.Exists(parentDir)} | "
                + $"parent dir contents: {listing}");
            return null;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"open failed with {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";

    /// <summary>Best-effort append to %AppData%\ClaudeUsageHUD\weekly-diagnostic.log - covers every "why did
    /// ReadLatestWeeklyPercent return null" branch above, since the very first attempt at diagnosing this
    /// (2026-09-08) only instrumented the last branch and the very next recurrence (2026-09-09) turned out to
    /// hit one of the earlier ones instead, leaving no trace of which.</summary>
    internal static void LogDiagnostic(string message)
    {
        try
        {
            string dir = Path.GetDirectoryName(DiagnosticLogPath)!;
            Directory.CreateDirectory(dir);

            if (File.Exists(DiagnosticLogPath) && new FileInfo(DiagnosticLogPath).Length > MaxDiagnosticLogBytes)
            {
                File.Delete(DiagnosticLogPath);
            }

            File.AppendAllText(DiagnosticLogPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics are best-effort - never let logging itself be the reason the HUD breaks.
        }
    }
}
