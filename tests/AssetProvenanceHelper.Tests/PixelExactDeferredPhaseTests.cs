using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// A canonical Pixel-Exact series may be split over several import manifests,
/// so a later output index legitimately has no target row in the currently
/// imported manifest part. These tests pin the pure, non-UI contracts that
/// make that deferral safe: the operator-facing report and preview text, and
/// the durable journal's round-trip and fail-closed validation rules.
/// </summary>
public sealed class PixelExactDeferredPhaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aph_pixel_deferred_" + Guid.NewGuid().ToString("N"));

    public PixelExactDeferredPhaseTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BuildPixelExactDeferredPhaseReport_NoDeferredIndexes_ReturnsEmptyCollection()
    {
        var lines = MainForm.BuildPixelExactDeferredPhaseReport(
            "scene_a",
            Array.Empty<int>(),
            3,
            Array.Empty<string>());

        Assert.Empty(lines);
    }

    [Fact]
    public void BuildPixelExactDeferredPhaseReport_SingleDeferredIndex_IncludesSeriesIndexFileNameAndContinuationHint()
    {
        var lines = MainForm.BuildPixelExactDeferredPhaseReport(
            "scene_a",
            [3],
            3,
            ["phase2.png"]);
        var text = string.Join(Environment.NewLine, lines);

        Assert.Contains("scene_a", text, StringComparison.Ordinal);
        Assert.Contains("output 3/3", text, StringComparison.Ordinal);
        Assert.Contains("phase2.png", text, StringComparison.Ordinal);
        Assert.Contains("continuation manifest", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPixelExactDeferredPhaseReport_MultipleDeferredIndexes_PairsFileNamesInOrderAndFillsUnknownForShorterList()
    {
        var lines = MainForm.BuildPixelExactDeferredPhaseReport(
            "scene_a",
            [2, 5],
            5,
            ["only-one.png"]);
        var text = string.Join(Environment.NewLine, lines);

        Assert.Contains("Deferred output 2/5: only-one.png", text, StringComparison.Ordinal);
        Assert.Contains("Deferred output 5/5: (unknown)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPixelExactPhasePreviewText_DeferredPhase_MentionsDeferredAndNoTargetRowAndDownloadFolderNote()
    {
        MainForm.PixelExactPhasePreview[] phases =
        [
            new MainForm.PixelExactPhasePreview(1, 2, "morning.png", "scene_morning", "768x1024"),
            new MainForm.PixelExactPhasePreview(2, 2, "night.png", string.Empty, string.Empty, IsDeferred: true)
        ];

        var text = MainForm.BuildPixelExactPhasePreviewText(phases);

        Assert.Contains("scene_morning (768x1024)", text, StringComparison.Ordinal);
        Assert.Contains("deferred", text, StringComparison.Ordinal);
        Assert.Contains("no target row", text, StringComparison.Ordinal);
        Assert.Contains("Image Download Folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPixelExactPhasePreviewText_NoDeferredPhases_OmitsImageDownloadFolderNote()
    {
        MainForm.PixelExactPhasePreview[] phases =
        [
            new MainForm.PixelExactPhasePreview(1, 1, "morning.png", "scene_morning", "768x1024")
        ];

        var text = MainForm.BuildPixelExactPhasePreviewText(phases);

        Assert.Contains("scene_morning (768x1024)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Image Download Folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPixelExactPhasePreviewText_EmptyPhaseList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MainForm.BuildPixelExactPhasePreviewText(Array.Empty<MainForm.PixelExactPhasePreview>()));
    }

    [Fact]
    public void JournalRoundTrip_DeferredLastOutputWithCompletedOthers_SurvivesSaveAndLoad()
    {
        var service = CreateService();
        var state = CreateStagedState(service, outputCount: 3);
        var ordered = state.Outputs.OrderBy(output => output.OutputIndex).ToArray();

        for (var index = 0; index < ordered.Length - 1; index++)
        {
            var output = ordered[index];
            output.ManifestFingerprint = "manifest-fingerprint";
            output.RequestKey = $"request-{output.OutputIndex}";
            output.AssetName = $"asset-{output.OutputIndex}";
            output.ExpectedCommitSession = new AssetSession();
            output.AssetFolderPath = Path.Combine(_root, $"asset-{output.OutputIndex}");
            output.State = PixelExactOutputCommitState.QueueCompleted;
        }

        var lastOutput = ordered[^1];
        lastOutput.DeferredNoTargetRow = true;
        // Left at its default State (Staged) and without any commit authority.

        state.Completed = true;
        service.Save(state);

        var reloaded = service.Load(state.SeriesId);

        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Completed);
        var deferred = Assert.Single(reloaded.Outputs, output => output.DeferredNoTargetRow);
        Assert.Equal(lastOutput.OutputIndex, deferred.OutputIndex);
        Assert.Equal(PixelExactOutputCommitState.Staged, deferred.State);
        Assert.All(
            reloaded.Outputs.Where(output => !output.DeferredNoTargetRow),
            output => Assert.Equal(PixelExactOutputCommitState.QueueCompleted, output.State));
    }

    [Fact]
    public void ValidateStateStructure_DeferredOutputCarryingCommitAuthority_FailsClosed()
    {
        var service = CreateService();
        var state = CreateStagedState(service, outputCount: 2);
        var output = state.Outputs[0];
        output.DeferredNoTargetRow = true;
        output.RequestKey = "should-not-be-here";

        Assert.Throws<InvalidDataException>(() => service.ValidateStateStructure(state));
    }

    [Fact]
    public void ValidateStateStructure_DeferredOutputWithNonStagedState_FailsClosed()
    {
        var service = CreateService();
        var state = CreateStagedState(service, outputCount: 2);
        var output = state.Outputs[0];
        output.ManifestFingerprint = "manifest-fingerprint";
        output.RequestKey = "request-1";
        output.AssetName = "asset-1";
        output.ExpectedCommitSession = new AssetSession();
        output.State = PixelExactOutputCommitState.CommitInProgress;
        output.DeferredNoTargetRow = true;

        Assert.Throws<InvalidDataException>(() => service.ValidateStateStructure(state));
    }

    [Fact]
    public void ValidateStateStructure_CompletedWithStagedOutputAndNoDeferralFlag_FailsClosed()
    {
        var service = CreateService();
        var state = CreateStagedState(service, outputCount: 2);
        // Every output is left at its freshly staged default: State == Staged
        // and DeferredNoTargetRow == false.
        state.Completed = true;

        Assert.Throws<InvalidDataException>(() => service.Save(state));
    }

    private PixelExactBatchStateService CreateService() => new(
        Path.Combine(_root, "pixel-exact-batch-state.json"),
        Path.Combine(_root, "pixel-exact-staging"));

    private PixelExactBatchState CreateStagedState(PixelExactBatchStateService service, int outputCount)
    {
        var manifest = CreateManifest();
        var request = manifest.Items[0];
        var state = service.CreateManualLocalCollectionState(manifest, request, outputCount);

        var sources = new List<string>();
        for (var index = 0; index < outputCount; index++)
        {
            var path = Path.Combine(_root, $"source-{index}.png");
            File.WriteAllBytes(path, [(byte)(index + 1), 0x10, 0x20]);
            sources.Add(path);
        }

        return service.StageBundle(state, sources, null);
    }

    private AssetRequestManifest CreateManifest() => new()
    {
        Version = 2,
        SourcePath = Path.Combine(_root, "manifest.json"),
        ManifestFingerprint = "manifest-fingerprint",
        Items = [Item("scene_collection", "collection-key", "A manual Pixel-Exact collection prompt.")]
    };

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
}
