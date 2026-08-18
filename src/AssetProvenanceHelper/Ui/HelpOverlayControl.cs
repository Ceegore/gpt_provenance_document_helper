#nullable enable
using System.Drawing;
using System.Windows.Forms;

namespace AssetProvenanceHelper.Ui;

public class HelpOverlayControl : UserControl
{
    private readonly Panel _contentPanel;
    private readonly Button _btnClose;
    private readonly TextBox _txtContent;
    private readonly Label _lblTitle;
    private readonly Label _lblFooter;

    public HelpOverlayControl()
    {
        Dock = DockStyle.Fill;
        Visible = false;
        BackColor = Color.FromArgb(200, 20, 25, 35); // Semi-transparent dark overlay

        _contentPanel = new Panel
        {
            Size = new Size(680, 560),
            BackColor = Color.White,
            Padding = new Padding(20),
            BorderStyle = BorderStyle.FixedSingle
        };

        _lblTitle = new Label
        {
            Text = $"{AppInfo.ProductName} — Help & Information",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        _btnClose = new Button
        {
            Text = "✕ Close",
            Size = new Size(80, 30),
            Location = new Point(580, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            UseVisualStyleBackColor = true
        };
        _btnClose.Click += (_, _) => HideOverlay();

        _lblFooter = new Label
        {
            Text = "Made by CeeGore",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Italic),
            AutoSize = true,
            Location = new Point(20, 525),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        _txtContent = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            Location = new Point(20, 52),
            Size = new Size(640, 460),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            Text = GetHelpText()
        };

        _contentPanel.Controls.Add(_lblTitle);
        _contentPanel.Controls.Add(_btnClose);
        _contentPanel.Controls.Add(_txtContent);
        _contentPanel.Controls.Add(_lblFooter);

        Controls.Add(_contentPanel);

        Resize += (_, _) => CenterContent();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                HideOverlay();
            }
        };
    }

    private void CenterContent()
    {
        _contentPanel.Location = new Point(
            Math.Max(10, (ClientSize.Width - _contentPanel.Width) / 2),
            Math.Max(10, (ClientSize.Height - _contentPanel.Height) / 2));
    }

    public void ShowOverlay()
    {
        Visible = true;
        BringToFront();
        CenterContent();
        FocusCloseButton();
    }

    public void HideOverlay()
    {
        Visible = false;
    }

    public void FocusCloseButton()
    {
        _btnClose.Focus();
    }

    private static string GetHelpText()
    {
        return
            "ABOUT\r\n" +
            "AI Asset Provenance Helper streamlines tracking and documenting AI-generated game assets and their reference images.\r\n\r\n" +
            "BASIC WORKFLOW\r\n" +
            "1. Configure Image Download Folder and Asset Root Folder in Settings.\r\n" +
            "2. Enter an Asset Name (subfolder name inside Asset Root Folder, entered without extension).\r\n" +
            "3. Select or drop your images into Reference and Main slots.\r\n" +
            "4. Enter the prompt used to generate the final image.\r\n" +
            "5. Complete the asset to write canonical files and provenance markdown.\r\n\r\n" +
            "REFERENCE-ASSISTED WORKFLOW\r\n" +
            "• Step 1 (Reference): Select Reference image -> click [Reference] (Ctrl+R). This copies the reference image and creates reference provenance.\r\n" +
            "• Step 2 (Main): Select Main image, enter Prompt -> click [Main Image] (Ctrl+M). This copies the main image to the root and ingame/ subfolder and creates final provenance.\r\n" +
            "• Replace Reference: If needed, select a new reference image and click [Replace Reference] before completing the main image.\r\n\r\n" +
            "NO REFERENCE MODE\r\n" +
            "• Check 'No reference mode' when the final AI asset was generated directly without an input reference image.\r\n" +
            "• The Reference card is hidden and Main card occupies the full width.\r\n" +
            "• Select Main image, enter Prompt -> click [Main Image] (Ctrl+M) to complete in a single atomic transaction.\r\n\r\n" +
            "LOCAL-FILE BEHAVIOR\r\n" +
            "• Downloaded source files are COPIED into the asset directory. They are never moved or deleted from your download folder.\r\n" +
            "• Image Download Folder is optional when choosing or dropping a source image manually.\r\n" +
            "• The final tree layout contains: root Main image, ingame/ canonical asset image, and markdown provenance documentation.\r\n\r\n" +
            "KEYBOARD SHORTCUTS\r\n" +
            "• Ctrl+R: Process Reference (Reference-assisted mode)\r\n" +
            "• Ctrl+M: Process Main Image (Both modes)\r\n" +
            "• Esc: Close this help overlay\r\n\r\n" +
            "LEGAL & DISCLAIMER INFORMATION\r\n" +
            "This tool creates internal provenance documentation. It is not legal advice and does not determine or guarantee copyright ownership, copyrightability, uniqueness, non-infringement, trademark clearance, commercial-use eligibility, or acceptance by a store/platform.\r\n\r\n" +
            "Generation-provider terms and applicable laws can change. Review the terms that applied to the generation workflow and verify the generated provenance record before relying on it.\r\n\r\n" +
            "Use No reference mode only when no reference image was supplied for the final generation.\r\n\r\n" +
            "Made by CeeGore";
    }
}
