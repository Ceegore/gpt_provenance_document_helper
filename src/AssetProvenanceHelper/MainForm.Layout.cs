#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private static Panel CreateFieldHost(Control innerControl)
    {
        var host = new Panel
        {
            Padding = new Padding(2),
            BackColor = UiTheme.Border,
            Dock = DockStyle.Fill
        };

        if (innerControl is TextBox tb)
        {
            tb.BorderStyle = BorderStyle.None;
        }

        innerControl.Dock = DockStyle.Fill;
        host.Controls.Add(innerControl);
        return host;
    }

    private static Button CreateButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3),
            UseVisualStyleBackColor = true
        };
    }

    private static Button CreateCtaButton(string text, Color accent)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 38,
            UseVisualStyleBackColor = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent,
            ForeColor = Color.White,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    [ExcludeFromCodeCoverage]
    private void BuildHeader(TableLayoutPanel root)
    {
        pnlHeader = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 44,
            Margin = new Padding(0, 0, 0, 8)
        };

        picLogo = new PictureBox
        {
            Size = new Size(36, 36),
            Location = new Point(0, 4),
            SizeMode = PictureBoxSizeMode.Zoom
        };

        try
        {
            if (Icon != null)
            {
                picLogo.Image = Icon.ToBitmap();
            }
        }
        catch
        {
            // Non-critical logo rendering fallback
        }

        lblHeaderTitle = new Label
        {
            Text = AppInfo.ProductName,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 13f, FontStyle.Bold),
            Location = new Point(44, 4),
            AutoSize = true
        };

        lblHeaderVersion = new Label
        {
            Text = $"v{AppInfo.Version}",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Regular),
            ForeColor = Color.Gray,
            Location = new Point(44, 26),
            AutoSize = true
        };

        btnHelp = new Button
        {
            Name = "btnHelp",
            Text = "?",
            Size = new Size(32, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(pnlHeader.Width - 36, 6),
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            UseVisualStyleBackColor = true
        };
        btnHelp.Click += (_, _) => ShowHelpOverlay();

        pnlHeader.Controls.Add(picLogo);
        pnlHeader.Controls.Add(lblHeaderTitle);
        pnlHeader.Controls.Add(lblHeaderVersion);
        pnlHeader.Controls.Add(btnHelp);

        pnlHeader.Resize += (_, _) =>
        {
            btnHelp.Location = new Point(pnlHeader.Width - 36, 6);
        };

        root.Controls.Add(pnlHeader, 0, 0);
    }

    [ExcludeFromCodeCoverage]
    private void BuildSettingsGroup(TableLayoutPanel root)
    {
        grpSettings = new GroupBox
        {
            Name = "grpSettings",
            Text = "Settings",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Download folder
        var lblDl = new Label { Text = "Image Download Folder", AutoSize = true, Anchor = AnchorStyles.Left };
        txtDownloadFolder = new TextBox { Name = "txtDownloadFolder" };
        pnlDownloadFolderHost = CreateFieldHost(txtDownloadFolder);
        btnBrowseDownload = CreateButton("Browse");
        btnBrowseDownload.Name = "btnBrowseDownload";

        layout.Controls.Add(lblDl, 0, 0);
        layout.Controls.Add(pnlDownloadFolderHost, 1, 0);
        layout.Controls.Add(btnBrowseDownload, 2, 0);

        // Asset Root
        var lblRoot = new Label { Text = "Asset Root Folder", AutoSize = true, Anchor = AnchorStyles.Left };
        txtAssetRoot = new TextBox { Name = "txtAssetRoot" };
        pnlAssetRootHost = CreateFieldHost(txtAssetRoot);
        btnBrowseAssetRoot = CreateButton("Browse");
        btnBrowseAssetRoot.Name = "btnBrowseAssetRoot";

        layout.Controls.Add(lblRoot, 0, 1);
        layout.Controls.Add(pnlAssetRootHost, 1, 1);
        layout.Controls.Add(btnBrowseAssetRoot, 2, 1);

        // AI Generation Provider
        var lblProvider = new Label { Text = "AI Generation Provider", AutoSize = true, Anchor = AnchorStyles.Left };
        cmbProvider = new ComboBox
        {
            Name = "cmbProvider",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        lblProviderWarning = new Label
        {
            Name = "lblProviderWarning",
            Text = string.Empty,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(230, 126, 34),
            Margin = new Padding(6, 0, 0, 0),
            Visible = false
        };
        var pnlProviderHost = CreateFieldHost(cmbProvider);

        layout.Controls.Add(lblProvider, 0, 2);
        layout.Controls.Add(pnlProviderHost, 1, 2);
        layout.Controls.Add(lblProviderWarning, 2, 2);

        grpSettings.Controls.Add(layout);
        root.Controls.Add(grpSettings, 0, 1);
    }

    [ExcludeFromCodeCoverage]
    private void BuildCurrentAssetGroup(TableLayoutPanel root)
    {
        grpCurrentAsset = new GroupBox
        {
            Name = "grpCurrentAsset",
            Text = "Current Asset",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lblName = new Label { Text = "Asset Name", AutoSize = true, Anchor = AnchorStyles.Left };
        txtAssetFolderName = new TextBox { Name = "txtAssetFolderName" };
        pnlAssetFolderNameHost = CreateFieldHost(txtAssetFolderName);

        var modeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        chkNoReference = new CheckBox
        {
            Name = "chkNoReference",
            Text = "No reference mode",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(10, 0, 0, 0)
        };

        chkDirectMode = new CheckBox
        {
            Name = "chkDirectMode",
            Text = "Direct mode",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(14, 0, 0, 0)
        };

        modeFlow.Controls.Add(chkNoReference);
        modeFlow.Controls.Add(chkDirectMode);

        layout.Controls.Add(lblName, 0, 0);
        layout.Controls.Add(pnlAssetFolderNameHost, 1, 0);
        layout.Controls.Add(modeFlow, 2, 0);

        grpCurrentAsset.Controls.Add(layout);
        root.Controls.Add(grpCurrentAsset, 0, 2);
    }

    [ExcludeFromCodeCoverage]
    private void BuildCardsSection(TableLayoutPanel root)
    {
        pnlCardsContainer = new TableLayoutPanel
        {
            Name = "pnlCardsContainer",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };

        pnlCardsContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pnlCardsContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // Reference Group
        grpReference = new GroupBox
        {
            Name = "grpReference",
            Text = "Reference Image",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        var refLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5
        };

        refLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Candidate text
        refLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Timestamp
        refLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Drop box
        refLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons & Saved
        refLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // CTA

        lblReferenceSelectedImage = new Label
        {
            Name = "lblReferenceSelectedImage",
            Text = "Selected candidate: none",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill
        };

        lblReferenceTimestamp = new Label
        {
            Name = "lblReferenceTimestamp",
            Text = "Modified: -",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray
        };

        lblReferenceDrop = new Label
        {
            Name = "lblReferenceDrop",
            Text = "Drop Reference Image Here\n(or use buttons below)",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            AllowDrop = true,
            BackColor = UiTheme.GroupBackground
        };

        pnlReferenceImageHost = CreateFieldHost(lblReferenceDrop);

        var refButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        btnRefreshReference = CreateButton("Refresh");
        btnRefreshReference.Name = "btnRefreshReference";
        btnChooseReference = CreateButton("Choose File...");
        btnChooseReference.Name = "btnChooseReference";
        btnDropReference = CreateButton("Drop file here");
        btnDropReference.Name = "btnDropReference";
        btnDropReference.AllowDrop = true;
        btnDropReference.DragEnter += (s, e) => ImageDrop_DragEnter(s, e);
        btnDropReference.DragDrop += (s, e) => ImageDrop_DragDrop(ImageSlot.Reference, e);
        _toolTip.SetToolTip(btnDropReference, "Drop an image file here to select it as the Reference candidate.");
        btnOpenDownloadsReference = CreateButton("Open Downloads");
        btnOpenDownloadsReference.Name = "btnOpenDownloadsReference";

        refButtons.Controls.Add(btnRefreshReference);
        refButtons.Controls.Add(btnChooseReference);
        refButtons.Controls.Add(btnDropReference);
        refButtons.Controls.Add(btnOpenDownloadsReference);

        lblReference = new Label
        {
            Name = "lblReference",
            Text = "Saved reference: none",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Italic)
        };

        var refActionsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        refActionsPanel.Controls.Add(refButtons, 0, 0);
        refActionsPanel.Controls.Add(lblReference, 0, 1);

        btnReference = CreateCtaButton("Reference", UiTheme.ReferenceAccent);
        btnReference.Name = "btnReference";

        refLayout.Controls.Add(lblReferenceSelectedImage, 0, 0);
        refLayout.Controls.Add(lblReferenceTimestamp, 0, 1);
        refLayout.Controls.Add(pnlReferenceImageHost, 0, 2);
        refLayout.Controls.Add(refActionsPanel, 0, 3);
        refLayout.Controls.Add(btnReference, 0, 4);

        grpReference.Controls.Add(refLayout);

        // Main Image Group
        grpMain = new GroupBox
        {
            Name = "grpMain",
            Text = "Main Image",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };

        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Selected text
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Timestamp
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); // Drop box
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); // Prompt
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // CTA

        lblMainSelectedImage = new Label
        {
            Name = "lblMainSelectedImage",
            Text = "Selected: none",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill
        };

        lblMainTimestamp = new Label
        {
            Name = "lblMainTimestamp",
            Text = "Modified: -",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray
        };

        lblMainDrop = new Label
        {
            Name = "lblMainDrop",
            Text = "Drop Main Image Here\n(or use buttons below)",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            AllowDrop = true,
            BackColor = UiTheme.GroupBackground
        };

        pnlMainImageHost = CreateFieldHost(lblMainDrop);

        var mainButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        btnRefreshMain = CreateButton("Refresh");
        btnRefreshMain.Name = "btnRefreshMain";
        btnChooseMain = CreateButton("Choose File...");
        btnChooseMain.Name = "btnChooseMain";
        btnDropMain = CreateButton("Drop file here");
        btnDropMain.Name = "btnDropMain";
        btnDropMain.AllowDrop = true;
        btnDropMain.DragEnter += (s, e) => ImageDrop_DragEnter(s, e);
        btnDropMain.DragDrop += (s, e) => ImageDrop_DragDrop(ImageSlot.Main, e);
        _toolTip.SetToolTip(btnDropMain, "Drop an image file here to select it as the Main candidate.");
        btnOpenDownloadsMain = CreateButton("Open Downloads");
        btnOpenDownloadsMain.Name = "btnOpenDownloadsMain";

        mainButtons.Controls.Add(btnRefreshMain);
        mainButtons.Controls.Add(btnChooseMain);
        mainButtons.Controls.Add(btnDropMain);
        mainButtons.Controls.Add(btnOpenDownloadsMain);

        var promptContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        promptContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        promptContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        promptContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        promptContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblPromptTitle = new Label
        {
            Text = "Final Prompt",
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };

        lblPromptPreview = new Label
        {
            Name = "lblPromptPreview",
            Text = "No prompt stored.",
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 2, 0, 2)
        };
        _toolTip.SetToolTip(lblPromptPreview, "Hover for the full Prompt.");

        txtPrompt = new TextBox
        {
            Name = "txtPrompt",
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill
        };
        pnlPromptHost = CreateFieldHost(txtPrompt);

        var promptButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };

        btnPasteClipboard = CreateButton("Paste Clipboard");
        btnPasteClipboard.Name = "btnPasteClipboard";
        btnClearPrompt = CreateButton("Clear");
        btnClearPrompt.Name = "btnClearPrompt";

        promptButtons.Controls.Add(btnPasteClipboard);
        promptButtons.Controls.Add(btnClearPrompt);

        promptContainer.Controls.Add(lblPromptTitle, 0, 0);
        promptContainer.Controls.Add(lblPromptPreview, 0, 1);
        promptContainer.Controls.Add(pnlPromptHost, 0, 2);
        promptContainer.Controls.Add(promptButtons, 0, 3);

        btnMainImage = CreateCtaButton("Main Image", UiTheme.MainAccent);
        btnMainImage.Name = "btnMainImage";

        mainLayout.Controls.Add(lblMainSelectedImage, 0, 0);
        mainLayout.Controls.Add(lblMainTimestamp, 0, 1);
        mainLayout.Controls.Add(pnlMainImageHost, 0, 2);
        mainLayout.Controls.Add(mainButtons, 0, 3);
        mainLayout.Controls.Add(promptContainer, 0, 4);
        mainLayout.Controls.Add(btnMainImage, 0, 5);

        grpMain.Controls.Add(mainLayout);

        pnlCardsContainer.Controls.Add(grpReference, 0, 0);
        pnlCardsContainer.Controls.Add(grpMain, 1, 0);

        root.Controls.Add(pnlCardsContainer, 0, 3);
    }

    [ExcludeFromCodeCoverage]
    private void BuildStatusGroup(TableLayoutPanel root)
    {
        grpStatus = new GroupBox
        {
            Name = "grpStatus",
            Text = "Status History & Actions",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 145,
            MinimumSize = new Size(0, 135),
            Padding = new Padding(10)
        };

        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };

        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lvRecentDocuments = new ListView
        {
            Name = "lvRecentDocuments",
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill
        };

        lvRecentDocuments.Columns.Add("Time", 75);
        lvRecentDocuments.Columns.Add("Type", 80);
        lvRecentDocuments.Columns.Add("Asset", 220);
        lvRecentDocuments.Columns.Add("Document", -2);

        txtStatusHistory = new TextBox
        {
            Name = "txtStatusHistory",
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Height = 0,
            Visible = false
        };

        var actionButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 6, 0, 0)
        };

        btnOpenAssetFolder = CreateButton("Open Asset Folder");
        btnOpenAssetFolder.Name = "btnOpenAssetFolder";
        btnCancel = CreateButton("Cancel Current Asset");
        btnCancel.Name = "btnCancel";

        actionButtons.Controls.Add(btnOpenAssetFolder);
        actionButtons.Controls.Add(btnCancel);

        statusLayout.Controls.Add(lvRecentDocuments, 0, 0);
        statusLayout.Controls.Add(txtStatusHistory, 0, 1);
        statusLayout.Controls.Add(actionButtons, 0, 2);

        grpStatus.Controls.Add(statusLayout);
        root.Controls.Add(grpStatus, 0, 4);
    }

    [ExcludeFromCodeCoverage]
    private void BuildRequestQueueGroup(TableLayoutPanel workspace)
    {
        grpRequestQueue = new GroupBox
        {
            Name = "grpRequestQueue",
            Text = "Request Queue",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(10)
        };

        var queueLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };

        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        btnImportRequest = CreateButton("Import Request...");
        btnImportRequest.Name = "btnImportRequest";
        btnImportRequest.Dock = DockStyle.Fill;

        lblRequestSource = new Label
        {
            Name = "lblRequestSource",
            Text = "No Request Manifest imported.",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray
        };

        lvRequestQueue = new ListView
        {
            Name = "lvRequestQueue",
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill
        };

        lvRequestQueue.Columns.Add("Status", 70);
        lvRequestQueue.Columns.Add("Asset", 180);
        lvRequestQueue.Columns.Add("Resolution", -2);

        lblRequestProgress = new Label
        {
            Name = "lblRequestProgress",
            Text = string.Empty,
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 0, 0)
        };

        queueLayout.Controls.Add(btnImportRequest, 0, 0);
        queueLayout.Controls.Add(lblRequestSource, 0, 1);
        queueLayout.Controls.Add(lvRequestQueue, 0, 2);
        queueLayout.Controls.Add(lblRequestProgress, 0, 3);

        grpRequestQueue.Controls.Add(queueLayout);
    }
}
