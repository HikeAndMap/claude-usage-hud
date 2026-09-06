using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// Tiny persisted settings (just the context-window size to divide by, and the HUD's last screen position) -
/// stored under %AppData%\ClaudeUsageHUD\settings.json. The actual per-model context-window SIZE isn't present
/// anywhere in the transcript files themselves (Claude Code computes that internally and only ever exposes it
/// through the statusLine hook's JSON, which doesn't fire for the desktop app - see project discussion,
/// 2026-09-06) - so rather than hardcode a guess that could silently be wrong for a given plan/model, this is
/// user-editable from the tray menu, defaulting to 200,000 (the long-standing baseline Claude context size).
/// </summary>
public sealed class HudSettings
{
    public long ContextWindowSize { get; set; } = 200_000;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "settings.json");

    public static HudSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                HudSettings? loaded = JsonSerializer.Deserialize<HudSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than crash the HUD over it.
        }

        return new HudSettings();
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Not being able to persist settings shouldn't crash the HUD - it just won't remember next launch.
        }
    }
}
