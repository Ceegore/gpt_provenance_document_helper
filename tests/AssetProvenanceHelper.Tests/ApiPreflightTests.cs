using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiPreflightTests : IDisposable
{
    private readonly string _tempDir;

    public ApiPreflightTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_preflight_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private sealed class CountingFakeProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public int GenerateCount;

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            return Task.FromResult(new ImageGenerationCandidate(
                CandidateId: "cand-1",
                CustomId: spec.CustomId,
                RawBytes: [1, 2, 3],
                RawSha256: "raw-sha",
                ProviderWidth: spec.GenerationWidth,
                ProviderHeight: spec.GenerationHeight));
        }

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult completedBatch, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string? LoadSecret(string name) => "sk-test-key";
        public void SaveSecret(string name, string secret) { }
        public void DeleteSecret(string name) { }
    }

    private static AssetRequestItem CreateItem(
        string key,
        string fileName,
        int width,
        int height,
        AlphaRequirement alpha,
        string prompt = "A test prompt")
    {
        return new AssetRequestItem
        {
            RequestKey = key,
            AssetName = Path.GetFileNameWithoutExtension(fileName),
            FileName = fileName,
            Prompt = prompt,
            Width = width,
            Height = height,
            Resolution = $"{width}x{height}",
            Alpha = alpha
        };
    }

    [Fact]
    public void Preflight_AlphaRequired_IsBlockedNotError()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "hero.png", 512, 512, AlphaRequirement.Required);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Errors);
        Assert.Single(result.BlockedAlpha);
        Assert.Empty(result.Eligible);
        Assert.Equal("k1", result.BlockedAlpha[0].RequestKey);
    }

    [Fact]
    public void Preflight_InvalidSize_ProducesError()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        // Ratio 4000x400 is 10:1 > 3:1 max ratio allowed by planner
        var item = CreateItem("k1", "wide.png", 4000, 400, AlphaRequirement.NotRequired);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Single(result.Errors);
        Assert.Equal("invalid_generation_size", result.Errors[0].Code);
        Assert.Empty(result.Eligible);
        Assert.Empty(result.BlockedAlpha);
    }

    [Fact]
    public void Preflight_EmptyPrompt_ProducesError()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.NotRequired, prompt: "   ");

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Single(result.Errors);
        Assert.Equal("empty_prompt", result.Errors[0].Code);
        Assert.Empty(result.Eligible);
    }

    [Fact]
    public void Preflight_Ready_NotEligible()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var stagedFile = Path.Combine(_tempDir, "staged.png");
        File.WriteAllBytes(stagedFile, [1, 2, 3]);

        jobStore.UpsertItem(new GenerationItemRecord(
            ManifestFingerprint: "fp",
            RequestKey: "k1",
            AssetName: "item",
            FileName: "item.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-k1",
            Status: GenerationItemStatus.Ready,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            StagedOutputPath: stagedFile));

        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.NotRequired);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Errors);
        Assert.Empty(result.Eligible);
        Assert.Equal(1, result.AlreadyReadyCount);
    }

    [Fact]
    public void Preflight_Done_NotEligible()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.NotRequired);
        item.IsCompleted = true;

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Eligible);
        Assert.Equal(0, result.TotalPendingCount);
    }

    [Fact]
    public void Preflight_Uncertain_NotEligible()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs.json");
        var jobStore = new GenerationJobStore(jobStorePath);

        jobStore.UpsertItem(new GenerationItemRecord(
            ManifestFingerprint: "fp",
            RequestKey: "k1",
            AssetName: "item",
            FileName: "item.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-k1",
            Status: GenerationItemStatus.UncertainAfterInterruption,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow));

        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.NotRequired);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Eligible);
        Assert.Equal(1, result.UncertainCount);
    }

    [Fact]
    public void Preflight_Opaque_Eligible()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.NotRequired);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Errors);
        Assert.Empty(result.BlockedAlpha);
        Assert.Single(result.Eligible);
    }

    [Fact]
    public void Preflight_UnknownAlpha_Eligible()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var service = new ApiPreflightService(jobStore);
        var item = CreateItem("k1", "item.png", 512, 512, AlphaRequirement.Unknown);

        var result = service.Preflight("fp", [item], new HashSet<string>());

        Assert.Empty(result.Errors);
        Assert.Empty(result.BlockedAlpha);
        Assert.Single(result.Eligible);
    }

    [Fact]
    public void Preflight_OneInvalidOneValid_StartsNoPaidRequest()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var secrets = new FakeSecretStore();
            var provider = new CountingFakeProvider();
            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                secretStore: secrets,
                imageGenerationProvider: provider,
                generationJobStore: jobStore);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? errorDialogShown = null;
                string? errorTitleShown = null;
                MainForm.MessageBoxProvider = (_, msg, title, _, _) =>
                {
                    errorDialogShown = msg;
                    errorTitleShown = title;
                };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_mixed.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "valid.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Valid prompt"
                    },
                    {
                      "filename": "invalid.png",
                      "resolution": "4000x400",
                      "alpha": "not_required",
                      "prompt": "Aspect ratio too wide"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;

                Assert.NotNull(btnImport);
                Assert.NotNull(btnGenerate);

                btnImport.PerformClick();

                // Trigger Generate Now
                btnGenerate.PerformClick();

                // Validation should have blocked before any confirmation or generation
                Assert.NotNull(errorDialogShown);
                Assert.Equal("Preflight validation failed", errorTitleShown);
                Assert.Contains("Cannot start generation because 1 local error(s) were found", errorDialogShown);
                Assert.Equal(0, provider.GenerateCount);

                form.Close();
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }
}
