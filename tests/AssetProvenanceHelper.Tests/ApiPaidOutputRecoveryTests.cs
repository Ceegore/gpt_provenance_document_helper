using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiPaidOutputRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public ApiPaidOutputRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_recovery_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = null;
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

    private static byte[] CreateTestPng(int width, int height, Color color)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private sealed class ControllableProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public int GenerateCount;
        public byte[]? GeneratedRawBytes = null;
        public string? GeneratedRawSha256 = null;
        public Exception? ExceptionToThrowOnGenerate = null;

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            if (ExceptionToThrowOnGenerate != null)
            {
                throw ExceptionToThrowOnGenerate;
            }

            var bytes = GeneratedRawBytes ?? CreateTestPng(spec.GenerationWidth, spec.GenerationHeight, Color.Blue);
            var sha = GeneratedRawSha256 ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            return Task.FromResult(new ImageGenerationCandidate(
                CandidateId: "cand-" + Guid.NewGuid().ToString("N"),
                CustomId: spec.CustomId,
                RawBytes: bytes,
                RawSha256: sha,
                ProviderWidth: spec.GenerationWidth,
                ProviderHeight: spec.GenerationHeight,
                ProviderRequestId: "req-test-123"));
        }

        public Task<string> UploadBatchInputFileAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchSubmissionResult> CreateBatchAsync(string inputFileId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult completedBatch, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string? LoadSecret(string name) => "sk-test-secret-12345";
        public void SaveSecret(string name, string secret) { }
        public void DeleteSecret(string name) { }
    }

    private (MainForm Form, ControllableProvider Provider, GenerationJobStore JobStore, GeneratedImageStagingService Staging) CreateTestForm(TestWorkspace workspace)
    {
        var settings = workspace.CreateSettings();
        var secrets = new FakeSecretStore();
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        var staging = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging"));
        var provider = new ControllableProvider();

        var form = new MainForm(
            settings,
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService(),
            secretStore: secrets,
            imageGenerationProvider: provider,
            generationJobStore: jobStore,
            stagingService: staging);

        return (form, provider, jobStore, staging);
    }

    private string CreateTestManifest(string filename = "hero.png", string prompt = "Test prompt")
    {
        var manifestPath = Path.Combine(_tempDir, "manifest_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(manifestPath, $$"""
        {
          "manifestVersion": 2,
          "assets": [
            {
              "filename": "{{filename}}",
              "resolution": "512x512",
              "alpha": "not_required",
              "prompt": "{{prompt}}"
            }
          ]
        }
        """);
        return manifestPath;
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Direct_FinalPromoteFails_JobRetainsCandidateIdRawPathRawHashAndRequestId()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore, staging) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnGenerate);

                btnImport.PerformClick();

                // Inject failure during final candidate promote
                GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = _ =>
                    throw new IOException("Simulated disk error during final promote");

                btnGenerate.PerformClick();

                // Wait for async generation
                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);

                var state = jobStore.Load();
                var item = Assert.Single(state.Items);

                // Critical assertions:
                // Raw output was persisted, so job MUST retain raw recovery metadata and be FailedRetryable
                Assert.Equal(GenerationItemStatus.FailedRetryable, item.Status);
                Assert.Equal("local_candidate_processing_failed", item.ErrorCode);
                Assert.False(string.IsNullOrWhiteSpace(item.CandidateId));
                Assert.False(string.IsNullOrWhiteSpace(item.ProviderRawPath));
                Assert.True(File.Exists(item.ProviderRawPath));
                Assert.False(string.IsNullOrWhiteSpace(item.RawSha256));
                Assert.Equal("req-test-123", item.ProviderRequestId);

                form.Close();
            }
            finally
            {
                GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = null;
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Batch_NormalizationFails_JobRetainsRawRecoveryAuthority()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "batch_jobs.json"));
        var staging = new GeneratedImageStagingService(Path.Combine(_tempDir, "batch_staging"));
        var service = new BatchIngestionService(jobStore, staging);

        var fp = "fp-batch-1";
        var localBatchId = "batch-local-1";
        var itemRecord = new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: "k1",
            AssetName: "asset1",
            FileName: "asset1.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-k1",
            Status: GenerationItemStatus.BatchSubmitted,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: localBatchId);

        jobStore.UpsertItem(itemRecord);

        var batch = new GenerationBatchRecord(
            LocalBatchId: localBatchId,
            ManifestFingerprint: fp,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: ["k1"],
            Status: "Submitted",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            ProviderBatchId: "batch-prov-1",
            SubmittedCount: 1);
        jobStore.UpsertBatch(batch);

        var rawBytes = CreateTestPng(816, 816, Color.Magenta);

        GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = _ =>
            throw new InvalidOperationException("Simulated promote failure in Batch");

        try
        {
            var status = new BatchStatusResult("batch-prov-1", "completed", "out-1", null, 1, 1, 0);
            var downloadResult = new BatchDownloadResult("batch-prov-1",
            [
                new BatchItemOutput("aph-k1", IsSuccess: true, ImageBytes: rawBytes, StatusCode: 200, ErrorCode: null, ErrorMessage: null, ProviderRequestId: "req-batch-1")
            ]);

            var summary = service.IngestResults(batch, status, downloadResult);
            Assert.Equal(0, summary.SuccessCount);
            Assert.Equal(1, summary.FailureCount);

            var updated = jobStore.GetItem(fp, "k1")!;
            Assert.Equal(GenerationItemStatus.FailedRetryable, updated.Status);
            Assert.Equal("local_candidate_processing_failed", updated.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(updated.CandidateId));
            Assert.False(string.IsNullOrWhiteSpace(updated.ProviderRawPath));
            Assert.True(File.Exists(updated.ProviderRawPath));
            Assert.False(string.IsNullOrWhiteSpace(updated.RawSha256));
        }
        finally
        {
            GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Restart_NormalizingWithRaw_AutoRecoversToReady_ZeroProviderCalls()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "recover_jobs.json"));
        var staging = new GeneratedImageStagingService(Path.Combine(_tempDir, "recover_staging"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, staging);

        var fp = "fp-norm-1";
        var rawBytes = CreateTestPng(816, 816, Color.Teal);
        var candId = "cand-norm-rec";
        var rawPath = staging.SaveRawCandidate(fp, "k1", candId, rawBytes);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        var item = new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: "k1",
            AssetName: "hero",
            FileName: "hero.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-k1",
            Status: GenerationItemStatus.Normalizing,
            CandidateId: candId,
            ProviderRawPath: rawPath,
            RawSha256: rawSha,
            ProviderRequestId: "req-norm-1",
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        jobStore.UpsertItem(item);

        var recoveredCount = recoveryService.RecoverAllForManifest(fp);
        Assert.Equal(1, recoveredCount);

        var updated = jobStore.GetItem(fp, "k1")!;
        Assert.Equal(GenerationItemStatus.Ready, updated.Status);
        Assert.True(File.Exists(updated.StagedOutputPath));
        Assert.Equal(candId, updated.CandidateId);
        Assert.Equal(rawSha, updated.RawSha256);
        Assert.False(string.IsNullOrWhiteSpace(updated.NormalizedSha256));
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Restart_NormalizingMissingRaw_BecomesUncertain_NotEligible()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "missing_raw_jobs.json"));
        var staging = new GeneratedImageStagingService(Path.Combine(_tempDir, "missing_raw_staging"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, staging);

        var fp = "fp-missing-raw";
        var item = new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: "k1",
            AssetName: "hero",
            FileName: "hero.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-k1",
            Status: GenerationItemStatus.Normalizing,
            CandidateId: "cand-gone",
            ProviderRawPath: Path.Combine(_tempDir, "non_existent.raw.png"),
            RawSha256: "fake-sha",
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        jobStore.UpsertItem(item);

        var recoveredCount = recoveryService.RecoverAllForManifest(fp);
        Assert.Equal(0, recoveredCount);

        var updated = jobStore.GetItem(fp, "k1")!;
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updated.Status);
        Assert.Equal("normalizing_raw_missing", updated.ErrorCode);

        var preflightService = new ApiPreflightService(jobStore);
        var preflight = preflightService.Preflight(fp, [new AssetRequestItem
        {
            RequestKey = "k1",
            AssetName = "hero",
            FileName = "hero.png",
            Prompt = "prompt",
            Width = 512,
            Height = 512,
            Resolution = "512x512",
            Alpha = AlphaRequirement.NotRequired
        }], new HashSet<string>());

        Assert.Empty(preflight.Eligible);
        Assert.Equal(1, preflight.UncertainCount);
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LocalProcessingFailure_GenerateAgain_RetriesLocallyNotRemotely()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore, staging) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnGenerate);

                btnImport.PerformClick();

                var manifestService = new AssetRequestManifestService(workspace.CreateValidationService());
                var manifest = manifestService.Load(manifestPath, [".png"]);
                var fp = manifest.ManifestFingerprint;
                var rk = manifest.Items[0].RequestKey;
                var rawBytes = CreateTestPng(816, 816, Color.Navy);
                var candId = "cand-pre-existing-raw";
                var rawPath = staging.SaveRawCandidate(fp, rk, candId, rawBytes);
                var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

                // Seed job as failed locally with saved raw output
                jobStore.UpsertItem(new GenerationItemRecord(
                    ManifestFingerprint: fp,
                    RequestKey: rk,
                    AssetName: "hero",
                    FileName: "hero.png",
                    Mode: GenerationMode.Direct,
                    ProviderId: "OpenAI",
                    Model: "gpt-image-2",
                    Quality: "medium",
                    TargetWidth: 512,
                    TargetHeight: 512,
                    GenerationWidth: 816,
                    GenerationHeight: 816,
                    CustomId: GenerationCustomId.Create(fp, rk),
                    Status: GenerationItemStatus.FailedRetryable,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    CandidateId: candId,
                    ProviderRawPath: rawPath,
                    RawSha256: rawSha,
                    ErrorCode: "local_candidate_processing_failed",
                    ErrorMessage: "Previous failure"));

                // Click Generate Now - LocalCandidateRecoveryService will run before preflight
                btnGenerate.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                // Critical assertion: Zero remote calls made!
                Assert.Equal(0, provider.GenerateCount);

                var updated = jobStore.GetItem(fp, rk)!;
                Assert.Equal(GenerationItemStatus.Ready, updated.Status);
                Assert.True(File.Exists(updated.StagedOutputPath));

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

    [Fact]
    public void RecoverAllForManifest_FailedRetryable_RawFileMissing_TransitionsToUncertain()
    {
        var stagingDir = Path.Combine(_tempDir, "staging_missing_raw");
        var jobsPath = Path.Combine(_tempDir, "jobs_missing_raw.json");
        var stagingService = new GeneratedImageStagingService(stagingDir);
        var jobStore = new GenerationJobStore(jobsPath);
        var recoveryService = new LocalCandidateRecoveryService(jobStore, stagingService);

        var fp = "fp_missing_raw";
        var rk = "rk_missing_raw";
        var nonExistentPath = Path.Combine(stagingDir, "does_not_exist.png");

        jobStore.UpsertItem(new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: rk,
            AssetName: "asset1",
            FileName: "asset1.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 512,
            GenerationHeight: 512,
            CustomId: GenerationCustomId.Create(fp, rk),
            Status: GenerationItemStatus.FailedRetryable,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CandidateId: "cand-missing",
            ProviderRawPath: nonExistentPath,
            ErrorCode: "local_candidate_processing_failed",
            ErrorMessage: "Previous failure"));

        var recovered = recoveryService.RecoverAllForManifest(fp);
        Assert.Equal(0, recovered);

        var updated = jobStore.GetItem(fp, rk);
        Assert.NotNull(updated);
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updated.Status);
        Assert.Equal("recovery_raw_missing", updated.ErrorCode);
    }
}
