using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private readonly record struct PixelSeedCompletion(string RequestKey, string SeriesId);
    /// <summary>A deferred phase this commit is about to resolve from the
    /// journal, so the journal can be closed out once the asset is durable.</summary>
    private readonly record struct PixelExactDeferredResolution(string SeriesId, int OutputIndex, string RequestKey);
    private readonly record struct PixelExactTarget(int OutputIndex, AssetRequestItem Request);
    /// <summary>Bound queue rows plus the output indices this manifest part does
    /// not carry. A canonical series may legitimately continue in a later
    /// manifest, so those indices are deferred instead of failing the batch.</summary>
    private sealed record PixelExactTargetResolution(
        IReadOnlyList<PixelExactTarget> Targets,
        IReadOnlyList<int> DeferredOutputIndexes,
        int OutputCount);
    internal sealed record PixelExactPhasePreview(
        int OutputIndex,
        int OutputCount,
        string SourceFileName,
        string TargetAssetName,
        string Resolution,
        bool IsDeferred = false);
    /// <summary>Where a canonical collection takes its master authority from.</summary>
    private enum PixelExactSeedAuthority
    {
        /// <summary>A durable seed receipt for this series is in the journal, or
        /// the row is a manual collection that never has one.</summary>
        Journal = 0,

        /// <summary>The series master was committed from a manifest part this
        /// journal no longer covers and the operator confirmed continuing.</summary>
        ConfirmedContinuation = 1
    }

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
            // Scoped to this series. A manifest part may end with a run of seed
            // rows whose collections all live in a later part, so several seed
            // receipts have to be pending at the same time.
            var seriesId = metadata.SeriesId!;
            var existing = _pixelExactBatchStateService.Load(seriesId);
            if (existing?.Completed == true)
            {
                _pixelExactBatchStateService.ClearCompletedState(seriesId);
                existing = null;
            }
            if (existing is not null && !string.Equals(existing.SeedRequestKey, _activeRequest.RequestKey, StringComparison.Ordinal))
            {
                ShowMessageBox($"Series '{seriesId}' already has a pending Pixel-Exact batch from a different queue row. Finish or discard it before committing this master image.", "Pixel-Exact batch pending", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            var state = _pixelExactBatchStateService.Load(metadata.SeriesId!);
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
            var state = _pixelExactBatchStateService.LoadBySeedRequestKey(requestKey);
            if (state is null || !state.SeedCommitted) return;
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
            if (ConfirmContinuationPixelExactOutputCommit(workflow))
            {
                HandleMainImage();
            }
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

        var resolution = TryResolvePixelExactTargets(workflow, _activeRequest);
        if (resolution is null)
        {
            return;
        }
        var targets = resolution.Targets;
        var journalSeriesId = ResolvePixelExactJournalSeriesId(workflow, _activeRequest);

        // A canonical collection normally consumes the durable receipt its own
        // seed row wrote. When the series started in an earlier manifest part
        // whose journal is no longer available, the operator confirms the
        // missing master authority instead of losing the whole collection.
        var seedAuthority = ResolvePixelExactSeedAuthority(workflow, journalSeriesId, resolution.OutputCount);
        if (seedAuthority is null)
        {
            return;
        }

        var settings = ReadSettingsFromUi();
        IReadOnlyList<string> sources = Array.Empty<string>();
        try
        {
            var pending = _pixelExactBatchStateService.Load(journalSeriesId);
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
                previewSources = _pixelExactBatchStateService.Load(journalSeriesId)?.Outputs
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

        if (!ConfirmPixelExactPhaseOrder(resolution, previewSources))
        {
            AddStatus("Pixel-Exact collection cancelled at phase-order confirmation.");
            return;
        }

        try
        {
            state = PreparePixelExactCollectionState(workflow, _activeRequest, sources, seedAuthority.Value);
            _pixelExactBatchStateService.ValidateStagedAuthority(state);
        }
        catch (Exception ex)
        {
            ShowError("Could not establish the durable Pixel-Exact collection receipt.", ex);
            return;
        }

        // The journal records the deferral before the first commit so an
        // interrupted batch can still be closed out with the same phases.
        if (!TryRecordDeferredPixelExactOutputs(state, resolution.DeferredOutputIndexes))
        {
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

        // A deferred phase is terminal for this batch. Without that the journal
        // slot would stay pending forever and block every following series.
        try
        {
            state.Completed = state.Outputs.All(output =>
                output.State == PixelExactOutputCommitState.QueueCompleted || output.DeferredNoTargetRow);
            _pixelExactBatchStateService.Save(state);
        }
        catch (Exception ex)
        {
            AddStatus($"Pixel-Exact outputs are committed, but final batch cleanup needs reconciliation: {ex.Message}");
        }

        var completedRequest = _activeRequest;
        _activeRequest = null;
        _activeApiCandidateMetadata = null;
        ResetAssetInputFieldsAfterDurableAction();
        ApplyState();

        var deferredReport = BuildPixelExactDeferredReport(state, resolution.DeferredOutputIndexes);
        foreach (var line in deferredReport)
        {
            AddStatus(line);
        }

        // The next queue row is selected before the summary dialog so the
        // operator can paste its prompt straight after acknowledging it.
        TryActivateNextOpenQueueRequest(completedRequest);

        ShowMessageBox(
            $"{completed} Pixel-Exact outputs were committed as individual queue assets."
                + (deferredReport.Count == 0
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, deferredReport)),
            "Pixel-Exact collection complete",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>Marks the phases this manifest part cannot bind. They keep their
    /// staged bytes and stay uncommitted, but they no longer hold the batch open.</summary>
    private bool TryRecordDeferredPixelExactOutputs(PixelExactBatchState state, IReadOnlyList<int> deferredOutputIndexes)
    {
        if (deferredOutputIndexes.Count == 0)
        {
            return true;
        }

        try
        {
            foreach (var outputIndex in deferredOutputIndexes)
            {
                var output = state.Outputs.SingleOrDefault(item => item.OutputIndex == outputIndex);
                if (output is null || output.State != PixelExactOutputCommitState.Staged)
                {
                    throw new InvalidDataException($"Pixel-Exact output {outputIndex} cannot be deferred from its current journal state.");
                }
                output.DeferredNoTargetRow = true;
            }
            _pixelExactBatchStateService.Save(state);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not record the deferred Pixel-Exact phases of this series.", ex);
            return false;
        }
    }

    internal static IReadOnlyList<string> BuildPixelExactDeferredPhaseReport(
        string seriesId,
        IReadOnlyList<int> deferredOutputIndexes,
        int outputCount,
        IReadOnlyList<string> originalSourceFileNames)
    {
        if (deferredOutputIndexes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>
        {
            $"Series '{seriesId}' has no queue row for output "
                + string.Join(", ", deferredOutputIndexes.Select(index => $"{index}/{outputCount}"))
                + " in the imported manifest. Those phases were not committed."
        };
        for (var index = 0; index < deferredOutputIndexes.Count; index++)
        {
            var fileName = index < originalSourceFileNames.Count ? originalSourceFileNames[index] : "(unknown)";
            lines.Add($"Deferred output {deferredOutputIndexes[index]}/{outputCount}: {fileName}");
        }
        lines.Add("Import the continuation manifest that carries these AusRefN rows, then commit each one with its own downloaded image.");
        return lines;
    }

    private IReadOnlyList<string> BuildPixelExactDeferredReport(PixelExactBatchState state, IReadOnlyList<int> deferredOutputIndexes)
    {
        var fileNames = deferredOutputIndexes
            .Select(outputIndex => state.Outputs.FirstOrDefault(output => output.OutputIndex == outputIndex))
            .Select(output => output is null ? "(unknown)" : Path.GetFileName(output.OriginalSourcePath))
            .ToArray();
        return BuildPixelExactDeferredPhaseReport(state.SeriesId, deferredOutputIndexes, state.BundleCount, fileNames);
    }

    private PixelExactTargetResolution? TryResolvePixelExactTargets(QueuePromptWorkflowMetadata workflow, AssetRequestItem activeRequest)
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
        var deferred = new List<int>();
        if (workflow.HasCanonicalMetadata)
        {
            for (var outputIndex = 2; outputIndex <= outputCount; outputIndex++)
            {
                var matches = FindCanonicalPixelExactOutputRows(workflow.SeriesId, outputCount, outputIndex);

                // Two rows claiming the same output index is corrupt metadata and
                // still fails closed. Zero rows is the documented cross-manifest
                // case: the continuation lives in a later manifest part, so the
                // phase is deferred instead of discarding the whole collection.
                if (matches.Count > 1)
                {
                    ShowMessageBox(
                        $"The Pixel-Exact series metadata contains {matches.Count} target rows for output {outputIndex}, but exactly one is required. No images were processed.",
                        "Invalid Pixel-Exact series",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return null;
                }

                if (matches.Count == 0)
                {
                    deferred.Add(outputIndex);
                    continue;
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

        if (targets.Select(target => target.Request.RequestKey).Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            ShowMessageBox("Pixel-Exact targets are not unique. No images were processed.", "Invalid Pixel-Exact series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return new PixelExactTargetResolution(targets, deferred, outputCount);
    }

    /// <summary>All canonical AusRefN rows of one series that claim a single
    /// output index. Zero rows means this manifest part does not carry the
    /// phase; more than one means the series metadata is ambiguous.</summary>
    private List<AssetRequestItem> FindCanonicalPixelExactOutputRows(string? seriesId, int outputCount, int outputIndex)
    {
        if (_currentManifest is null)
        {
            return [];
        }

        return _currentManifest.Items
            .Where(item =>
            {
                var parsed = _queuePromptWorkflowParser.Parse(item.Prompt);
                return parsed.Kind == QueuePromptWorkflowKind.PixelExactOutput
                    && parsed.HasCanonicalMetadata
                    && string.Equals(parsed.SeriesId, seriesId, StringComparison.Ordinal)
                    && parsed.PixelOutputCount == outputCount
                    && parsed.OutputIndex == outputIndex;
            })
            .ToList();
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

    private bool ConfirmPixelExactPhaseOrder(PixelExactTargetResolution resolution, IReadOnlyList<string> orderedSources)
    {
        var targets = resolution.Targets;
        if (targets.Count == 0 || resolution.OutputCount != orderedSources.Count)
        {
            ShowMessageBox(
                "The detected Pixel-Exact source images and queue targets do not have the same count. No files were written.",
                "Pixel-Exact phase order unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        // Sources are addressed by output index, never by position in the target
        // list. A deferred phase leaves a hole there and must not shift the rest.
        var phases = targets
            .Select(target => new PixelExactPhasePreview(
                target.OutputIndex,
                resolution.OutputCount,
                Path.GetFileName(orderedSources[target.OutputIndex - 1]),
                target.Request.AssetName,
                target.Request.Resolution))
            .Concat(resolution.DeferredOutputIndexes.Select(outputIndex => new PixelExactPhasePreview(
                outputIndex,
                resolution.OutputCount,
                Path.GetFileName(orderedSources[outputIndex - 1]),
                string.Empty,
                string.Empty,
                IsDeferred: true)))
            .OrderBy(phase => phase.OutputIndex)
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

        var rows = phases.Select(phase => phase.IsDeferred
            ? $"{phase.OutputIndex}/{phase.OutputCount}: {phase.SourceFileName}  →  deferred (no target row in this manifest)"
            : $"{phase.OutputIndex}/{phase.OutputCount}: {phase.SourceFileName}  →  {phase.TargetAssetName} ({phase.Resolution})");
        var deferredNote = phases.Any(phase => phase.IsDeferred)
            ? Environment.NewLine
                + "Deferred phases are not written now. Their downloaded images stay in the Image Download Folder for the continuation manifest."
            : string.Empty;
        return "Review the ordered Pixel-Exact phases before any asset is written."
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, rows)
            + Environment.NewLine + deferredNote + Environment.NewLine
            + "The helper will freeze these files and commit them oldest-to-newest. Continue?";
    }

    private PixelExactBatchState PreparePixelExactCollectionState(
        QueuePromptWorkflowMetadata workflow,
        AssetRequestItem activeRequest,
        IReadOnlyList<string> sources,
        PixelExactSeedAuthority seedAuthority)
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

        var existing = _pixelExactBatchStateService.Load(ResolvePixelExactJournalSeriesId(workflow, activeRequest));
        PixelExactBatchState state;
        if (workflow.HasCanonicalMetadata)
        {
            if (seedAuthority == PixelExactSeedAuthority.Journal)
            {
                // A batch this very row already staged is resumable on its own
                // authority. A confirmed continuation never has a seed receipt,
                // so requiring one here would make its retry unresumable.
                var resumesOwnBatch = existing is not null
                    && existing.Outputs.Count > 0
                    && string.Equals(existing.CollectionRequestKey, activeRequest.RequestKey, StringComparison.Ordinal);
                if (existing is null
                    || !existing.HasCanonicalSeriesIdentity
                    || !string.Equals(existing.SeriesId, workflow.SeriesId, StringComparison.Ordinal)
                    || existing.BundleCount != outputCount
                    || !resumesOwnBatch && (!existing.SeedCommitted || !existing.SeedQueueCompleted))
                {
                    throw new InvalidDataException("The matching Pixel-Exact seed has not been committed and marked done. Process the preceding seed row first.");
                }
                state = existing;
            }
            else
            {
                // Confirmed continuation: the series master was committed from a
                // manifest part this journal no longer covers. The collection
                // still records its own prompt and staged image authority.
                if (existing is not null)
                {
                    throw new InvalidDataException("The pending Pixel-Exact journal belongs to another collection request.");
                }
                state = _pixelExactBatchStateService.CreateCollectionState(workflow, _currentManifest, activeRequest);
            }
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

    /// <summary>The journal key of a collection row: its canonical series, or
    /// the derived identity of a manual (unannotated) collection.</summary>
    private static string ResolvePixelExactJournalSeriesId(QueuePromptWorkflowMetadata workflow, AssetRequestItem activeRequest) =>
        workflow.HasCanonicalMetadata && !string.IsNullOrWhiteSpace(workflow.SeriesId)
            ? workflow.SeriesId!
            : Services.PixelExactBatchStateService.DeriveManualSeriesId(activeRequest.RequestKey);

    /// <summary>The canonical seed row of one series in the imported manifest,
    /// or null when the series started in another manifest part.</summary>
    private AssetRequestItem? FindCanonicalPixelExactSeedRow(string? seriesId)
    {
        if (_currentManifest is null || string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        return _currentManifest.Items.FirstOrDefault(item =>
        {
            var parsed = _queuePromptWorkflowParser.Parse(item.Prompt);
            return parsed.Kind == QueuePromptWorkflowKind.PixelExactSeed
                && parsed.HasCanonicalMetadata
                && string.Equals(parsed.SeriesId, seriesId, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Decides whether this collection may run, and on whose authority. Returns
    /// null when it must not run at all, which is when the series' own seed row
    /// is importable here and still open - that row has to be processed first.
    /// </summary>
    private PixelExactSeedAuthority? ResolvePixelExactSeedAuthority(QueuePromptWorkflowMetadata workflow, string journalSeriesId, int outputCount)
    {
        if (!workflow.HasCanonicalMetadata)
        {
            return PixelExactSeedAuthority.Journal;
        }

        PixelExactBatchState? existing;
        try
        {
            existing = _pixelExactBatchStateService.Load(journalSeriesId);
        }
        catch (Exception ex)
        {
            ShowError("Could not read the Pixel-Exact batch journal for this series.", ex);
            return null;
        }

        if (existing is not null)
        {
            // A journal entry for this series exists; the established checks in
            // PreparePixelExactCollectionState decide whether it is usable.
            return PixelExactSeedAuthority.Journal;
        }

        var seedRow = FindCanonicalPixelExactSeedRow(workflow.SeriesId);
        if (seedRow is not null && IsOpenQueueRequest(seedRow))
        {
            ShowMessageBox(
                $"The master row of series '{workflow.SeriesId}' has not been committed yet. Process '{seedRow.AssetName}' first, then run this collection.",
                "Pixel-Exact master missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        var confirmation = ShowConfirmDialog(
            $"No durable master receipt for series '{workflow.SeriesId}' exists in this helper's Pixel-Exact journal, and the manifest carries no open master row for it."
                + Environment.NewLine + Environment.NewLine
                + "This is expected when the master was committed from an earlier manifest part."
                + Environment.NewLine + Environment.NewLine
                + "It is NOT expected if that master row is still open in another manifest part. This collection does not produce the master asset - process that row in its own part first, or it stays missing."
                + Environment.NewLine + Environment.NewLine
                + $"Commit the {outputCount} downloaded images as this series' outputs anyway? The provenance records each queue row's own prompt.",
            "Continue Pixel-Exact series without a local master receipt",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.OK)
        {
            AddStatus($"Pixel-Exact collection cancelled: series '{workflow.SeriesId}' has no local master receipt.");
            return null;
        }
        return PixelExactSeedAuthority.ConfirmedContinuation;
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
        // Deleting the master invalidates the external-reference authority for
        // every later phase of that series. Do not leave a misleading resumable
        // journal behind - but only for the series the deleted row belongs to.
        var seed = _pixelExactBatchStateService.LoadBySeedRequestKey(request.RequestKey);
        if (seed is not null)
        {
            _pixelExactBatchStateService.DiscardPendingState(seed.SeriesId);
            return;
        }

        var state = _pixelExactBatchStateService.LoadByOutputRequestKey(request.RequestKey);
        if (state is null)
        {
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
            var state = _pixelExactBatchStateService.LoadByCollectionRequestKey(item.RequestKey);
            return state is not null
                && state.Outputs.Any(output => output.State != PixelExactOutputCommitState.QueueCompleted && !output.DeferredNoTargetRow);
        }
        catch
        {
            return false;
        }
    }

    private void ShowCollectionRequestGuidance() =>
        ShowMessageBox(
            "This row is filled automatically by its preceding RefN collection request. Select that RefN row, download all requested images, then click Main Image once.",
            "Select the collection request",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

    /// <summary>
    /// A canonical AusRefN row is normally written by its RefN collection. When
    /// the series continues into a later manifest part that collection row is
    /// not importable here, so the phase is committed as a single confirmed
    /// asset instead of leaving the queue permanently blocked.
    /// </summary>
    private bool ConfirmContinuationPixelExactOutputCommit(QueuePromptWorkflowMetadata workflow)
    {
        // Only a canonical row carries the series identity this path needs. A
        // legacy AusRefN prompt has no SERIE or OUTPUT_INDEX, so it keeps the
        // established guidance instead of failing the click silently.
        if (_currentManifest is null || _activeRequest is null || workflow.OutputIndex is not int outputIndex || workflow.PixelOutputCount is not int outputCount)
        {
            ShowCollectionRequestGuidance();
            return false;
        }

        var collectionRow = _currentManifest.Items.FirstOrDefault(item =>
        {
            var parsed = _queuePromptWorkflowParser.Parse(item.Prompt);
            return parsed.Kind == QueuePromptWorkflowKind.PixelExactRef
                && parsed.HasCanonicalMetadata
                && string.Equals(parsed.SeriesId, workflow.SeriesId, StringComparison.Ordinal)
                && parsed.PixelOutputCount == outputCount;
        });

        if (collectionRow is not null && IsOpenQueueRequest(collectionRow))
        {
            ShowCollectionRequestGuidance();
            return false;
        }

        _pendingPixelExactDeferredResolution = null;
        PixelExactBatchState? journal;
        try
        {
            journal = _pixelExactBatchStateService.Load(workflow.SeriesId!);
            if (journal is not null
                && !journal.Completed
                && journal.Outputs.Any(output => output.OutputIndex == outputIndex && !output.DeferredNoTargetRow && output.State != PixelExactOutputCommitState.QueueCompleted))
            {
                ShowMessageBox(
                    "A Pixel-Exact collection of this series is still pending and already holds a staged image for this output. Finish that collection from its RefN row before committing this row on its own.",
                    "Pixel-Exact collection pending",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not read the Pixel-Exact batch journal for this output row.", ex);
            return false;
        }

        // The RefN collection that deferred this phase froze its image and
        // recorded which download it was. Offer exactly that image instead of
        // making the operator identify it again among unrelated downloads.
        var deferred = journal?.Outputs.FirstOrDefault(output =>
            output.OutputIndex == outputIndex
            && output.DeferredNoTargetRow
            && output.State == PixelExactOutputCommitState.Staged);
        var deferredSource = deferred is null ? null : ResolveDeferredPixelExactPhaseSource(deferred);

        var confirmation = ShowConfirmDialog(
            deferredSource is null
                ? $"No open RefN collection request for series '{workflow.SeriesId}' exists in the imported manifest, so output {outputIndex}/{outputCount} cannot be filled automatically here."
                    + Environment.NewLine + Environment.NewLine
                    + $"Commit the selected image directly as '{_activeRequest.AssetName}'?"
                    + Environment.NewLine + Environment.NewLine
                    + "Use this for a series that continues in another manifest part. The provenance records this queue row's own prompt."
                : $"The RefN collection of series '{workflow.SeriesId}' deferred output {outputIndex}/{outputCount} to this manifest part and still holds its frozen image:"
                    + Environment.NewLine + Environment.NewLine
                    + Path.GetFileName(deferred!.OriginalSourcePath)
                    + Environment.NewLine + Environment.NewLine
                    + $"Commit that image as '{_activeRequest.AssetName}'? Any image selected by hand is replaced by it.",
            "Commit continuation output",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.OK)
        {
            AddStatus($"Continuation commit cancelled for output {outputIndex}/{outputCount}.");
            return false;
        }

        if (deferredSource is not null)
        {
            SetSelectedImage(ImageSlot.Main, deferredSource);
            _pendingPixelExactDeferredResolution = new PixelExactDeferredResolution(journal!.SeriesId, outputIndex, _activeRequest.RequestKey);
        }
        return true;
    }

    /// <summary>The still-trustworthy bytes of a deferred phase: the frozen
    /// staging copy first, then the original download, and only while the file
    /// still hashes to the receipt the collection wrote.</summary>
    private static string? ResolveDeferredPixelExactPhaseSource(PixelExactStagedOutput deferred)
    {
        foreach (var candidate in new[] { deferred.StagedPath, deferred.OriginalSourcePath })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(candidate))).ToLowerInvariant();
                if (string.Equals(hash, deferred.Sha256, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            catch (IOException)
            {
                // An unreadable candidate simply is not offered.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>Closes out a deferred phase once its asset and its queue row are
    /// durable, so it is neither offered twice nor pinned in the journal.</summary>
    private void FinalizePixelExactDeferredOutputAfterCommit(AssetSession session, bool queueProgressSaved)
    {
        var pending = _pendingPixelExactDeferredResolution;
        _pendingPixelExactDeferredResolution = null;
        if (pending is not { } resolution
            || !queueProgressSaved
            || _currentManifest is null
            || !string.Equals(session.SourceRequestKey, resolution.RequestKey, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var state = _pixelExactBatchStateService.Load(resolution.SeriesId);
            var output = state?.Outputs.FirstOrDefault(item => item.OutputIndex == resolution.OutputIndex && item.DeferredNoTargetRow);
            if (state is null || output is null)
            {
                return;
            }

            output.DeferredNoTargetRow = false;
            output.ManifestFingerprint = _currentManifest.ManifestFingerprint;
            output.RequestKey = resolution.RequestKey;
            output.AssetName = session.AssetFolderName;
            output.ExpectedCommitSession = _pixelExactBatchStateService.CloneSessionReceipt(session);
            output.AssetFolderPath = session.AssetFolder;
            output.AssetCommittedAtUtc = DateTimeOffset.UtcNow;
            output.State = PixelExactOutputCommitState.QueueCompleted;
            _pixelExactBatchStateService.Save(state);
            AddStatus($"Deferred Pixel-Exact phase {resolution.OutputIndex}/{state.BundleCount} of series '{state.SeriesId}' resolved.");
        }
        catch (Exception ex)
        {
            // The asset is already durable; only the journal needs attention.
            AddStatus($"Deferred Pixel-Exact phase state requires reconciliation: {ex.Message}");
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

        if (TryActivateQueueRow(next))
        {
            AddStatus("Pixel-Exact collection request loaded. Generate/download all displayed Pixel phases, then click Main Image once.");
        }
    }

    /// <summary>
    /// Selects the first still-open queue row at or after the row that was just
    /// finished, so a completed collection hands the operator its next single
    /// prompt (and the clipboard copy) without manual scrolling.
    /// </summary>
    private void TryActivateNextOpenQueueRequest(AssetRequestItem? completedRequest)
    {
        if (_currentManifest is null)
        {
            return;
        }

        // Searched over the rendered rows, not the manifest: the queue filter can
        // hide a manifest row, and handing back a request the operator cannot see
        // would silently do nothing.
        var rows = lvRequestQueue.Items.Cast<ListViewItem>().ToList();
        var completedIndex = completedRequest is null
            ? -1
            : rows.FindIndex(row => ReferenceEquals(row.Tag, completedRequest));
        var next = rows.Skip(completedIndex + 1).FirstOrDefault(IsOpenQueueRow)
            ?? rows.FirstOrDefault(IsOpenQueueRow);
        if (next?.Tag is not AssetRequestItem request)
        {
            return;
        }

        if (TryActivateQueueRow(request))
        {
            AddStatus($"Next open Request loaded: {request.AssetName}");
        }
    }

    private bool IsOpenQueueRow(ListViewItem row) =>
        row.Tag is AssetRequestItem item && IsOpenQueueRequest(item);

    private bool IsOpenQueueRequest(AssetRequestItem item) =>
        !item.IsCompleted && !_completedRequestKeys.Contains(item.RequestKey);

    private bool TryActivateQueueRow(AssetRequestItem request)
    {
        var row = lvRequestQueue.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, request));
        if (row is null)
        {
            return false;
        }

        // Selection is moved first. The refresh inside the activation restores
        // whatever was selected when it started, so leaving the old row selected
        // would highlight it while the form and clipboard hold the new one - and
        // a following Enter would then act on the stale row.
        foreach (var selected in lvRequestQueue.SelectedItems.Cast<ListViewItem>().ToList())
        {
            selected.Selected = false;
        }
        row.Selected = true;
        row.Focused = true;

        HandleRequestQueueItemActivate(row);

        // The row is re-created by the refresh inside the activation, so the
        // scroll target is resolved again rather than reusing the stale item.
        var refreshed = lvRequestQueue.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, request));
        try
        {
            refreshed?.EnsureVisible();
        }
        catch (InvalidOperationException)
        {
            // A headless or not-yet-created list view cannot scroll. The row is
            // still selected, which is all the workflow depends on.
        }

        // Activation refuses some rows outright. Reporting success only when it
        // actually took keeps the caller's status line honest.
        return ReferenceEquals(_activeRequest, request);
    }
}
