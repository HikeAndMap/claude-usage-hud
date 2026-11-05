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
    private bool _signingIn;

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

        ToolStripMenuItem signInItem = new("Sign in for live usage...");
        signInItem.Click += async (s, e) => await SignInForLiveUsage();
        menu.Items.Add(signInItem);

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

        // Only a fresh browser sign-in can recover a dead refresh token, so say so instead of silently showing
        // "n/a" - and let a click on the balloon start the sign-in directly.
        HudLogin.SignInExpired += () =>
        {
            _trayIcon.ShowBalloonTip(15000, "Claude Usage HUD", "Live usage sign-in expired - click here to sign in again.", ToolTipIcon.Warning);
        };
        _trayIcon.BalloonTipClicked += async (s, e) => await SignInForLiveUsage();
        _hud.SignInRequested += async () => await SignInForLiveUsage();

        // The pasted-token fallback was removed (a setup-token can't read usage); don't leave its encrypted
        // secret lying around.
        try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "usage-token.bin")); }
        catch (Exception) { /* best effort */ }

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

    private async Task SignInForLiveUsage()
    {
        // The hint, the balloon and the menu item can all trigger this - a second click while the browser flow
        // is still waiting would start a second loopback listener for no reason.
        if (_signingIn) return;
        _signingIn = true;
        try
        {
            string? error = await HudLogin.SignInAsync();
            if (error != null)
            {
                MessageBox.Show($"Sign-in failed: {error}", "Claude Usage HUD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await _hud.RefreshLiveUsageAsync();
        }
        finally
        {
            _signingIn = false;
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
