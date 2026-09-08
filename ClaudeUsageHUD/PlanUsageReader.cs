using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// Reads the Claude desktop app's own plan-usage history file (%AppData%\Claude\plan-usage-history.json) to get
/// the current weekly usage percentage - the same data backing the desktop app's built-in usage indicator, just
/// with the rolling 5-hour figure ignored since that one's already visible elsewhere in the desktop app itself
/// (see project discussion, 2026-09-06). The file is a JSON object with a "samples" array, each entry shaped
/// like {"t": epochMs, "org": orgId, "u": {"fh": five-hour %, "sd": seven-day/weekly %}}, appended roughly every
/// 15 minutes; the last entry is the current reading.
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
        if (!File.Exists(FilePath)) return null;

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("samples", out JsonElement samples)) return null;
        if (samples.ValueKind != JsonValueKind.Array) return null;

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
        // recorded at all (2026-09-08: seen once with a live file that in fact DID have "sd" throughout,
        // meaning whatever the running process read here differed from the file on disk - never reproduced
        // on demand, cleared by an app restart). Log what we actually saw so a recurrence is diagnosable
        // without needing to catch the HUD showing "n/a" live again.
        LogNotFound(samples);
        return null;
    }

    private static void LogNotFound(JsonElement samples)
    {
        try
        {
            int count = samples.GetArrayLength();
            int tailStart = Math.Max(0, count - 5);

            var lines = new List<string>
            {
                $"[{DateTimeOffset.UtcNow:O}] no sample with \"sd\" found; array length={count}",
            };
            for (int i = tailStart; i < count; i++)
            {
                lines.Add($"  [{i}] {samples[i].GetRawText()}");
            }
            lines.Add("");

            string dir = Path.GetDirectoryName(DiagnosticLogPath)!;
            Directory.CreateDirectory(dir);

            if (File.Exists(DiagnosticLogPath) && new FileInfo(DiagnosticLogPath).Length > MaxDiagnosticLogBytes)
            {
                File.Delete(DiagnosticLogPath);
            }

            File.AppendAllLines(DiagnosticLogPath, lines);
        }
        catch (Exception)
        {
            // Diagnostics are best-effort - never let logging itself be the reason the HUD breaks.
        }
    }
}
