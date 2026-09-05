#nullable enable
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
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
    private readonly ProviderTemplateCatalogService? _providerTemplateCatalogService;
    private readonly RecentDocumentHistoryService? _recentDocumentHistoryService;
    private readonly RequestProgressService? _requestProgressService;
    private readonly RequestQueueStateService? _requestQueueStateService;
    private readonly IImageGenerationProvider _imageGenerationProvider;
    private readonly ISecretStore _secretStore;
    private readonly GenerationJobStore _generationJobStore;

    private AppSettings _settings;
    private AssetSession? _currentSession;
    private string? _lastCompletedAssetFolderPath;

    private UiState _state = UiState.Idle;
    private bool _templatesValid;

    private ProviderCatalogResult? _providerCatalog;
    private ProviderTemplateDefinition? _selectedProvider;
    private readonly List<ProviderTemplateDefinition> _sessionSnapshotProviders =
        new();

    private AssetRequestManifest? _currentManifest;
    private AssetRequestItem? _activeRequest;
    private readonly HashSet<string> _completedRequestKeys =
        new(StringComparer.Ordinal);
    private bool _settingRequestBoundFields;

    /// <summary>
    /// Source paths of Main images durably committed during this app session.
    /// In-memory only and intentionally not persisted - "momentary session" per the
    /// feature request. Used to warn before reprocessing the same downloads.
    /// </summary>
    private readonly HashSet<string> _committedMainSourcesThisSession =
        new(StringComparer.OrdinalIgnoreCase);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string?>? ClipboardProvider { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<string>? ClipboardWriter { get; set; }

    public MainForm(
        AppSettings settings,
        SettingsService settingsService,
        ImageFinderService imageFinderService,
        TemplateService templateService,
        ValidationService validationService,
        AssetProcessorService assetProcessorService,
        SessionService sessionService,
        ProviderTemplateCatalogService? providerTemplateCatalogService = null,
        RecentDocumentHistoryService? recentDocumentHistoryService = null,
        RequestProgressService? requestProgressService = null,
        IImageGenerationProvider? imageGenerationProvider = null,
        ISecretStore? secretStore = null,
        GenerationJobStore? generationJobStore = null,
        GeneratedImageStagingService? stagingService = null,
        RequestQueueStateService? requestQueueStateService = null)
    {
        _settings = settings;
        _settingsService = settingsService;
        _imageFinderService = imageFinderService;
        _templateService = templateService;
        _validationService = validationService;
        _assetProcessorService = assetProcessorService;
        _sessionService = sessionService;
        _providerTemplateCatalogService = providerTemplateCatalogService;
        _recentDocumentHistoryService = recentDocumentHistoryService;
        _requestProgressService = requestProgressService;
        _requestQueueStateService = requestQueueStateService;
        _imageGenerationProvider = imageGenerationProvider ?? new OpenAiImageGenerationProvider();
        _secretStore = secretStore ?? new DpapiSecretStore();
        _generationJobStore = generationJobStore ?? new GenerationJobStore(Path.Combine(AppBootstrap.GetStateDirectory(), "generation-jobs.json"));
        _stagingService = stagingService ?? new GeneratedImageStagingService();

        InitializeComponent();

        LoadSettingsIntoUi();
        WireEvents();
        ValidateTemplatesAtStartup();
        LoadProviderCatalogAtStartup();
        LoadRecentDocumentsIntoUi();
        RestoreRequestQueueOnStartup();
        ApplyState();
        _generationJobStore.RecoverInterruptedJobsOnStartup();
        var candidateRecovery = new LocalCandidateRecoveryService(_generationJobStore, _stagingService);
        candidateRecovery.RecoverAllCandidates();
        InitializeBatchMonitoring();

        Shown += (_, _) =>
        {
            RecoverSessionOnStartup();
            CheckAndStartBatchMonitoring();
        };
    }

    private void OpenSettingsDialog()
    {
        try
        {
            using var dialog = new Dialogs.SettingsDialog(_settings, _secretStore);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    _settingsService.Save(_settings);
                    AddStatus("Settings updated.");
                }
                catch (Exception ex)
                {
                    ShowError("Could not save settings.", ex);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            ShowMessageBox(
                "The encrypted API secret store is corrupt or cannot be decrypted "
                + "for the current Windows user."
                + Environment.NewLine
                + Environment.NewLine
                + "The stored key was NOT overwritten."
                + Environment.NewLine
                + Environment.NewLine
                + ex.Message,
                "API Secret Store Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            if (_secretStore is DpapiSecretStore dpapiStore)
            {
                var confirmReset = ShowConfirmDialog(
                    "Do you want to delete the corrupted secret store file?"
                    + Environment.NewLine
                    + Environment.NewLine
                    + "If deleted, you will need to re-enter your OpenAI API key in Settings.",
                    "Reset Corrupted Secret Store",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirmReset == DialogResult.Yes)
                {
                    dpapiStore.ResetCorruptStore();
                    AddStatus("Corrupted API secret store was deleted.");
                }
            }
        }

        ApplyRequestQueueState();
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
        chkDirectMode.CheckedChanged += (_, _) => OnDirectModeChanged();
        chkKeepSettings.CheckedChanged += (_, _) =>
        {
            _settings.KeepSettingsEnabled = chkKeepSettings.Checked;
        };

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

        btnMainImage.Click += (_, _) => HandleMainImageEntryPoint();
        btnCancel.Click += (_, _) => HandleCancel();
        btnOpenAssetFolder.Click += (_, _) => OpenAssetFolder();

        txtDownloadFolder.Leave += (_, _) => SaveSettingsSafe();
        txtAssetRoot.Leave += (_, _) => SaveSettingsSafe();
        FormClosing += (_, e) =>
        {
            if (_isGeneratingDirect || _isSubmittingBatch)
            {
                var message = _isGeneratingDirect
                    ? "Direct API generation is currently in progress.\n\nClosing may lose responses to requests that OpenAI has already started and those requests may still be billable.\n\nDo you want to close anyway?"
                    : "Batch API submission is currently in progress.\n\nClosing may interrupt batch file upload or creation, which may result in an orphaned or untracked batch on OpenAI.\n\nDo you want to close anyway?";

                var choice = ShowConfirmDialog(
                    message,
                    "Generation in progress",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (choice != DialogResult.Yes && choice != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }

                try
                {
                    _apiGenerationCts?.Cancel();
                }
                catch
                {
                }
            }

            SaveSettingsSafe();
        };

        txtPrompt.TextChanged += (_, _) =>
        {
            ClearPromptValidation();
            UpdatePromptPreview();
            CheckActiveRequestBinding();
        };
        txtAssetFolderName.TextChanged += (_, _) =>
        {
            HighlightField(pnlAssetFolderNameHost, false);
            CheckActiveRequestBinding();
        };
        txtAssetRoot.TextChanged += (_, _) => HighlightField(pnlAssetRootHost, false);
        txtDownloadFolder.TextChanged += (_, _) => HighlightField(pnlDownloadFolderHost, false);

        cmbProvider.SelectedIndexChanged += (_, _) => OnProviderSelectionChanged();
        btnImportRequest.Click += (_, _) => HandleImportRequest();
        btnClearRequestQueue.Click += (_, _) => HandleClearRequestQueue();
        btnGenerateNow.Click += (_, _) => HandleGenerateNow();
        btnQueueProductionBatch.Click += (_, _) => HandleQueueProductionBatch();
        btnRetrySelectedApi.Click += (_, _) => HandleRetrySelectedApi();
        lvRequestQueue.SelectedIndexChanged += (_, _) => ApplyRequestQueueState();
        lvRequestQueue.MouseUp += (_, e) => HandleRequestQueueMouseUp(e);
        lvRequestQueue.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                if (lvRequestQueue.SelectedItems.Count > 0)
                {
                    HandleRequestQueueItemActivate(lvRequestQueue.SelectedItems[0]);
                    e.Handled = true;
                }
            }
        };
        lvRecentDocuments.MouseMove += (_, e) => UpdateRecentDocumentTooltip(e);

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

    private void OnDirectModeChanged()
    {
        _settings.DirectModeEnabled = chkDirectMode.Checked;

        if (_state == UiState.Idle)
        {
            ApplyState();
        }
    }

    private void LoadSettingsIntoUi()
    {
        txtDownloadFolder.Text = _settings.DownloadFolder;
        txtAssetRoot.Text = _settings.AssetRootFolder;
        chkDirectMode.Checked = _settings.DirectModeEnabled;
        chkKeepSettings.Checked = _settings.KeepSettingsEnabled;
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

        BrowseDownloadFolderWithDialog();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private void BrowseDownloadFolderWithDialog()
    {
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

        BrowseAssetRootWithDialog();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private void BrowseAssetRootWithDialog()
    {
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
            if (_state == UiState.Idle && !chkNoReference.Checked && !chkDirectMode.Checked)
            {
                e.SuppressKeyPress = true;
                HandleReference();
                return;
            }
            else if (_state == UiState.ReferenceReady && !chkDirectMode.Checked)
            {
                e.SuppressKeyPress = true;
                HandleReplaceReference();
                return;
            }
        }

        if (e.KeyCode == Keys.M)
        {
            var canMain =
                _state == UiState.ReferenceReady
                || chkNoReference.Checked
                || (chkDirectMode.Checked
                    && _state == UiState.Idle);

            if (canMain)
            {
                e.SuppressKeyPress = true;
                HandleMainImageEntryPoint();
            }
        }
    }

    private void ApplyState()
    {
        var referenceReady = _state == UiState.ReferenceReady;
        var noReference = chkNoReference.Checked && !referenceReady;
        var direct = chkDirectMode.Checked;

        txtAssetRoot.Enabled = !referenceReady;
        btnBrowseAssetRoot.Enabled = !referenceReady;
        txtAssetFolderName.Enabled = !referenceReady;

        txtDownloadFolder.Enabled = true;
        btnBrowseDownload.Enabled = true;

        if (referenceReady)
        {
            chkNoReference.Enabled = false;
            chkNoReference.Checked = false;
            chkDirectMode.Enabled = false;
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
            chkDirectMode.Enabled = true;
            grpReference.Visible = false;

            pnlCardsContainer.ColumnStyles[0].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[0].Width = 0;
            pnlCardsContainer.ColumnStyles[1].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[1].Width = 100;

            btnReference.Enabled = false;
            btnReference.Text = "Reference";

            btnMainImage.Enabled = _templatesValid && CanStartNewAssetWithProvider;
            btnCancel.Enabled = false;
        }
        else
        {
            chkNoReference.Enabled = true;
            chkDirectMode.Enabled = true;
            grpReference.Visible = true;

            pnlCardsContainer.ColumnStyles[0].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[0].Width = 50;
            pnlCardsContainer.ColumnStyles[1].SizeType = SizeType.Percent;
            pnlCardsContainer.ColumnStyles[1].Width = 50;

            if (!noReference && !referenceReady && direct)
            {
                btnReference.Enabled = false;
                btnMainImage.Enabled = _templatesValid && CanStartNewAssetWithProvider;
            }
            else
            {
                btnReference.Enabled = _templatesValid && CanStartNewAssetWithProvider;
                btnMainImage.Enabled = false;
            }

            btnReference.Text = "Reference";
            btnCancel.Enabled = false;
        }

        btnRefreshReference.Enabled = !direct && (referenceReady || !noReference);
        btnRefreshMain.Enabled = !direct;

        cmbProvider.Enabled =
            !referenceReady
            && _providerCatalog?.HasUsableTemplates == true;

        // Variants are available in BOTH workflows, but the count binds the asset
        // folder name at Reference time (plan D-1), so it locks once a reference
        // session is active - exactly like chkNoReference / chkDirectMode above.
        // The selection itself is never reset here: it is still needed while a
        // reference session is live, because it drives the batch that finishes
        // variant A.
        cmbVariants.Enabled = !referenceReady;

        var assetFolder = _currentSession?.AssetFolder ?? _lastCompletedAssetFolderPath;
        btnOpenAssetFolder.Enabled = !string.IsNullOrWhiteSpace(assetFolder) && Directory.Exists(assetFolder);

        ApplyRequestQueueState();
    }

    /// <summary>
    /// Resets the per-asset input fields after a durable completion or cancellation.
    /// Image selections and the saved-reference label are always cleared - a stale
    /// selection points at a file a committed asset has already consumed. Text
    /// inputs and the Variants count survive when Keep Settings is on.
    /// </summary>
    private void ResetAssetInputFieldsAfterDurableAction()
    {
        if (!chkKeepSettings.Checked)
        {
            txtPrompt.Clear();
            txtAssetFolderName.Clear();
            ResetVariantSelectionToNone();
        }

        lblReference.Text = "Saved reference: none";

        SetSelectedImage(ImageSlot.Reference, null);
        SetSelectedImage(ImageSlot.Main, null);
        ClearValidationVisuals();
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
        if (IsDisposed || Disposing || txtStatusHistory.IsDisposed)
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

    private DialogResult ShowConfirmDialog(
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        if (ConfirmBoxProvider is not null)
        {
            return ConfirmBoxProvider(
                this,
                message,
                caption,
                buttons,
                icon);
        }

        return MessageBox.Show(
            this,
            message,
            caption,
            buttons,
            icon);
    }

    [ThreadStatic]
    internal static Action<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon>? MessageBoxProvider;

    [ThreadStatic]
    internal static Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult>? ConfirmBoxProvider;

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

    [ThreadStatic]
    internal static Action? OnNoReferenceJournalSavedBeforeStatusHook;

    [ThreadStatic]
    internal static Action? OnReplacementDurableCommitUiHook;

    [ThreadStatic]
    internal static Action<int, string>? OnVariantCommittedHook;
}
