#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace AssetProvenanceHelper.Ui;

/// <summary>
/// Floating in-form overlay showing the complete Final Prompt.
/// Not a message box; it lives inside the form and can be kept open while
/// the cursor moves from the preview label to the overlay.
/// </summary>
public sealed class PromptPreviewOverlayControl : UserControl
{
    private readonly TextBox _txtFullPrompt;
    private readonly Button _btnClose;

    public PromptPreviewOverlayControl()
    {
        Visible = false;
        BackColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblTitle = new Label
        {
            Text = "Full Prompt",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        _txtFullPrompt = new TextBox
        {
            Name = "_txtFullPrompt",
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 6, 0, 6)
        };

        _btnClose = new Button
        {
            Name = "_btnClose",
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            UseVisualStyleBackColor = true
        };
        _btnClose.Click += (_, _) => HideOverlay();

        layout.Controls.Add(lblTitle, 0, 0);
        layout.Controls.Add(_txtFullPrompt, 0, 1);
        layout.Controls.Add(_btnClose, 0, 2);

        Controls.Add(layout);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                HideOverlay();
            }
        };
    }

    public event EventHandler? CloseRequested;

    public void SetPromptText(string prompt)
    {
        if (_txtFullPrompt.Text != prompt)
        {
            _txtFullPrompt.Text = prompt;
        }
    }

    public Size GetPreferredSize() =>
        new(700, 320);

    public void ShowOverlay()
    {
        Visible = true;
        BringToFront();
        _btnClose.Focus();
    }

    public void HideOverlay()
    {
        if (!Visible)
        {
            return;
        }

        Visible = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}