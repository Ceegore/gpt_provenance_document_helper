using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class RequestQueueStateServiceTests
{
    private static AssetRequestManifest CreateManifest(TestWorkspace workspace)
    {
        var path = Path.Combine(workspace.Root, "requests.json");
        File.WriteAllText(path, """
            {
              "manifestVersion": 2,
              "assets": [
                { "filename": "first.png", "resolution": "512x512", "alpha": "not_required", "prompt": "first" },
                { "filename": "second.png", "resolution": "1024x1024", "alpha": "unknown", "prompt": "second" }
              ]
            }
            """);

        return new AssetRequestManifestService(workspace.CreateValidationService())
            .Load(path, workspace.CreateSettings().AcceptedExtensions);
    }

    [Fact]
    public void SaveThenLoad_RestoresOrderedValidatedSnapshot_WhenSourceWasRemoved()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateRequestQueueStateService();
        var manifest = CreateManifest(workspace);

        service.Save(manifest);
        File.Delete(manifest.SourcePath);

        var restored = service.Load(workspace.CreateSettings().AcceptedExtensions);

        Assert.NotNull(restored);
        Assert.Equal(manifest.SourcePath, restored.SourcePath);
        Assert.Equal(manifest.ManifestFingerprint, restored.ManifestFingerprint);
        Assert.Equal(["first.png", "second.png"], restored.Items.Select(item => item.FileName));
        Assert.All(restored.Items, item => Assert.False(item.IsCompleted));
    }

    [Fact]
    public void Save_IsAtomicAndClearRemovesOnlyTheQueueSnapshot()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateRequestQueueStateService();
        service.Save(CreateManifest(workspace));

        Assert.True(File.Exists(workspace.RequestQueueStatePath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));

        service.Clear();

        Assert.False(File.Exists(workspace.RequestQueueStatePath));
        Assert.Null(service.Load(workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void Load_RejectsTamperedRequestKeyAndKeepsSnapshotForExplicitClear()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateRequestQueueStateService();
        service.Save(CreateManifest(workspace));

        var json = File.ReadAllText(workspace.RequestQueueStatePath)
            .Replace("\"RequestKey\": \"", "\"RequestKey\": \"tampered", StringComparison.Ordinal);
        File.WriteAllText(workspace.RequestQueueStatePath, json);

        Assert.Throws<InvalidDataException>(() => service.Load(workspace.CreateSettings().AcceptedExtensions));
        Assert.True(File.Exists(workspace.RequestQueueStatePath));
    }
}
