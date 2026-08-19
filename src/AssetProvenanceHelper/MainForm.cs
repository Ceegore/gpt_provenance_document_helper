#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

public partial class MainForm : Form
{
    private enum UiState
    {
        Idle,
        ReferenceReady
    }

    private readonly SettingsService _settingsService;
    private readonly ImageFinderService _imageFinderService;
    private readonly TemplateService _templateService;
    private readonly ValidationService _validationService;
    private readonly AssetProcessorService _assetProcessorService;
    private readonly SessionService _sessionService;

    private AppSettings _settings;
    private AssetSession? _currentSession;
    private string? _lastCompletedAssetFolderPath;

    private UiState _state = UiState.Idle;
    private bool _templatesValid;

    internal Func<string?>? ClipboardProvider { get; set; }

    public MainForm(
        AppSettings settings,
        SettingsService settingsService,
        ImageFinderService imageFinderService,
        TemplateService templateService,
        ValidationService validationService,
        AssetProcessorService assetProcessorService,
        SessionService sessionService)
    {
        _settings = settings;
        _settingsService = settingsService;
        _imageFinderService = imageFinderService;
        _templateService = templateService;
        _validationService = validationService;
        _assetProcessorService = assetProcessorService;
        _sessionService = sessionService;

        InitializeComponent();

        LoadSettingsIntoUi();
        WireEvents();
        ValidateTemplatesAtStartup();
        ApplyState();

        Shown += (_, _) => RecoverSessionOnStartup();
    }

    private void WireEvents()
    {
        btnBrowseDownload.Click += (_, _) => BrowseDownloadFolder();
        btnBrowseAssetRoot.Click += (_, _) => BrowseAssetRoot();

        // Reference controls
        btnRefreshReference.Click += (_, _) => RefreshImageSelection(ImageSlot.Reference);
        btnChooseReference.Click += (_, _) => ChooseImageFile(ImageSlot.Reference);
        btnOpenDownloadsReference.Click += (_, _) => OpenDownloads();
        lblReferenceDrop.DragEnter += ImageDrop_DragEnter;
        lblReferenceDrop.DragDrop += (_, e) => ImageDrop_DragDrop(ImageSlot.Reference, e);

        // Main controls
        btnRefreshMain.Click += (_, _) => RefreshImageSelection(ImageSlot.Main);
        btnChooseMain.Click += (_, _) => ChooseImageFile(ImageSlot.Main);
        btnOpenDownloadsMain.Click += (_, _) => OpenDownloads();
        lblMainDrop.DragEnter += ImageDrop_DragEnter;
        lblMainDrop.DragDrop += (_, e) => ImageDrop_DragDrop(ImageSlot.Main, e);

        chkNoReference.CheckedChanged += (_, _) => OnNoReferenceChanged();

        btnReference.Click += (_, _) =>
        {
            if (_state == UiState.Idle)
            {
                HandleReference();
            }
            else
            {
                HandleReplaceReference();
            }
        };

        btnPasteClipboard.Click += (_, _) => PasteClipboard();
        btnClearPrompt.Click += (_, _) =>
        {
            txtPrompt.Clear();
        };

        btnMainImage.Click += (_, _) => HandleMainImage();
        btnCancel.Click += (_, _) => HandleCancel();
        btnOpenAssetFolder.Click += (_, _) => OpenAssetFolder();

        txtDownloadFolder.Leave += (_, _) => SaveSettingsSafe();
        txtAssetRoot.Leave += (_, _) => SaveSettingsSafe();
        FormClosing += (_, _) => SaveSettingsSafe();

        txtPrompt.TextChanged += (_, _) => ClearPromptValidation();
        txtAssetFolderName.TextChanged += (_, _) => HighlightField(pnlAssetFolderNameHost, false);
        txtAssetRoot.TextChanged += (_, _) => HighlightField(pnlAssetRootHost, false);
        txtDownloadFolder.TextChanged += (_, _) => HighlightField(pnlDownloadFolderHost, false);

        helpOverlay.CloseRequested += (_, _) => pnlMainContent.Enabled = true;

        KeyDown += MainForm_KeyDown;
    }

