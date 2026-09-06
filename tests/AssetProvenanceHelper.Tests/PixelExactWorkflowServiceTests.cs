using System.Security.Cryptography;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class PixelExactWorkflowServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aph_pixel_" + Guid.NewGuid().ToString("N"));
    private readonly QueuePromptWorkflowParser _parser = new();

    public PixelExactWorkflowServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_CanonicalSeedCollectionAndMapping_PreservesTheDocumentedContract()
    {
        var seed = _parser.Parse("Seed. FLOWMETA: SERIE=scene_a; SERIENGROESSE=3; NEXT=Ref2. PROZESSMARKER: Einzeln");
        var collection = _parser.Parse("Collection. FLOWMETA: SERIE=scene_a; OUTPUT_COUNT=2. PROZESSMARKER: Ref2");
        var mapping = _parser.Parse("Map. FLOWMETA: SERIE=scene_a; OUTPUT_INDEX=2; MASTER=Ref2. PROZESSMARKER: AusRef2");

        Assert.Equal(QueuePromptWorkflowKind.PixelExactSeed, seed.Kind);
        Assert.True(seed.HasCanonicalMetadata);
        Assert.Equal("scene_a", seed.SeriesId);
        Assert.Equal(2, seed.PixelOutputCount);
        Assert.Equal(3, seed.TotalPhases);

        Assert.Equal(QueuePromptWorkflowKind.PixelExactRef, collection.Kind);
        Assert.Equal(2, collection.PixelOutputCount);
        Assert.Equal(1, collection.OutputIndex);

        Assert.Equal(QueuePromptWorkflowKind.PixelExactOutput, mapping.Kind);
        Assert.Equal(2, mapping.PixelOutputCount);
        Assert.Equal(2, mapping.OutputIndex);
    }

    [Fact]
    public void Parse_CanonicalContradiction_FailsClosed()
    {
        var result = _parser.Parse("FLOWMETA: SERIE=scene_a; OUTPUT_COUNT=3. PROZESSMARKER: Ref2");

        Assert.Equal(QueuePromptWorkflowKind.Invalid, result.Kind);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void BatchState_StagesExactOrderedFiles_AndPersistsTheirHashes()
    {
        var sourceOne = Path.Combine(_root, "one.png");
        var sourceTwo = Path.Combine(_root, "two.png");
        File.WriteAllBytes(sourceOne, [1, 2, 3, 4]);
        File.WriteAllBytes(sourceTwo, [5, 6, 7, 8]);

        var service = new PixelExactBatchStateService(
            Path.Combine(_root, "pixel-exact-batch-state.json"),
            Path.Combine(_root, "pixel-exact"));
        var manifest = CreateManifest();
        var collection = manifest.Items[1];
        var metadata = _parser.Parse(collection.Prompt);
        var state = service.CreateCollectionState(metadata, manifest, collection);

        var staged = service.StageBundle(state, [sourceOne, sourceTwo], null);
        var reloaded = service.Load();

        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Outputs.Count);
        Assert.Equal(1, reloaded.Outputs[0].OutputIndex);
        Assert.Equal(2, reloaded.Outputs[1].OutputIndex);
        Assert.True(File.Exists(reloaded.Outputs[0].StagedPath));
        Assert.True(File.Exists(reloaded.Outputs[1].StagedPath));
        Assert.Equal(Hash(sourceOne), reloaded.Outputs[0].Sha256);
        Assert.Equal(Hash(sourceTwo), reloaded.Outputs[1].Sha256);
        service.ValidateStagedAuthority(reloaded);
    }

    [Fact]
    public void RequestProgress_KeepsSeparateManifestsAndMigratesSchemaOne()
    {
        var path = Path.Combine(_root, "request-progress.json");
        var progress = new RequestProgressService(path);
        progress.Save("manifest-a", ["a-1"]);
        progress.Save("manifest-b", ["b-1", "b-2"]);

        Assert.Equal(["a-1"], progress.LoadForManifest("manifest-a").OrderBy(value => value));
        Assert.Equal(["b-1", "b-2"], progress.LoadForManifest("manifest-b").OrderBy(value => value));

        File.WriteAllText(path, "{\"SchemaVersion\":1,\"ManifestFingerprint\":\"legacy\",\"CompletedRequestKeys\":[\"done\"]}");
        Assert.Equal(["done"], progress.LoadForManifest("legacy"));
    }

    [Fact]
    public void BatchState_CreatesAnExplicitManualCollection_WhenNoMetadataExists()
    {
        var service = new PixelExactBatchStateService(
            Path.Combine(_root, "pixel-exact-batch-state.json"),
            Path.Combine(_root, "pixel-exact"));
        var manifest = CreateManifest();

        var state = service.CreateManualLocalCollectionState(manifest, manifest.Items[1], 2);

        Assert.False(state.HasCanonicalSeriesIdentity);
        Assert.Equal(2, state.BundleCount);
        Assert.Equal(3, state.TotalPhases);
        Assert.Equal(manifest.Items[1].RequestKey, state.CollectionRequestKey);
        Assert.Equal(manifest.Items[1].Prompt, state.CollectionGenerationPrompt);
    }

    [Fact]
    public void SeriesProgress_UsesOnlyCanonicalSeries_AndKeepsItsCompletedContext()
    {
        var manifest = CreateManifest();
        manifest.Items[0].IsCompleted = true;
        var service = new QueueSeriesProgressService(_parser);

        var series = Assert.Single(service.Summarize(manifest.Items, new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("scene_a", series.SeriesId);
        Assert.Equal(1, series.CompletedPhases);
        Assert.Equal(3, series.TotalPhases);
        Assert.True(series.IsOpen);
    }

    [Fact]
    public void PhasePreview_ListsTheExactOldestToNewestTargetMapping()
    {
        var text = MainForm.BuildPixelExactPhasePreviewText(
        [
            new MainForm.PixelExactPhasePreview(1, 2, "morning.png", "scene_morning", "768x1024"),
            new MainForm.PixelExactPhasePreview(2, 2, "night.png", "scene_night", "768x1024")
        ]);

        Assert.Contains("1/2: morning.png  →  scene_morning (768x1024)", text);
        Assert.Contains("2/2: night.png  →  scene_night (768x1024)", text);
        Assert.Contains("oldest-to-newest", text);
    }

    private AssetRequestManifest CreateManifest()
    {
        var seedPrompt = "Seed. FLOWMETA: SERIE=scene_a; SERIENGROESSE=3; NEXT=Ref2. PROZESSMARKER: Einzeln";
        var collectionPrompt = "Collection. FLOWMETA: SERIE=scene_a; OUTPUT_COUNT=2. PROZESSMARKER: Ref2";
        var mappingPrompt = "Map. FLOWMETA: SERIE=scene_a; OUTPUT_INDEX=2; MASTER=Ref2. PROZESSMARKER: AusRef2";
        return new AssetRequestManifest
        {
            Version = 2,
            SourcePath = Path.Combine(_root, "manifest.json"),
            ManifestFingerprint = "manifest-a",
            Items =
            [
                Item("scene_master", "seed-key", seedPrompt),
                Item("scene_state_two", "collection-key", collectionPrompt),
                Item("scene_state_three", "mapping-key", mappingPrompt)
            ]
        };
    }

    private static AssetRequestItem Item(string assetName, string requestKey, string prompt) => new()
    {
        FileName = assetName + ".png",
        AssetName = assetName,
        Width = 768,
        Height = 1024,
        Resolution = "768x1024",
        Prompt = prompt,
        RequestKey = requestKey
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
