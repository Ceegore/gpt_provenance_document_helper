using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationJobStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _stateFilePath;

    public GenerationJobStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aph_store_tests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup failure
        }
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyState()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var state = store.Load();

        Assert.NotNull(state);
        Assert.Empty(state.Items);
        Assert.Empty(state.Batches);
    }

    [Fact]
    public void SaveAndLoad_RoundtripsSuccessfully()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp1",
            RequestKey: "rk1",
            AssetName: "icon",
            FileName: "icon.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp1-rk1",
            Status: GenerationItemStatus.Ready,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            StagedOutputPath: "C:\\test\\staged.png");

        store.UpsertItem(item);

        var loadedItem = store.GetItem("fp1", "rk1");
        Assert.NotNull(loadedItem);
        Assert.Equal("icon", loadedItem.AssetName);
        Assert.Equal(GenerationItemStatus.Ready, loadedItem.Status);
        Assert.Equal("C:\\test\\staged.png", loadedItem.StagedOutputPath);
    }

    [Fact]
    public void Save_DoesNotContainApiKey()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp1",
            RequestKey: "rk1",
            AssetName: "icon",
            FileName: "icon.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp1-rk1",
            Status: GenerationItemStatus.Ready,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        store.UpsertItem(item);

        var json = File.ReadAllText(_stateFilePath);
        Assert.DoesNotContain("sk-", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_DirectInFlight_TransitionsToUncertainAfterInterruption()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp1",
            RequestKey: "rk1",
            AssetName: "icon",
            FileName: "icon.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp1-rk1",
            Status: GenerationItemStatus.DirectInFlight,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        store.UpsertItem(item);

        // Reload fresh from disk simulating application restart
        var reloadedStore = new GenerationJobStore(_stateFilePath);
        reloadedStore.RecoverInterruptedJobsOnStartup();
        var loadedItem = reloadedStore.GetItem("fp1", "rk1");

        Assert.NotNull(loadedItem);
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, loadedItem.Status);
        Assert.Contains("interrupted", loadedItem.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_InterruptedBatchPreparation_TransitionsBatchToFailedAndItemsToUncertain()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var batch = new GenerationBatchRecord(
            LocalBatchId: "b-test-1",
            ManifestFingerprint: "fp1",
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: new[] { "rk1" },
            Status: "preparing",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var item = new GenerationItemRecord(
            ManifestFingerprint: "fp1",
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
            CustomId: "aph-fp1-rk1",
            Status: GenerationItemStatus.BatchPreparing,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: "b-test-1");

        store.UpsertBatch(batch);
        store.UpsertItem(item);

        // Active batches before reload has no provider batch id
        Assert.Empty(store.GetActiveBatches());

        // Reload store and perform startup recovery
        var reloadedStore = new GenerationJobStore(_stateFilePath);
        reloadedStore.RecoverInterruptedJobsOnStartup();
        var loadedBatch = reloadedStore.GetBatch("b-test-1");
        var loadedItem = reloadedStore.GetItem("fp1", "rk1");

        Assert.NotNull(loadedBatch);
        Assert.Equal("failed", loadedBatch.Status);
        Assert.Contains("interrupted", loadedBatch.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(loadedItem);
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, loadedItem.Status);
    }
}
