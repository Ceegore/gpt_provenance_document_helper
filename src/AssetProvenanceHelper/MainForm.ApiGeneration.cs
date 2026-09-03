using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private readonly GeneratedImageStagingService _stagingService;
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

        var preflightService = new ApiPreflightService(_generationJobStore);
        var preflight = preflightService.Preflight(
            _currentManifest.ManifestFingerprint,
            _currentManifest.Items,
            _completedRequestKeys);

        if (preflight.Errors.Count > 0)
        {
            var errorDetails = string.Join(Environment.NewLine, preflight.Errors.Select(e => $"- {e.FileName} ({e.RequestKey}): {e.Message}"));
            ShowMessageBox(
                $"Cannot start generation because {preflight.Errors.Count} local error(s) were found in the manifest:" + Environment.NewLine + Environment.NewLine +
                errorDetails + Environment.NewLine + Environment.NewLine +
                "No API requests were started. Please fix the manifest issues.",
                "Preflight validation failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (preflight.Eligible.Count == 0)
        {
            var details =
                $"No eligible pending assets found to generate." + Environment.NewLine + Environment.NewLine +
                $"Total pending: {preflight.TotalPendingCount}" + Environment.NewLine +
                $"Already ready: {preflight.AlreadyReadyCount}" + Environment.NewLine +
                $"In flight/queued: {preflight.InFlightCount}" + Environment.NewLine +
                (preflight.UncertainCount > 0 ? $"Uncertain (requires manual retry): {preflight.UncertainCount}" + Environment.NewLine : string.Empty) +
                $"Blocked (alpha required): {preflight.BlockedAlpha.Count}";

            ShowMessageBox(
                details,
                "Generate Now",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var eligible = preflight.Eligible;
        var blockedAlphaCount = preflight.BlockedAlpha.Count;

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

        var manifest = _currentManifest
            ?? throw new InvalidOperationException("Manifest disappeared before generation start.");

        var run = new ApiGenerationRunSnapshot(
            ManifestFingerprint: manifest.ManifestFingerprint,
            ProviderId: "OpenAI",
            Model: _settings.OpenAiModel,
            Quality: _settings.DirectImageQuality,
            DirectStartsPerMinute: _settings.DirectStartsPerMinute,
            DirectMaxConcurrency: _settings.DirectMaxConcurrency,
            DirectRetryAttempts: _settings.DirectRetryAttempts,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var itemsToQueue = new List<GenerationItemRecord>(eligible.Count);
        foreach (var item in eligible)
        {
            var plan = ImageSizePlanner.Plan(item.Width, item.Height);
            var customId = GenerationCustomId.Create(run.ManifestFingerprint, item.RequestKey);
            itemsToQueue.Add(new GenerationItemRecord(
                ManifestFingerprint: run.ManifestFingerprint,
                RequestKey: item.RequestKey,
                AssetName: item.AssetName,
                FileName: item.FileName,
                Mode: GenerationMode.Direct,
                ProviderId: run.ProviderId,
                Model: run.Model,
                Quality: run.Quality,
                TargetWidth: item.Width,
                TargetHeight: item.Height,
                GenerationWidth: plan.GenerationWidth,
                GenerationHeight: plan.GenerationHeight,
                CustomId: customId,
                Status: GenerationItemStatus.QueuedDirect,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }
        _generationJobStore.UpsertItems(itemsToQueue);

        RefreshRequestQueueVisuals();
        _ = RunDirectGenerationAsync(eligible, apiKey, run);
    }

    private async Task RunDirectGenerationAsync(IReadOnlyList<AssetRequestItem> items, string apiKey, ApiGenerationRunSnapshot run)
    {
        _isGeneratingDirect = true;
        _apiGenerationCts = new CancellationTokenSource();
        var globalErrorSignaled = 0;
        ApplyRequestQueueState();

        using var rateLimiter = new RequestStartRateLimiter(
            run.DirectStartsPerMinute,
            run.DirectMaxConcurrency);

        try
        {
            var tasks = items.Select(async item =>
            {
                var plan = ImageSizePlanner.Plan(item.Width, item.Height);
                var customId = GenerationCustomId.Create(run.ManifestFingerprint, item.RequestKey);
                var spec = new ImageGenerationSpec(
                    ManifestFingerprint: run.ManifestFingerprint,
                    RequestKey: item.RequestKey,
                    AssetName: item.AssetName,
                    FileName: item.FileName,
                    Prompt: item.Prompt,
                    TargetWidth: item.Width,
                    TargetHeight: item.Height,
                    AlphaRequirement: item.Alpha,
                    ProviderId: run.ProviderId,
                    Model: run.Model,
                    Quality: run.Quality,
                    GenerationWidth: plan.GenerationWidth,
                    GenerationHeight: plan.GenerationHeight,
                    CustomId: customId,
                    RetryAttempts: run.DirectRetryAttempts);

                var itemRecord = new GenerationItemRecord(
                    ManifestFingerprint: run.ManifestFingerprint,
                    RequestKey: item.RequestKey,
                    AssetName: item.AssetName,
                    FileName: item.FileName,
                    Mode: GenerationMode.Direct,
                    ProviderId: run.ProviderId,
                    Model: run.Model,
                    Quality: run.Quality,
                    TargetWidth: item.Width,
                    TargetHeight: item.Height,
                    GenerationWidth: plan.GenerationWidth,
                    GenerationHeight: plan.GenerationHeight,
                    CustomId: customId,
                    Status: GenerationItemStatus.QueuedDirect,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);

                if (_apiGenerationCts.IsCancellationRequested)
                {
                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Pending,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    return;
                }

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

                    if (candidate.RawBytes == null || candidate.RawBytes.Length == 0)
                    {
                        throw new InvalidDataException("Provider returned empty image data.");
                    }

                    var rawSha = string.IsNullOrEmpty(candidate.RawSha256)
                        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(candidate.RawBytes)).ToLowerInvariant()
                        : candidate.RawSha256;

                    // 1. Raw atomic write + Flush(true) before normalization
                    var rawPath = _stagingService.SaveRawCandidate(
                        run.ManifestFingerprint,
                        item.RequestKey,
                        candidate.CandidateId,
                        candidate.RawBytes);

                    // 2. Persist job Normalizing + raw path
                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Normalizing,
                        CandidateId = candidate.CandidateId,
                        ProviderRawPath = rawPath,
                        RawSha256 = rawSha,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    SafeInvoke(RefreshRequestQueueVisuals);

                    // 3. Normalize
                    var normResult = ImageNormalizationService.Normalize(candidate.RawBytes, plan);

                    // 4. Final atomic write + metadata atomic write
                    var metadata = new ApiCandidateMetadata(
                        CandidateId: candidate.CandidateId,
                        Provider: run.ProviderId,
                        Model: run.Model,
                        Mode: "direct",
                        CustomId: customId,
                        TargetResolution: $"{item.Width}x{item.Height}",
                        ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                        RawSha256: rawSha,
                        NormalizedSha256: normResult.NormalizedSha256,
                        NormalizedImagePath: string.Empty,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        ProviderRequestId: candidate.ProviderRequestId);

                    var normalizedPath = _stagingService.CompleteCandidate(
                        run.ManifestFingerprint,
                        item.RequestKey,
                        candidate.CandidateId,
                        normResult.NormalizedBytes,
                        metadata);

                    // 5. Job Ready
                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Ready,
                        CandidateId = candidate.CandidateId,
                        ProviderRawPath = rawPath,
                        StagedOutputPath = normalizedPath,
                        RawSha256 = rawSha,
                        NormalizedSha256 = normResult.NormalizedSha256,
                        ProviderRequestId = candidate.ProviderRequestId,
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
                    var isGlobal = IsGlobalDirectError(ex, out var globalReason);

                    _generationJobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.FailedPermanent,
                        ErrorCode = isGlobal ? "global_direct_error" : "direct_failed",
                        ErrorMessage = ex.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });

                    if (isGlobal && Interlocked.CompareExchange(ref globalErrorSignaled, 1, 0) == 0)
                    {
                        _apiGenerationCts?.Cancel();

                        SafeInvoke(() =>
                        {
                            ShowMessageBox(
                                $"Direct API generation stopped due to a global error:{Environment.NewLine}{Environment.NewLine}{globalReason}",
                                "Direct Generation Halted",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        });
                    }
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

    internal static bool IsGlobalDirectError(Exception ex, out string reason)
    {
        var current = ex;
        while (current != null)
        {
            if (current is OpenAiApiException apiEx)
            {
                if (apiEx.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    reason = "Invalid or expired API key (HTTP 401 Unauthorized).";
                    return true;
                }
                if (apiEx.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    reason = "Access forbidden for this account or project (HTTP 403 Forbidden).";
                    return true;
                }
                if (apiEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    reason = $"Model or resource not found (HTTP 404): {apiEx.Message}";
                    return true;
                }
            }
            if (current is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            {
                if (httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    reason = "Invalid or expired API key (HTTP 401 Unauthorized).";
                    return true;
                }
                if (httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    reason = "Access forbidden for this account or project (HTTP 403 Forbidden).";
                    return true;
                }
                if (httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    reason = $"Model or resource not found (HTTP 404): {httpEx.Message}";
                    return true;
                }
            }
            current = current.InnerException;
        }

        reason = string.Empty;
        return false;
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

        var preflightService = new ApiPreflightService(_generationJobStore);
        var preflight = preflightService.Preflight(
            _currentManifest.ManifestFingerprint,
            _currentManifest.Items,
            _completedRequestKeys);

        if (preflight.Errors.Count > 0)
        {
            var errorDetails = string.Join(Environment.NewLine, preflight.Errors.Select(e => $"- {e.FileName} ({e.RequestKey}): {e.Message}"));
            ShowMessageBox(
                $"Cannot queue production batch because {preflight.Errors.Count} local error(s) were found in the manifest:" + Environment.NewLine + Environment.NewLine +
                errorDetails + Environment.NewLine + Environment.NewLine +
                "No batch was submitted. Please fix the manifest issues.",
                "Preflight validation failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (preflight.Eligible.Count == 0)
        {
            var details =
                $"No eligible pending assets found for production batch." + Environment.NewLine + Environment.NewLine +
                $"Total pending: {preflight.TotalPendingCount}" + Environment.NewLine +
                $"Already ready: {preflight.AlreadyReadyCount}" + Environment.NewLine +
                $"In flight/queued: {preflight.InFlightCount}" + Environment.NewLine +
                (preflight.UncertainCount > 0 ? $"Uncertain (requires manual retry): {preflight.UncertainCount}" + Environment.NewLine : string.Empty) +
                $"Blocked (alpha required): {preflight.BlockedAlpha.Count}";

            ShowMessageBox(
                details,
                "Queue Production Batch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var eligible = preflight.Eligible.ToList();
        var blockedAlphaCount = preflight.BlockedAlpha.Count;

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
            Status: "PendingSubmission",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedCount: eligible.Count,
            CompletedCount: 0,
            FailedCount: 0);

        _generationJobStore.UpsertBatch(batchRecord);

        var specs = new List<ImageGenerationSpec>(eligible.Count);
        var batchItemsToQueue = new List<GenerationItemRecord>(eligible.Count);
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

            batchItemsToQueue.Add(new GenerationItemRecord(
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
                Status: GenerationItemStatus.BatchQueued,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                BatchId: localBatchId));
        }

        _generationJobStore.UpsertItems(batchItemsToQueue);

        SafeInvoke(RefreshRequestQueueVisuals);

        string inputFileId;
        try
        {
            inputFileId = await _imageGenerationProvider.UploadBatchInputFileAsync(specs, apiKey).ConfigureAwait(false);
        }
        catch (Exception uploadEx)
        {
            _generationJobStore.UpsertBatch(batchRecord with
            {
                Status = "FailedLocal",
                ErrorMessage = $"Upload failed: {uploadEx.Message}",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var manifestItems = _generationJobStore.GetItemsForManifest(_currentManifest.ManifestFingerprint)
                .ToDictionary(i => i.RequestKey, StringComparer.Ordinal);
            var failedItems = eligible
                .Select(item => manifestItems.TryGetValue(item.RequestKey, out var existing) ? existing : null)
                .Where(i => i != null)
                .Select(i => i! with
                {
                    Status = GenerationItemStatus.FailedRetryable,
                    ErrorCode = "batch_upload_failed",
                    ErrorMessage = uploadEx.Message,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                })
                .ToList();
            _generationJobStore.UpsertItems(failedItems);

            SafeInvoke(() =>
            {
                _isSubmittingBatch = false;
                ApplyRequestQueueState();
                RefreshRequestQueueVisuals();
                ShowMessageBox($"Failed to upload batch input file: {uploadEx.Message}", "Batch Submission Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
            return;
        }

        try
        {
            batchRecord = batchRecord with
            {
                ProviderInputFileId = inputFileId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            _generationJobStore.UpsertBatch(batchRecord);
        }
        catch (Exception persistInputEx)
        {
            try
            {
                _generationJobStore.UpsertBatch(batchRecord with
                {
                    Status = "FailedLocal",
                    ProviderInputFileId = inputFileId,
                    ErrorMessage = $"Failed to persist input file ID '{inputFileId}': {persistInputEx.Message}",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            catch { }

            SafeInvoke(() =>
            {
                _isSubmittingBatch = false;
                ApplyRequestQueueState();
                RefreshRequestQueueVisuals();
                ShowMessageBox(
                    $"Uploaded input file '{inputFileId}', but failed to save state locally: {persistInputEx.Message}. " +
                    "Batch creation was aborted to prevent untracked remote batches. Retain this input file ID for manual cleanup.",
                    "Batch Persistence Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            });
            return;
        }

        BatchSubmissionResult result;
        try
        {
            result = await _imageGenerationProvider.CreateBatchAsync(inputFileId, apiKey).ConfigureAwait(false);
        }
        catch (Exception createEx)
        {
            _generationJobStore.UpsertBatch(batchRecord with
            {
                Status = "FailedLocal",
                ErrorMessage = $"Batch creation failed: {createEx.Message}",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var isInterruption = createEx is TaskCanceledException or TimeoutException or HttpRequestException;
            var targetStatus = isInterruption
                ? GenerationItemStatus.UncertainAfterInterruption
                : GenerationItemStatus.FailedPermanent;
            var errorMsg = isInterruption
                ? $"Batch creation timed out or was interrupted ({createEx.Message}). Remote status is uncertain; check OpenAI dashboard for input file '{inputFileId}' before retrying."
                : createEx.Message;

            var manifestItems = _generationJobStore.GetItemsForManifest(_currentManifest.ManifestFingerprint)
                .ToDictionary(i => i.RequestKey, StringComparer.Ordinal);
            var failedItems = eligible
                .Select(item => manifestItems.TryGetValue(item.RequestKey, out var existing) ? existing : null)
                .Where(i => i != null)
                .Select(i => i! with
                {
                    Status = targetStatus,
                    ErrorCode = isInterruption ? "batch_creation_uncertain" : "batch_creation_failed",
                    ErrorMessage = errorMsg,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                })
                .ToList();
            _generationJobStore.UpsertItems(failedItems);

            SafeInvoke(() =>
            {
                _isSubmittingBatch = false;
                ApplyRequestQueueState();
                RefreshRequestQueueVisuals();
                ShowMessageBox(
                    $"Batch creation failed: {createEx.Message}." + Environment.NewLine + Environment.NewLine +
                    $"Input file ID '{inputFileId}' was uploaded and retained.",
                    "Batch Creation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            });
            return;
        }

        try
        {
            _generationJobStore.UpsertBatch(batchRecord with
            {
                ProviderBatchId = result.ProviderBatchId,
                ProviderInputFileId = inputFileId,
                Status = "Submitted",
                SubmittedCount = eligible.Count,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var manifestItems = _generationJobStore.GetItemsForManifest(_currentManifest!.ManifestFingerprint)
                .ToDictionary(i => i.RequestKey, StringComparer.Ordinal);
            var submittedItems = eligible
                .Select(item => manifestItems.TryGetValue(item.RequestKey, out var existing) ? existing : null)
                .Where(i => i != null)
                .Select(i => i! with
                {
                    Status = GenerationItemStatus.BatchSubmitted,
                    ProviderBatchId = result.ProviderBatchId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                })
                .ToList();
            _generationJobStore.UpsertItems(submittedItems);

            SafeInvoke(() =>
            {
                AddStatus($"Production batch submitted (ID: {result.ProviderBatchId}). Monitoring active.");
                CheckAndStartBatchMonitoring();
                RefreshRequestQueueVisuals();
            });
        }
        catch (Exception persistBatchIdEx)
        {
            SafeInvoke(() =>
            {
                ShowMessageBox(
                    $"Batch was submitted to OpenAI with ID '{result.ProviderBatchId}', but saving state locally failed: {persistBatchIdEx.Message}",
                    "State Save Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
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
                    var hasErrorFile = !string.IsNullOrWhiteSpace(status.ErrorFileId);
                    var isTerminal = string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "expired", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(status.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

                    var ingestionService = new BatchIngestionService(_generationJobStore, _stagingService);

                    if (isTerminal && (hasOutputFile || hasErrorFile))
                    {
                        BatchDownloadResult results;
                        try
                        {
                            results = await _imageGenerationProvider.DownloadBatchResultsAsync(status, apiKey).ConfigureAwait(false);
                        }
                        catch (Exception dlEx)
                        {
                            ingestionService.HandleDownloadInterruption(batch, dlEx);
                            SafeInvoke(() =>
                            {
                                AddStatus($"Batch {batch.ProviderBatchId} download failed: {dlEx.Message}");
                                RefreshRequestQueueVisuals();
                            });
                            continue;
                        }

                        var summary = ingestionService.IngestResults(batch, status, results);

                        SafeInvoke(() =>
                        {
                            AddStatus($"Batch {batch.ProviderBatchId} ended with status '{status.Status}'. Ingested {summary.SuccessCount} ready, {summary.FailureCount} failed, {summary.MissingCustomIds.Count} missing.");
                            RefreshRequestQueueVisuals();
                        });
                    }
                    else if (isTerminal)
                    {
                        ingestionService.IngestResults(batch, status, new BatchDownloadResult(batch.ProviderBatchId, []));

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
