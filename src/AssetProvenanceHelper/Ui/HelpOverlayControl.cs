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

    public event EventHandler? CloseRequested;

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
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
            "AI GENERATION PROVIDERS\r\n" +
            "AI Generation Provider templates are loaded when this application starts.\r\n\r\n" +
            "Provider template folder:\r\n\r\n" +
            "<application folder>\\provider_templates\\\r\n\r\n" +
            "Each selectable .md file represents one Provider.\r\n\r\n" +
            "Example:\r\n\r\n" +
            "ChatGPT.md   -> ChatGPT\r\n" +
            "Gemini.md    -> Gemini\r\n\r\n" +
            "Files whose filename begins with \"_\" are helper/template files and are not\r\n" +
            "shown in the Provider dropdown.\r\n\r\n" +
            "TO ADD A PROVIDER\r\n\r\n" +
            "1. Open the provider_templates folder.\r\n" +
            "2. Copy _TEMPLATE.md.\r\n" +
            "3. Rename the copy, for example:\r\n" +
            "   Gemini.md\r\n" +
            "4. Edit the Markdown file however you want.\r\n" +
            "5. Keep ALL required fields exactly as written:\r\n" +
            "   <<<PROVIDER>>>\r\n" +
            "   <<<DATE>>>\r\n" +
            "   <<<FILENAME>>>\r\n" +
            "   <<<ASSET_NAME>>>\r\n" +
            "   <<<PROJECT>>>\r\n" +
            "   <<<ROLE>>>\r\n" +
            "   <<<WORKFLOW>>>\r\n" +
            "   <<<REFERENCE_FILENAME>>>\r\n" +
            "   <<<PROMPT>>>\r\n" +
            "6. Save the file as UTF-8.\r\n" +
            "7. Restart AI Asset Provenance Helper.\r\n\r\n" +
            "After restart the new Provider automatically appears in the dropdown if the\r\n" +
            "template is valid.\r\n\r\n" +
            "The Markdown text, headings, paragraphs and provider-specific explanatory\r\n" +
            "content can otherwise be arranged freely.\r\n\r\n" +
            "The application never asks for Provider-specific runtime fields.\r\n" +
            "It does not ask for model, seed, API key, account, subscription, generation ID\r\n" +
            "or any other Provider-specific metadata.\r\n\r\n" +
            "For Reference provenance, <<<PROMPT>>> becomes \"not recorded\" because this\r\n" +
            "helper does not collect a separate Reference-generation Prompt.\r\n\r\n" +
            "An unsupported or malformed <<<...>>> field makes only that Provider template\r\n" +
            "invalid. It does not prevent the application from starting.\r\n\r\n" +
            "The original Provider template file is never modified. The helper creates a\r\n" +
            "rendered copy for each provenance output and replaces the predefined tags in\r\n" +
            "that copy.\r\n\r\n" +
            "ASSET REQUEST IMPORT\r\n\r\n" +
            "A prepared Request Manifest can be imported into the Request Queue on the\r\n" +
            "right side of the application.\r\n\r\n" +
            "The exact JSON template is included at:\r\n\r\n" +
            "<application folder>\\examples\\asset_request_manifest_template.json\r\n\r\n" +
            "A ready-to-use instruction for converting an existing asset-request document\r\n" +
            "with another AI is included at:\r\n\r\n" +
            "<application folder>\\examples\\asset_request_conversion_prompt.txt\r\n\r\n" +
            "Every requested asset contains exactly:\r\n\r\n" +
            "filename\r\n" +
            "resolution\r\n" +
            "prompt\r\n\r\n" +
            "When you click a Pending Request:\r\n\r\n" +
            "- Asset Name is filled automatically.\r\n" +
            "- Final Prompt is filled automatically.\r\n" +
            "- The complete Prompt is copied to the clipboard.\r\n\r\n" +
            "The Request remains Pending until the Main Image has been successfully\r\n" +
            "committed by this helper.\r\n\r\n" +
            "A Done Request is shown with the word Done and a green background.\r\n\r\n" +
            "Request progress is restored when the same semantic Manifest is imported\r\n" +
            "again.\r\n\r\n" +
            "DIRECT MODE\r\n\r\n" +
            "Direct mode removes the manual Refresh click.\r\n\r\n" +
            "When Direct mode is enabled, the Refresh buttons remain visible but disabled.\r\n\r\n" +
            "Main Image in Direct mode performs a fresh automatic Download-folder selection\r\n" +
            "and therefore can replace a manually selected candidate.\r\n\r\n" +
            "KEEP SETTINGS\r\n\r\n" +
            "When checked, completing or cancelling an asset keeps Asset Name, Final Prompt\r\n" +
            "and the Variants count so they do not have to be reentered.\r\n\r\n" +
            "Image selections and the 'Saved reference' label are always cleared, even with\r\n" +
            "Keep Settings on - a retained image selection points at a download-folder file\r\n" +
            "a committed asset has already consumed.\r\n\r\n" +
            "Keep Settings never applies to Request Manifest import, which always clears the\r\n" +
            "current fields.\r\n\r\n" +
            "The Variants count itself always resets to 'none' on every application start,\r\n" +
            "even when Keep Settings is on.\r\n\r\n" +
            "VARIANTS\r\n\r\n" +
            "Produces multiple independent assets from one Asset Name and one prompt in a\r\n" +
            "single sequential batch, named with an A/B/C... suffix.\r\n\r\n" +
            "• Works in both No-reference and Reference-assisted mode.\r\n" +
            "• N variants use the N newest supported downloads (N + 1 in Direct mode +\r\n" +
            "  Reference-assisted mode: the oldest of those is the Reference).\r\n" +
            "• The OLDEST of those N becomes 'A', the next 'B', and so on.\r\n" +
            "• Set the Variants count BEFORE clicking Reference - it locks once a reference\r\n" +
            "  session is active, because that click binds the variant-A folder name.\r\n" +
            "• In Reference-assisted mode every variant folder gets its own byte-identical\r\n" +
            "  copy of the same Reference image and its own Reference provenance document.\r\n" +
            "• If one variant fails, the earlier variants stay completed; later variants are\r\n" +
            "  not attempted. A summary reports which variants succeeded and which failed.\r\n\r\n" +
            "NO-REFERENCE\r\n\r\n" +
            "1. Prepare/select the asset and Prompt.\r\n" +
            "2. Generate the image in the browser.\r\n" +
            "3. Download the image.\r\n" +
            "4. Return to the helper.\r\n" +
            "5. Click Main Image.\r\n\r\n" +
            "The helper automatically selects the newest supported image in the configured\r\n" +
            "Image Download Folder and then runs the normal Main Image workflow.\r\n\r\n" +
            "REFERENCE-ASSISTED\r\n\r\n" +
            "1. Prepare/select the asset and Prompt.\r\n" +
            "2. Generate/download the Reference image FIRST.\r\n" +
            "3. Generate/download the final Main image SECOND.\r\n" +
            "4. Return to the helper.\r\n" +
            "5. Click Main Image.\r\n\r\n" +
            "The helper selects:\r\n\r\n" +
            "second-newest supported image = Reference\r\n" +
            "newest supported image        = Main\r\n\r\n" +
            "Both candidates are validated before Reference processing begins.\r\n\r\n" +
            "The Reference button remains visible but disabled while Direct mode is active.\r\n\r\n" +
            "If Reference succeeds but Main fails, the Reference remains saved. Generate\r\n" +
            "and download a new Main image and click Main Image again. On that retry only\r\n" +
            "Main is refreshed.\r\n\r\n" +
            "Made by CeeGore";
    }
}
