#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void HandleMainImage()
    {
        var hasApiCandidate = _activeApiCandidateMetadata is not null;
        var variantCount = GetSelectedVariantCount();

        if (hasApiCandidate && variantCount > 0)
        {
            ShowMessageBox(
                "A staged API Candidate is active. Variants applies to the legacy download-folder workflow and cannot be combined with this API Candidate. Set Variants to 'none' or unload the API Candidate first.",
                "Variants unavailable for API Candidate",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (variantCount > 0)
        {
            HandleVariantBatch(variantCount);
            return;
        }

        if (!ValidateMainActionUi())
        {
            return;
        }

        if (_activeApiCandidateMetadata != null && _activeRequest != null && _currentManifest != null)
        {
            var job = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, _activeRequest.RequestKey);
            if (job == null)
            {
                _activeApiCandidateMetadata = null;
                SetSelectedImage(ImageSlot.Main, null);
                ShowMessageBox("Candidate job record could not be found. Commit cancelled.", "Commit blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var verifier = new CandidateVerificationService(_stagingService);
            var verification = verifier.VerifyCandidate(job, _activeRequest.Width, _activeRequest.Height);
            if (!verification.IsValid)
            {
                _activeApiCandidateMetadata = null;
                SetSelectedImage(ImageSlot.Main, null);

                var hasRecoverableRaw = HasRecoverableRawAuthority(job);
                var updatedJob = job with
                {
                    Status = hasRecoverableRaw
                        ? Core.Generation.GenerationItemStatus.FailedRetryable
                        : Core.Generation.GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode = hasRecoverableRaw
                        ? "local_candidate_processing_failed"
                        : "candidate_verification_failed_no_raw_authority",
                    ErrorMessage = verification.ErrorMessage ?? "Candidate verification failed before commit.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                _generationJobStore.UpsertItem(updatedJob);

                if (hasRecoverableRaw)
                {
                    new LocalCandidateRecoveryService(_generationJobStore, _stagingService).TryRecoverCandidate(updatedJob);
                }

                RefreshRequestQueueVisuals();
                ShowMessageBox(
                    $"Candidate verification failed before commit:" + Environment.NewLine + Environment.NewLine +
                    $"{verification.ErrorMessage}" + Environment.NewLine + Environment.NewLine +
                    "The commit was cancelled and the candidate was unloaded.",
                    "Commit blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        var isNoReference = chkNoReference.Checked || (_currentSession?.WorkflowMode == AssetWorkflowMode.NoReference);
        var sourceImage = GetSelectedImage(ImageSlot.Main)!;
        var prompt = txtPrompt.Text;
        var processedAt = DateTimeOffset.Now;
        var settings = ReadSettingsFromUi();

        if (isNoReference && _currentSession is null)
        {
            if (!CanStartNewAssetWithProvider)
            {
                ShowMessageBox(
                    "No valid AI Generation Provider template is available.\n\n"
                    + "Add a valid template to the provider_templates folder and restart the application.",
                    "Provider required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            HandleNoReferenceMainImage(settings, sourceImage, prompt, processedAt);
        }
        else if (_currentSession is not null)
        {
            HandleReferenceAssistedMainImage(settings, sourceImage, prompt, processedAt);
        }
        else
        {
            ShowMessageBox(
                "No active reference session exists.",
                "Main Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void HandleNoReferenceMainImage(
        AppSettings settings,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt)
    {
        var assetName = txtAssetFolderName.Text.Trim();
        var targetAssetFolder = Path.Combine(settings.AssetRootFolder, assetName);

        if (Directory.Exists(targetAssetFolder))
        {
            var useExisting = TwoChoiceDialog.ShowChoice(
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

        CommitNoReferenceAsset(settings, assetName, sourceImage, prompt, processedAt, suppressUiCompletion: false);
    }

    private bool CommitNoReferenceAsset(
        AppSettings settings,
        string assetName,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt,
        bool suppressUiCompletion)
    {
        AssetSession session;
        try
        {
            var providerSnapshot = _activeApiCandidateMetadata is not null
                ? GetOpenAiApiProviderSnapshot()
                : GetProviderSnapshotForNewAsset();

            session = _assetProcessorService.CreateNoReferenceMainSession(
                settings,
                assetName,
                sourceImage,
                prompt,
                processedAt,
                providerSnapshot,
                _activeRequest?.RequestKey);

            if (_activeApiCandidateMetadata is not null)
            {
                PopulateApiCandidateMetadataIntoSession(session);

                var recomputedProvenance = _templateService.RenderFinalForSession(
                    session,
                    session.MainFilename!,
                    prompt,
                    processedAt);

                session.MainProvenanceHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        new System.Text.UTF8Encoding(false).GetBytes(recomputedProvenance)))
                    .ToLowerInvariant();
            }

            _sessionService.Save(session);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not initialize and save no-reference asset session.",
                ex);
            return false;
        }

        try
        {
            OnNoReferenceJournalSavedBeforeStatusHook?.Invoke();
            AddStatus("No-reference Main session saved.");
        }
        catch
        {
            // Best-effort UI update; proceed with commit
        }

        return ExecuteMainCommit(session, sourceImage, prompt, processedAt, suppressUiCompletion);
    }

    private void HandleReferenceAssistedMainImage(
        AppSettings settings,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt)
    {
        var session = _currentSession!;

        var sessionValidation = _validationService.ValidateSession(session);
        if (!sessionValidation.IsValid)
        {
            ShowValidationError("Invalid reference session", sessionValidation);
            return;
        }

        var exactValidation = _validationService.ValidateExactReferenceOutput(session, _templateService);
        if (!exactValidation.IsValid)
        {
            ShowValidationError(
                "Reference provenance is inconsistent or modified",
                exactValidation);
            return;
        }

        var destinationCheck = _validationService.ValidateMainDestinationAvailability(
            session,
            settings.AcceptedExtensions,
            sourceImage);

        if (!destinationCheck.IsValid)
        {
            ShowValidationError(
                "Main image destination is unavailable",
                destinationCheck);
            return;
        }

        try
        {
            PopulateApiCandidateMetadataIntoSession(session);

            _assetProcessorService.PrepareMainCommit(
                session,
                settings.AcceptedExtensions,
                sourceImage,
                prompt,
                processedAt);
        }
        catch (Exception prepEx)
        {
            ShowError("Could not prepare Main image commit.", prepEx);
            return;
        }

        try
        {
            _sessionService.Save(session);
        }
        catch (Exception saveEx)
        {
            session.ResetMainCommitMetadata();
            ShowError(
                "Could not update session state before Main Image processing. Operation was aborted.",
                saveEx);
            return;
        }

        ExecuteMainCommit(session, sourceImage, prompt, processedAt);
    }

    private bool ExecuteMainCommit(
        AssetSession session,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt,
        bool suppressUiCompletion = false)
    {
        if (!TryPersistPixelExactSeedReceiptBeforeMainWrite(session))
        {
            return false;
        }

        string committedFilename;

        try
        {
            committedFilename = _assetProcessorService.ProcessMainImage(
                session,
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
                var rollback = _assetProcessorService.RollbackMain(
                    session,
                    committedFilename);

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Main Image was created, session deletion failed, and automatic rollback was incomplete.\n\n"
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical completion error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return false;
                }

                if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
                {
                    try
                    {
                        // Replaces the still-durable active Main journal with the recovered Reference session.
                        _sessionService.Save(session);

                        _currentSession = session;
                        _state = UiState.ReferenceReady;

                        ApplyState();

                        ShowError(
                            "The asset could not be finalized because session.json could not be removed. "
                            + "Main outputs were rolled back and the Reference session was restored.",
                            deleteException);

                        return false;
                    }
                    catch (Exception saveException)
                    {
                        ShowError(
                            "CRITICAL: Main output rollback succeeded but the recovered Reference session could not be persisted.",
                            saveException);

                        Close();
                        return false;
                    }
                }
                else
                {
                    // NoReference mode: No stable session should remain
                    try
                    {
                        _sessionService.Delete();

                        _currentSession = null;
                        _state = UiState.Idle;
                        ApplyState();
                        return false;
                    }
                    catch (Exception retryDeleteException)
                    {
                        ShowError(
                            "CRITICAL: Main outputs were rolled back, but the NoReference journal could not be deleted.",
                            retryDeleteException);

                        Close();
                        return false;
                    }
                }
            }
        }
        catch (AssetProcessingException ape) when (!ape.RollbackComplete)
        {
            ShowMessageBox(
                "CRITICAL: Main Image processing failed and automatic rollback was incomplete.\n\n" + ape.Message,
                "Critical Main Processing Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
            return false;
        }
        catch (Exception ex)
        {
            var isNoReference = session.WorkflowMode == AssetWorkflowMode.NoReference;
            if (TryReconcileFailedMainCommit(session, isNoReference))
            {
                ShowError("Main Image processing failed.", ex);
            }
            return false;
        }

        // DURABLE COMMIT POINT: Complete outputs exist and active session.json is deleted.
        _committedMainSourcesThisSession.Add(ValidationService.NormalizePath(sourceImage));
        TryCollectCommittedMainImage(session, committedFilename);

        if (!suppressUiCompletion)
        {
            CompleteMainUiAfterDurableCommit(session, committedFilename, processedAt);
        }

        return true;
    }

    /// <summary>
    /// Creates the optional flat visual-review copy only after the normal asset
    /// transaction has committed. A collection failure never rolls back or
    /// compromises a provenance-complete asset.
    /// </summary>
    private void TryCollectCommittedMainImage(AssetSession session, string committedFilename)
    {
        if (!_settings.CollectEnabled || string.IsNullOrWhiteSpace(_settings.CollectFolder))
        {
            return;
        }

        try
        {
            var source = Path.Combine(session.AssetFolder, committedFilename);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Committed Main image is unavailable for collection.", source);
            }

            var collectFolder = ValidationService.NormalizePath(_settings.CollectFolder);
            if (Directory.Exists(collectFolder) && ValidationService.IsReparsePoint(collectFolder))
            {
                throw new IOException("Collect folder is a reparse point and cannot be used safely.");
            }

            Directory.CreateDirectory(collectFolder);
            if (ValidationService.IsReparsePoint(collectFolder))
            {
                throw new IOException("Collect folder is a reparse point and cannot be used safely.");
            }

            var destination = GetCollectDestinationPath(collectFolder, session.AssetFolderName, source);
            if (File.Exists(destination))
            {
                var existingHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination)));
                var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
                if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new IOException($"Collect destination already exists with different content: {Path.GetFileName(destination)}");
            }

            var temporary = Path.Combine(collectFolder, ".collect-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }

            AddStatus($"Collected visual copy: {Path.GetFileName(destination)}");
        }
        catch (Exception ex)
        {
            AddStatus($"Asset committed, but its collect copy could not be created: {ex.Message}");
        }
    }

    private static string GetCollectDestinationPath(string collectFolder, string assetName, string source)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var fileName = assetName + "__" + stem + "__" + hash[..12] + extension;
        var destination = ValidationService.NormalizePath(Path.Combine(collectFolder, fileName));
        if (!ValidationService.PathsEqual(Path.GetDirectoryName(destination) ?? string.Empty, collectFolder))
        {
            throw new InvalidDataException("Collect destination escaped the selected folder.");
        }
        return destination;
    }

    private void CompleteMainUiAfterDurableCommit(
        AssetSession session,
        string committedFilename,
        DateTimeOffset processedAt)
    {
        var pixelSeed = CapturePixelExactSeedCompletion(session, committedFilename, processedAt);

        // Capture Request completion before UI fields are cleared.
        var queueProgressSaved = CompleteActiveRequestAfterMainCommit(session);

        if (pixelSeed is not null && queueProgressSaved)
        {
            FinalizePixelExactSeedAfterQueueCompletion(pixelSeed.Value.RequestKey);
        }

        _lastCompletedAssetFolderPath = session.AssetFolder;
        _currentSession = null;
        _state = UiState.Idle;

        try
        {
            ResetAssetInputFieldsAfterDurableAction();

            RecordRecentDocument(
                ProvenanceDocumentKind.Final,
                Path.Combine(
                    session.AssetFolder,
                    AppConstants.FinalProvenanceFileName),
                session.AssetFolderName,
                processedAt);

            if (_currentSession is null
                && _state == UiState.Idle)
            {
                ReloadProviderCatalog();
            }

            AddStatus($"Main image copied: {committedFilename}");
            AddStatus("Ingame copy created.");
            AddStatus("Final provenance created.");
            AddStatus("Asset completed.");

            ApplyState();

            if (pixelSeed is not null)
            {
                TryActivateNextPixelExactCollection(pixelSeed.Value.SeriesId);
            }

            ShowMessageBox(
                "Asset completed successfully.",
                "Asset Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            // Never roll back a committed asset.
            try
            {
                ShowMessageBox(
                    "The asset was completed successfully, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                    "Post-Commit UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
        }
    }

    private bool TryReconcileFailedMainCommit(
        AssetSession session,
        bool noReferenceMode)
    {
        ValidationResult rollback;

        try
        {
            rollback =
                _assetProcessorService.RollbackMain(
                    session,
                    session.MainFilename);
        }
        catch (Exception ex)
        {
            ShowError(
                "CRITICAL: Failed Main transaction could not be safely reconciled.",
                ex);
            Close();
            return false;
        }

        if (!rollback.IsValid)
        {
            var rootMainPath = Path.Combine(session.AssetFolder, session.MainFilename ?? "");
            var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            var ingamePath = session.GetIngameImagePath();
            var tempMain = session.GetMainTempImagePath();
            var tempIngame = session.GetMainTempIngamePath();
            var tempProv = session.GetMainTempProvenancePath();

            var noArtifactsCreated =
                !File.Exists(finalProvPath) &&
                (string.IsNullOrWhiteSpace(ingamePath) || !File.Exists(ingamePath)) &&
                (string.IsNullOrWhiteSpace(tempMain) || !File.Exists(tempMain)) &&
                (string.IsNullOrWhiteSpace(tempIngame) || !File.Exists(tempIngame)) &&
                (string.IsNullOrWhiteSpace(tempProv) || !File.Exists(tempProv));

            if (noArtifactsCreated && File.Exists(rootMainPath))
            {
                // The commit failed before copying because destination already existed with foreign content.
                // We safely preserve the foreign file and reset the aborted transaction state.
                session.ResetMainCommitMetadata();
                rollback = ValidationResult.Success();
            }
            else
            {
                ShowMessageBox(
                    "CRITICAL: Failed Main transaction could not be fully rolled back.\n\n"
                    + string.Join(Environment.NewLine, rollback.Errors),
                    "Critical Main rollback error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
                return false;
            }
        }

        if (!noReferenceMode)
        {
            try
            {
                _sessionService.Save(session);
                _currentSession = session;
                _state = UiState.ReferenceReady;
                ApplyState();
                return true;
            }
            catch (Exception ex)
            {
                ShowError(
                    "CRITICAL: Main rollback succeeded, but the restored Reference session could not be saved.",
                    ex);
                Close();
                return false;
            }
        }

        try
        {
            _sessionService.Delete();
            _currentSession = null;
            _state = UiState.Idle;
            ApplyState();
            return true;
        }
        catch (Exception ex)
        {
            // Do not save the reset in-memory NoReference object. The durable
            // journal still contains the active transaction and is the only
            // reliable recovery authority for the next startup.
            ShowError(
                "CRITICAL: Main outputs were rolled back, but the no-reference session journal could not be removed.",
                ex);
            Close();
            return false;
        }
    }

    private void PasteClipboard()
    {
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
            ShowError("Could not access clipboard.", ex);
        }
    }

    private void OpenDownloads()
    {
        var path = txtDownloadFolder.Text;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            HighlightField(pnlDownloadFolderHost, true);
            txtDownloadFolder.Focus();
            ShowMessageBox(
                "Image download folder does not exist.",
                "Open Image Folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        HighlightField(pnlDownloadFolderHost, false);
        OpenFolder(path);
    }

    private void OpenAssetFolder()
    {
        string? path = _currentSession?.AssetFolder ?? _lastCompletedAssetFolderPath;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        OpenFolder(path);
    }

    private void OpenFolder(string path)
    {
        if (OpenFolderProvider is not null)
        {
            OpenFolderProvider(path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open folder '{path}'.", ex);
        }
    }

    private void PopulateApiCandidateMetadataIntoSession(AssetSession session)
    {
        if (_activeApiCandidateMetadata is null)
        {
            return;
        }

        session.ApiCandidateId = _activeApiCandidateMetadata.CandidateId;
        session.ApiProvider = _activeApiCandidateMetadata.Provider;
        session.ApiModel = _activeApiCandidateMetadata.Model;
        session.ApiMode = _activeApiCandidateMetadata.Mode;
        session.ApiCustomId = _activeApiCandidateMetadata.CustomId;
        session.ApiTargetResolution = _activeApiCandidateMetadata.TargetResolution;
        session.ApiProviderResolution = _activeApiCandidateMetadata.ProviderResolution;
        session.ApiRawSha256 = _activeApiCandidateMetadata.RawSha256;
        session.ApiNormalizedSha256 = _activeApiCandidateMetadata.NormalizedSha256;
        session.ApiProviderRequestId = _activeApiCandidateMetadata.ProviderRequestId;
        session.ApiBatchId = _activeApiCandidateMetadata.BatchId;
        session.ApiCreatedAtUtc = _activeApiCandidateMetadata.CreatedAtUtc.ToString("O");
    }

    private ProviderTemplateSnapshot GetOpenAiApiProviderSnapshot()
    {
        if (_providerTemplateCatalogService is null)
        {
            throw new InvalidOperationException(
                "Provider template catalog is unavailable. "
                + "API Candidate commit cannot continue.");
        }

        var catalog = _providerTemplateCatalogService.Load();
        var definition = catalog.Templates.SingleOrDefault(template =>
            string.Equals(template.FileName, "OpenAI API.md", StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            throw new InvalidOperationException(
                "OpenAI API provider template is missing or invalid. "
                + "API Candidate commit was blocked to prevent incorrect provenance.");
        }

        return definition.CreateSnapshot();
    }
}
