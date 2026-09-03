using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationRecoveryTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _stateFilePath;

    public GenerationRecoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aph_recovery_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _stateFilePath = Path.Combine(_tempDirectory, "generation-jobs.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    [Fact]
    public void Load_DirectInFlight_ConvertsToUncertainAfterInterruption()
    {
        var store1 = new GenerationJobStore(_stateFilePath);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp",
            RequestKey: "rk",
            AssetName: "enemy",
            FileName: "enemy.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk",
            Status: GenerationItemStatus.DirectInFlight,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        store1.Save(new GenerationState { Items = [item] });

        // Simulate app restart: fresh store loading file and executing startup recovery
        var store2 = new GenerationJobStore(_stateFilePath);
        store2.RecoverInterruptedJobsOnStartup();
        var loaded = store2.Load();

        Assert.Single(loaded.Items);
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, loaded.Items[0].Status);
        Assert.Contains("uncertain", loaded.Items[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ActiveBatches_ResumesActiveMonitoring()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var batch1 = new GenerationBatchRecord(
            LocalBatchId: "b1",
            ManifestFingerprint: "fp",
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: ["rk1", "rk2"],
            Status: "in_progress",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            ProviderBatchId: "batch_remote_123");

        var batch2 = new GenerationBatchRecord(
            LocalBatchId: "b2",
            ManifestFingerprint: "fp",
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: ["rk3"],
            Status: "completed",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            ProviderBatchId: "batch_remote_456");

        store.UpsertBatch(batch1);
        store.UpsertBatch(batch2);

        var active = store.GetActiveBatches();
        Assert.Single(active);
        Assert.Equal("b1", active[0].LocalBatchId);
        Assert.Equal("batch_remote_123", active[0].ProviderBatchId);
    }

    [Fact]
    public void RecoverInterruptedJobsOnStartup_InterruptedPreparingBatch_MarksBatchFailedAndItemsUncertain()
    {
        var store1 = new GenerationJobStore(_stateFilePath);

        var batch = new GenerationBatchRecord(
            LocalBatchId: "b_prep",
            ManifestFingerprint: "fp",
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: ["rk1"],
            Status: "preparing",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            ProviderBatchId: null);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp",
            RequestKey: "rk1",
            AssetName: "icon",
            FileName: "icon.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk1",
            Status: GenerationItemStatus.BatchSubmitted,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: "b_prep");

        store1.Save(new GenerationState { Batches = [batch], Items = [item] });

        var store2 = new GenerationJobStore(_stateFilePath);
        store2.RecoverInterruptedJobsOnStartup();

        var loaded = store2.Load();
        Assert.Equal("failed", loaded.Batches[0].Status);
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, loaded.Items[0].Status);
    }
}
