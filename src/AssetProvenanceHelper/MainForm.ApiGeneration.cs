using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private readonly GeneratedImageStagingService _stagingService = new();
    private ApiCandidateMetadata? _activeApiCandidateMetadata;
    private CancellationTokenSource? _apiGenerationCts;
    private bool _isGeneratingDirect;

    internal ApiCandidateMetadata? ActiveApiCandidateMetadata => _activeApiCandidateMetadata;

    private static bool IsJobActiveOrInFlight(GenerationItemRecord job)
    {
        return job.Status is GenerationItemStatus.DirectInFlight
            or GenerationItemStatus.QueuedDirect
            or GenerationItemStatus.DirectRateLimited
            or GenerationItemStatus.BatchPreparing
            or GenerationItemStatus.BatchSubmitted
            or GenerationItemStatus.BatchRunning
            or GenerationItemStatus.Preparing
            or GenerationItemStatus.Normalizing
            or GenerationItemStatus.Downloading;
    }

    private void HandleGenerateNow()
    {
        if (_currentManifest == null)
        {
            ShowMessageBox(
                "No Request Manifest imported." + Environment.NewLine + Environment.NewLine +
                "Import a Request Manifest first.",
                "Generate Now",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            ShowMessageBox(
                "Automated API generation is not supported while in Reference-Assisted mode." + Environment.NewLine +
                "Finish or cancel the current reference asset first.",
                "Generate Now blocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var apiKey = _secretStore.LoadSecret(SettingsDialog.OpenAiApiKeySecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowMessageBox(
                "OpenAI API key is not configured." + Environment.NewLine + Environment.NewLine +
                "Open Settings to configure your API key.",
                "API Key required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var pendingItems = _currentManifest.Items
            .Where(i => !i.IsCompleted && !_completedRequestKeys.Contains(i.RequestKey))
            .ToList();

        if (pendingItems.Count == 0)
        {
            ShowMessageBox(
                "All items in the current Request Manifest are already completed.",
                "Generate Now",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var eligible = new List<AssetRequestItem>();
        var blockedAlphaCount = 0;
        var alreadyReadyCount = 0;
        var inFlightCount = 0;
        var uncertainCount = 0;

        foreach (var item in pendingItems)
        {
            var job = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, item.RequestKey);
            if (job != null)
            {
                if (job.Status == GenerationItemStatus.Ready && !string.IsNullOrEmpty(job.StagedOutputPath) && File.Exists(job.StagedOutputPath))
                {
                    alreadyReadyCount++;
                    continue;
                }

                if (job.Status == GenerationItemStatus.UncertainAfterInterruption)
                {
                    uncertainCount++;
                    continue;
                }

                if (IsJobActiveOrInFlight(job))
                {
                    inFlightCount++;
                    continue;
                }
            }

            if (item.Alpha == AlphaRequirement.Required)
            {
                blockedAlphaCount++;
                continue;
            }

            try
            {
                ImageSizePlanner.Plan(item.Width, item.Height);
            }
            catch
            {
                continue;
            }

            eligible.Add(item);
        }

        if (eligible.Count == 0)
        {
            var details =
                $"No eligible pending assets found to generate." + Environment.NewLine + Environment.NewLine +
                $"Total pending: {pendingItems.Count}" + Environment.NewLine +
                $"Already ready: {alreadyReadyCount}" + Environment.NewLine +
                $"In flight/queued: {inFlightCount}" + Environment.NewLine +
                (uncertainCount > 0 ? $"Uncertain (requires manual retry): {uncertainCount}" + Environment.NewLine : string.Empty) +
                $"Blocked (alpha required): {blockedAlphaCount}";

            ShowMessageBox(
                details,
                "Generate Now",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var confirmMsg =
            $"Generate {eligible.Count} pending assets now using the standard API?" + Environment.NewLine + Environment.NewLine +
            $"Provider: OpenAI" + Environment.NewLine +
            $"Model: {_settings.OpenAiModel}" + Environment.NewLine +
            $"Quality: {_settings.DirectImageQuality}" + Environment.NewLine +
            $"Eligible: {eligible.Count}" + Environment.NewLine +
            $"Blocked (alpha required): {blockedAlphaCount}" + Environment.NewLine + Environment.NewLine +
            "This mode uses normal API pricing and normal model rate limits." + Environment.NewLine +
            "Requests will be rate-limited locally.";

        if (eligible.Count > 100)
        {
            confirmMsg += Environment.NewLine + Environment.NewLine +
                "⚠️ WARNING: You are about to generate more than 100 items directly via the API. " +
                "Standard API billing rates apply. Please ensure this is intended.";
        }

        if (ShowConfirmDialog(confirmMsg, "Generate Now (API)", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        foreach (var item in eligible)
        {
            var plan = ImageSizePlanner.Plan(item.Width, item.Height);
            var customId = GenerationCustomId.Create(_currentManifest.ManifestFingerprint, item.RequestKey);
            _generationJobStore.UpsertItem(new GenerationItemRecord(
                ManifestFingerprint: _currentManifest.ManifestFingerprint,
                RequestKey: item.RequestKey,
                AssetName: item.AssetName,
                FileName: item.FileName,
                Mode: GenerationMode.Direct,
                ProviderId: "OpenAI",
                Model: _settings.OpenAiModel,
                Quality: _settings.DirectImageQuality,
                TargetWidth: item.Width,
                TargetHeight: item.Height,
                GenerationWidth: plan.GenerationWidth,
                GenerationHeight: plan.GenerationHeight,
                CustomId: customId,
                Status: GenerationItemStatus.QueuedDirect,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }

        RefreshRequestQueueVisuals();
        _ = RunDirectGenerationAsync(eligible, apiKey);
    }

    private async Task RunDirectGenerationAsync(IReadOnlyList<AssetRequestItem> items, string apiKey)
    {
        _isGeneratingDirect = true;
        _apiGenerationCts = new CancellationTokenSource();
        ApplyRequestQueueState();

        using var rateLimiter = new RequestStartRateLimiter(
            _settings.DirectStartsPerMinute,
            _settings.DirectMaxConcurrency);

        try
        {
            var tasks = items.Select(async item =>
            {
                if (_currentManifest == null || _apiGenerationCts.IsCancellationRequested) return;

                var plan = ImageSizePlanner.Plan(item.Width, item.Height);
                var customId = GenerationCustomId.Create(_currentManifest.ManifestFingerprint, item.RequestKey);
                var spec = new ImageGenerationSpec(
                    ManifestFingerprint: _currentManifest.ManifestFingerprint,
                    RequestKey: item.RequestKey,
                    AssetName: item.AssetName,
                    FileName: item.FileName,
                    Prompt: item.Prompt,
                    TargetWidth: item.Width,
                    TargetHeight: item.Height,
                    AlphaRequirement: item.Alpha,
                    ProviderId: "OpenAI",
                    Model: _settings.OpenAiModel,
                    Quality: _settings.DirectImageQuality,
                    GenerationWidth: plan.GenerationWidth,
                    GenerationHeight: plan.GenerationHeight,
                    CustomId: customId);

                var itemRecord = new GenerationItemRecord(
                    ManifestFingerprint: _currentManifest.ManifestFingerprint,
                    RequestKey: item.RequestKey,
                    AssetName: item.AssetName,
                    FileName: item.FileName,
                    Mode: GenerationMode.Direct,
                    ProviderId: "OpenAI",
                    Model: _settings.OpenAiModel,
                    Quality: _settings.DirectImageQuality,
                    TargetWidth: item.Width,
                    TargetHeight: item.Height,
                    GenerationWidth: plan.GenerationWidth,
                    GenerationHeight: plan.GenerationHeight,
                    CustomId: customId,
                    Status: GenerationItemStatus.QueuedDirect,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);

                var acquiredPermit = false;
                try
                {
                    using var permit = await rateLimiter.AcquireAsync(_apiGenerationCts.Token).ConfigureAwait(false);
                    acquiredPermit = true;

                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.DirectInFlight,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    SafeInvoke(RefreshRequestQueueVisuals);

                    var candidate = await _imageGenerationProvider.GenerateAsync(spec, apiKey, _apiGenerationCts.Token).ConfigureAwait(false);
                    var normResult = ImageNormalizationService.Normalize(candidate.RawBytes, plan);

                    var metadata = new ApiCandidateMetadata(
                        CandidateId: candidate.CandidateId,
                        Provider: "OpenAI",
                        Model: _settings.OpenAiModel,
                        Mode: "direct",
                        CustomId: customId,
                        TargetResolution: $"{item.Width}x{item.Height}",
                        ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                        RawSha256: normResult.RawSha256,
                        NormalizedSha256: normResult.NormalizedSha256,
                        NormalizedImagePath: string.Empty,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        ProviderRequestId: candidate.ProviderRequestId);

                    var normalizedPath = _stagingService.SaveCandidate(
                        _currentManifest.ManifestFingerprint,
                        item.RequestKey,
                        candidate.CandidateId,
                        candidate.RawBytes,
                        normResult.NormalizedBytes,
                        metadata);

                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Ready,
                        StagedOutputPath = normalizedPath,
                        RawSha256 = normResult.RawSha256,
                        NormalizedSha256 = normResult.NormalizedSha256,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                catch (OperationCanceledException)
                {
                    if (acquiredPermit)
                    {
                        _generationJobStore.UpsertItem(itemRecord with
                        {
                            Status = GenerationItemStatus.UncertainAfterInterruption,
                            ErrorCode = "direct_interrupted",
                            ErrorMessage = "Request was cancelled or interrupted while in-flight. Remote billing status is uncertain.",
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                    else
                    {
                        _generationJobStore.UpsertItem(itemRecord with
                        {
                            Status = GenerationItemStatus.Pending,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                }
                catch (Exception ex)
                {
                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.FailedPermanent,
                        ErrorCode = "direct_failed",
                        ErrorMessage = ex.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                finally
                {
                    SafeInvoke(RefreshRequestQueueVisuals);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            SafeInvoke(() => AddStatus("Direct API generation completed."));
        }
        catch (Exception ex)
        {
            SafeInvoke(() => AddStatus($"Direct API generation stopped: {ex.Message}"));
        }
        finally
        {
            _isGeneratingDirect = false;
            SafeInvoke(ApplyRequestQueueState);
            SafeInvoke(RefreshRequestQueueVisuals);
        }
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Form disposed or handle destroyed while posting
            }
        }
        else
        {
            action();
        }
    }

    private System.Windows.Forms.Timer? _batchPollingTimer;
    private bool _isSubmittingBatch;
    private bool _isPollingBatches;

    internal void InitializeBatchMonitoring()
    {
        _batchPollingTimer?.Dispose();
        _batchPollingTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(10, _settings.BatchPollSeconds) * 1000
        };
        _batchPollingTimer.Tick += async (_, _) => await PollActiveBatchesAsync().ConfigureAwait(false);
        CheckAndStartBatchMonitoring();
    }

    internal void CheckAndStartBatchMonitoring()
    {
        var activeBatches = _generationJobStore.GetActiveBatches();
        if (activeBatches.Count > 0)
        {
            _batchPollingTimer?.Start();
        }
        else
        {
            _batchPollingTimer?.Stop();
        }
    }

    private void HandleQueueProductionBatch()
    {
        ExecuteQueueProductionBatch();
    }

    private void ExecuteQueueProductionBatch()
    {
        if (_currentManifest == null)
        {
            ShowMessageBox(
                "No Request Manifest imported." + Environment.NewLine + Environment.NewLine +
                "Import a Request Manifest first.",
                "Queue Production Batch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_state == UiState.ReferenceReady)
        {
            ShowMessageBox(
                "Automated API generation is not supported while in Reference-Assisted mode." + Environment.NewLine +
                "Finish or cancel the current reference asset first.",
                "Production Batch blocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var apiKey = _secretStore.LoadSecret(SettingsDialog.OpenAiApiKeySecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowMessageBox(
                "OpenAI API key is not configured." + Environment.NewLine + Environment.NewLine +
                "Open Settings to configure your API key.",
                "API Key required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var pendingItems = _currentManifest.Items
            .Where(i => !i.IsCompleted && !_completedRequestKeys.Contains(i.RequestKey))
            .ToList();

        if (pendingItems.Count == 0)
        {
            ShowMessageBox(
                "All items in the current Request Manifest are already completed.",
                "Queue Production Batch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var eligible = new List<AssetRequestItem>();
        var blockedAlphaCount = 0;
        var alreadyReadyCount = 0;
        var inFlightCount = 0;
        var uncertainCount = 0;

        foreach (var item in pendingItems)
        {
            var job = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, item.RequestKey);
            if (job != null)
            {
                if (job.Status == GenerationItemStatus.Ready && !string.IsNullOrEmpty(job.StagedOutputPath) && File.Exists(job.StagedOutputPath))
                {
                    alreadyReadyCount++;
                    continue;
                }

                if (job.Status == GenerationItemStatus.UncertainAfterInterruption)
                {
                    uncertainCount++;
                    continue;
                }

                if (IsJobActiveOrInFlight(job))
                {
                    inFlightCount++;
                    continue;
                }
            }

            if (item.Alpha == AlphaRequirement.Required)
            {
                blockedAlphaCount++;
                continue;
            }

            try
            {
                ImageSizePlanner.Plan(item.Width, item.Height);
            }
            catch
            {
                continue;
            }

            eligible.Add(item);
        }

        if (eligible.Count == 0)
        {
            var details =
                $"No eligible pending assets found for production batch." + Environment.NewLine + Environment.NewLine +
                $"Total pending: {pendingItems.Count}" + Environment.NewLine +
                $"Already ready: {alreadyReadyCount}" + Environment.NewLine +
                $"In flight/queued: {inFlightCount}" + Environment.NewLine +
                (uncertainCount > 0 ? $"Uncertain (requires manual retry): {uncertainCount}" + Environment.NewLine : string.Empty) +
                $"Blocked (alpha required): {blockedAlphaCount}";

            ShowMessageBox(
                details,
                "Queue Production Batch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var totalEligibleCount = eligible.Count;
        if (eligible.Count > _settings.MaxBatchRequestsPerSubmission)
        {
            eligible = eligible.Take(_settings.MaxBatchRequestsPerSubmission).ToList();
        }

        var confirmMsg =
            $"Submit {eligible.Count} pending assets to OpenAI Production Batch?" + Environment.NewLine + Environment.NewLine +
            $"Provider: OpenAI" + Environment.NewLine +
            $"Model: {_settings.OpenAiModel}" + Environment.NewLine +
            $"Quality: {_settings.BatchImageQuality}" + Environment.NewLine +
            $"Eligible: {eligible.Count}" + Environment.NewLine +
            $"Blocked (alpha required): {blockedAlphaCount}" + Environment.NewLine + Environment.NewLine +
            "Production Batch benefits:" + Environment.NewLine +
            "- 50% discount on API generation costs" + Environment.NewLine +
            "- Separate batch quota" + Environment.NewLine + Environment.NewLine +
            "Notice:" + Environment.NewLine +
            "- Batches process asynchronously with up to a 24-hour turnaround window." + Environment.NewLine +
            "- The application will monitor the batch and automatically stage results when ready.";

        if (totalEligibleCount > _settings.MaxBatchRequestsPerSubmission)
        {
            confirmMsg += Environment.NewLine + Environment.NewLine +
                $"⚠️ NOTE: Eligible items ({totalEligibleCount}) exceeded the configured batch cap ({_settings.MaxBatchRequestsPerSubmission}). " +
                $"This submission is capped at the first {_settings.MaxBatchRequestsPerSubmission} items.";
        }

        if (eligible.Count > 100)
        {
            confirmMsg += Environment.NewLine + Environment.NewLine +
                "⚠️ CAUTION: Large batch submission (> 100 items). Please verify that your OpenAI account has sufficient batch quota.";
        }

        if (ShowConfirmDialog(confirmMsg, "Queue Production Batch (50% discount)", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _ = SubmitBatchAsync(eligible, apiKey);
    }

    private async Task SubmitBatchAsync(IReadOnlyList<AssetRequestItem> eligible, string apiKey)
    {
        _isSubmittingBatch = true;
        ApplyRequestQueueState();

        var localBatchId = "batch-" + Guid.NewGuid().ToString("N")[..12];
        var batchRecord = new GenerationBatchRecord(
            LocalBatchId: localBatchId,
            ManifestFingerprint: _currentManifest!.ManifestFingerprint,
            ProviderId: "OpenAI",
            Model: _settings.OpenAiModel,
            Quality: _settings.BatchImageQuality,
            RequestKeys: eligible.Select(e => e.RequestKey).ToList(),
            Status: "preparing",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedCount: eligible.Count,
            CompletedCount: 0,
            FailedCount: 0);

        _generationJobStore.UpsertBatch(batchRecord);

        var specs = new List<ImageGenerationSpec>();
        foreach (var item in eligible)
        {
            var plan = ImageSizePlanner.Plan(item.Width, item.Height);
            var customId = GenerationCustomId.Create(_currentManifest.ManifestFingerprint, item.RequestKey);
            var spec = new ImageGenerationSpec(
                ManifestFingerprint: _currentManifest.ManifestFingerprint,
                RequestKey: item.RequestKey,
                AssetName: item.AssetName,
                FileName: item.FileName,
                Prompt: item.Prompt,
                TargetWidth: item.Width,
                TargetHeight: item.Height,
                AlphaRequirement: item.Alpha,
                ProviderId: "OpenAI",
                Model: _settings.OpenAiModel,
                Quality: _settings.BatchImageQuality,
                GenerationWidth: plan.GenerationWidth,
                GenerationHeight: plan.GenerationHeight,
                CustomId: customId);

            specs.Add(spec);

            _generationJobStore.UpsertItem(new GenerationItemRecord(
                ManifestFingerprint: _currentManifest.ManifestFingerprint,
                RequestKey: item.RequestKey,
                AssetName: item.AssetName,
                FileName: item.FileName,
                Mode: GenerationMode.Batch,
                ProviderId: "OpenAI",
                Model: _settings.OpenAiModel,
                Quality: _settings.BatchImageQuality,
                TargetWidth: item.Width,
                TargetHeight: item.Height,
                GenerationWidth: plan.GenerationWidth,
                GenerationHeight: plan.GenerationHeight,
                CustomId: customId,
                Status: GenerationItemStatus.BatchSubmitted,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                BatchId: localBatchId));
        }

        SafeInvoke(RefreshRequestQueueVisuals);

        try
        {
            var result = await _imageGenerationProvider.SubmitBatchAsync(specs, apiKey).ConfigureAwait(false);

            _generationJobStore.UpsertBatch(batchRecord with
            {
                ProviderBatchId = result.ProviderBatchId,
                ProviderInputFileId = result.ProviderInputFileId,
                Status = "submitted",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            foreach (var item in eligible)
            {
                var existing = _generationJobStore.GetItem(_currentManifest!.ManifestFingerprint, item.RequestKey);
                if (existing != null)
                {
                    _generationJobStore.UpsertItem(existing with
                    {
                        ProviderBatchId = result.ProviderBatchId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }

            SafeInvoke(() =>
            {
                AddStatus($"Production batch submitted (ID: {result.ProviderBatchId}). Monitoring active.");
                CheckAndStartBatchMonitoring();
                RefreshRequestQueueVisuals();
            });
        }
        catch (Exception ex)
        {
            _generationJobStore.UpsertBatch(batchRecord with
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var isInterruption = ex is TaskCanceledException or TimeoutException or HttpRequestException;
            var targetStatus = isInterruption
                ? GenerationItemStatus.UncertainAfterInterruption
                : GenerationItemStatus.FailedPermanent;
            var errorMsg = isInterruption
                ? $"Batch submission timed out or interrupted ({ex.Message}). Remote status is uncertain; verify OpenAI dashboard before retrying."
                : ex.Message;

            foreach (var item in eligible)
            {
                var existingItem = _generationJobStore.GetItem(_currentManifest.ManifestFingerprint, item.RequestKey);
                if (existingItem != null)
                {
                    _generationJobStore.UpsertItem(existingItem with
                    {
                        Status = targetStatus,
                        ErrorCode = isInterruption ? "batch_submission_uncertain" : "batch_submission_failed",
                        ErrorMessage = errorMsg,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }

            SafeInvoke(() =>
            {
                AddStatus(isInterruption
                    ? $"Production batch submission interrupted: {ex.Message} (status uncertain)"
                    : $"Production batch submission failed: {ex.Message}");
                RefreshRequestQueueVisuals();
            });
        }
        finally
        {
            _isSubmittingBatch = false;
            SafeInvoke(ApplyRequestQueueState);
        }
    }

    internal async Task PollActiveBatchesAsync()
    {
        if (_isPollingBatches) return;
        _isPollingBatches = true;

        try
        {
            var apiKey = _secretStore.LoadSecret(SettingsDialog.OpenAiApiKeySecretName);
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            var activeBatches = _generationJobStore.GetActiveBatches();
            if (activeBatches.Count == 0)
            {
                SafeInvoke(() => _batchPollingTimer?.Stop());
                return;
            }

            foreach (var batch in activeBatches)
            {
                if (string.IsNullOrWhiteSpace(batch.ProviderBatchId)) continue;

                try
                {
                    var status = await _imageGenerationProvider.GetBatchStatusAsync(batch.ProviderBatchId, apiKey).ConfigureAwait(false);

                    var hasOutputFile = !string.IsNullOrWhiteSpace(status.OutputFileId);
                    var isTerminal = string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "expired", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

                    if (isTerminal && hasOutputFile)
                    {
                        _generationJobStore.UpsertBatch(batch with
                        {
                            CompletedCount = status.CompletedCount,
                            FailedCount = status.FailedCount,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });

                        var results = await _imageGenerationProvider.DownloadBatchResultsAsync(status, apiKey).ConfigureAwait(false);

                        var batchItems = _generationJobStore.GetItemsForBatch(batch.LocalBatchId);
                        var handledCustomIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var output in results.Items)
                        {
                            var itemRecord = batchItems.FirstOrDefault(i => string.Equals(i.CustomId, output.CustomId, StringComparison.OrdinalIgnoreCase));
                            if (itemRecord == null) continue;

                            handledCustomIds.Add(output.CustomId);

                            if (output.IsSuccess && output.ImageBytes != null && output.ImageBytes.Length > 0)
                            {
                                try
                                {
                                    var plan = ImageSizePlanner.Plan(itemRecord.TargetWidth, itemRecord.TargetHeight);
                                    var normResult = ImageNormalizationService.Normalize(output.ImageBytes, plan);

                                    var candidateId = Guid.NewGuid().ToString("N");
                                    var metadata = new ApiCandidateMetadata(
                                        CandidateId: candidateId,
                                        Provider: batch.ProviderId,
                                        Model: batch.Model,
                                        Mode: "batch",
                                        CustomId: output.CustomId,
                                        TargetResolution: $"{itemRecord.TargetWidth}x{itemRecord.TargetHeight}",
                                        ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                                        RawSha256: normResult.RawSha256,
                                        NormalizedSha256: normResult.NormalizedSha256,
                                        NormalizedImagePath: string.Empty,
                                        CreatedAtUtc: DateTimeOffset.UtcNow,
                                        ProviderRequestId: output.ProviderRequestId,
                                        BatchId: !string.IsNullOrEmpty(batch.ProviderBatchId) ? batch.ProviderBatchId : batch.LocalBatchId);

                                    var normalizedPath = _stagingService.SaveCandidate(
                                        batch.ManifestFingerprint,
                                        itemRecord.RequestKey,
                                        candidateId,
                                        output.ImageBytes,
                                        normResult.NormalizedBytes,
                                        metadata);

                                    _generationJobStore.UpsertItem(itemRecord with
                                    {
                                        Status = GenerationItemStatus.Ready,
                                        StagedOutputPath = normalizedPath,
                                        RawSha256 = normResult.RawSha256,
                                        NormalizedSha256 = normResult.NormalizedSha256,
                                        UpdatedAtUtc = DateTimeOffset.UtcNow
                                    });
                                }
                                catch (Exception normEx)
                                {
                                    _generationJobStore.UpsertItem(itemRecord with
                                    {
                                        Status = GenerationItemStatus.FailedPermanent,
                                        ErrorCode = "normalization_error",
                                        ErrorMessage = normEx.Message,
                                        UpdatedAtUtc = DateTimeOffset.UtcNow
                                    });
                                }
                            }
                            else
                            {
                                _generationJobStore.UpsertItem(itemRecord with
                                {
                                    Status = GenerationItemStatus.FailedPermanent,
                                    ErrorCode = output.ErrorCode ?? "batch_item_failed",
                                    ErrorMessage = output.ErrorMessage ?? "Batch item failed",
                                    UpdatedAtUtc = DateTimeOffset.UtcNow
                                });
                            }
                        }

                        // Any item in the batch that never appeared in the output/error files (e.g. uncompleted when expired):
                        foreach (var bItem in batchItems)
                        {
                            if (!handledCustomIds.Contains(bItem.CustomId) &&
                                bItem.Status != GenerationItemStatus.Ready &&
                                bItem.Status != GenerationItemStatus.Committed)
                            {
                                _generationJobStore.UpsertItem(bItem with
                                {
                                    Status = GenerationItemStatus.FailedPermanent,
                                    ErrorCode = status.Status,
                                    ErrorMessage = $"Batch ended with status '{status.Status}' before item was processed.",
                                    UpdatedAtUtc = DateTimeOffset.UtcNow
                                });
                            }
                        }

                        _generationJobStore.UpsertBatch(batch with
                        {
                            Status = status.Status,
                            CompletedAtUtc = DateTimeOffset.UtcNow,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });

                        SafeInvoke(() =>
                        {
                            AddStatus($"Batch {batch.ProviderBatchId} ended with status '{status.Status}'. Results ingested.");
                            RefreshRequestQueueVisuals();
                        });
                    }
                    else if (isTerminal)
                    {
                        _generationJobStore.UpsertBatch(batch with
                        {
                            Status = status.Status,
                            CompletedCount = status.CompletedCount,
                            FailedCount = status.FailedCount,
                            ErrorMessage = $"Batch {status.Status}",
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });

                        var batchItems = _generationJobStore.GetItemsForBatch(batch.LocalBatchId);
                        foreach (var bItem in batchItems)
                        {
                            if (bItem.Status != GenerationItemStatus.Ready && bItem.Status != GenerationItemStatus.Committed)
                            {
                                _generationJobStore.UpsertItem(bItem with
                                {
                                    Status = GenerationItemStatus.FailedPermanent,
                                    ErrorCode = status.Status,
                                    ErrorMessage = $"Batch {status.Status}",
                                    UpdatedAtUtc = DateTimeOffset.UtcNow
                                });
                            }
                        }

                        SafeInvoke(() =>
                        {
                            AddStatus($"Batch {batch.ProviderBatchId} ended with status '{status.Status}'.");
                            RefreshRequestQueueVisuals();
                        });
                    }
                    else
                    {
                        _generationJobStore.UpsertBatch(batch with
                        {
                            Status = status.Status,
                            CompletedCount = status.CompletedCount,
                            FailedCount = status.FailedCount,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                }
                catch (Exception batchEx)
                {
                    SafeInvoke(() => AddStatus($"Polling error for batch {batch.ProviderBatchId}: {batchEx.Message}"));
                }
            }
        }
        finally
        {
            _isPollingBatches = false;
        }
    }
}
