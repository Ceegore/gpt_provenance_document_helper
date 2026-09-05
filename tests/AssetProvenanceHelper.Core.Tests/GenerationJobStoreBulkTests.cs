using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationJobStoreBulkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;

    public GenerationJobStoreBulkTests()
    {
        GenerationJobStore.OnBeforeSaveCoreForTests = null;
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_jobstore_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "jobs.json");
    }

    public void Dispose()
    {
        GenerationJobStore.OnBeforeSaveCoreForTests = null;
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static GenerationItemRecord CreateTestRecord(string fingerprint, string key, string assetName = "asset") =>
        new(
            ManifestFingerprint: fingerprint,
            RequestKey: key,
            AssetName: assetName,
            FileName: $"{assetName}.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: $"custom-{key}",
            Status: GenerationItemStatus.Pending,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void JobStore_UpsertItems_SingleFileWrite()
    {
        var store = new GenerationJobStore(_statePath);
        var saveCount = 0;
        GenerationJobStore.OnBeforeSaveCoreForTests = _ => saveCount++;

        var items = Enumerable.Range(1, 10)
            .Select(i => CreateTestRecord("fp-1", $"key-{i}", $"asset-{i}"))
            .ToList();

        store.UpsertItems(items);

        // Crucial: exactly ONE disk write for all 10 items
        Assert.Equal(1, saveCount);

        var loaded = store.GetItemsForManifest("fp-1");
        Assert.Equal(10, loaded.Count);
    }

    [Fact]
    public void JobStore_GetItemsForManifest_ReturnsAllMatching()
    {
        var store = new GenerationJobStore(_statePath);

        var itemsA = Enumerable.Range(1, 3)
            .Select(i => CreateTestRecord("fp-A", $"key-A-{i}"));
        var itemsB = Enumerable.Range(1, 2)
            .Select(i => CreateTestRecord("fp-B", $"key-B-{i}"));

        store.UpsertItems(itemsA.Concat(itemsB));

        var resultsA = store.GetItemsForManifest("fp-A");
        Assert.Equal(3, resultsA.Count);
        Assert.All(resultsA, item => Assert.Equal("fp-A", item.ManifestFingerprint));

        var resultsB = store.GetItemsForManifest("fp-B");
        Assert.Equal(2, resultsB.Count);
        Assert.All(resultsB, item => Assert.Equal("fp-B", item.ManifestFingerprint));

        var resultsNonExistent = store.GetItemsForManifest("fp-unknown");
        Assert.Empty(resultsNonExistent);
    }

    [Fact]
    public void JobStore_BulkUpsert_MaintainsIntegrityOnCrashSeam()
    {
        var store = new GenerationJobStore(_statePath);

        // Seed 2 valid items
        var initialItems = new[]
        {
            CreateTestRecord("fp-base", "seed-1"),
            CreateTestRecord("fp-base", "seed-2")
        };
        store.UpsertItems(initialItems);

        // Now attempt bulk upsert that crashes right before writing temp file / atomic move
        GenerationJobStore.OnBeforeSaveCoreForTests = _ =>
            throw new IOException("Simulated disk I/O crash during bulk save");

        var newItems = Enumerable.Range(1, 5)
            .Select(i => CreateTestRecord("fp-base", $"crash-{i}"))
            .ToList();

        Assert.Throws<IOException>(() => store.UpsertItems(newItems));

        // Restore save hook
        GenerationJobStore.OnBeforeSaveCoreForTests = null;

        // Fresh instance reading the file from disk
        var freshStore = new GenerationJobStore(_statePath);
        var existing = freshStore.GetItemsForManifest("fp-base");

        // Integrity preserved: original 2 items exist, corrupt or partial data not saved
        Assert.Equal(2, existing.Count);
        Assert.Contains(existing, i => i.RequestKey == "seed-1");
        Assert.Contains(existing, i => i.RequestKey == "seed-2");
    }

    [Fact]
    public void JobStore_UpsertItems_UpdatesExistingAndSkipsNulls()
    {
        var store = new GenerationJobStore(_statePath);

        // Seed 1 item
        store.UpsertItems([CreateTestRecord("fp-update", "k1", "original-name")]);

        // Now upsert updated version of k1, plus a null item, plus a new item
        var updated = CreateTestRecord("fp-update", "k1", "updated-name");
        var newItem = CreateTestRecord("fp-update", "k2", "new-name");

        store.UpsertItems([null!, updated, newItem]);

        var items = store.GetItemsForManifest("fp-update");
        Assert.Equal(2, items.Count);
        Assert.Equal("updated-name", items.Single(i => i.RequestKey == "k1").AssetName);
        Assert.Equal("new-name", items.Single(i => i.RequestKey == "k2").AssetName);
    }
}
