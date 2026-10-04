namespace ClaudeUsageHUD;

/// <summary>Modal for pasting the OAuth token that gives the HUD live weekly/5h usage (see OAuthUsageClient).
/// The box masks input; the token goes straight to UsageTokenStore and is never shown again or logged.</summary>
public sealed class UsageTokenPrompt : Form
{
    /// <summary>The trimmed token to save, or null when the user chose "Remove saved token".</summary>
    public string? EnteredToken { get; private set; }

    private readonly TextBox _input;

    public UsageTokenPrompt(bool hasSavedToken)
    {
        Text = "Live Usage Token";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 170);

        var label = new Label
        {
            Text = "Paste a Claude OAuth token (sk-ant-oat...) so the HUD can fetch live weekly and 5h usage.\n"
                + "It is stored encrypted for your Windows account only.",
            Location = new Point(12, 12),
            Size = new Size(396, 48),
        };

        _input = new TextBox
        {
            UseSystemPasswordChar = true,
            Location = new Point(12, 66),
            Width = 396,
        };

        var saveButton = new Button { Text = "Save", Location = new Point(252, 130) };
        saveButton.Click += (s, e) =>
        {
            string token = _input.Text.Trim().Trim('"');
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
            if (token.Length == 0) return;
            EnteredToken = token;
            DialogResult = DialogResult.OK;
        };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(333, 130) };

        Controls.Add(label);
        Controls.Add(_input);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);

        if (hasSavedToken)
        {
            var removeButton = new Button { Text = "Remove saved token", Width = 130, Location = new Point(12, 130) };
            removeButton.Click += (s, e) =>
            {
                EnteredToken = null;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(removeButton);
        }

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }
}
