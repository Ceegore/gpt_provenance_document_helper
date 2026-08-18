#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;

    private TextBox txtProject = null!;

    private TextBox txtDownloadFolder = null!;
    private Button btnBrowseDownload = null!;

    private TextBox txtAssetRoot = null!;
    private Button btnBrowseAssetRoot = null!;

    private TextBox txtAssetFolderName = null!;

    private Label lblLatestImage = null!;
    private Label lblLatestTimestamp = null!;
    private Label lblManualSelection = null!;

    private Button btnRefresh = null!;
    private Button btnChooseFile = null!;
    private Button btnOpenDownloads = null!;

    private Label lblReference = null!;
    private Button btnReference = null!;

    private TextBox txtPrompt = null!;
    private Button btnPasteClipboard = null!;
    private Button btnClearPrompt = null!;

    private Button btnMainImage = null!;

    private Button btnOpenAssetFolder = null!;
    private Button btnCancel = null!;

    private TextBox txtStatusHistory = null!;

    [ExcludeFromCodeCoverage]
    protected override void Dispose(
        bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(
            disposing);
    }

    [ExcludeFromCodeCoverage]
    private void InitializeComponent()
    {
        Text =
            "AI Asset Provenance Helper";

        StartPosition =
            FormStartPosition.CenterScreen;

        MinimumSize =
            new Size(
                760,
                760);

        Size =
            new Size(
                900,
                880);

        KeyPreview =
            true;

        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                AutoScroll =
                    true,

                Padding =
                    new Padding(14),

                ColumnCount =
                    3,

                RowCount =
                    1,

                GrowStyle =
                    TableLayoutPanelGrowStyle.AddRows
            };

        root.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                180));

        root.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));

        root.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));

        Controls.Add(
            root);

        var row =
            0;

        AddSectionHeader(
            root,
            ref row,
            "SETTINGS");

        txtProject =
            CreateTextBox();
        txtProject.Name = "txtProject";

        AddLabeledRow(
            root,
            ref row,
            "Project",
            txtProject,
            null);

        txtDownloadFolder =
            CreateTextBox();
        txtDownloadFolder.Name = "txtDownloadFolder";

        btnBrowseDownload =
            CreateButton(
                "Browse");
        btnBrowseDownload.Name = "btnBrowseDownload";

        AddLabeledRow(
            root,
            ref row,
            "Firefox Download Folder",
            txtDownloadFolder,
            btnBrowseDownload);

        txtAssetRoot =
            CreateTextBox();
        txtAssetRoot.Name = "txtAssetRoot";

        btnBrowseAssetRoot =
            CreateButton(
                "Browse");
        btnBrowseAssetRoot.Name = "btnBrowseAssetRoot";

        AddLabeledRow(
            root,
            ref row,
            "Asset Root Folder",
            txtAssetRoot,
            btnBrowseAssetRoot);

        txtAssetFolderName =
            CreateTextBox();
        txtAssetFolderName.Name = "txtAssetFolderName";

        AddLabeledRow(
            root,
            ref row,
            "Asset Folder Name",
            txtAssetFolderName,
            null);

        AddSectionHeader(
            root,
            ref row,
            "CURRENT ASSET");

        lblLatestImage =
            new Label
            {
                Name = "lblLatestImage",

                Text =
                    "No image found.",

                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill
            };

        AddLabeledRow(
            root,
            ref row,
            "Latest Download",
            lblLatestImage,
            null);

        lblLatestTimestamp =
            new Label
            {
                Name = "lblLatestTimestamp",

                Text =
                    "Modified: -",

                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill
            };

        AddLabeledRow(
            root,
            ref row,
            string.Empty,
            lblLatestTimestamp,
            null);

        lblManualSelection =
            new Label
            {
                Name = "lblManualSelection",

                Text =
                    "Manual selection: none",

                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill,

                BorderStyle =
                    BorderStyle.FixedSingle,

                Padding =
                    new Padding(8),

                AllowDrop =
                    true
            };

        AddLabeledRow(
            root,
            ref row,
            "Manual Selection",
            lblManualSelection,
            null);

        var imageButtons =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill,

                FlowDirection =
                    FlowDirection.LeftToRight
            };

        btnRefresh =
            CreateButton(
                "Refresh");
        btnRefresh.Name = "btnRefresh";

        btnChooseFile =
            CreateButton(
                "Choose File...");
        btnChooseFile.Name = "btnChooseFile";

        btnOpenDownloads =
            CreateButton(
                "Open Downloads");
        btnOpenDownloads.Name = "btnOpenDownloads";

        imageButtons.Controls.Add(
            btnRefresh);

        imageButtons.Controls.Add(
            btnChooseFile);

        imageButtons.Controls.Add(
            btnOpenDownloads);

        root.Controls.Add(
            new Label(),
            0,
            row);

        root.Controls.Add(
            imageButtons,
            1,
            row);

        root.SetColumnSpan(
            imageButtons,
            2);

        row++;

        lblReference =
            new Label
            {
                Name = "lblReference",

                Text =
                    "No reference selected.",

                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill
            };

        AddLabeledRow(
            root,
            ref row,
            "Reference",
            lblReference,
            null);

        btnReference =
            CreateButton(
                "Reference");
        btnReference.Name = "btnReference";

        root.Controls.Add(
            new Label(),
            0,
            row);

        root.Controls.Add(
            btnReference,
            1,
            row);

        root.SetColumnSpan(
            btnReference,
            2);

        row++;

        var promptLabel =
            new Label
            {
                Text =
                    "Final Prompt",

                AutoSize =
                    true,

                Font =
                    new Font(
                        Font,
                        FontStyle.Bold),

                Margin =
                    new Padding(
                        3,
                        14,
                        3,
                        4)
            };

        root.Controls.Add(
            promptLabel,
            0,
            row);

        root.SetColumnSpan(
            promptLabel,
            3);

        row++;

        txtPrompt =
            new TextBox
            {
                Name = "txtPrompt",

                Multiline =
                    true,

                ScrollBars =
                    ScrollBars.Vertical,

                Dock =
                    DockStyle.Fill,

                Height =
                    120
            };

        root.Controls.Add(
            txtPrompt,
            0,
            row);

        root.SetColumnSpan(
            txtPrompt,
            3);

        row++;

        var promptButtons =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill
            };

        btnPasteClipboard =
            CreateButton(
                "Paste Clipboard");
        btnPasteClipboard.Name = "btnPasteClipboard";

        btnClearPrompt =
            CreateButton(
                "Clear");
        btnClearPrompt.Name = "btnClearPrompt";

        promptButtons.Controls.Add(
            btnPasteClipboard);

        promptButtons.Controls.Add(
            btnClearPrompt);

        root.Controls.Add(
            promptButtons,
            0,
            row);

        root.SetColumnSpan(
            promptButtons,
            3);

        row++;

        btnMainImage =
            CreateButton(
                "Main Image");
        btnMainImage.Name = "btnMainImage";

        btnMainImage.Height =
            38;

        root.Controls.Add(
            btnMainImage,
            0,
            row);

        root.SetColumnSpan(
            btnMainImage,
            3);

        row++;

        var sessionButtons =
            new FlowLayoutPanel
            {
                AutoSize =
                    true,

                Dock =
                    DockStyle.Fill,

                FlowDirection =
                    FlowDirection.LeftToRight,

                Margin =
                    new Padding(
                        0,
                        14,
                        0,
                        0)
            };

        btnOpenAssetFolder =
            CreateButton(
                "Open Asset Folder");
        btnOpenAssetFolder.Name = "btnOpenAssetFolder";

        btnCancel =
            CreateButton(
                "Cancel Current Asset");
        btnCancel.Name = "btnCancel";

        sessionButtons.Controls.Add(
            btnOpenAssetFolder);

        sessionButtons.Controls.Add(
            btnCancel);

        root.Controls.Add(
            sessionButtons,
            0,
            row);

        root.SetColumnSpan(
            sessionButtons,
            3);

        row++;

        AddSectionHeader(
            root,
            ref row,
            "STATUS");

        txtStatusHistory =
            new TextBox
            {
                Name = "txtStatusHistory",

                Multiline =
                    true,

                ReadOnly =
                    true,

                ScrollBars =
                    ScrollBars.Vertical,

                Dock =
                    DockStyle.Fill,

                Height =
                    170
            };

        root.Controls.Add(
            txtStatusHistory,
            0,
            row);

        root.SetColumnSpan(
            txtStatusHistory,
            3);
    }

    private static TextBox CreateTextBox()
    {
        return new TextBox
        {
            Dock =
                DockStyle.Fill
        };
    }

    private static Button CreateButton(
        string text)
    {
        return new Button
        {
            Text =
                text,

            AutoSize =
                true,

            Padding =
                new Padding(
                    8,
                    3,
                    8,
                    3)
        };
    }

    [ExcludeFromCodeCoverage]
    private static void AddSectionHeader(
        TableLayoutPanel root,
        ref int row,
        string text)
    {
        var label =
            new Label
            {
                Text =
                    text,

                AutoSize =
                    true,

                Font =
                    new Font(
                        SystemFonts.DefaultFont,
                        FontStyle.Bold),

                Margin =
                    new Padding(
                        3,
                        16,
                        3,
                        7)
            };

        root.Controls.Add(
            label,
            0,
            row);

        root.SetColumnSpan(
            label,
            3);

        row++;
    }

    [ExcludeFromCodeCoverage]
    private static void AddLabeledRow(
        TableLayoutPanel root,
        ref int row,
        string labelText,
        Control mainControl,
        Control? thirdControl)
    {
        var label =
            new Label
            {
                Text =
                    labelText,

                AutoSize =
                    true,

                Anchor =
                    AnchorStyles.Left,

                Padding =
                    new Padding(
                        0,
                        5,
                        0,
                        0)
            };

        root.Controls.Add(
            label,
            0,
            row);

        root.Controls.Add(
            mainControl,
            1,
            row);

        if (thirdControl is not null)
        {
            root.Controls.Add(
                thirdControl,
                2,
                row);
        }
        else
        {
            root.SetColumnSpan(
                mainControl,
                2);
        }

        row++;
    }
}
