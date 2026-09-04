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
        GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
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
    public void Direct_ProviderReturnsImage_RawSaveFails_StatusUncertain()
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

                // Inject failure during raw write
                GeneratedImageStagingService.OnBeforeSaveRawForTests = _ =>
                    throw new IOException("Simulated disk error during raw save");

                btnGenerate.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);

                var state = jobStore.Load();
                var item = Assert.Single(state.Items);

                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);
                Assert.Equal("provider_output_received_local_persist_failed", item.ErrorCode);
                Assert.True(item.ProviderOutputReceived);
                Assert.NotNull(item.ProviderOutputReceivedAtUtc);
                Assert.False(string.IsNullOrWhiteSpace(item.CandidateId));
                Assert.Equal("req-test-123", item.ProviderRequestId);
                Assert.True(string.IsNullOrWhiteSpace(item.ProviderRawPath));

                form.Close();
            }
            finally
            {
                GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
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
    public void Direct_ProviderSuccess_RawWriteFails_SecondGenerateProviderCountStill1()
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

                // Inject failure during raw write
                GeneratedImageStagingService.OnBeforeSaveRawForTests = _ =>
                    throw new IOException("Simulated disk error during raw save");

                btnGenerate.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);

                var state = jobStore.Load();
                var item = Assert.Single(state.Items);
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);

                // Second click on Generate Now: must NOT call provider again!
                GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
                btnGenerate.PerformClick();

                for (var i = 0; i < 30; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);

                form.Close();
            }
            finally
            {
                GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
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
    public void Direct_RawSaveLocalRetrySucceeds_ExactlyOneProviderCall()
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

                // Fail attempts 1 and 2, then succeed on attempt 3
                var attempts = 0;
                GeneratedImageStagingService.OnBeforeSaveRawForTests = _ =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        throw new IOException($"Transient disk error attempt {attempts}");
                    }
                };

                btnGenerate.PerformClick();

                for (var i = 0; i < 70; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);
                Assert.Equal(3, attempts);

                var state = jobStore.Load();
                var item = Assert.Single(state.Items);
                Assert.Equal(GenerationItemStatus.Ready, item.Status);
                Assert.True(item.ProviderOutputReceived);
                Assert.False(string.IsNullOrWhiteSpace(item.ProviderRawPath));
                Assert.True(File.Exists(item.ProviderRawPath));
                Assert.False(string.IsNullOrWhiteSpace(item.StagedOutputPath));
                Assert.True(File.Exists(item.StagedOutputPath));

                form.Close();
            }
            finally
            {
                GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
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
    public void Restart_PngPromoted_MetadataMissing_CleansOrphanAndRebuildsBundle()
    {
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "recover_jobs_n022.json"));
        var staging = new GeneratedImageStagingService(Path.Combine(_tempDir, "recover_staging_n022"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, staging);

        var fp = "fp-n022";
        var rawBytes = CreateTestPng(816, 816, Color.Gold);
        var candId = "cand-n022";
        var rawPath = staging.SaveRawCandidate(fp, "k1", candId, rawBytes);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        // Simulate partial sequential promote: final PNG exists, but metadata JSON does not exist
        var itemDir = staging.GetItemDirectory(fp, "k1");
        var orphanFinalPng = Path.Combine(itemDir, $"{candId}.png");
        File.WriteAllBytes(orphanFinalPng, new byte[] { 1, 2, 3, 4 });
        var metadataPath = Path.Combine(itemDir, $"{candId}.metadata.json");
        Assert.False(File.Exists(metadataPath));

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
            ProviderRequestId: "req-n022-1",
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        jobStore.UpsertItem(item);

        var recoveredCount = recoveryService.RecoverAllForManifest(fp);
        Assert.Equal(1, recoveredCount);

        var updated = jobStore.GetItem(fp, "k1")!;
        Assert.Equal(GenerationItemStatus.Ready, updated.Status);
        Assert.True(File.Exists(updated.StagedOutputPath));
        Assert.True(File.Exists(metadataPath));

        var rebuiltFinalBytes = File.ReadAllBytes(updated.StagedOutputPath!);
        Assert.True(rebuiltFinalBytes.Length > 100);
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

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void MainFormStartup_NormalizingWithRaw_BecomesReadyBeforeGenerateClick()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var secrets = new FakeSecretStore();
            var jobStorePath = Path.Combine(_tempDir, "jobs_norm_start.json");
            var stagingPath = Path.Combine(_tempDir, "staging_norm_start");
            var jobStore = new GenerationJobStore(jobStorePath);
            var staging = new GeneratedImageStagingService(stagingPath);
            var provider = new ControllableProvider();

            var manifestPath = CreateTestManifest("hero.png");
            var manifestService = new AssetRequestManifestService(workspace.CreateValidationService());
            var manifest = manifestService.Load(manifestPath, [".png"]);
            var manifestFingerprint = manifest.ManifestFingerprint;
            var requestKey = manifest.Items[0].RequestKey;

            var rawBytes = CreateTestPng(816, 816, Color.Orange);
            var candId = "cand-norm-startup";
            var rawPath = staging.SaveRawCandidate(manifestFingerprint, requestKey, candId, rawBytes);
            var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

            jobStore.UpsertItem(new GenerationItemRecord(
                ManifestFingerprint: manifestFingerprint,
                RequestKey: requestKey,
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
                CustomId: GenerationCustomId.Create(manifestFingerprint, requestKey),
                Status: GenerationItemStatus.Normalizing,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                CandidateId: candId,
                ProviderRawPath: rawPath,
                RawSha256: rawSha));

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
                generationJobStore: jobStore,
                stagingService: staging);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                // Do NOT click generate. Simply verify startup recovery has completed
                var item = jobStore.GetItem(manifestFingerprint, requestKey)!;
                Assert.Equal(GenerationItemStatus.Ready, item.Status);
                Assert.True(File.Exists(item.StagedOutputPath));
                Assert.Equal(0, provider.GenerateCount);

                form.Close();
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void MainFormStartup_NormalizingMissingRaw_BecomesUncertain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var secrets = new FakeSecretStore();
            var jobStorePath = Path.Combine(_tempDir, "jobs_norm_missing.json");
            var stagingPath = Path.Combine(_tempDir, "staging_norm_missing");
            var jobStore = new GenerationJobStore(jobStorePath);
            var staging = new GeneratedImageStagingService(stagingPath);
            var provider = new ControllableProvider();

            var manifestFingerprint = "fp_missing_startup";
            var requestKey = "hero.png";
            var missingRawPath = Path.Combine(stagingPath, "missing_cand.raw.png");

            jobStore.UpsertItem(new GenerationItemRecord(
                ManifestFingerprint: manifestFingerprint,
                RequestKey: requestKey,
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
                CustomId: GenerationCustomId.Create(manifestFingerprint, requestKey),
                Status: GenerationItemStatus.Normalizing,
                SubmittedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                CandidateId: "cand-missing-raw",
                ProviderRawPath: missingRawPath,
                RawSha256: "dummy-sha"));

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
                generationJobStore: jobStore,
                stagingService: staging);

            try
            {
                form.Show();

                var item = jobStore.GetItem(manifestFingerprint, requestKey)!;
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);
                Assert.Equal("normalizing_raw_missing", item.ErrorCode);

                form.Close();
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void UncertainDirect_UserConfirmsRetry_TransitionsPending_AndRegeneratesOnNextClick()
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

                var manifestPath = CreateTestManifest("hero.png");
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                var btnRetry = form.Controls.Find("btnRetrySelectedApi", true).FirstOrDefault() as Button;
                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

                Assert.NotNull(btnImport);
                Assert.NotNull(btnGenerate);
                Assert.NotNull(btnRetry);
                Assert.NotNull(lvQueue);

                btnImport.PerformClick();

                var manifestService = new AssetRequestManifestService(workspace.CreateValidationService());
                var manifest = manifestService.Load(manifestPath, [".png"]);
                var fp = manifest.ManifestFingerprint;
                var rk = manifest.Items[0].RequestKey;

                // Set item into UncertainAfterInterruption
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
                    Status: GenerationItemStatus.UncertainAfterInterruption,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    ErrorCode: "provider_output_received_local_persist_failed",
                    ErrorMessage: "Previous crash"));

                // Generate click is skipped for Uncertain items
                btnGenerate.PerformClick();
                for (var i = 0; i < 30; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }
                Assert.Equal(0, provider.GenerateCount);

                // Select the queue item
                Assert.True(lvQueue.Items.Count > 0);
                lvQueue.Items[0].Selected = true;
                Application.DoEvents();

                Assert.True(btnRetry.Enabled);

                // Confirm retry
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                btnRetry.PerformClick();
                Application.DoEvents();

                var updated = jobStore.GetItem(fp, rk)!;
                Assert.Equal(GenerationItemStatus.Pending, updated.Status);
                Assert.Null(updated.ErrorCode);

                // Now clicking Generate Now will explicitly regenerate it
                btnGenerate.PerformClick();
                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.GenerateCount);
                var readyItem = jobStore.GetItem(fp, rk)!;
                Assert.Equal(GenerationItemStatus.Ready, readyItem.Status);

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
    [Trait("Category", "RecoveryCritical")]
    public void UncertainDirect_UserCancelsRetry_RemainsUncertain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore, staging) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.Cancel;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest("hero.png");
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnRetry = form.Controls.Find("btnRetrySelectedApi", true).FirstOrDefault() as Button;
                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

                Assert.NotNull(btnImport);
                Assert.NotNull(btnRetry);
                Assert.NotNull(lvQueue);

                btnImport.PerformClick();

                var manifestService = new AssetRequestManifestService(workspace.CreateValidationService());
                var manifest = manifestService.Load(manifestPath, [".png"]);
                var fp = manifest.ManifestFingerprint;
                var rk = manifest.Items[0].RequestKey;

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
                    Status: GenerationItemStatus.UncertainAfterInterruption,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    ErrorCode: "provider_output_received_local_persist_failed"));

                lvQueue.Items[0].Selected = true;
                Application.DoEvents();

                Assert.True(btnRetry.Enabled);

                // User cancels confirmation
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.Cancel;
                btnRetry.PerformClick();
                Application.DoEvents();

                var item = jobStore.GetItem(fp, rk)!;
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);

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
    [Trait("Category", "RecoveryCritical")]
    public void UncertainBatchKnownProviderId_RetryActionBlocked()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore, staging) = CreateTestForm(workspace);

            try
            {
                var messageBoxShown = false;
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { messageBoxShown = true; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest("hero.png");
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnRetry = form.Controls.Find("btnRetrySelectedApi", true).FirstOrDefault() as Button;
                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

                Assert.NotNull(btnImport);
                Assert.NotNull(btnRetry);
                Assert.NotNull(lvQueue);

                btnImport.PerformClick();

                var manifestService = new AssetRequestManifestService(workspace.CreateValidationService());
                var manifest = manifestService.Load(manifestPath, [".png"]);
                var fp = manifest.ManifestFingerprint;
                var rk = manifest.Items[0].RequestKey;

                jobStore.UpsertItem(new GenerationItemRecord(
                    ManifestFingerprint: fp,
                    RequestKey: rk,
                    AssetName: "hero",
                    FileName: "hero.png",
                    Mode: GenerationMode.Batch,
                    ProviderId: "OpenAI",
                    Model: "gpt-image-2",
                    Quality: "medium",
                    TargetWidth: 512,
                    TargetHeight: 512,
                    GenerationWidth: 816,
                    GenerationHeight: 816,
                    CustomId: GenerationCustomId.Create(fp, rk),
                    ProviderBatchId: "batch_remote_active",
                    Status: GenerationItemStatus.UncertainAfterInterruption,
                    SubmittedAtUtc: DateTimeOffset.UtcNow,
                    UpdatedAtUtc: DateTimeOffset.UtcNow,
                    ErrorCode: "batch_results_download_failed"));

                lvQueue.Items[0].Selected = true;
                Application.DoEvents();

                Assert.True(btnRetry.Enabled);

                btnRetry.PerformClick();
                Application.DoEvents();

                // Must show info dialog and block reset
                Assert.True(messageBoxShown);

                var item = jobStore.GetItem(fp, rk)!;
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);

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
    public void Batch_LocalRecovery_MetadataUsesProviderBatchIdNotLocalBatchId()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs_n007.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging_n007"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, stagingService);

        var manifestFp = "fp_n007";
        var requestKey = "rk_n007";
        var candidateId = "cand_n007";

        var rawBytes = CreateTestPng(1024, 1024, Color.Orange);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        var rawPath = stagingService.SaveRawCandidate(manifestFp, requestKey, candidateId, rawBytes);

        var job = new GenerationItemRecord(
            ManifestFingerprint: manifestFp,
            RequestKey: requestKey,
            AssetName: "asset_n007",
            FileName: "asset_n007.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 1024,
            TargetHeight: 1024,
            GenerationWidth: 1024,
            GenerationHeight: 1024,
            CustomId: "custom_n007",
            Status: GenerationItemStatus.Normalizing,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CandidateId: candidateId,
            ProviderRawPath: rawPath,
            RawSha256: rawSha,
            BatchId: "local-batch-helper-999",
            ProviderBatchId: "batch_remote_123");

        jobStore.UpsertItem(job);

        var recovered = recoveryService.TryRecoverCandidate(job);
        Assert.True(recovered);

        var meta = stagingService.LoadMetadata(manifestFp, requestKey, candidateId);
        Assert.NotNull(meta);
        Assert.Equal("batch_remote_123", meta.BatchId);
        Assert.NotEqual("local-batch-helper-999", meta.BatchId);

        using var workspace = new TestWorkspace();
        var templateSrc = Path.Combine(AppContext.BaseDirectory, "provider_templates", "OpenAI API.md");
        if (!File.Exists(templateSrc))
        {
            templateSrc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AssetProvenanceHelper", "provider_templates", "OpenAI API.md"));
        }
        File.Copy(templateSrc, Path.Combine(workspace.ProviderTemplates, "OpenAI API.md"), overwrite: true);

        var catalogService = workspace.CreateProviderTemplateCatalogService();
        var snapshot = catalogService.Load().Templates.Single(t => t.FileName == "OpenAI API.md").CreateSnapshot();

        var templateService = workspace.CreateTemplateService();
        var session = new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.NoReference,
            ProjectName = "Proj",
            AssetFolderName = "asset",
            MainFilename = "asset_n007.png",
            ProviderTemplate = snapshot,
            ApiCandidateId = meta.CandidateId,
            ApiProvider = meta.Provider,
            ApiModel = meta.Model,
            ApiMode = meta.Mode,
            ApiCustomId = meta.CustomId,
            ApiTargetResolution = meta.TargetResolution,
            ApiProviderResolution = meta.ProviderResolution,
            ApiRawSha256 = meta.RawSha256,
            ApiNormalizedSha256 = meta.NormalizedSha256,
            ApiProviderRequestId = meta.ProviderRequestId,
            ApiBatchId = meta.BatchId,
            ApiCreatedAtUtc = meta.CreatedAtUtc.ToString("O")
        };

        var rendered = templateService.RenderFinalForSession(
            session,
            session.MainFilename,
            "A test prompt",
            DateTimeOffset.UtcNow);

        Assert.Contains("Batch ID: batch_remote_123", rendered);
        Assert.DoesNotContain("local-batch-helper-999", rendered);
    }

    [Fact]
    public void Direct_MetadataCreatedAt_EqualsProviderOutputReceiptTime()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore, stagingService) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest("asset_receipt.png");
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                Assert.NotNull(btnGenerate);
                btnGenerate.PerformClick();

                var spinWait = 0;
                while (spinWait < 100)
                {
                    Application.DoEvents();
                    var item = jobStore.GetItem(fp, rk);
                    if (item?.Status == GenerationItemStatus.Ready) break;
                    Thread.Sleep(50);
                    spinWait++;
                }

                var job = jobStore.GetItem(fp, rk);
                Assert.NotNull(job);
                Assert.Equal(GenerationItemStatus.Ready, job.Status);
                Assert.True(job.ProviderOutputReceived);
                Assert.NotNull(job.ProviderOutputReceivedAtUtc);

                var meta = stagingService.LoadMetadata(fp, rk, job.CandidateId!);
                Assert.NotNull(meta);
                Assert.Equal(job.ProviderOutputReceivedAtUtc.Value.ToUnixTimeMilliseconds(), meta.CreatedAtUtc.ToUnixTimeMilliseconds());

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
    public void Direct_LocalRecovery_PreservesOriginalCreatedAt()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs_direct_rec_time.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging_direct_rec_time"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, stagingService);

        var manifestFp = "fp_direct_time";
        var requestKey = "rk_direct_time";
        var candidateId = "cand_direct_time";

        var plan = ImageSizePlanner.Plan(512, 512);
        var rawBytes = CreateTestPng(plan.GenerationWidth, plan.GenerationHeight, Color.Green);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        var rawPath = stagingService.SaveRawCandidate(manifestFp, requestKey, candidateId, rawBytes);

        var originalReceivedAt = new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);

        var job = new GenerationItemRecord(
            ManifestFingerprint: manifestFp,
            RequestKey: requestKey,
            AssetName: "asset_time",
            FileName: "asset_time.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: plan.GenerationWidth,
            GenerationHeight: plan.GenerationHeight,
            CustomId: "custom_time",
            Status: GenerationItemStatus.Normalizing,
            SubmittedAtUtc: originalReceivedAt,
            UpdatedAtUtc: originalReceivedAt,
            CandidateId: candidateId,
            ProviderRawPath: rawPath,
            RawSha256: rawSha,
            ProviderOutputReceived: true,
            ProviderOutputReceivedAtUtc: originalReceivedAt);

        jobStore.UpsertItem(job);

        var recovered = recoveryService.TryRecoverCandidate(job);
        Assert.True(recovered);

        var meta = stagingService.LoadMetadata(manifestFp, requestKey, candidateId);
        Assert.NotNull(meta);
        Assert.Equal(originalReceivedAt, meta.CreatedAtUtc);
    }

    [Fact]
    public void Batch_LocalRecovery_PreservesOriginalCreatedAt()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs_batch_rec_time.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging_batch_rec_time"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, stagingService);

        var manifestFp = "fp_batch_time";
        var requestKey = "rk_batch_time";
        var candidateId = "cand_batch_time";

        var plan = ImageSizePlanner.Plan(512, 512);
        var rawBytes = CreateTestPng(plan.GenerationWidth, plan.GenerationHeight, Color.Purple);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        var rawPath = stagingService.SaveRawCandidate(manifestFp, requestKey, candidateId, rawBytes);

        var originalReceivedAt = new DateTimeOffset(2026, 4, 20, 14, 0, 0, TimeSpan.Zero);

        var job = new GenerationItemRecord(
            ManifestFingerprint: manifestFp,
            RequestKey: requestKey,
            AssetName: "asset_batch_time",
            FileName: "asset_batch_time.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: plan.GenerationWidth,
            GenerationHeight: plan.GenerationHeight,
            CustomId: "custom_batch_time",
            Status: GenerationItemStatus.Normalizing,
            SubmittedAtUtc: originalReceivedAt,
            UpdatedAtUtc: originalReceivedAt,
            CandidateId: candidateId,
            ProviderRawPath: rawPath,
            RawSha256: rawSha,
            BatchId: "local-batch-1",
            ProviderBatchId: "batch_remote_789",
            ProviderOutputReceived: true,
            ProviderOutputReceivedAtUtc: originalReceivedAt);

        jobStore.UpsertItem(job);

        var recovered = recoveryService.TryRecoverCandidate(job);
        Assert.True(recovered);

        var meta = stagingService.LoadMetadata(manifestFp, requestKey, candidateId);
        Assert.NotNull(meta);
        Assert.Equal(originalReceivedAt, meta.CreatedAtUtc);
    }

    [Fact]
    public void LocalRecovery_SizePlannerMismatch_ThrowsInvalidDataException()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs_n017.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging_n017"));
        var recoveryService = new LocalCandidateRecoveryService(jobStore, stagingService);

        var manifestFp = "fp_n017";
        var requestKey = "rk_n017";
        var candidateId = "cand_n017";

        var rawBytes = CreateTestPng(1024, 1024, Color.Green);
        var rawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        var rawPath = stagingService.SaveRawCandidate(manifestFp, requestKey, candidateId, rawBytes);

        var job = new GenerationItemRecord(
            ManifestFingerprint: manifestFp,
            RequestKey: requestKey,
            AssetName: "asset_n017",
            FileName: "asset_n017.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 1024,
            TargetHeight: 1024,
            GenerationWidth: 512,
            GenerationHeight: 512,
            CustomId: "custom_n017",
            Status: GenerationItemStatus.Normalizing,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CandidateId: candidateId,
            ProviderRawPath: rawPath,
            RawSha256: rawSha);

        jobStore.UpsertItem(job);

        var recovered = recoveryService.TryRecoverCandidate(job);
        Assert.False(recovered);

        var updated = jobStore.GetItem(manifestFp, requestKey)!;
        Assert.Equal(GenerationItemStatus.FailedRetryable, updated.Status);
        Assert.Equal("local_candidate_processing_failed", updated.ErrorCode);
        Assert.Contains("Stored provider resolution does not match the current size planner", updated.ErrorMessage);
    }
}
