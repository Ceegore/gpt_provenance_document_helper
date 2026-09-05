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

    [Fact]
    public void UpsertItems_And_GetItemsForManifest_RoundtripsCorrectly()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var item1 = new GenerationItemRecord("fpA", "rk1", "a1", "a1.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c1", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var item2 = new GenerationItemRecord("fpA", "rk2", "a2", "a2.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c2", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var item3 = new GenerationItemRecord("fpB", "rk3", "a3", "a3.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c3", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        store.UpsertItems([item1, item2, item3]);

        var fpAItems = store.GetItemsForManifest("fpA");
        Assert.Equal(2, fpAItems.Count);

        var fpBItems = store.GetItemsForManifest("fpB");
        Assert.Single(fpBItems);
    }

    [Fact]
    public void GetItemsForBatch_And_GetActiveBatches_FiltersAccurately()
    {
        var store = new GenerationJobStore(_stateFilePath);

        var item1 = new GenerationItemRecord("fpA", "rk1", "a1", "a1.png", GenerationMode.Batch, "p", "m", "q", 512, 512, 816, 816, "c1", GenerationItemStatus.BatchSubmitted, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BatchId: "b1");
        var item2 = new GenerationItemRecord("fpA", "rk2", "a2", "a2.png", GenerationMode.Batch, "p", "m", "q", 512, 512, 816, 816, "c2", GenerationItemStatus.BatchSubmitted, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BatchId: "b2");

        store.UpsertItems([item1, item2]);

        var batch1Items = store.GetItemsForBatch("b1");
        Assert.Single(batch1Items);
        Assert.Equal("rk1", batch1Items[0].RequestKey);

        var activeBatch = new GenerationBatchRecord("b1", "fpA", "p", "m", "q", ["rk1"], "in_progress", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderBatchId: "pb1");
        var completedBatch = new GenerationBatchRecord("b2", "fpA", "p", "m", "q", ["rk2"], "completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderBatchId: "pb2");

        store.UpsertBatch(activeBatch);
        store.UpsertBatch(completedBatch);

        var active = store.GetActiveBatches();
        Assert.Single(active);
        Assert.Equal("b1", active[0].LocalBatchId);
    }

    [Fact]
    public void UpsertItem_UpdatesExistingItemAtIndex0()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var item1 = new GenerationItemRecord("fpA", "rk1", "a1", "a1.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c1", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        store.UpsertItem(item1);
        var updated = item1 with { Status = GenerationItemStatus.Ready };
        store.UpsertItem(updated);

        var loaded = store.Load();
        Assert.Single(loaded.Items);
        Assert.Equal(GenerationItemStatus.Ready, loaded.Items[0].Status);
    }

    [Fact]
    public void UpsertBatch_UpdatesExistingBatchAtIndex0()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var batch1 = new GenerationBatchRecord("b1", "fpA", "p", "m", "q", ["rk1"], "validating", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderBatchId: "pb1");

        store.UpsertBatch(batch1);
        var updated = batch1 with { Status = "completed" };
        store.UpsertBatch(updated);

        var loaded = store.Load();
        Assert.Single(loaded.Batches);
        Assert.Equal("completed", loaded.Batches[0].Status);
    }

    [Fact]
    public void GetItem_RetrievesMatchingOrReturnsNull()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var item1 = new GenerationItemRecord("fp1", "rk1", "a1", "a1.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c1", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var item2 = new GenerationItemRecord("fp2", "rk2", "a2", "a2.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c2", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        store.UpsertItems([item1, item2]);

        Assert.NotNull(store.GetItem("fp1", "rk1"));
        Assert.Null(store.GetItem("fp1", "rk2"));
        Assert.Null(store.GetItem("fp2", "rk1"));
        Assert.Null(store.GetItem("non-existent", "non-existent"));
    }

    [Fact]
    public void GetBatch_RetrievesMatchingOrReturnsNull()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var batch1 = new GenerationBatchRecord("b1", "fp1", "p", "m", "q", ["rk1"], "in_progress", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderBatchId: "pb1");

        store.UpsertBatch(batch1);

        Assert.NotNull(store.GetBatch("b1"));
        Assert.Null(store.GetBatch("non-existent"));
    }

    [Fact]
    public void GetItemsForManifest_Whitespace_ThrowsArgumentException()
    {
        var store = new GenerationJobStore(_stateFilePath);
        Assert.Throws<ArgumentException>(() => store.GetItemsForManifest(""));
        Assert.Throws<ArgumentException>(() => store.GetItemsForManifest("   "));
    }

    [Fact]
    public void SaveState_CreatesDirectory_IfMissing()
    {
        var subDir = Path.Combine(_tempDirectory, "subdir_" + Guid.NewGuid().ToString("N"));
        var fileInSubDir = Path.Combine(subDir, "jobs.json");
        var store = new GenerationJobStore(fileInSubDir);

        var item = new GenerationItemRecord("fp", "rk", "a", "a.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.UpsertItem(item);

        Assert.True(File.Exists(fileInSubDir));
    }

    [Fact]
    public void DefaultConstructor_SetsDefaultStatePathInAppData()
    {
        var store = new GenerationJobStore();
        Assert.NotNull(store.StatePath);
        Assert.Contains("Ceegore", store.StatePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AssetProvenanceHelper", store.StatePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("generation-jobs.json", store.StatePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_CorruptedJson_ThrowsInvalidDataException()
    {
        File.WriteAllText(_stateFilePath, "{ this is not valid json");
        var store = new GenerationJobStore(_stateFilePath);
        var ex = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains("Failed to deserialize generation jobs", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Load_CaseInsensitiveProperties_ParsesSuccessfully()
    {
        var json = """
        {
            "BATCHES": [],
            "ITEMS": [
                {
                    "MANIFESTFINGERPRINT": "fp_case",
                    "REQUESTKEY": "rk_case",
                    "ASSETNAME": "a",
                    "OUTPUTFILENAME": "a.png",
                    "MODE": 0,
                    "PROMPT": "p",
                    "MODEL": "m",
                    "QUALITY": "q",
                    "TARGETWIDTH": 512,
                    "TARGETHEIGHT": 512,
                    "GENERATIONWIDTH": 816,
                    "GENERATIONHEIGHT": 816,
                    "CUSTOMID": "c",
                    "STATUS": 0,
                    "SUBMITTEDATUTC": "2026-01-01T00:00:00Z",
                    "UPDATEDATUTC": "2026-01-01T00:00:00Z"
                }
            ]
        }
        """;
        File.WriteAllText(_stateFilePath, json);
        var store = new GenerationJobStore(_stateFilePath);
        var loaded = store.Load();
        Assert.Single(loaded.Items);
        Assert.Equal("fp_case", loaded.Items[0].ManifestFingerprint);
    }

    [Fact]
    public void NullArgumentChecks_ThrowArgumentNullException()
    {
        var store = new GenerationJobStore(_stateFilePath);
        Assert.Throws<ArgumentNullException>("state", () => store.Save(null!));
        Assert.Throws<ArgumentNullException>("item", () => store.UpsertItem(null!));
        Assert.Throws<ArgumentNullException>("items", () => store.UpsertItems(null!));
        Assert.Throws<ArgumentNullException>("batch", () => store.UpsertBatch(null!));
    }

    [Fact]
    public void UpsertItems_EmptyList_DoesNotSave()
    {
        var nonExistentPath = Path.Combine(_tempDirectory, "no_save.json");
        var store = new GenerationJobStore(nonExistentPath);
        var saveCalled = false;
        GenerationJobStore.OnBeforeSaveCoreForTests = _ => saveCalled = true;
        try
        {
            store.UpsertItems(Array.Empty<GenerationItemRecord>());
            Assert.False(saveCalled);
            Assert.False(File.Exists(nonExistentPath));
        }
        finally
        {
            GenerationJobStore.OnBeforeSaveCoreForTests = null;
        }
    }


    [Fact]
    public void UpsertItem_SameManifestDifferentRequestKey_AppendsInsteadOfReplacing()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var item1 = new GenerationItemRecord("fpSame", "rk1", "a1", "a1.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c1", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var item2 = new GenerationItemRecord("fpSame", "rk2", "a2", "a2.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c2", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        store.UpsertItem(item1);
        store.UpsertItem(item2);

        var loaded = store.Load();
        Assert.Equal(2, loaded.Items.Count);
        Assert.Contains(loaded.Items, i => i.RequestKey == "rk1");
        Assert.Contains(loaded.Items, i => i.RequestKey == "rk2");
    }

    [Fact]
    public void Save_FailureDuringMove_CleansUpTempFileAndRethrows()
    {
        // Make statePath an existing directory so File.Move fails
        var dirStatePath = Path.Combine(_tempDirectory, "directoryAsStatePath");
        Directory.CreateDirectory(dirStatePath);
        var store = new GenerationJobStore(dirStatePath);

        var item = new GenerationItemRecord("fp", "rk", "a", "a.png", GenerationMode.Direct, "p", "m", "q", 512, 512, 816, 816, "c", GenerationItemStatus.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.ThrowsAny<Exception>(() => store.UpsertItem(item));

        var tempFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void GetActiveBatches_ExcludesFailedExpiredCancelled()
    {
        var store = new GenerationJobStore(_stateFilePath);
        var now = DateTimeOffset.UtcNow;
        var bActive = new GenerationBatchRecord("bActive", "fp", "p", "m", "q", ["rk1"], "in_progress", now, now, ProviderBatchId: "pb1");
        var bFailed = new GenerationBatchRecord("bFailed", "fp", "p", "m", "q", ["rk2"], "failed", now, now, ProviderBatchId: "pb2");
        var bExpired = new GenerationBatchRecord("bExpired", "fp", "p", "m", "q", ["rk3"], "expired", now, now, ProviderBatchId: "pb3");
        var bCancelled = new GenerationBatchRecord("bCancelled", "fp", "p", "m", "q", ["rk4"], "cancelled", now, now, ProviderBatchId: "pb4");

        store.UpsertBatch(bActive);
        store.UpsertBatch(bFailed);
        store.UpsertBatch(bExpired);
        store.UpsertBatch(bCancelled);

        var active = store.GetActiveBatches();
        Assert.Single(active);
        Assert.Equal("bActive", active[0].LocalBatchId);
    }
}
