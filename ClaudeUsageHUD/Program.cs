namespace ClaudeUsageHUD;

static class Program
{
    /// <summary>
    /// Runs as a tray-icon background utility (TrayAppContext), never a normal top-level window - the actual
    /// floating HUD is a borderless, tool-window-styled Form owned by that context.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
