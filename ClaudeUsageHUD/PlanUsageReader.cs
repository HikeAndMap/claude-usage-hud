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

        return null;
    }
}
