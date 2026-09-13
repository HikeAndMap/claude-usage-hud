namespace ClaudeUsageHUD;

/// <summary>
/// The floating HUD itself - a small, borderless, always-on-top, draggable panel showing the active Claude
/// Code session's context-window usage (percentage + raw token count) plus the account's weekly and rolling
/// 5-hour plan-usage percentages, refreshed on a timer by re-reading the active session's transcript tail (see
/// SessionUsageReader) and the desktop app's own plan-usage history file (see PlanUsageReader). Colors mirror
/// the same 70%/90% yellow/red thresholds the user's own statusline.ps1 script already uses, for a consistent
/// feel.
/// </summary>
public sealed class HudForm : Form
{
    private const int PollIntervalMs = 4000;
    private const int YellowThresholdPct = 70;
    private const int RedThresholdPct = 90;

    // How long weekly/5h reads have to fail in a row - after previously succeeding this session - before we
    // conclude the process itself is wedged (see RefreshWeekly/RefreshFiveHour's isolation comment and
    // PlanUsageReader's TryOpen: this has recurred repeatedly, always tied to one specific long-running
    // process, always cleared instantly by an external restart, never explained by anything wrong with the
    // data on disk) and restart ourselves rather than needing a manual restart every time.
    private const int PlanUsageStuckThresholdTicks = 30; // ~2 minutes at PollIntervalMs
    private static readonly TimeSpan SelfRestartCooldown = TimeSpan.FromMinutes(5);
    private static readonly string SelfRestartMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageHUD", "last-self-restart.txt");

    private readonly HudSettings _settings;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Label _label;
    private readonly Label _weeklyLabel;
    private readonly Label _fiveHourLabel;

    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private bool _dragging;

    // Only true once weekly or 5h has read successfully at least once this process's life - guards against
    // self-restarting during the legitimate "Claude desktop hasn't created the file yet" window right after
    // boot, which should never be mistaken for the wedged-process case above.
    private bool _everHadValidPlanUsage;
    private int _consecutivePlanUsageFailures;

    public HudForm(HudSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 30);
        Size = new Size(200, 80);

