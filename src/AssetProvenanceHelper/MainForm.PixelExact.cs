using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private readonly record struct PixelSeedCompletion(string RequestKey, string SeriesId);
    private readonly record struct PixelExactTarget(int OutputIndex, AssetRequestItem Request);
    internal sealed record PixelExactPhasePreview(
        int OutputIndex,
        int OutputCount,
        string SourceFileName,
        string TargetAssetName,
        string Resolution);
    /// <summary>0 means no collection on this row; otherwise 1..MaxPixelExactOutputCount.</summary>
    private int GetSelectedPixelExactOutputCount() => Math.Max(0, cmbPixelExactCount.SelectedIndex);

    private void SetPixelExactOutputCount(int count)
    {
        if (count is < 0 or > AppConstants.MaxPixelExactOutputCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        cmbPixelExactCount.SelectedIndex = count;
    }

    private void OnPixelExactChanged()
    {
        if (_settingWorkflowSelectors)
        {
            return;
        }

        if (chkPixelExact.Checked)
        {
            var previous = _settingWorkflowSelectors;
            _settingWorkflowSelectors = true;
            try
            {
                ResetVariantSelectionToNone();
                SetDirectModeCheckedProgrammatically(false);
            }
            finally
            {
                _settingWorkflowSelectors = previous;
            }
        }
        ApplyState();
    }

    private void SetNoReferenceCheckedProgrammatically(bool value)
    {
        var previous = _settingWorkflowSelectors;
        _settingWorkflowSelectors = true;
        try { chkNoReference.Checked = value; }
        finally { _settingWorkflowSelectors = previous; }
    }

    private void SetDirectModeCheckedProgrammatically(bool value)
    {
        var previous = _settingWorkflowSelectors;
        _settingWorkflowSelectors = true;
        try
        {
            chkDirectMode.Checked = value;
            _settings.DirectModeEnabled = value;
        }
        finally { _settingWorkflowSelectors = previous; }
    }

    private void ApplyPixelExactControlState(bool referenceReady)
    {
        var metadata = GetActiveQueueWorkflowMetadata();
        var recognizedPixel = metadata.IsPixelExact;
        var pixel = chkPixelExact.Checked && !referenceReady;
        chkPixelExact.Enabled = !referenceReady && metadata.Kind is not QueuePromptWorkflowKind.Variants and not QueuePromptWorkflowKind.Single;
        lblPixelExactCount.Visible = pixel;
        cmbPixelExactCount.Visible = pixel;
        cmbPixelExactCount.Enabled = pixel && !recognizedPixel;
        cmbVariants.Enabled = !referenceReady && !pixel && !(metadata.Kind is QueuePromptWorkflowKind.PixelExactRef or QueuePromptWorkflowKind.PixelExactOutput);
        chkDirectMode.Enabled = !referenceReady && !pixel;
    }

    private bool TryPersistPixelExactSeedReceiptBeforeMainWrite(Models.AssetSession session)
    {
        if (!chkPixelExact.Checked || _currentManifest is null || _activeRequest is null)
        {
            return true;
        }

        var metadata = GetActiveQueueWorkflowMetadata();
        if (metadata.Kind != Models.QueuePromptWorkflowKind.PixelExactSeed || !metadata.HasCanonicalMetadata
            || !string.Equals(session.SourceRequestKey, _activeRequest.RequestKey, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var existing = _pixelExactBatchStateService.Load();
            if (existing?.Completed == true)
            {
                _pixelExactBatchStateService.ClearCompletedState();
                existing = null;
            }
            if (existing is not null && (!string.Equals(existing.SeriesId, metadata.SeriesId, StringComparison.Ordinal)
                || !string.Equals(existing.SeedRequestKey, _activeRequest.RequestKey, StringComparison.Ordinal)))
            {
                ShowMessageBox("Another Pixel-Exact batch is pending. Finish or discard it before committing this master image.", "Pixel-Exact batch pending", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var state = existing ?? _pixelExactBatchStateService.CreateSeedReceiptState(metadata, _currentManifest, _activeRequest, session);
            _pixelExactBatchStateService.Save(state);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not persist the Pixel-Exact seed receipt before Main processing.", ex);
            return false;
        }
    }

    private PixelSeedCompletion? CapturePixelExactSeedCompletion(Models.AssetSession session, string committedFilename, DateTimeOffset processedAt)
    {
        if (!chkPixelExact.Checked || _activeRequest is null || _currentManifest is null)
        {
            return null;
        }
        var metadata = GetActiveQueueWorkflowMetadata();
        if (metadata.Kind != Models.QueuePromptWorkflowKind.PixelExactSeed || !metadata.HasCanonicalMetadata
            || !string.Equals(session.SourceRequestKey, _activeRequest.RequestKey, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var state = _pixelExactBatchStateService.Load();
            if (state is null || !string.Equals(state.SeedRequestKey, _activeRequest.RequestKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The durable Pixel-Exact seed receipt is unavailable.");
            }
            var path = Path.Combine(session.AssetFolder, committedFilename);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!string.Equals(hash, state.SeedExpectedSession?.MainHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The committed master image does not match the pre-write Pixel-Exact receipt.");
            }
            state.SeedCommitted = true;
            state.MasterAssetName = session.AssetFolderName;
            state.MasterReferencePath = path;
            state.MasterReferenceSha256 = hash;
            state.MasterProcessedAt = processedAt;
            state.MasterProviderTemplate = session.ProviderTemplate?.Clone();
            _pixelExactBatchStateService.Save(state);
            return new PixelSeedCompletion(_activeRequest.RequestKey, state.SeriesId);
        }
        catch (Exception ex)
        {
            // The asset is already durable. Preserve it and surface the journal
            // fault rather than pretending that the collection can safely start.
            ShowError("Master asset committed, but Pixel-Exact master authority could not be recorded.", ex);
            return null;
        }
    }

    private void FinalizePixelExactSeedAfterQueueCompletion(string requestKey)
    {
        try
        {
            var state = _pixelExactBatchStateService.Load();
            if (state is null || !state.SeedCommitted || !string.Equals(state.SeedRequestKey, requestKey, StringComparison.Ordinal)) return;
            if (!_completedRequestKeys.Contains(requestKey)) return;
            state.SeedQueueCompleted = true;
            _pixelExactBatchStateService.Save(state);
        }
        catch (Exception ex)
        {
            AddStatus($"Pixel-Exact master queue state requires reconciliation: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the browser/download-folder part of the Pixel-Exact workflow.
    /// A seed is an ordinary one-image Main commit. A RefN item freezes the N
    /// downloaded images first, then commits every output to its real queue row.
    /// </summary>
    private void HandlePixelExactMainImage(QueuePromptWorkflowMetadata workflow)
    {
        if (workflow.Kind == QueuePromptWorkflowKind.PixelExactSeed)
        {
            // The normal no-reference path supplies the durable seed receipt
            // immediately before its first file-system write.
            HandleMainImage();
            return;
        }

        if (workflow.Kind == QueuePromptWorkflowKind.PixelExactOutput)
        {
            ShowMessageBox(
                "This row is filled automatically by its preceding RefN collection request. Select that RefN row, download all requested images, then click Main Image once.",
                "Select the collection request",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var isExplicitManualCollection = workflow.Kind == QueuePromptWorkflowKind.Unknown
            && GetSelectedPixelExactOutputCount() > 0;
        if (workflow.Kind != QueuePromptWorkflowKind.PixelExactRef
            && !isExplicitManualCollection
            || _currentManifest is null
            || _activeRequest is null)
        {
            ShowMessageBox(
                "Pixel-Exact processing requires an active RefN queue request, or an explicitly selected Pixel phases count for an unannotated manual queue row.",
                "Pixel-Exact unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            ShowMessageBox(
                "Finish or cancel the active reference session before starting a Pixel-Exact collection.",
                "Pixel-Exact blocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!ValidateMainActionUi(requireSelectedMainImage: false))
        {
            return;
        }

        var targets = TryResolvePixelExactTargets(workflow, _activeRequest);
        if (targets is null)
        {
            return;
        }

        var settings = ReadSettingsFromUi();
        IReadOnlyList<string> sources = Array.Empty<string>();
        try
        {
            var pending = _pixelExactBatchStateService.Load();
            var needsFreshDownloads = pending is null || pending.Outputs.Count == 0;
            if (needsFreshDownloads)
            {
                sources = TryResolvePixelExactMainImages(
                    settings,
                    workflow.PixelOutputCount ?? GetSelectedPixelExactOutputCount()) ?? [];
                if (sources.Count == 0)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not read the Pixel-Exact batch journal.", ex);
            return;
        }

        PixelExactBatchState state;
        var previewSources = sources;
        try
        {
            if (previewSources.Count == 0)
            {
                previewSources = _pixelExactBatchStateService.Load()?.Outputs
                    .OrderBy(output => output.OutputIndex)
                    .Select(output => output.StagedPath)
                    .ToArray()
                    ?? [];
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not prepare the Pixel-Exact phase preview.", ex);
            return;
        }

        if (!ConfirmPixelExactPhaseOrder(targets, previewSources))
        {
            AddStatus("Pixel-Exact collection cancelled at phase-order confirmation.");
            return;
        }

        try
        {
            state = PreparePixelExactCollectionState(workflow, _activeRequest, sources);
            _pixelExactBatchStateService.ValidateStagedAuthority(state);
        }
        catch (Exception ex)
        {
            ShowError("Could not establish the durable Pixel-Exact collection receipt.", ex);
            return;
        }

        var collectionPrompt = state.CollectionGenerationPrompt!;
        var completed = 0;

        foreach (var target in targets)
        {
            var output = state.Outputs.Single(item => item.OutputIndex == target.OutputIndex);

            if (output.State == PixelExactOutputCommitState.QueueCompleted)
            {
                completed++;
                continue;
            }

            if (output.State == PixelExactOutputCommitState.CommitInProgress)
            {
                ShowMessageBox(
                    $"Pixel-Exact output {target.OutputIndex} has an interrupted commit. Recover or cancel the active session before continuing this collection.",
                    "Pixel-Exact recovery required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (output.State == PixelExactOutputCommitState.AssetCommitted)
            {
                if (!TryMarkPixelExactQueueCompletion(state, output, target.Request))
                {
                    return;
                }
                completed++;
                continue;
            }

            if (target.Request.IsCompleted || _completedRequestKeys.Contains(target.Request.RequestKey))
            {
                ShowMessageBox(
                    $"Pixel-Exact output {target.OutputIndex} is already marked completed, but its durable batch journal disagrees. No files were changed.",
                    "Pixel-Exact reconciliation required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            AssetSession session;
            var processedAt = DateTimeOffset.Now;
            try
            {
                session = _assetProcessorService.CreateNoReferenceMainSession(
                    settings,
                    target.Request.AssetName,
                    output.StagedPath,
                    collectionPrompt,
                    processedAt,
                    state.BundleProviderTemplate?.Clone() ?? GetProviderSnapshotForNewAsset(),
                    target.Request.RequestKey);

                // Write the expected transaction before session.json. A crash can
                // never make an unknown Downloads file look like a later phase.
                output.ManifestFingerprint = _currentManifest.ManifestFingerprint;
                output.RequestKey = target.Request.RequestKey;
                output.AssetName = target.Request.AssetName;
                output.ExpectedCommitSession = _pixelExactBatchStateService.CloneSessionReceipt(session);
                output.State = PixelExactOutputCommitState.CommitInProgress;
                _pixelExactBatchStateService.Save(state);
                _sessionService.Save(session);
            }
            catch (Exception ex)
            {
                RestorePixelExactOutputToStaged(state, output);
                ShowError($"Could not prepare Pixel-Exact output {target.OutputIndex}.", ex);
                return;
            }

            if (!ExecuteMainCommit(session, output.StagedPath, collectionPrompt, processedAt, suppressUiCompletion: true))
            {
                return;
            }

            try
            {
                var committedPath = Path.Combine(session.AssetFolder, session.MainFilename!);
                var committedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(committedPath))).ToLowerInvariant();
                if (!string.Equals(committedHash, output.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Committed Pixel-Exact output does not match its staged image receipt.");
                }

                output.AssetFolderPath = session.AssetFolder;
                output.AssetCommittedAtUtc = DateTimeOffset.UtcNow;
                output.State = PixelExactOutputCommitState.AssetCommitted;
                _pixelExactBatchStateService.Save(state);
            }
            catch (Exception ex)
            {
                // The asset is already durable. Keep the state at the exact
                // reconciliation point and never remap a different download.
                ShowError($"Pixel-Exact output {target.OutputIndex} was committed, but its receipt could not be finalized.", ex);
                return;
            }

            if (!TryMarkPixelExactQueueCompletion(state, output, target.Request))
            {
                return;
            }

            _lastCompletedAssetFolderPath = session.AssetFolder;
            try
            {
                RecordRecentDocument(
                    ProvenanceDocumentKind.Final,
                    Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName),
                    target.Request.AssetName,
                    processedAt);
            }
            catch (Exception ex)
            {
                AddStatus($"Pixel-Exact output {target.OutputIndex} was committed, but could not be added to recent history: {ex.Message}");
            }
            completed++;
            AddStatus($"Pixel-Exact output {target.OutputIndex} committed: {target.Request.AssetName}");
        }

        try
        {
            state.Completed = state.Outputs.All(output => output.State == PixelExactOutputCommitState.QueueCompleted);
            _pixelExactBatchStateService.Save(state);
        }
        catch (Exception ex)
        {
            AddStatus($"Pixel-Exact outputs are committed, but final batch cleanup needs reconciliation: {ex.Message}");
        }

        _activeRequest = null;
        _activeApiCandidateMetadata = null;
        ResetAssetInputFieldsAfterDurableAction();
        ApplyState();
        ShowMessageBox(
            $"{completed} Pixel-Exact outputs were committed as individual queue assets.",
            "Pixel-Exact collection complete",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private IReadOnlyList<PixelExactTarget>? TryResolvePixelExactTargets(QueuePromptWorkflowMetadata workflow, AssetRequestItem activeRequest)
    {
        if (_currentManifest is null)
        {
            return null;
        }

        var outputCount = workflow.PixelOutputCount ?? GetSelectedPixelExactOutputCount();
        if (outputCount is < 1 or > AppConstants.MaxPixelExactOutputCount)
        {
            ShowMessageBox("Select a Pixel phases count from 1 to 10 before processing this manual queue row.", "Pixel phases required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        var targets = new List<PixelExactTarget> { new(1, activeRequest) };
        if (workflow.HasCanonicalMetadata)
        {
            for (var outputIndex = 2; outputIndex <= outputCount; outputIndex++)
            {
                var matches = _currentManifest.Items
                    .Where(item =>
                    {
                        var parsed = _queuePromptWorkflowParser.Parse(item.Prompt);
                        return parsed.Kind == QueuePromptWorkflowKind.PixelExactOutput
                            && parsed.HasCanonicalMetadata
                            && string.Equals(parsed.SeriesId, workflow.SeriesId, StringComparison.Ordinal)
                            && parsed.PixelOutputCount == outputCount
                            && parsed.OutputIndex == outputIndex;
                    })
                    .ToList();

                if (matches.Count != 1)
                {
                    ShowMessageBox(
                        $"The Pixel-Exact series metadata does not contain exactly one target row for output {outputIndex}. No images were processed.",
                        "Invalid Pixel-Exact series",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return null;
                }
                targets.Add(new PixelExactTarget(outputIndex, matches[0]));
            }
        }
        else
        {
            var activeIndex = Array.IndexOf(_currentManifest.Items.ToArray(), activeRequest);
            var followers = activeIndex < 0 ? [] : _currentManifest.Items.Skip(activeIndex + 1).Take(outputCount - 1).ToList();
            var manualUnknownRow = workflow.Kind == QueuePromptWorkflowKind.Unknown;
            if (followers.Count != outputCount - 1 || followers.Any(item =>
                {
                    var parsed = _queuePromptWorkflowParser.Parse(item.Prompt);
                    return manualUnknownRow
                        ? parsed.Kind is not QueuePromptWorkflowKind.Unknown
                            and not QueuePromptWorkflowKind.PixelExactOutput
                            || parsed.Kind == QueuePromptWorkflowKind.PixelExactOutput && parsed.PixelOutputCount != outputCount
                        : parsed.Kind != QueuePromptWorkflowKind.PixelExactOutput || parsed.PixelOutputCount != outputCount;
                }))
            {
                ShowMessageBox(
                    manualUnknownRow
                        ? "A manually configured Pixel-Exact row must be followed immediately by its unannotated target rows (or matching AusRefN rows). No images were processed."
                        : "This legacy RefN request must be followed immediately by its matching AusRefN queue rows. No images were processed.",
                    "Incomplete Pixel-Exact sequence",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }
            for (var index = 0; index < followers.Count; index++) targets.Add(new PixelExactTarget(index + 2, followers[index]));
        }

        if (targets.Select(target => target.Request.RequestKey).Distinct(StringComparer.Ordinal).Count() != outputCount)
        {
            ShowMessageBox("Pixel-Exact targets are not unique. No images were processed.", "Invalid Pixel-Exact series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return targets;
    }

    private IReadOnlyList<string>? TryResolvePixelExactMainImages(AppSettings settings, int outputCount)
    {
        var validation = _validationService.ValidateDownloadFolder(settings.DownloadFolder);
        if (!validation.IsValid)
        {
            HighlightField(pnlDownloadFolderHost, true);
            ShowValidationError("Pixel-Exact requires a valid Image Download Folder.", validation);
            return null;
        }

        try
        {
            var newestFirst = _imageFinderService.FindLatestImages(settings, outputCount);
            if (newestFirst.Count != outputCount)
            {
                ShowMessageBox(
                    $"Pixel phases is set to {outputCount}, but only {newestFirst.Count} supported images were found in the Image Download Folder.",
                    "Not enough Pixel-Exact images",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return null;
            }

            foreach (var source in newestFirst)
            {
                var image = _validationService.ValidateImageFile(source, settings.AcceptedExtensions);
                if (!image.IsValid)
                {
                    ShowValidationError($"Pixel-Exact image '{Path.GetFileName(source)}' is invalid.", image);
                    return null;
                }
            }

            // External tools generally write the final phase last. The queue and
            // provenance therefore use deterministic oldest-to-newest ordering.
            return newestFirst.Reverse().ToArray();
        }
        catch (Exception ex)
        {
            ShowError("Could not scan the Image Download Folder for Pixel-Exact images.", ex);
            return null;
        }
    }

    private bool ConfirmPixelExactPhaseOrder(IReadOnlyList<PixelExactTarget> targets, IReadOnlyList<string> orderedSources)
    {
        if (targets.Count == 0 || targets.Count != orderedSources.Count)
        {
            ShowMessageBox(
                "The detected Pixel-Exact source images and queue targets do not have the same count. No files were written.",
                "Pixel-Exact phase order unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var phases = targets
            .Select((target, index) => new PixelExactPhasePreview(
                target.OutputIndex,
                targets.Count,
                Path.GetFileName(orderedSources[index]),
                target.Request.AssetName,
                target.Request.Resolution))
            .ToArray();
        var confirmation = ShowConfirmDialog(
            BuildPixelExactPhasePreviewText(phases),
            "Confirm Pixel-Exact phase order",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        return confirmation == DialogResult.OK;
    }

    internal static string BuildPixelExactPhasePreviewText(IReadOnlyList<PixelExactPhasePreview> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Count == 0)
        {
            throw new ArgumentException("At least one Pixel-Exact phase is required.", nameof(phases));
        }

        var rows = phases.Select(phase =>
            $"{phase.OutputIndex}/{phase.OutputCount}: {phase.SourceFileName}  →  {phase.TargetAssetName} ({phase.Resolution})");
        return "Review the ordered Pixel-Exact phases before any asset is written."
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, rows)
            + Environment.NewLine + Environment.NewLine
            + "The helper will freeze these files and commit them oldest-to-newest. Continue?";
    }

    private PixelExactBatchState PreparePixelExactCollectionState(QueuePromptWorkflowMetadata workflow, AssetRequestItem activeRequest, IReadOnlyList<string> sources)
    {
        if (_currentManifest is null)
        {
            throw new InvalidOperationException("Pixel-Exact collection has no active manifest authority.");
        }

        var outputCount = workflow.PixelOutputCount ?? GetSelectedPixelExactOutputCount();
        if (outputCount is < 1 or > AppConstants.MaxPixelExactOutputCount)
        {
            throw new InvalidOperationException("Pixel-Exact collection has no selected output count.");
        }

        var existing = _pixelExactBatchStateService.Load();
        PixelExactBatchState state;
        if (workflow.HasCanonicalMetadata)
        {
            if (existing is null
                || !existing.HasCanonicalSeriesIdentity
                || !existing.SeedCommitted
                || !existing.SeedQueueCompleted
                || !string.Equals(existing.SeriesId, workflow.SeriesId, StringComparison.Ordinal)
                || existing.BundleCount != outputCount)
            {
                throw new InvalidDataException("The matching Pixel-Exact seed has not been committed and marked done. Process the preceding seed row first.");
            }
            state = existing;
        }
        else
        {
            if (existing is not null && !existing.Completed)
            {
                throw new InvalidDataException("Another Pixel-Exact collection is pending. Finish it or clear the queue after confirmation before starting a new one.");
            }
            state = _pixelExactBatchStateService.CreateManualLocalCollectionState(_currentManifest, activeRequest, outputCount);
        }

        if (state.Outputs.Count == 0)
        {
            state.CollectionManifestFingerprint = _currentManifest.ManifestFingerprint;
            state.CollectionRequestKey = activeRequest.RequestKey;
            state.CollectionGenerationPrompt = activeRequest.Prompt;
            state.CollectionOrigin = workflow.CollectionOrigin;
            state.ReferenceOrigin = workflow.ReferenceOrigin;
            state.BundleCount = outputCount;
            state.TotalPhases = outputCount + 1;
            _pixelExactBatchStateService.Save(state);
            return _pixelExactBatchStateService.StageBundle(state, sources, GetProviderSnapshotForNewAsset());
        }

        if (!string.Equals(state.CollectionManifestFingerprint, _currentManifest.ManifestFingerprint, StringComparison.Ordinal)
            || !string.Equals(state.CollectionRequestKey, activeRequest.RequestKey, StringComparison.Ordinal)
            || state.BundleCount != outputCount)
        {
            throw new InvalidDataException("The pending Pixel-Exact journal belongs to another collection request.");
        }

        return state;
    }

    private bool TryMarkPixelExactQueueCompletion(PixelExactBatchState state, PixelExactStagedOutput output, AssetRequestItem request)
    {
        if (_currentManifest is null || !string.Equals(output.ManifestFingerprint, _currentManifest.ManifestFingerprint, StringComparison.Ordinal)
            || !string.Equals(output.RequestKey, request.RequestKey, StringComparison.Ordinal))
        {
            ShowMessageBox("Pixel-Exact queue authority no longer matches the imported manifest.", "Pixel-Exact reconciliation required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var next = new HashSet<string>(_completedRequestKeys, StringComparer.Ordinal) { request.RequestKey };
        try
        {
            _requestProgressService?.Save(_currentManifest.ManifestFingerprint, next);
        }
        catch (Exception ex)
        {
            ShowError($"Pixel-Exact output '{request.AssetName}' was committed, but its queue completion could not be saved.", ex);
            return false;
        }

        _completedRequestKeys.Clear();
        _completedRequestKeys.UnionWith(next);
        request.IsCompleted = true;
        output.State = PixelExactOutputCommitState.QueueCompleted;
        _pixelExactBatchStateService.Save(state);
        RefreshRequestQueueVisuals();
        UpdateRequestProgressLabel();
        return true;
    }

    private void RestorePixelExactOutputToStaged(PixelExactBatchState state, PixelExactStagedOutput output)
    {
        try
        {
            output.ManifestFingerprint = null;
            output.RequestKey = null;
            output.AssetName = null;
            output.ExpectedCommitSession = null;
            output.State = PixelExactOutputCommitState.Staged;
            _pixelExactBatchStateService.Save(state);
        }
        catch
        {
            // Keep the most conservative durable state; the caller reports the
            // original preparation failure rather than masking it with cleanup.
        }
    }

    private void ResetPixelExactJournalForDeletedRequest(AssetRequestItem request)
    {
        var state = _pixelExactBatchStateService.Load();
        if (state is null)
        {
            return;
        }

        // Deleting the master invalidates the external-reference authority for
        // every later phase. Do not leave a misleading resumable journal behind.
        if (string.Equals(state.SeedRequestKey, request.RequestKey, StringComparison.Ordinal))
        {
            _pixelExactBatchStateService.DiscardPendingState();
            return;
        }

        var output = state.Outputs.FirstOrDefault(item => string.Equals(item.RequestKey, request.RequestKey, StringComparison.Ordinal));
        if (output is null)
        {
            return;
        }

        output.State = PixelExactOutputCommitState.Staged;
        output.ManifestFingerprint = null;
        output.RequestKey = null;
        output.AssetName = null;
        output.ExpectedCommitSession = null;
        output.AssetFolderPath = null;
        output.AssetCommittedAtUtc = null;
        state.Completed = false;
        _pixelExactBatchStateService.Save(state);
    }

    private bool IsResettablePixelExactCollectionRequest(AssetRequestItem item)
    {
        var workflow = _queuePromptWorkflowParser.Parse(item.Prompt);
        if (workflow.Kind != QueuePromptWorkflowKind.PixelExactRef)
        {
            return false;
        }

        try
        {
            var state = _pixelExactBatchStateService.Load();
            return state is not null
                && state.Outputs.Any(output => output.State != PixelExactOutputCommitState.QueueCompleted)
                && string.Equals(state.CollectionRequestKey, item.RequestKey, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void TryActivateNextPixelExactCollection(string seriesId)
    {
        if (_currentManifest is null)
        {
            return;
        }

        var next = _currentManifest.Items.FirstOrDefault(item =>
        {
            var workflow = _queuePromptWorkflowParser.Parse(item.Prompt);
            return !item.IsCompleted
                && !_completedRequestKeys.Contains(item.RequestKey)
                && workflow.Kind == QueuePromptWorkflowKind.PixelExactRef
                && workflow.HasCanonicalMetadata
                && string.Equals(workflow.SeriesId, seriesId, StringComparison.Ordinal);
        });

        if (next is null)
        {
            return;
        }

        var row = lvRequestQueue.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, next));
        if (row is not null)
        {
            HandleRequestQueueItemActivate(row);
            AddStatus("Pixel-Exact collection request loaded. Generate/download all displayed Pixel phases, then click Main Image once.");
        }
    }
}
