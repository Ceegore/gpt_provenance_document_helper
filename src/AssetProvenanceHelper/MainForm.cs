using System.Diagnostics;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

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

    private string? _latestImagePath;
    private string? _manualSelectionPath;
    private string? _lastCompletedAssetFolderPath;

    private UiState _state =
        UiState.Idle;

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
        _settings =
            settings;

        _settingsService =
            settingsService;

        _imageFinderService =
            imageFinderService;

        _templateService =
            templateService;

        _validationService =
            validationService;

        _assetProcessorService =
            assetProcessorService;

        _sessionService =
            sessionService;

        InitializeComponent();

        LoadSettingsIntoUi();
        WireEvents();

        ValidateTemplatesAtStartup();

        ApplyState();

        RefreshLatestImage();

        Shown +=
            (_, _) =>
                RecoverSessionOnStartup();
    }

    private void WireEvents()
    {
        btnBrowseDownload.Click +=
            (_, _) =>
                BrowseDownloadFolder();

        btnBrowseAssetRoot.Click +=
            (_, _) =>
                BrowseAssetRoot();

        btnRefresh.Click +=
            (_, _) =>
                RefreshLatestImage();

        btnChooseFile.Click +=
            (_, _) =>
                ChooseFile();

        btnOpenDownloads.Click +=
            (_, _) =>
                OpenDownloads();

        btnReference.Click +=
            (_, _) =>
            {
                if (_state ==
                    UiState.Idle)
                {
                    HandleReference();
                }
                else
                {
                    HandleReplaceReference();
                }
            };

        btnPasteClipboard.Click +=
            (_, _) =>
                PasteClipboard();

        btnClearPrompt.Click +=
            (_, _) =>
                txtPrompt.Clear();

        btnMainImage.Click +=
            (_, _) =>
                HandleMainImage();

        btnCancel.Click +=
            (_, _) =>
                HandleCancel();

        btnOpenAssetFolder.Click +=
            (_, _) =>
                OpenAssetFolder();

        txtProject.Leave +=
            (_, _) =>
                SaveSettingsSafe();

        FormClosing +=
            (_, _) =>
                SaveSettingsSafe();

        KeyDown +=
            MainForm_KeyDown;

        lblManualSelection.DragEnter +=
            ManualSelection_DragEnter;

        lblManualSelection.DragDrop +=
            ManualSelection_DragDrop;
    }

    private void LoadSettingsIntoUi()
    {
        txtProject.Text =
            _settings.ProjectName;

        txtDownloadFolder.Text =
            _settings.DownloadFolder;

        txtAssetRoot.Text =
            _settings.AssetRootFolder;
    }

    private void ValidateTemplatesAtStartup()
    {
        var validation =
            _templateService
                .ValidateTemplates();

        _templatesValid =
            validation.IsValid;

        if (!_templatesValid)
        {
            AddStatus(
                "Template validation failed.");

            ShowValidationError(
                "Template validation failed",
                validation);
        }
        else
        {
            AddStatus(
                "Templates validated.");
        }
    }

    private AppSettings ReadSettingsFromUi()
    {
        _settings.ProjectName =
            txtProject.Text;

        _settings.DownloadFolder =
            txtDownloadFolder.Text;

        _settings.AssetRootFolder =
            txtAssetRoot.Text;

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
                RefreshLatestImage();
            }
            return;
        }

        using var dialog =
            new FolderBrowserDialog
            {
                Description =
                    "Select Firefox download folder",

                SelectedPath =
                    txtDownloadFolder.Text
            };

        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            return;
        }

        txtDownloadFolder.Text =
            dialog.SelectedPath;

        SaveSettingsSafe();

        RefreshLatestImage();
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

        using var dialog =
            new FolderBrowserDialog
            {
                Description =
                    "Select asset root folder",

                SelectedPath =
                    txtAssetRoot.Text
            };

        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            return;
        }

        txtAssetRoot.Text =
            dialog.SelectedPath;

        SaveSettingsSafe();
    }

    private void SaveSettingsSafe()
    {
        try
        {
            var settings =
                ReadSettingsFromUi();

            _settingsService.Save(
                settings);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not save settings.",
                ex);
        }
    }

    private void RefreshLatestImage()
    {
        try
        {
            var settings =
                ReadSettingsFromUi();

            _latestImagePath =
                _imageFinderService
                    .FindLatestImage(
                        settings);

            if (_latestImagePath is null)
            {
                lblLatestImage.Text =
                    "No image found.";

                lblLatestTimestamp.Text =
                    "Modified: -";

                return;
            }

            var info =
                new FileInfo(
                    _latestImagePath);

            lblLatestImage.Text =
                info.Name;

            lblLatestTimestamp.Text =
                $"Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _latestImagePath =
                null;

            lblLatestImage.Text =
                "Could not determine latest image.";

            lblLatestTimestamp.Text =
                "Modified: -";

            ShowError(
                "Could not determine latest image.",
                ex);
        }
    }

    private string? ResolveImageSelection()
    {
        if (!string.IsNullOrWhiteSpace(
                _manualSelectionPath))
        {
            return _manualSelectionPath;
        }

        RefreshLatestImage();

        return _latestImagePath;
    }

    private void ChooseFile()
    {
        string? selectedFilePath = null;

        if (OpenFileDialogProvider is not null)
        {
            selectedFilePath = OpenFileDialogProvider(this, txtDownloadFolder.Text);
            if (selectedFilePath is null)
            {
                return;
            }
        }
        else
        {
            using var dialog =
                new OpenFileDialog
                {
                    Title =
                        "Choose image",

                    Filter =
                        "Image files (*.png;*.webp;*.jpg;*.jpeg)|*.png;*.webp;*.jpg;*.jpeg|All files (*.*)|*.*",

                    CheckFileExists =
                        true,

                    Multiselect =
                        false
                };

            if (dialog.ShowDialog(this) !=
                DialogResult.OK)
            {
                return;
            }

            selectedFilePath = dialog.FileName;
        }

        var validation =
            _validationService
                .ValidateImageFile(
                    selectedFilePath,
                    _settings.AcceptedExtensions);

        if (!validation.IsValid)
        {
            ShowValidationError(
                "Invalid image",
                validation);

            return;
        }

        SetManualSelection(
            selectedFilePath);
    }

    private void SetManualSelection(
        string path)
    {
        _manualSelectionPath =
            path;

        lblManualSelection.Text =
            $"Manual selection: {path}";
    }

    private void ClearManualSelection()
    {
        _manualSelectionPath =
            null;

        lblManualSelection.Text =
            "Manual selection: none";
    }

    private void ManualSelection_DragEnter(
        object? sender,
        DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(
                DataFormats.FileDrop) == true)
        {
            e.Effect =
                DragDropEffects.Copy;
        }
        else
        {
            e.Effect =
                DragDropEffects.None;
        }
    }

    private void ManualSelection_DragDrop(
        object? sender,
        DragEventArgs e)
    {
        try
        {
            var files =
                e.Data?
                    .GetData(
                        DataFormats.FileDrop)
                    as string[];

            if (files is null ||
                files.Length != 1)
            {
                ShowMessageBox(
                    "Drop exactly one image file.",
                    "Invalid drop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var validation =
                _validationService
                    .ValidateImageFile(
                        files[0],
                        _settings.AcceptedExtensions);

            if (!validation.IsValid)
            {
                ShowValidationError(
                    "Invalid image",
                    validation);

                return;
            }

            SetManualSelection(
                files[0]);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not use dropped image.",
                ex);
        }
    }

    private void HandleReference()
    {
        AssetSession? createdSession =
            null;

        try
        {
            var settings =
                ReadSettingsFromUi();

            var settingsValidation =
                _validationService
                    .ValidateSettings(
                        settings);

            if (!settingsValidation.IsValid)
            {
                ShowValidationError(
                    "Invalid settings",
                    settingsValidation);

                return;
            }

            var folderName =
                txtAssetFolderName.Text;

            var folderValidation =
                _validationService
                    .ValidateAssetFolderName(
                        folderName);

            if (!folderValidation.IsValid)
            {
                ShowValidationError(
                    "Invalid Asset Folder Name",
                    folderValidation);

                return;
            }

            var templateValidation =
                _templateService
                    .ValidateTemplates();

            if (!templateValidation.IsValid)
            {
                ShowValidationError(
                    "Invalid templates",
                    templateValidation);

                return;
            }

            var sourceImage =
                ResolveImageSelection();

            if (sourceImage is null)
            {
                ShowMessageBox(
                    "No usable image was found.",
                    "Reference",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var imageValidation =
                _validationService
                    .ValidateImageFile(
                        sourceImage,
                        settings.AcceptedExtensions);

            if (!imageValidation.IsValid)
            {
                ShowValidationError(
                    "Invalid reference image",
                    imageValidation);

                return;
            }

            var targetAssetFolder =
                Path.Combine(
                    settings.AssetRootFolder,
                    folderName);

            if (Directory.Exists(
                    targetAssetFolder))
            {
                var useExisting =
                    TwoChoiceDialog.ShowChoice(
                        this,
                        "Existing destination",
                        "The destination folder already exists.\n\nUse existing folder?",
                        "Use Existing",
                        "Cancel");

                if (!useExisting)
                {
                    return;
                }
            }

            createdSession =
                _assetProcessorService
                    .ProcessReference(
                        settings,
                        folderName,
                        sourceImage,
                        DateTimeOffset.Now);

            try
            {
                _sessionService.Save(
                    createdSession);
            }
            catch (Exception saveException)
            {
                var rollback =
                    _assetProcessorService
                        .RollbackReference(
                            createdSession);

                if (!rollback.IsValid)
                {
                    throw new IOException(
                        "Could not save session and reference rollback was incomplete."
                        + Environment.NewLine
                        + string.Join(
                            Environment.NewLine,
                            rollback.Errors),
                        saveException);
                }

                throw;
            }

            _currentSession =
                createdSession;

            _state =
                UiState.ReferenceReady;

            lblReference.Text =
                createdSession.ReferenceFilename;

            ClearManualSelection();

            AddStatus(
                $"Reference copied: {createdSession.ReferenceFilename}");

            AddStatus(
                "Reference provenance created.");

            AddStatus(
                "Reference session saved.");

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError(
                "Reference processing failed.",
                ex);
        }
    }

    private void HandleReplaceReference()
    {
        if (_currentSession is null)
        {
            return;
        }

        var confirmed =
            TwoChoiceDialog.ShowChoice(
                this,
                "Replace Reference",
                "Replace the current reference image?",
                "Replace",
                "Cancel");

        if (!confirmed)
        {
            return;
        }

        ReferenceReplacementTransaction? transaction =
            null;

        try
        {
            var source =
                ResolveImageSelection();

            if (source is null)
            {
                ShowMessageBox(
                    "No usable image was found.",
                    "Replace Reference",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var validation =
                _validationService
                    .ValidateImageFile(
                        source,
                        _settings.AcceptedExtensions);

            if (!validation.IsValid)
            {
                ShowValidationError(
                    "Invalid replacement image",
                    validation);

                return;
            }

            transaction =
                _assetProcessorService
                    .PrepareReferenceReplacement(
                        _currentSession,
                        _settings.AcceptedExtensions,
                        source,
                        DateTimeOffset.Now);

            try
            {
                _sessionService.Save(
                    transaction.NewSession);
            }
            catch (Exception saveException)
            {
                var rollback =
                    _assetProcessorService
                        .RollbackReferenceReplacement(
                            transaction);

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Replacement session could not be saved and the old reference could not be fully restored.\n\n"
                        + string.Join(
                            Environment.NewLine,
                            rollback.Errors),
                        "Critical replacement error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();

                    return;
                }

                throw new IOException(
                    "Could not save replacement session. The previous reference was restored.",
                    saveException);
            }

            OnBeforeReferenceReplacementCommit?.Invoke(transaction);

            var cleanup =
                _assetProcessorService
                    .CommitReferenceReplacement(
                        transaction);

            if (!cleanup.IsValid)
            {
                var newValidation = _validationService.ValidateReferenceOutput(transaction.NewSession);
                var exactNewProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                    transaction.NewSession,
                    transaction.NewSession.ReferenceProvenancePath,
                    _templateService);

                if (!newValidation.IsValid || !exactNewProvValidation.IsValid)
                {
                    var rollback = _assetProcessorService.RollbackReferenceReplacement(transaction);
                    if (!rollback.IsValid)
                    {
                        ShowMessageBox(
                            "CRITICAL: Reference replacement failed, new reference output was invalid, and previous reference could not be fully restored.\n\n"
                            + string.Join(Environment.NewLine, rollback.Errors),
                            "Critical Replacement Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);

                        Close();
                        return;
                    }

                    try
                    {
                        _sessionService.Save(transaction.OldSession);
                    }
                    catch (Exception saveEx)
                    {
                        ShowError(
                            "CRITICAL: Reference replacement failed, previous reference files were restored, but old session record could not be saved.",
                            saveEx);

                        Close();
                        return;
                    }

                    _currentSession = transaction.OldSession;
                    lblReference.Text = _currentSession.ReferenceFilename;
                    ClearManualSelection();
                    ApplyState();

                    var combinedErrors = cleanup.Errors.ToList();
                    if (!exactNewProvValidation.IsValid)
                    {
                        combinedErrors.AddRange(exactNewProvValidation.Errors);
                    }

                    ShowMessageBox(
                        "Reference replacement failed because the new reference output was invalid. The previous reference state was restored.\n\n"
                        + string.Join(Environment.NewLine, combinedErrors.Distinct()),
                        "Reference Replacement Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    return;
                }

                _currentSession =
                    transaction.NewSession;

                lblReference.Text =
                    _currentSession.ReferenceFilename;

                ClearManualSelection();

                AddStatus(
                    $"Reference replaced: {_currentSession.ReferenceFilename}");

                AddStatus(
                    "Reference provenance updated.");

                AddStatus(
                    "Reference session updated.");

                ShowMessageBox(
                    "Reference replacement succeeded, but old temporary backup files could not be fully cleaned up.\n\n"
                    + string.Join(
                        Environment.NewLine,
                        cleanup.Errors),
                    "Replacement cleanup warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                ApplyState();
                return;
            }

            _currentSession =
                transaction.NewSession;

            lblReference.Text =
                _currentSession.ReferenceFilename;

            ClearManualSelection();

            AddStatus(
                $"Reference replaced: {_currentSession.ReferenceFilename}");

            AddStatus(
                "Reference provenance updated.");

            AddStatus(
                "Reference session updated.");

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError(
                "Reference replacement failed.",
                ex);
        }
    }

    private void HandleMainImage()
    {
        if (_currentSession is null)
        {
            ShowMessageBox(
                "No active reference session exists.",
                "Main Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        var sessionValidation =
            _validationService
                .ValidateSession(
                    _currentSession);

        if (!sessionValidation.IsValid)
        {
            ShowValidationError(
                "Invalid reference session",
                sessionValidation);

            return;
        }

        var sourceImage =
            ResolveImageSelection();

        if (sourceImage is null)
        {
            ShowMessageBox(
                "No usable image was found.",
                "Main Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        var imageValidation =
            _validationService
                .ValidateImageFile(
                    sourceImage,
                    _settings.AcceptedExtensions);

        if (!imageValidation.IsValid)
        {
            ShowValidationError(
                "Invalid main image",
                imageValidation);

            return;
        }

        var prompt =
            txtPrompt.Text;

        if (string.IsNullOrWhiteSpace(
                prompt))
        {
            string? clipboardText = null;
            bool hasText = false;

            // BUG-011: Safe clipboard access
            try
            {
                if (ClipboardProvider is not null)
                {
                    clipboardText = ClipboardProvider();
                    hasText = !string.IsNullOrWhiteSpace(clipboardText);
                }
                else if (Clipboard.ContainsText())
                {
                    clipboardText = Clipboard.GetText();
                    hasText = !string.IsNullOrWhiteSpace(clipboardText);
                }
            }
            catch
            {
                hasText = false;
            }

            if (!hasText)
            {
                ShowMessageBox(
                    "Prompt field is empty and the clipboard does not contain text.",
                    "Main Image",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var paste =
                TwoChoiceDialog.ShowChoice(
                    this,
                    "Prompt field is empty",
                    "Prompt field is empty.\n\nPaste current clipboard contents?",
                    "Paste and Continue",
                    "Cancel");

            if (!paste)
            {
                return;
            }

            prompt =
                clipboardText ?? string.Empty;

            txtPrompt.Text =
                prompt;
        }

        string? mainFilename =
            Path.GetFileName(sourceImage);
        var processedAt =
            DateTimeOffset.Now;

        string sourceImageHash;
        try
        {
            sourceImageHash =
                _assetProcessorService.ComputeSha256(sourceImage);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not read the selected Main image.",
                ex);

            return;
        }

        // BUG-002 & BUG-R2-006 & BUG-R9-002: Persist IsMainCommitting, Main metadata, MainTransactionId, and MainHash before performing any file writes.
        // If updating the session file fails, abort Main processing immediately.
        _currentSession.IsMainCommitting = true;
        _currentSession.MainFilename = mainFilename;
        _currentSession.MainPrompt = prompt;
        _currentSession.MainProcessedAt = processedAt;
        _currentSession.MainHash = sourceImageHash;
        _currentSession.MainTransactionId = Guid.NewGuid().ToString("N");

        try
        {
            _sessionService.Save(_currentSession);
        }
        catch (Exception saveEx)
        {
            _currentSession.ResetMainCommitMetadata();

            ShowError(
                "Could not update session state before Main Image processing. Operation was aborted.",
                saveEx);

            return;
        }

        try
        {
            mainFilename =
                _assetProcessorService
                    .ProcessMainImage(
                        _currentSession,
                        _settings.AcceptedExtensions,
                        sourceImage,
                        prompt,
                        processedAt);

            try
            {
                _sessionService.Delete();
            }
            catch (Exception deleteException)
            {
                var rollback =
                    _assetProcessorService
                        .RollbackMain(
                            _currentSession,
                            mainFilename);

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Main Image was created, session deletion failed, and automatic rollback was incomplete.\n\n"
                        + string.Join(
                            Environment.NewLine,
                            rollback.Errors),
                        "Critical completion error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();

                    return;
                }

                throw new IOException(
                    "Main Image was rolled back because session.json could not be removed.",
                    deleteException);
            }

            _lastCompletedAssetFolderPath =
                _currentSession.AssetFolder;

            AddStatus(
                $"Main image copied: {mainFilename}");

            AddStatus(
                "Final provenance created.");

            AddStatus(
                "Asset completed.");

            _currentSession =
                null;

            _state =
                UiState.Idle;

            txtPrompt.Clear();
            txtAssetFolderName.Clear();

            lblReference.Text =
                "No reference selected.";

            ClearManualSelection();

            ApplyState();

            RefreshLatestImage();

            ShowMessageBox(
                "Asset completed successfully.",
                "Asset Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (AssetProcessingException ape) when (!ape.RollbackComplete)
        {
            // BUG-R4-003: Rollback failed - do NOT erase Main metadata so recovery can detect incomplete state on restart.
            ShowMessageBox(
                "CRITICAL: Main Image processing failed and automatic rollback was incomplete.\n\n" + ape.Message,
                "Critical Main Processing Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
        }
        catch (Exception ex)
        {
            // Reset committing state on failure only if rollback was clean or files were not written
            if (_currentSession is not null)
            {
                _currentSession.ResetMainCommitMetadata();

                try
                {
                    _sessionService.Save(_currentSession);
                }
                catch (Exception saveEx)
                {
                    ShowError(
                        "CRITICAL: Main Image processing failed, but updated session could not be saved.",
                        saveEx);

                    Close();
                    return;
                }
            }

            ShowError(
                "Main Image processing failed.",
                ex);

            ApplyState();
        }
    }

    private void HandleCancel()
    {
        if (_currentSession is null)
        {
            return;
        }

        var sessionValidation =
            _validationService
                .ValidateSession(
                    _currentSession);

        if (!sessionValidation.IsValid)
        {
            ShowValidationError(
                "Current session is inconsistent. No asset files were deleted.",
                sessionValidation);

            return;
        }

        // BUG-R13-003: Check reference output integrity before asking for cancel confirmation
        var refValidation =
            _validationService
                .ValidateReferenceOutput(
                    _currentSession);

        if (!refValidation.IsValid)
        {
            ShowValidationError(
                "Current reference artifacts are inconsistent or modified. No asset files were deleted.",
                refValidation);

            return;
        }

        var confirmed =
            TwoChoiceDialog.ShowChoice(
                this,
                "Cancel current asset",
                "Cancel current asset?\n\nThe reference files created during this session will be removed.",
                "Cancel Asset",
                "Keep Working");

        if (!confirmed)
        {
            return;
        }

        try
        {
            _sessionService.Cancel(
                _currentSession);

            AddStatus(
                "Current asset session cancelled.");

            _currentSession =
                null;

            _state =
                UiState.Idle;

            txtPrompt.Clear();
            txtAssetFolderName.Clear();

            lblReference.Text =
                "No reference selected.";

            ClearManualSelection();

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not cancel current asset safely.",
                ex);
        }
    }

    private void RecoverSessionOnStartup()
    {
        if (!_sessionService.Exists())
        {
            return;
        }

        AssetSession? session;

        try
        {
            session =
                _sessionService.Load();
        }
        catch (Exception ex)
        {
            var deleteRecord =
                TwoChoiceDialog.ShowChoice(
                    this,
                    "Broken session file",
                    "The existing session.json could not be parsed.\n\n"
                    + ex.Message
                    + "\n\n"
                    + "Because its asset paths cannot be trusted, no asset files will be deleted automatically.\n\n"
                    + "Delete only the broken session record?",
                    "Delete Session Record",
                    "Exit");

            if (deleteRecord)
            {
                try
                {
                    _sessionService.Delete();

                    AddStatus(
                        "Broken session record deleted. Asset files were left untouched.");
                }
                catch (Exception deleteException)
                {
                    ShowError(
                        "Could not delete broken session record.",
                        deleteException);

                    Close();
                }
            }
            else
            {
                Close();
            }

            return;
        }

        if (session is null)
        {
            return;
        }

        var validation =
            _validationService
                .ValidateSession(
                    session);

        if (!validation.IsValid)
        {
            var deleteRecord =
                TwoChoiceDialog.ShowChoice(
                    this,
                    "Invalid unfinished session",
                    "An unfinished session exists, but it is inconsistent:\n\n"
                    + string.Join(
                        Environment.NewLine,
                        validation.Errors)
                    + "\n\n"
                    + "Because the recorded asset state is no longer trusted, no asset files will be deleted automatically.\n\n"
                    + "Delete only the session record?",
                    "Delete Session Record",
                    "Exit");

            if (!deleteRecord)
            {
                Close();

                return;
            }

            try
            {
                _sessionService.Delete();

                AddStatus(
                    "Invalid session record deleted. Asset files were left untouched.");
            }
            catch (Exception ex)
            {
                ShowError(
                    "Could not delete invalid session record.",
                    ex);

                Close();
            }

            return;
        }

        // BUG-R4-001: If crash occurred while cancellation was in progress, resume cancellation.
        if (session.CancelPhase != CancelPhase.None)
        {
            try
            {
                _sessionService.Cancel(session);
                AddStatus("Interrupted cancellation was resumed and completed successfully.");
            }
            catch (Exception ex)
            {
                ShowError("CRITICAL: Resuming interrupted cancellation failed.", ex);
                Close();
            }

            return;
        }

        // BUG-002 & BUG-R2-006: Strict crash recovery for Main Image commits.
        // Never assume completed solely based on IsMainCommitting and File.Exists(final.md).
        // Check whether the complete asset (Main image + Final Provenance + MainHash) actually exists and is 100% valid.
        if (session.IsMainCommitting && !string.IsNullOrWhiteSpace(session.MainFilename))
        {
            // BUG-R9-003: Check the Reference baseline state first.
            // If the Reference baseline itself is corrupt/tampered (e.g. corrupt reference provenance),
            // do NOT treat this as an incomplete Main commit (which would delete valid Main image/provenance).
            // Treat it as an inconsistent/untrusted session without destructive file operations.
            var refBaselineValidation = _validationService.ValidateReferenceOutput(session);
            if (!refBaselineValidation.IsValid)
            {
                var deleteCorruptSession =
                    TwoChoiceDialog.ShowChoice(
                        this,
                        "Inconsistent asset session",
                        $"An active asset session contains invalid reference data and cannot be safely resumed or rolled back.\n\nAsset:\n{session.AssetFolderName}\n\nValidation errors:\n{string.Join(Environment.NewLine, refBaselineValidation.Errors)}\n\nDelete this session record? (No asset files will be deleted)",
                        "Delete Session Record",
                        "Exit");

                if (deleteCorruptSession)
                {
                    try
                    {
                        _sessionService.Delete();
                        AddStatus($"Corrupt session record for '{session.AssetFolderName}' deleted.");
                        ApplyState();
                    }
                    catch (Exception ex)
                    {
                        ShowError("Could not delete invalid session record.", ex);
                        Close();
                    }
                }
                else
                {
                    Close();
                }

                return;
            }

            var mainImagePath = Path.Combine(session.AssetFolder, session.MainFilename);
            var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            var mainDateStr = (session.MainProcessedAt ?? session.ReferenceProcessedAt)
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            var completeValidation = _validationService.ValidateCompleteAsset(
                session,
                mainImagePath,
                finalProvPath,
                session.MainFilename,
                mainDateStr,
                session.MainPrompt ?? string.Empty,
                session.MainHash);

            if (completeValidation.IsValid)
            {
                // The asset is verified complete and fully intact.
                var deleteRecord =
                    TwoChoiceDialog.ShowChoice(
                        this,
                        "Completed asset session",
                        $"An asset session was interrupted after completion.\n\nAsset:\n{session.AssetFolderName}\n\nThe complete asset (main image and final provenance) is valid and intact.\n\nDelete the leftover session record?",
                        "Delete Session Record",
                        "Exit");

                if (deleteRecord)
                {
                    try
                    {
                        _sessionService.Delete();
                        _lastCompletedAssetFolderPath = session.AssetFolder;
                        AddStatus(
                            $"Leftover session record for completed asset '{session.AssetFolderName}' deleted.");
                        ApplyState();
                    }
                    catch (Exception ex)
                    {
                        ShowError(
                            "Could not delete completed session record.",
                            ex);

                        Close();
                    }
                }
                else
                {
                    Close();
                }

                return;
            }
            else
            {
                // BUG-R2-004 & BUG-R15-001: Main commit was interrupted mid-flight and is incomplete.
                // Clean up any incomplete Main artifacts so reference session remains clean and resumable.
                ValidationResult rollback;
                try
                {
                    rollback = _assetProcessorService.RollbackMain(session, session.MainFilename);
                }
                catch (Exception ex)
                {
                    ShowMessageBox(
                        "CRITICAL: Recovery could not safely evaluate or roll back the incomplete Main commit.\n\n"
                        + ex.Message + "\n\nNo asset files were deleted automatically.",
                        "Critical Recovery Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Recovery found an incomplete Main commit, but automatic rollback failed.\n\n"
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical Recovery Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                session.ResetMainCommitMetadata();

                try
                {
                    _sessionService.Save(session);
                }
                catch (Exception saveEx)
                {
                    ShowError(
                        "CRITICAL: Incomplete Main commit was rolled back, but updated session could not be saved.",
                        saveEx);

                    Close();
                    return;
                }
            }
        }

        var resume =
            TwoChoiceDialog.ShowChoice(
                this,
                "Unfinished asset session",
                $"An unfinished asset session was found.\n\nAsset:\n{session.AssetFolderName}\n\nReference:\n{session.ReferenceFilename}",
                "Resume",
                "Cancel Session");

        if (!resume)
        {
            try
            {
                _sessionService.Cancel(
                    session);

                AddStatus(
                    "Recovered session cancelled.");
            }
            catch (Exception ex)
            {
                ShowError(
                    "Could not cancel recovered session.",
                    ex);

                Close();
            }

            return;
        }

        _currentSession =
            session;

        _state =
            UiState.ReferenceReady;

        txtProject.Text =
            session.ProjectName;

        txtAssetRoot.Text =
            session.AssetRootFolder;

        txtAssetFolderName.Text =
            session.AssetFolderName;

        lblReference.Text =
            session.ReferenceFilename;

        AddStatus(
            $"Session resumed: {session.AssetFolderName}");

        ApplyState();
    }

    private void PasteClipboard()
    {
        // BUG-011: Wrap in try/catch to handle ExternalException if clipboard is locked by another process
        try
        {
            string? text = null;
            if (ClipboardProvider is not null)
            {
                text = ClipboardProvider();
            }
            else
            {
                if (!Clipboard.ContainsText())
                {
                    ShowMessageBox(
                        "Clipboard does not contain text.",
                        "Paste Clipboard",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    return;
                }

                text = Clipboard.GetText();
            }

            if (string.IsNullOrEmpty(text))
            {
                ShowMessageBox(
                    "Clipboard does not contain text.",
                    "Paste Clipboard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            txtPrompt.Text = text;
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not access clipboard.",
                ex);
        }
    }

    private void OpenDownloads()
    {
        var path =
            txtDownloadFolder.Text;

        if (!Directory.Exists(path))
        {
            ShowMessageBox(
                "Download folder does not exist.",
                "Open Downloads",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        OpenFolder(
            path);
    }

    private void OpenAssetFolder()
    {
        string? path =
            _currentSession?.AssetFolder
            ?? _lastCompletedAssetFolderPath;

        if (string.IsNullOrWhiteSpace(path) ||
            !Directory.Exists(path))
        {
            return;
        }

        OpenFolder(
            path);
    }

    private void OpenFolder(
        string path)
    {
        if (OpenFolderProvider is not null)
        {
            OpenFolderProvider(path);
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        path,

                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            ShowError(
                $"Could not open folder '{path}'.",
                ex);
        }
    }

    private void MainForm_KeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (!e.Control)
        {
            return;
        }

        if (e.KeyCode ==
                Keys.R &&
            _state ==
                UiState.Idle)
        {
            e.SuppressKeyPress =
                true;

            HandleReference();

            return;
        }

        if (e.KeyCode ==
                Keys.M &&
            _state ==
                UiState.ReferenceReady)
        {
            e.SuppressKeyPress =
                true;

            HandleMainImage();
        }
    }

    private void ApplyState()
    {
        var referenceReady =
            _state ==
            UiState.ReferenceReady;

        txtProject.Enabled =
            !referenceReady;

        txtAssetRoot.Enabled =
            !referenceReady;

        btnBrowseAssetRoot.Enabled =
            !referenceReady;

        txtAssetFolderName.Enabled =
            !referenceReady;

        txtDownloadFolder.Enabled =
            true;

        btnBrowseDownload.Enabled =
            true;

        btnReference.Enabled =
            _templatesValid;

        btnReference.Text =
            referenceReady
                ? "Replace Reference"
                : "Reference";

        btnMainImage.Enabled =
            referenceReady &&
            _templatesValid;

        btnCancel.Enabled =
            referenceReady;

        var assetFolder =
            _currentSession?.AssetFolder
            ?? _lastCompletedAssetFolderPath;

        btnOpenAssetFolder.Enabled =
            !string.IsNullOrWhiteSpace(assetFolder)
            &&
            Directory.Exists(assetFolder);
    }

    private void AddStatus(
        string message)
    {
        var line =
            $"{DateTime.Now:HH:mm:ss} {message}";

        if (txtStatusHistory.TextLength > 0)
        {
            txtStatusHistory.AppendText(
                Environment.NewLine);
        }

        txtStatusHistory.AppendText(
            line);

        txtStatusHistory.SelectionStart =
            txtStatusHistory.TextLength;

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

    private void ShowMessageBox(
        string message,
        string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None)
    {
        if (MessageBoxProvider is not null)
        {
            MessageBoxProvider(
                this,
                message,
                title,
                buttons,
                icon);

            return;
        }

        MessageBox.Show(
            this,
            message,
            title,
            buttons,
            icon);
    }

    private void ShowValidationError(
        string title,
        ValidationResult validation)
    {
        ShowMessageBox(
            string.Join(
                Environment.NewLine,
                validation.Errors),
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void ShowError(
        string context,
        Exception exception)
    {
        ShowMessageBox(
            context
            + Environment.NewLine
            + Environment.NewLine
            + exception.Message,
            "Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        AddStatus(
            $"{context} {exception.Message}");
    }
}