        _label = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Text = "Claude: ...",
        };
        _weeklyLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Text = "Weekly: ...",
        };
        _fiveHourLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Text = "5h: ...",
        };
        Controls.Add(_fiveHourLabel);
        Controls.Add(_weeklyLabel);
        Controls.Add(_label);

        // Dragging - a borderless Form has no title bar to grab, so all three labels double as one.
        _label.MouseDown += OnDragStart;
        _label.MouseMove += OnDragMove;
        _label.MouseUp += OnDragEnd;
        _weeklyLabel.MouseDown += OnDragStart;
        _weeklyLabel.MouseMove += OnDragMove;
        _weeklyLabel.MouseUp += OnDragEnd;
        _fiveHourLabel.MouseDown += OnDragStart;
        _fiveHourLabel.MouseMove += OnDragMove;
        _fiveHourLabel.MouseUp += OnDragEnd;
        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += OnDragEnd;

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (s, e) => Refresh_();
        _pollTimer.Start();

        Load += (s, e) =>
        {
            PositionInitialLocation();
            Refresh_();
        };
        FormClosing += (s, e) => SavePosition();
    }

    private void PositionInitialLocation()
    {
        if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
        {
            Location = new Point(_settings.WindowX, _settings.WindowY);
            return;
        }

        // First-ever launch - default to the bottom-right corner, just above the tray, per explicit user
        // preference (2026-09-06). WorkingArea already excludes the taskbar itself, so anchoring off its
        // Bottom/Right edges naturally sits the HUD directly above the tray icons regardless of taskbar
        // size/DPI, without needing to special-case the taskbar's own height. Pushed up by an extra half of
        // the HUD's own height (so the box's bottom edge lands where its center used to sit) and the right
        // margin widened by 50%, both per explicit user preference (2026-09-06) after the second (weekly)
        // label made the box taller and crowd the taskbar.
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 30, area.Bottom - Height - 10 - Height / 2);
    }

    private void SavePosition()
    {
        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settings.Save();
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStartScreen = Cursor.Position;
        _dragStartWindow = Location;
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point delta = Point.Subtract(Cursor.Position, new Size(_dragStartScreen));
        Location = Point.Add(_dragStartWindow, new Size(delta));
    }

    private void OnDragEnd(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        SavePosition();
    }

    /// <summary>Re-reads the active session's transcript tail and updates the label - never throws out to the
    /// timer, since a transient read failure (Claude Code mid-write, file briefly locked) should just skip a
    /// tick, not crash the HUD.</summary>
    public void Refresh_()
    {
        try
        {
            string? activeFile = SessionUsageReader.FindActiveSessionFile();
            if (activeFile == null)
            {
                _label.Text = "Claude: no session";
                _label.ForeColor = Color.Gray;
                return;
            }

            UsageSnapshot? usage = SessionUsageReader.ReadLatestUsage(activeFile);
            if (usage == null)
            {
                _label.Text = "Claude: (starting up)";
                _label.ForeColor = Color.Gray;
                return;
            }

            long contextSize = _settings.ContextWindowSize;
            int pct = contextSize > 0 ? (int)Math.Floor(usage.PromptTokens * 100.0 / contextSize) : 0;

            _label.Text = $"Context: {pct}% ({FormatTokenCount(usage.PromptTokens)})";
            _label.ForeColor = ColorForPercent(pct);
        }
        catch (Exception ex)
        {
            _label.Text = "Claude: error";
            _label.ForeColor = Color.Gray;
            System.Diagnostics.Debug.WriteLine("HudForm.Refresh_ failed: " + ex);
        }

        bool weeklyOk = RefreshWeekly();
        bool fiveHourOk = RefreshFiveHour();
        CheckSelfHeal(weeklyOk || fiveHourOk);
    }

    /// <summary>Updates the weekly-usage line, isolated from the context-window refresh above so a hiccup
    /// reading one file (e.g. the desktop app mid-write to plan-usage-history.json) never blanks the other.
    /// Returns whether the read succeeded, for CheckSelfHeal.</summary>
    private bool RefreshWeekly()
    {
        try
        {
            int? weeklyPct = PlanUsageReader.ReadLatestWeeklyPercent();
            if (weeklyPct == null)
            {
                _weeklyLabel.Text = "Weekly: n/a";
                _weeklyLabel.ForeColor = Color.Gray;
                return false;
            }

            _weeklyLabel.Text = $"Weekly: {weeklyPct}%";
            _weeklyLabel.ForeColor = ColorForPercent(weeklyPct.Value);
            return true;
        }
        catch (Exception ex)
        {
            _weeklyLabel.Text = "Weekly: error";
            _weeklyLabel.ForeColor = Color.Gray;
            System.Diagnostics.Debug.WriteLine("HudForm.RefreshWeekly failed: " + ex);
            return false;
        }
    }

    /// <summary>Updates the rolling-5-hour line, isolated from the other two refreshes for the same reason as
    /// RefreshWeekly above. Returns whether the read succeeded, for CheckSelfHeal.</summary>
    private bool RefreshFiveHour()
    {
        try
        {
            PlanUsageReader.FiveHourUsage? usage = PlanUsageReader.ReadLatestFiveHourUsage();
            if (usage == null)
            {
                _fiveHourLabel.Text = "5h: n/a";
                _fiveHourLabel.ForeColor = Color.Gray;
                return false;
            }

            string resetSuffix = usage.Value.ResetAt is { } resetAt
                ? $" ({FormatRemaining(resetAt - DateTimeOffset.UtcNow)})"
                : "";
            _fiveHourLabel.Text = $"5h: {usage.Value.Percent}%{resetSuffix}";
            _fiveHourLabel.ForeColor = ColorForPercent(usage.Value.Percent);
            return true;
        }
        catch (Exception ex)
        {
            _fiveHourLabel.Text = "5h: error";
            _fiveHourLabel.ForeColor = Color.Gray;
            System.Diagnostics.Debug.WriteLine("HudForm.RefreshFiveHour failed: " + ex);
            return false;
        }
    }

    /// <summary>Tracks whether weekly/5h reads are stuck failing despite having worked before this session,
    /// and restarts the whole HUD process if so - see the class-level comment on PlanUsageStuckThresholdTicks
    /// for why a restart (rather than any code fix here) is the actual remedy that's worked every time this
    /// has been observed.</summary>
    private void CheckSelfHeal(bool thisTickSucceeded)
    {
        if (thisTickSucceeded)
        {
            _everHadValidPlanUsage = true;
            _consecutivePlanUsageFailures = 0;
            return;
        }

        if (!_everHadValidPlanUsage) return;

        _consecutivePlanUsageFailures++;
        if (_consecutivePlanUsageFailures < PlanUsageStuckThresholdTicks) return;
        if (!SelfRestartCooldownElapsed()) return;

        PlanUsageReader.LogDiagnostic(
            $"Self-restarting HUD - weekly/5h reads failed for {_consecutivePlanUsageFailures * PollIntervalMs / 1000}s "
            + "straight despite having worked earlier this session.");
        RecordSelfRestartTime();
        SelfRestart();
    }

    private static bool SelfRestartCooldownElapsed()
    {
        try
        {
            if (!File.Exists(SelfRestartMarkerPath)) return true;
            string text = File.ReadAllText(SelfRestartMarkerPath);
            return !DateTimeOffset.TryParse(text, out DateTimeOffset last)
                || DateTimeOffset.UtcNow - last >= SelfRestartCooldown;
        }
        catch (Exception)
        {
            return true; // best effort - a marker-file glitch shouldn't block a real fix
        }
    }

    private static void RecordSelfRestartTime()
    {
        try
        {
            string dir = Path.GetDirectoryName(SelfRestartMarkerPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SelfRestartMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            // Best effort - worst case we self-restart more often than the cooldown intends.
        }
    }

    private static void SelfRestart()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
            }
        }
        finally
        {
            Application.Exit();
        }
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "resetting";
        int totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    private static string FormatTokenCount(long tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.0}M"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:0.0}k"
        : tokens.ToString();

    /// <summary>Shared green/yellow/red coloring for both the context and weekly lines, so the two stay
    /// visually consistent at the same 70%/90% thresholds.</summary>
    private static Color ColorForPercent(int pct) =>
        pct >= RedThresholdPct ? Color.FromArgb(255, 90, 90)
        : pct >= YellowThresholdPct ? Color.FromArgb(255, 210, 90)
        : Color.FromArgb(120, 220, 120);

    protected override CreateParams CreateParams
    {
        get
        {
            // WS_EX_TOOLWINDOW - keeps this HUD out of Alt-Tab and the taskbar entirely, matching
            // ShowInTaskbar=false's intent (that property alone doesn't fully hide a TopMost borderless
            // window from Alt-Tab on every Windows version).
            const int WS_EX_TOOLWINDOW = 0x80;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
