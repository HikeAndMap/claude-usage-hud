namespace ClaudeUsageHUD;

/// <summary>Tiny modal for setting the context-window size to divide token usage by - see HudSettings' own
/// remarks on why this has to be user-editable rather than inferred (the real per-model/per-plan size isn't
/// exposed anywhere the HUD can read it).</summary>
public sealed class ContextWindowSizePrompt : Form
{
    public long EnteredSize { get; private set; }

    private readonly NumericUpDown _input;

    public ContextWindowSizePrompt(long currentSize)
    {
        Text = "Context Window Size";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var label = new Label
        {
            Text = "Context window size (tokens), e.g. 200000 or 1000000:",
            AutoSize = true,
            Location = new Point(12, 12),
        };

        _input = new NumericUpDown
        {
            Minimum = 1_000,
            Maximum = 10_000_000,
            Increment = 1_000,
            Value = Math.Clamp(currentSize, 1_000, 10_000_000),
            Location = new Point(12, 40),
            Width = 296,
        };

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(132, 80) };
        okButton.Click += (s, e) => EnteredSize = (long)_input.Value;
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(213, 80) };

        Controls.Add(label);
        Controls.Add(_input);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
