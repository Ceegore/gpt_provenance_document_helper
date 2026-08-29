#nullable enable
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private IContainer? components = null;

    private TableLayoutPanel pnlWorkspace = null!;
    private TableLayoutPanel pnlMainContent = null!;
    private Panel pnlHeader = null!;
    private PictureBox picLogo = null!;
    private Label lblHeaderTitle = null!;
    private Label lblHeaderVersion = null!;
    private Button btnHelp = null!;

    private GroupBox grpSettings = null!;
    private TextBox txtDownloadFolder = null!;
    private Panel pnlDownloadFolderHost = null!;
    private Button btnBrowseDownload = null!;
    private TextBox txtAssetRoot = null!;
    private Panel pnlAssetRootHost = null!;
    private Button btnBrowseAssetRoot = null!;
    private ComboBox cmbProvider = null!;
    private Label lblProviderWarning = null!;

    private GroupBox grpCurrentAsset = null!;
    private TextBox txtAssetFolderName = null!;
    private Panel pnlAssetFolderNameHost = null!;
    private CheckBox chkNoReference = null!;
    private CheckBox chkDirectMode = null!;

    private TableLayoutPanel pnlCardsContainer = null!;
    private GroupBox grpReference = null!;
    private Label lblReferenceSelectedImage = null!;
    private Label lblReferenceTimestamp = null!;
    private Panel pnlReferenceImageHost = null!;
    private Label lblReferenceDrop = null!;
    private Button btnRefreshReference = null!;
    private Button btnChooseReference = null!;
    private Button btnDropReference = null!;
    private Button btnOpenDownloadsReference = null!;
    private Label lblReference = null!;
    private Button btnReference = null!;

    private GroupBox grpMain = null!;
    private Label lblMainSelectedImage = null!;
    private Label lblMainTimestamp = null!;
    private Panel pnlMainImageHost = null!;
    private Label lblMainDrop = null!;
    private Button btnRefreshMain = null!;
    private Button btnChooseMain = null!;
    private Button btnDropMain = null!;
    private Button btnOpenDownloadsMain = null!;
    private Label lblPromptPreview = null!;
    private TextBox txtPrompt = null!;
    private Panel pnlPromptHost = null!;
    private Button btnPasteClipboard = null!;
    private Button btnClearPrompt = null!;
    private Button btnMainImage = null!;

    private GroupBox grpStatus = null!;
    private ListView lvRecentDocuments = null!;
    private TextBox txtStatusHistory = null!;
    private Button btnOpenAssetFolder = null!;
    private Button btnCancel = null!;

    private GroupBox grpRequestQueue = null!;
    private Button btnImportRequest = null!;
    private Label lblRequestSource = null!;
    private ListView lvRequestQueue = null!;
    private Label lblRequestProgress = null!;

    private HelpOverlayControl helpOverlay = null!;
    private ToolTip _toolTip = null!;

    [ExcludeFromCodeCoverage]
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopCtaPulse();
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    [ExcludeFromCodeCoverage]
    private void InitializeComponent()
    {
        components = new Container();
        _toolTip = new ToolTip(components);

        Text = AppInfo.ProductName;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1240, 700);
        Size = new Size(1500, 800);
        KeyPreview = true;
        AutoScroll = false;

        pnlWorkspace = new TableLayoutPanel
        {
            Name = "pnlWorkspace",
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 1
        };

        pnlWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

        pnlMainContent = new TableLayoutPanel
        {
            Name = "pnlMainContent",
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Margin = new Padding(0, 0, 12, 0),
            ColumnCount = 1,
            RowCount = 5
        };

        pnlMainContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlMainContent.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Header
        pnlMainContent.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Settings
        pnlMainContent.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Current Asset
        pnlMainContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Reference & Main Cards
        pnlMainContent.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Status Group

        BuildHeader(pnlMainContent);
        BuildSettingsGroup(pnlMainContent);
        BuildCurrentAssetGroup(pnlMainContent);
        BuildCardsSection(pnlMainContent);
        BuildStatusGroup(pnlMainContent);
        BuildRequestQueueGroup(pnlWorkspace);

        pnlWorkspace.Controls.Add(pnlMainContent, 0, 0);
        pnlWorkspace.Controls.Add(grpRequestQueue, 1, 0);

        Controls.Add(pnlWorkspace);

        helpOverlay = new HelpOverlayControl();
        helpOverlay.Name = "helpOverlay";
        Controls.Add(helpOverlay);
        helpOverlay.BringToFront();

        BuildPromptOverlay();
    }
}