    private void OnNoReferenceChanged()
    {
        if (_state != UiState.Idle)
        {
            return;
        }

        if (chkNoReference.Checked)
        {
            SetSelectedImage(ImageSlot.Reference, null);
            ClearReferenceValidationVisuals();
        }

        ApplyState();
    }

    private void LoadSettingsIntoUi()
    {
        txtDownloadFolder.Text = _settings.DownloadFolder;
        txtAssetRoot.Text = _settings.AssetRootFolder;
    }

    private void ValidateTemplatesAtStartup()
    {
        var validation = _templateService.ValidateTemplates();
        _templatesValid = validation.IsValid;

        if (!_templatesValid)
        {
            AddStatus("Template validation failed.");
            ShowValidationError("Template validation failed", validation);
        }
        else
        {
            AddStatus("Templates validated.");
        }
    }

    private AppSettings ReadSettingsFromUi()
    {
        _settings.DownloadFolder = txtDownloadFolder.Text;
        _settings.AssetRootFolder = txtAssetRoot.Text;
        return _settings;
    }

    private void BrowseDownloadFolder()
    {
        if (FolderBrowserDialogProvider is not null)
        {
            var selected = FolderBrowserDialogProvider(this, txtDownloadFolder.Text);
            if (selected is not null)
            {
                txtDownloadFolder.Text = selected;
                SaveSettingsSafe();
            }
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select image download folder",
            SelectedPath = txtDownloadFolder.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtDownloadFolder.Text = dialog.SelectedPath;
            SaveSettingsSafe();
        }
    }

    private void BrowseAssetRoot()
    {
        if (FolderBrowserDialogProvider is not null)
        {
            var selected = FolderBrowserDialogProvider(this, txtAssetRoot.Text);
            if (selected is not null)
            {
                txtAssetRoot.Text = selected;
                SaveSettingsSafe();
            }
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select asset root folder",
            SelectedPath = txtAssetRoot.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            txtAssetRoot.Text = dialog.SelectedPath;
            SaveSettingsSafe();
        }
    }

    private void SaveSettingsSafe()
    {
        try
        {
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
        }
        catch (Exception ex)
        {
            ShowError("Could not save settings.", ex);
        }
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (helpOverlay != null && helpOverlay.Visible)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                helpOverlay.HideOverlay();
                pnlMainContent.Enabled = true;
                return;
            }

