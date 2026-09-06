#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private static readonly Color DoneRowBackColor =
        Color.FromArgb(222, 242, 222);

    private void HandleImportRequest()
    {
        if (_isGeneratingDirect || _isSubmittingBatch)
        {
            ShowMessageBox(
                "A generation or batch submission is currently being prepared. "
                + "Wait until the local operation has finished before importing another manifest.",
                "Import blocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string? path = null;

        if (OpenFileDialogProvider is not null)
        {
            path = OpenFileDialogProvider(this, txtDownloadFolder.Text);
            if (path is null)
            {
                return;
            }
        }
        else
        {
            path = PickManifestPathWithDialog();
            if (path is null)
            {
                return;
            }
        }

        var manifestService =
            new AssetRequestManifestService(
                _validationService);

        AssetRequestManifest manifest;

        try
        {
            manifest =
                manifestService.Load(
                    path,
                    _settings.AcceptedExtensions);
        }
        catch (Exception ex)
        {
            // Atomic import: the current queue and all user fields stay untouched.
            ShowMessageBox(
                "Request Manifest could not be imported."
                + Environment.NewLine
                + Environment.NewLine
                + ex.Message
                + Environment.NewLine
                + Environment.NewLine
                + "No Request Queue changes were applied.",
                "Import failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Recovered queue-originated session: the manifest must contain the
        // durable session Request key, otherwise the association would be lost.
        // A manual Reference session must never accept an import either:
        // the queue stays locked until the session is completed or cancelled.
        if (_state == UiState.ReferenceReady)
        {
            if (_currentSession?.SourceRequestKey is null)
            {
                ShowMessageBox(
                    "The active reference-assisted asset is not bound to a Request."
                    + Environment.NewLine
                    + "Finish or cancel it before importing a Request Manifest.",
                    "Import rejected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!manifest.Items.Any(
                    item =>
                        string.Equals(
                            item.RequestKey,
                            _currentSession.SourceRequestKey,
                            StringComparison.Ordinal)))
            {
                ShowMessageBox(
                    "The active recovered asset belongs to a Request that is not present in this manifest."
                    + Environment.NewLine
                    + "Import was cancelled.",
                    "Import rejected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        // Atomic apply: only now replace the current queue.
        _currentManifest = manifest;
        _activeRequest = null;
        _completedRequestKeys.Clear();

        try
        {
            _requestQueueStateService?.Save(manifest);
        }
        catch (Exception ex)
        {
            ShowMessageBox(
                "The Request Manifest was validated, but its restart-safe queue snapshot could not be saved."
                + Environment.NewLine + Environment.NewLine + ex.Message,
                "Queue persistence failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        txtAssetFolderName.Clear();
        txtPrompt.Clear();
        UpdatePromptPreview();
        SetSelectedImage(ImageSlot.Main, null);

        if (_state != UiState.ReferenceReady)
        {
            SetSelectedImage(ImageSlot.Reference, null);
        }

        try
        {
            var restored =
                _requestProgressService?.LoadForManifest(
                    manifest.ManifestFingerprint)
                ?? new HashSet<string>(
                    StringComparer.Ordinal);

            _completedRequestKeys.UnionWith(restored);
        }
        catch
        {
            // Corrupt progress state is handled as empty; import still succeeds.
        }

        foreach (var item in manifest.Items)
        {
            item.IsCompleted =
                _completedRequestKeys.Contains(
                    item.RequestKey);
        }

        lblRequestSource.Text =
            Path.GetFileName(manifest.SourcePath);

        try
        {
            var recoveryService = new LocalCandidateRecoveryService(_generationJobStore, _stagingService);
            recoveryService.RecoverAllForManifest(manifest.ManifestFingerprint);
        }
        catch
        {
            // Best effort candidate recovery on import
        }

        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
        BindRecoveredSessionRequest();
        ApplyRequestQueueState();

        AddStatus(
            $"Request Manifest imported: {Path.GetFileName(manifest.SourcePath)}");
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private string? PickManifestPathWithDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Request Manifest",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (Directory.Exists(txtDownloadFolder.Text))
        {
            dialog.InitialDirectory = txtDownloadFolder.Text;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.FileName;
    }

    private void RefreshRequestQueueVisuals()
    {
        if (IsDisposed || Disposing || lvRequestQueue.IsDisposed)
        {
            return;
        }

        lvRequestQueue.BeginUpdate();

        try
        {
            lvRequestQueue.Items.Clear();

            if (_currentManifest is null)
            {
                return;
            }

            var preloadedJobs = _generationJobStore
                .GetItemsForManifest(_currentManifest.ManifestFingerprint)
                .ToDictionary(j => j.RequestKey, StringComparer.Ordinal);

            var openSeriesIds = _queueSeriesProgressService
                .Summarize(_currentManifest.Items, _completedRequestKeys)
                .Where(series => series.IsOpen)
                .Select(series => series.SeriesId)
                .ToHashSet(StringComparer.Ordinal);
            var showOpenPixelSeriesOnly = cmbRequestQueueFilter.SelectedIndex == 1;

            foreach (var request in _currentManifest.Items)
            {
                if (showOpenPixelSeriesOnly && !IsRequestInOpenCanonicalPixelSeries(request, openSeriesIds))
                {
                    continue;
                }

                preloadedJobs.TryGetValue(request.RequestKey, out var preloadedJob);
                var (statusText, backColor) = GetRequestItemVisualStatus(request, preloadedJob);

                var lvi =
                    new ListViewItem(
                        new[]
                        {
                            statusText,
                            request.AssetName,
                            request.Resolution,
                            request.IsCompleted || _completedRequestKeys.Contains(request.RequestKey) ? "×" : string.Empty
                        })
                    {
                        Tag = request
                    };

                if (backColor != Color.White)
                {
                    lvi.BackColor = backColor;
                }

                if (_activeRequest is not null
                    && string.Equals(
                        _activeRequest.RequestKey,
                        request.RequestKey,
                        StringComparison.Ordinal))
                {
                    lvi.Font = GetQueueBoldFont();
                }

                lvRequestQueue.Items.Add(lvi);
            }
        }
        finally
        {
            lvRequestQueue.EndUpdate();
        }
    }

    private void HandleRequestQueueFilterChanged()
    {
        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
    }

    private bool IsRequestInOpenCanonicalPixelSeries(AssetRequestItem request, ISet<string> openSeriesIds)
    {
        var workflow = _queuePromptWorkflowParser.Parse(request.Prompt);
        return workflow.IsPixelExact
            && workflow.HasCanonicalMetadata
            && workflow.SeriesId is not null
            && openSeriesIds.Contains(workflow.SeriesId);
    }

    private Font? _queueBoldFont;

    private Font GetQueueBoldFont()
    {
        if (_queueBoldFont is null || Math.Abs(_queueBoldFont.SizeInPoints - lvRequestQueue.Font.SizeInPoints) > 0.001f)
        {
            _queueBoldFont?.Dispose();
            _queueBoldFont = new Font(lvRequestQueue.Font, FontStyle.Bold);
        }
        return _queueBoldFont;
    }

    private void HandleRequestQueueMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit =
            lvRequestQueue.HitTest(
                e.Location);

        if (hit.Item is null)
        {
            return;
        }

        // The compact action column is intentionally handled before the normal
        // row activation. It is the only destructive queue gesture and always
        // obtains a second confirmation below.
        if (hit.SubItem is not null
            && hit.Item.SubItems.Count > 3
            && ReferenceEquals(hit.SubItem, hit.Item.SubItems[3]))
        {
            HandleCompletedRequestReset(hit.Item);
            return;
        }

        HandleRequestQueueItemActivate(hit.Item);
    }

    private void HandleCompletedRequestReset(ListViewItem lvi)
    {
        if (lvi.Tag is not AssetRequestItem item
            || _currentManifest is null
            || (!item.IsCompleted && !_completedRequestKeys.Contains(item.RequestKey))
            || _state == UiState.ReferenceReady
            || _isGeneratingDirect
            || _isSubmittingBatch)
        {
            return;
        }

        var confirmation = ShowConfirmDialog(
            $"Delete the completed asset folder for '{item.AssetName}' and reset this queue row to Pending?\n\nOnly the direct child folder under the configured Asset Root Folder will be removed. This cannot be undone.",
            "Delete completed queue asset",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes && confirmation != DialogResult.OK)
        {
            return;
        }

        var settings = ReadSettingsFromUi();
        string assetFolder;
        try
        {
            assetFolder = GetSafeDirectAssetFolder(settings.AssetRootFolder, item.AssetName);
            if (!Directory.Exists(assetFolder))
            {
                throw new DirectoryNotFoundException($"Completed asset folder was not found: {assetFolder}");
            }
            if (ValidationService.IsReparsePoint(assetFolder) || ContainsReparsePoint(assetFolder))
            {
                throw new IOException("The completed asset folder contains a reparse point and cannot be deleted safely.");
            }

            RemoveCollectCopiesForAsset(assetFolder, item.AssetName);
            Directory.Delete(assetFolder, recursive: true);
        }
        catch (Exception ex)
        {
            ShowError("Completed queue asset was not deleted; the queue row was left unchanged.", ex);
            return;
        }

        try
        {
            var next = new HashSet<string>(_completedRequestKeys, StringComparer.Ordinal);
            next.Remove(item.RequestKey);
            _requestProgressService?.Save(_currentManifest.ManifestFingerprint, next);
            _completedRequestKeys.Clear();
            _completedRequestKeys.UnionWith(next);
            item.IsCompleted = false;
            ResetPixelExactJournalForDeletedRequest(item);
            RefreshRequestQueueVisuals();
            UpdateRequestProgressLabel();
            AddStatus($"Completed queue asset deleted and reset: {item.AssetName}");
        }
        catch (Exception ex)
        {
            // The asset is intentionally already gone. Surface the durable queue
            // bookkeeping failure so a re-import/reconciliation can be chosen.
            ShowError("Asset folder was deleted, but its queue reset could not be saved.", ex);
        }
    }

    private static string GetSafeDirectAssetFolder(string assetRoot, string assetName)
    {
        var normalizedRoot = ValidationService.NormalizePath(assetRoot);
        if (!Directory.Exists(normalizedRoot) || ValidationService.IsReparsePoint(normalizedRoot))
        {
            throw new IOException("Asset Root Folder is unavailable or is a reparse point.");
        }

        var target = ValidationService.NormalizePath(Path.Combine(normalizedRoot, assetName));
        if (!ValidationService.PathsEqual(Path.GetDirectoryName(target) ?? string.Empty, normalizedRoot))
        {
            throw new InvalidDataException("Queue asset folder is not a direct child of Asset Root Folder.");
        }
        return target;
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (ValidationService.IsReparsePoint(path))
                {
                    return true;
                }
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                }
            }
        }
        return false;
    }

    private void RemoveCollectCopiesForAsset(string assetFolder, string assetName)
    {
        // Copies may have been collected before the user later disabled the
        // option; use the remembered folder so reset removes only its own
        // deterministic copies as well.
        if (string.IsNullOrWhiteSpace(_settings.CollectFolder))
        {
            return;
        }

        var collectFolder = ValidationService.NormalizePath(_settings.CollectFolder);
        if (!Directory.Exists(collectFolder))
        {
            return;
        }
        if (ValidationService.IsReparsePoint(collectFolder))
        {
            throw new IOException("Collect folder is a reparse point and cannot be modified safely.");
        }

        foreach (var source in Directory.EnumerateFiles(assetFolder, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => _settings.AcceptedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            var copy = GetCollectDestinationPath(collectFolder, assetName, source);
            if (File.Exists(copy))
            {
                if (ValidationService.IsReparsePoint(copy))
                {
                    throw new IOException("Collect copy is a reparse point and cannot be removed safely.");
                }
                File.Delete(copy);
            }
        }
    }

    internal void HandleRequestQueueItemActivate(
        ListViewItem? lvi)
    {
        if (lvi?.Tag is not AssetRequestItem item)
        {
            return;
        }

        if ((item.IsCompleted
                || _completedRequestKeys.Contains(item.RequestKey))
            && !IsResettablePixelExactCollectionRequest(item))
        {
            // Done rows may be selected visually but never reactivated.
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            var sessionKey =
                _currentSession?.SourceRequestKey;

            if (sessionKey is null
                || !string.Equals(
                    sessionKey,
                    item.RequestKey,
                    StringComparison.Ordinal))
            {
                ShowMessageBox(
                    "Finish or cancel the current reference-assisted asset before selecting another Request.",
                    "Request selection blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        _activeRequest = item;
        var hadActiveStagedCandidate = _activeApiCandidateMetadata is not null;
        _activeApiCandidateMetadata = null;

        _settingRequestBoundFields = true;

        try
        {
            txtAssetFolderName.Text =
                item.AssetName;

            txtPrompt.Text =
                item.Prompt;

        }
        finally
        {
            _settingRequestBoundFields = false;
        }

        UpdatePromptPreview();
        ApplyQueueWorkflowAutodetection(item);
        TryCopyPromptToClipboard(item.Prompt);

        if (_currentManifest != null)
        {
            var job = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, item.RequestKey);
            if (job?.Status == Core.Generation.GenerationItemStatus.Ready)
            {
                var verifier = new CandidateVerificationService(_stagingService);
                var verification = verifier.VerifyCandidate(job, item.Width, item.Height);

                if (verification.IsValid && verification.Candidate != null)
                {
                    ResetVariantSelectionToNone();
                    _activeApiCandidateMetadata = verification.Candidate.Metadata;
                    SelectProviderByFileName("OpenAI API.md");
                    SetSelectedImage(ImageSlot.Main, verification.Candidate.ImagePath);
                    AddStatus($"Staged candidate loaded for '{item.AssetName}'. Review and commit when ready.");
                }
                else
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
                        ErrorMessage = verification.ErrorMessage ?? "Candidate verification failed.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    _generationJobStore.UpsertItem(updatedJob);

                    if (hasRecoverableRaw)
                    {
                        var recoveryService = new LocalCandidateRecoveryService(_generationJobStore, _stagingService);
                        if (recoveryService.TryRecoverCandidate(updatedJob))
                        {
                            var refreshedJob = _generationJobStore.GetItem(job.ManifestFingerprint, job.RequestKey);
                            if (refreshedJob?.Status == Core.Generation.GenerationItemStatus.Ready)
                            {
                                var reverified = verifier.VerifyCandidate(refreshedJob, item.Width, item.Height);
                                if (reverified.IsValid && reverified.Candidate != null)
                                {
                                    ResetVariantSelectionToNone();
                                    _activeApiCandidateMetadata = reverified.Candidate.Metadata;
                                    SelectProviderByFileName("OpenAI API.md");
                                    SetSelectedImage(ImageSlot.Main, reverified.Candidate.ImagePath);
                                    AddStatus($"Staged candidate for '{item.AssetName}' was automatically rebuilt and loaded into Main.");
                                    RefreshRequestQueueVisuals();
                                    return;
                                }
                            }
                        }
                    }

                    ShowMessageBox(
                        $"Staged candidate for '{item.AssetName}' failed verification:" + Environment.NewLine + Environment.NewLine +
                        $"{verification.ErrorMessage}" + Environment.NewLine + Environment.NewLine +
                        "The candidate was not loaded into Main.",
                        "Candidate Verification Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            else if (hadActiveStagedCandidate)
            {
                SetSelectedImage(ImageSlot.Main, null);
            }
        }
        else if (hadActiveStagedCandidate)
        {
            SetSelectedImage(ImageSlot.Main, null);
        }

        RefreshRequestQueueVisuals();
    }

    private void CheckActiveRequestBinding()
    {
        if (_settingRequestBoundFields
            || _activeRequest is null)
        {
            return;
        }

        var stillMatches =
            string.Equals(
                txtAssetFolderName.Text.Trim(),
                _activeRequest.AssetName,
                StringComparison.Ordinal)
            && string.Equals(
                txtPrompt.Text,
                _activeRequest.Prompt,
                StringComparison.Ordinal);

        if (stillMatches)
        {
            return;
        }

        var hadApiCandidate = _activeApiCandidateMetadata is not null;
        _activeRequest = null;
        _activeApiCandidateMetadata = null;

        if (hadApiCandidate)
        {
            SetSelectedImage(ImageSlot.Main, null);
            AddStatus("Active API candidate unloaded because asset name or prompt was modified.");
        }

        RefreshRequestQueueVisuals();
    }

    private void TryCopyPromptToClipboard(
        string prompt)
    {
        try
        {
            if (ClipboardWriter is not null)
            {
                ClipboardWriter(prompt);
                return;
            }

            Clipboard.SetText(prompt);
        }
        catch (Exception)
        {
            ShowMessageBox(
                "The Request was loaded successfully, but its Prompt could not be copied to the clipboard."
                + Environment.NewLine
                + Environment.NewLine
                + "You can still use the Prompt shown in Final Prompt.",
                "Clipboard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ApplyRequestQueueState()
    {
        if (IsDisposed || Disposing || btnImportRequest.IsDisposed)
        {
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            var sessionKey =
                _currentSession?.SourceRequestKey;

            if (sessionKey is null)
            {
                // Manual Reference session: import blocked until done/cancelled.
                btnImportRequest.Enabled = false;
            }
            else if (_currentManifest is not null)
            {
                // Same-run queue already loaded.
                btnImportRequest.Enabled = false;
            }
            else
            {
                // Recovered queue-originated session without a loaded manifest:
                // import is allowed so the queue association can be restored.
                btnImportRequest.Enabled = true;
            }

            btnGenerateNow.Enabled = false;
            btnQueueProductionBatch.Enabled = false;
            btnRetrySelectedApi.Enabled = false;
            _toolTip.SetToolTip(btnGenerateNow, "Automated Reference-Assisted Generation is not supported in this version. Finish or cancel current reference asset first.");
            _toolTip.SetToolTip(btnQueueProductionBatch, "Automated Reference-Assisted Generation is not supported in this version. Finish or cancel current reference asset first.");
        }
        var apiMutationActive =
            _isGeneratingDirect || _isSubmittingBatch;

        btnClearRequestQueue.Enabled = !apiMutationActive
            && _state != UiState.ReferenceReady
            && (_currentManifest is not null || _requestQueueStateService?.HasPersistedState == true);

        if (_state != UiState.ReferenceReady)
        {
            btnImportRequest.Enabled = !apiMutationActive;

            var hasApiKey = HasOpenAiApiKeyConfigured();
            var unsupportedWorkflowReason = string.Empty;
            var hasUnsupportedMultiOutputWorkflow = _currentManifest is not null
                && ManifestContainsUnsupportedAutomatedMultiOutputWorkflow(_currentManifest, out unsupportedWorkflowReason);
            var canRunApi =
                _currentManifest is not null
                && !apiMutationActive
                && hasApiKey
                && !hasUnsupportedMultiOutputWorkflow;

            btnGenerateNow.Enabled = canRunApi;
            btnQueueProductionBatch.Enabled = canRunApi;

            if (_currentManifest is not null && hasUnsupportedMultiOutputWorkflow)
            {
                _toolTip.SetToolTip(btnGenerateNow, unsupportedWorkflowReason);
                _toolTip.SetToolTip(btnQueueProductionBatch, unsupportedWorkflowReason);
            }
            else if (_currentManifest is not null && !hasApiKey)
            {
                var noKeyTooltip = "Configure an OpenAI API key in Settings first.";
                _toolTip.SetToolTip(btnGenerateNow, noKeyTooltip);
                _toolTip.SetToolTip(btnQueueProductionBatch, noKeyTooltip);
            }
            else
            {
                _toolTip.SetToolTip(btnGenerateNow, null);
                _toolTip.SetToolTip(btnQueueProductionBatch, null);
            }

            var selectedItem = lvRequestQueue.SelectedItems.Count > 0 ? lvRequestQueue.SelectedItems[0].Tag as AssetRequestItem : null;
            GenerationItemRecord? selectedJob = null;
            if (_currentManifest is not null && selectedItem is not null)
            {
                selectedJob = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, selectedItem.RequestKey);
            }

            var canRetrySelected = _currentManifest is not null
                && selectedJob is not null
                && selectedJob.Status == GenerationItemStatus.UncertainAfterInterruption
                && !apiMutationActive;

            btnRetrySelectedApi.Enabled = canRetrySelected;
        }
    }

    private void HandleRetrySelectedApi()
    {
        if (_currentManifest is null) return;
        if (_isGeneratingDirect || _isSubmittingBatch) return;
        if (lvRequestQueue.SelectedItems.Count == 0) return;

        var selectedItem = lvRequestQueue.SelectedItems[0].Tag as AssetRequestItem;
        if (selectedItem is null) return;

        var job = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, selectedItem.RequestKey);
        if (job is null || job.Status != GenerationItemStatus.UncertainAfterInterruption) return;

        if (job.Mode == GenerationMode.Batch)
        {
            if (!string.IsNullOrWhiteSpace(job.ProviderBatchId))
            {
                ShowMessageBox(
                    $"This request belongs to a remote Batch that was submitted to OpenAI (Batch ID: {job.ProviderBatchId}). Resetting this request to retry is not permitted while the remote Batch exists. Please monitor the existing batch instead.",
                    "Retry Not Permitted",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var confirmBatch = ShowConfirmDialog(
                $"A remote Batch may already exist on OpenAI. Before retrying, check the OpenAI dashboard for the recorded input file or custom ID '{job.CustomId}'. Continue only if you verified that retrying is safe and you accept the risk of duplicate charges.{Environment.NewLine}{Environment.NewLine}Do you want to resolve and reset this item to Pending?",
                "Confirm Batch Item Retry / Resolve",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirmBatch != DialogResult.OK)
            {
                return;
            }
        }
        else
        {
            var confirmDirect = ShowConfirmDialog(
                $"OpenAI may already have processed and billed this request.{Environment.NewLine}{Environment.NewLine}Retrying may create a second image and a second charge.{Environment.NewLine}{Environment.NewLine}Only continue if you understand this risk.{Environment.NewLine}{Environment.NewLine}Do you want to reset this item to Pending so it can be generated again?",
                "Confirm Direct Generation Retry",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirmDirect != DialogResult.OK)
            {
                return;
            }
        }

        job = job with
        {
            Status = GenerationItemStatus.Pending,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        _generationJobStore.UpsertItem(job);
        AddStatus($"Reset request '{selectedItem.RequestKey}' to Pending.");
        ApplyRequestQueueState();
        RefreshRequestQueueVisuals();
    }

    private bool HasOpenAiApiKeyConfigured()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(_secretStore.LoadSecret(Dialogs.SettingsDialog.OpenAiApiKeySecretName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Marks the matching Request Done only after the Main durable commit.
    /// Progress persistence is updated only while the manifest is loaded.
    /// </summary>
    private bool CompleteActiveRequestAfterMainCommit(
        AssetSession session)
    {
        var completedRequestKey =
            _activeRequest?.RequestKey
            ?? session.SourceRequestKey;

        _activeRequest = null;
        _activeApiCandidateMetadata = null;

        if (string.IsNullOrWhiteSpace(completedRequestKey)
            || _currentManifest is null)
        {
            return true;
        }

        var existingJob =
            _generationJobStore.GetItem(
                _currentManifest.ManifestFingerprint,
                completedRequestKey);

        if (existingJob is not null)
        {
            _generationJobStore.UpsertItem(
                existingJob with
                {
                    Status =
                        Core.Generation.GenerationItemStatus.Committed,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                });
        }

        var item =
            _currentManifest.Items.FirstOrDefault(
                request =>
                    string.Equals(
                        request.RequestKey,
                        completedRequestKey,
                        StringComparison.Ordinal));

        if (item is null)
        {
            return true;
        }

        var progressSaved = true;
        try
        {
            var next = new HashSet<string>(_completedRequestKeys, StringComparer.Ordinal)
            {
                completedRequestKey
            };
            _requestProgressService?.Save(
                _currentManifest.ManifestFingerprint,
                next);
            _completedRequestKeys.Clear();
            _completedRequestKeys.UnionWith(next);
        }
        catch (Exception ex)
        {
            // The Main asset is already durable and the established queue UX
            // reports it as Done. Pixel-Exact callers use this return value to
            // withhold their stricter batch-journal transition until a later
            // reconciliation can persist the completion.
            progressSaved = false;
            AddStatus($"Asset committed, but queue completion could not be saved: {ex.Message}");
        }

        item.IsCompleted = true;
        _completedRequestKeys.Add(completedRequestKey);

        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
        return progressSaved;
    }

    private void HandleRequestCancellation()
    {
        _activeRequest = null;
        _activeApiCandidateMetadata = null;
        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
    }

    private void RestoreRequestQueueOnStartup()
    {
        if (_requestQueueStateService is null)
        {
            return;
        }

        try
        {
            var manifest = _requestQueueStateService.Load(_settings.AcceptedExtensions);
            if (manifest is null)
            {
                return;
            }

            _currentManifest = manifest;
            var restored = _requestProgressService?.LoadForManifest(manifest.ManifestFingerprint)
                ?? new HashSet<string>(StringComparer.Ordinal);
            _completedRequestKeys.UnionWith(restored);
            foreach (var item in manifest.Items)
            {
                item.IsCompleted = _completedRequestKeys.Contains(item.RequestKey);
            }

            lblRequestSource.Text = Path.GetFileName(manifest.SourcePath) + " (restored)";
            RefreshRequestQueueVisuals();
            UpdateRequestProgressLabel();
            AddStatus("Request Queue restored from local state.");
        }
        catch (Exception ex)
        {
            AddStatus($"Saved Request Queue could not be restored: {ex.Message}");
        }
    }

    private void HandleClearRequestQueue()
    {
        if (_isGeneratingDirect || _isSubmittingBatch || _state == UiState.ReferenceReady)
        {
            return;
        }

        try
        {
            var pixelState = _pixelExactBatchStateService.Load();
            if (pixelState is not null && !pixelState.Completed)
            {
                var confirmation = ShowConfirmDialog(
                    "A Pixel-Exact collection is still pending. Clearing the queue will discard its staged download receipt, so it cannot be resumed safely.\n\nClear the queue and discard that pending collection?",
                    "Discard pending Pixel-Exact collection",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirmation != DialogResult.Yes && confirmation != DialogResult.OK)
                {
                    return;
                }
                _pixelExactBatchStateService.DiscardPendingState();
            }
            else if (pixelState is not null)
            {
                _pixelExactBatchStateService.ClearCompletedState();
            }
        }
        catch (Exception ex)
        {
            ShowError("Queue was not cleared because its Pixel-Exact state could not be handled safely.", ex);
            return;
        }

        var clearedManifestFingerprint = _currentManifest?.ManifestFingerprint;
        _requestQueueStateService?.Clear();
        if (!string.IsNullOrWhiteSpace(clearedManifestFingerprint))
        {
            _requestProgressService?.ClearForManifest(clearedManifestFingerprint);
        }
        _currentManifest = null;
        _activeRequest = null;
        _activeApiCandidateMetadata = null;
        _completedRequestKeys.Clear();
        cmbRequestQueueFilter.SelectedIndex = 0;
        SetSelectedImage(ImageSlot.Main, null);
        lblRequestSource.Text = "No Request Manifest imported.";
        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
        ApplyRequestQueueState();
        AddStatus("Request Queue cleared.");
    }

    private void UpdateRequestProgressLabel()
    {
        if (IsDisposed || Disposing || lblRequestProgress.IsDisposed)
        {
            return;
        }

        if (_currentManifest is null)
        {
            lblRequestProgress.Text = string.Empty;
            lblPixelSeriesProgress.Text = string.Empty;
            return;
        }

        lblRequestProgress.Text = $"{_completedRequestKeys.Count} of {_currentManifest.Items.Count} done";

        var series = _queueSeriesProgressService.Summarize(_currentManifest.Items, _completedRequestKeys);
        if (series.Count == 0)
        {
            lblPixelSeriesProgress.Text = "Pixel series: none detected";
            return;
        }

        var open = series.Where(item => item.IsOpen).ToList();
        var active = _activeRequest is null
            ? null
            : series.FirstOrDefault(item => string.Equals(item.SeriesId, _queuePromptWorkflowParser.Parse(_activeRequest.Prompt).SeriesId, StringComparison.Ordinal));
        var activeText = active is null
            ? string.Empty
            : $"Current series: {active.CompletedPhases}/{active.TotalPhases} complete • ";
        var filterText = cmbRequestQueueFilter.SelectedIndex == 1 ? " • filter: open series" : string.Empty;
        lblPixelSeriesProgress.Text = $"{activeText}Pixel series: {series.Count - open.Count}/{series.Count} complete, {open.Count} open{filterText}";
    }

    /// <summary>
    /// Rebinds a recovered queue-originated Reference session when the manifest
    /// is imported (or re-imported) and contains the durable Request key.
    /// </summary>
    private void BindRecoveredSessionRequest()
    {
        if (_state != UiState.ReferenceReady
            || _currentSession?.SourceRequestKey is null
            || _currentManifest is null)
        {
            return;
        }

        var item =
            _currentManifest.Items.FirstOrDefault(
                request =>
                    string.Equals(
                        request.RequestKey,
                        _currentSession.SourceRequestKey,
                        StringComparison.Ordinal));

        if (item is null
            || item.IsCompleted
            || _completedRequestKeys.Contains(
                item.RequestKey))
        {
            return;
        }

        _activeRequest = item;

        _settingRequestBoundFields = true;

        try
        {
            txtAssetFolderName.Text =
                _currentSession.AssetFolderName;

            txtPrompt.Text = item.Prompt;
        }
        finally
        {
            _settingRequestBoundFields = false;
        }

        UpdatePromptPreview();
        RefreshRequestQueueVisuals();
    }

    private static bool HasRecoverableRawAuthority(GenerationItemRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.CandidateId)
            || string.IsNullOrWhiteSpace(job.ProviderRawPath)
            || string.IsNullOrWhiteSpace(job.RawSha256)
            || !File.Exists(job.ProviderRawPath))
        {
            return false;
        }

        try
        {
            var actual = CandidateVerificationService.ComputeSha256File(job.ProviderRawPath);
            return string.Equals(actual, job.RawSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
