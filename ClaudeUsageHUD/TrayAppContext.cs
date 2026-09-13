namespace ClaudeUsageHUD;

/// <summary>
/// Runs the whole app as a tray-icon-only ApplicationContext (no main window) hosting the floating HudForm -
/// standard pattern for a Windows background utility that shouldn't show up as its own taskbar app. The tray
/// icon's own context menu is the only UI for toggling the HUD, quitting, or adjusting the context-window
/// size setting.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly HudForm _hud;
    private readonly HudSettings _settings;

    public TrayAppContext()
    {
        _settings = HudSettings.Load();
        _hud = new HudForm(_settings);

        var menu = new ContextMenuStrip();
        ToolStripMenuItem toggleItem = new("Show/Hide HUD");
        toggleItem.Click += (s, e) => ToggleHud();
        menu.Items.Add(toggleItem);

        ToolStripMenuItem setSizeItem = new("Set context window size...");
        setSizeItem.Click += (s, e) => PromptForContextWindowSize();
        menu.Items.Add(setSizeItem);

        menu.Items.Add(new ToolStripSeparator());

        // Manual escape hatch for the same wedged-process condition HudForm's self-heal detects on its own
        // (see PlanUsageStuckThresholdTicks) - useful the moment you notice "n/a" without waiting out the
        // 3-minute detection window, and as a general "just restart it" button for anything else off.
        ToolStripMenuItem restartItem = new("Restart");
        restartItem.Click += (s, e) => RestartApp();
        menu.Items.Add(restartItem);

        ToolStripMenuItem exitItem = new("Exit");
        exitItem.Click += (s, e) => ExitApp();
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreateIcon(),
            Text = "Claude Usage HUD",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (s, e) => ToggleHud();

        _hud.Show();
    }

    private void ToggleHud() => _hud.Visible = !_hud.Visible;

    private void PromptForContextWindowSize()
    {
        using var prompt = new ContextWindowSizePrompt(_settings.ContextWindowSize);
        if (prompt.ShowDialog() == DialogResult.OK)
        {
            _settings.ContextWindowSize = prompt.EnteredSize;
            _settings.Save();
            _hud.Refresh_();
        }
    }

    private void RestartApp()
    {
        _trayIcon.Visible = false;
        HudForm.RestartApplication();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _hud.Close();
        Application.Exit();
    }
}