            // Suppress all hotkeys while help overlay is visible
            return;
        }

        if (e.KeyCode == Keys.F1)
        {
            e.SuppressKeyPress = true;
            ShowHelpOverlay();
            return;
        }

        if (!e.Control)
        {
            return;
        }

        if (e.KeyCode == Keys.O)
        {
            e.SuppressKeyPress = true;
            OpenAssetFolder();
            return;
        }

        if (e.KeyCode == Keys.R)
        {
            if (_state == UiState.Idle && !chkNoReference.Checked)
            {
                e.SuppressKeyPress = true;
                HandleReference();
                return;
            }
            else if (_state == UiState.ReferenceReady)
            {
                e.SuppressKeyPress = true;
                HandleReplaceReference();
                return;
            }
        }

        if (e.KeyCode == Keys.M)
        {
            if (_state == UiState.ReferenceReady || chkNoReference.Checked)
            {
                e.SuppressKeyPress = true;
                HandleMainImage();
            }
        }
    }

    private void ApplyState()
    {
        var referenceReady = _state == UiState.ReferenceReady;
        var noReference = chkNoReference.Checked && !referenceReady;

        txtAssetRoot.Enabled = !referenceReady;
        btnBrowseAssetRoot.Enabled = !referenceReady;
        txtAssetFolderName.Enabled = !referenceReady;

        txtDownloadFolder.Enabled = true;
        btnBrowseDownload.Enabled = true;

        if (referenceReady)
        {
            chkNoReference.Enabled = false;
            chkNoReference.Checked = false;
            grpReference.Visible = true;

            pnlCardsContainer.ColumnStyles[0].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[0].Width = 50;
            pnlCardsContainer.ColumnStyles[1].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[1].Width = 50;

            btnReference.Enabled = _templatesValid;
            btnReference.Text = "Replace Reference";

            btnMainImage.Enabled = _templatesValid;
            btnCancel.Enabled = true;
        }
        else if (noReference)
        {
            chkNoReference.Enabled = true;
            grpReference.Visible = false;

            pnlCardsContainer.ColumnStyles[0].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[0].Width = 0;
            pnlCardsContainer.ColumnStyles[1].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[1].Width = 100;

            btnReference.Enabled = false;
            btnReference.Text = "Reference";

            btnMainImage.Enabled = _templatesValid;
            btnCancel.Enabled = false;
        }
        else
        {
            chkNoReference.Enabled = true;
            grpReference.Visible = true;

            pnlCardsContainer.ColumnStyles[0].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[0].Width = 50;
            pnlCardsContainer.ColumnStyles[1].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[1].Width = 50;

            btnReference.Enabled = _templatesValid;
            btnReference.Text = "Reference";

            btnMainImage.Enabled = false;
            btnCancel.Enabled = false;
        }

        var assetFolder = _currentSession?.AssetFolder ?? _lastCompletedAssetFolderPath;
        btnOpenAssetFolder.Enabled = !string.IsNullOrWhiteSpace(assetFolder) && Directory.Exists(assetFolder);
    }

    private void ShowHelpOverlay()
    {
        pnlMainContent.Enabled = false;
        helpOverlay.ShowOverlay();
    }

    private void ShowMessageBox(
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        if (MessageBoxProvider is not null)
        {
            MessageBoxProvider(
                this,
                message,
                caption,
                buttons,
                icon);
            return;
        }

        MessageBox.Show(
            this,
            message,
            caption,
            buttons,
            icon);
    }

    private void ShowError(
        string context,
        Exception ex)
    {
        if (IsDisposed)
        {
            return;
        }

        AddStatus($"{context} {ex.Message}");

        ShowMessageBox(
            $"{context}\n\n{ex.Message}",
            "Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ShowValidationError(
        string caption,
        ValidationResult validation)
    {
        if (IsDisposed)
        {
            return;
        }

        AddStatus($"{caption}: {string.Join("; ", validation.Errors)}");

        ShowMessageBox(
            string.Join(
                Environment.NewLine,
                validation.Errors),
            caption,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void AddStatus(string message)
    {
        if (IsDisposed || !IsHandleCreated || txtStatusHistory.IsDisposed)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss} {message}";

        if (txtStatusHistory.TextLength > 0)
        {
            txtStatusHistory.AppendText(Environment.NewLine);
        }

        txtStatusHistory.AppendText(line);
        txtStatusHistory.SelectionStart = txtStatusHistory.TextLength;
        txtStatusHistory.ScrollToCaret();
    }

    [ThreadStatic]
    internal static Action<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon>? MessageBoxProvider;

    [ThreadStatic]
    internal static Func<IWin32Window?, string?, string?>? FolderBrowserDialogProvider;

    [ThreadStatic]
    internal static Func<IWin32Window?, string?, string?>? OpenFileDialogProvider;

    [ThreadStatic]
    internal static Action<string>? OpenFolderProvider;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnBeforeReferenceReplacementCommit;

    [ThreadStatic]
    internal static Action<AssetSession>? OnReferenceStableSessionSavedHook;

    [ThreadStatic]
    internal static Action? OnCancelDurableCommitHook;

    [ThreadStatic]
    internal static Action? OnReplacementRollbackDurableCommitHook;
}
