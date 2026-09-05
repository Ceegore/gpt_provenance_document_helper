using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class CandidateVerificationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GeneratedImageStagingService _stagingService;
    private readonly CandidateVerificationService _verifier;

    public CandidateVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_verify_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging"));
        _verifier = new CandidateVerificationService(_stagingService);
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

    private static string ComputeSha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private (GenerationItemRecord Job, string NormalizedPath, string RawPath) StageTestCandidate(
        string manifestFp,
        string requestKey,
        string candId,
        int width,
        int height,
        Color color,
        bool setRawPathOnJob = true)
    {
        var plan = ImageSizePlanner.Plan(width, height);
        var rawBytes = CreateTestPng(plan.GenerationWidth, plan.GenerationHeight, color);
        var normBytes = CreateTestPng(width, height, color);
        var rawSha = ComputeSha(rawBytes);
        var normSha = ComputeSha(normBytes);

        var metadata = new ApiCandidateMetadata(
            CandidateId: candId,
            Provider: "OpenAI",
            Model: "gpt-image-2",
            Mode: "direct",
            CustomId: "custom-" + requestKey,
            TargetResolution: $"{width}x{height}",
            ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
            RawSha256: rawSha,
            NormalizedSha256: normSha,
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var normalizedPath = _stagingService.SaveCandidate(manifestFp, requestKey, candId, rawBytes, normBytes, metadata);
        var rawPath = _stagingService.GetRawCandidatePath(manifestFp, requestKey, candId);

        var job = new GenerationItemRecord(
            ManifestFingerprint: manifestFp,
            RequestKey: requestKey,
            AssetName: "asset_" + requestKey,
            FileName: "asset_" + requestKey + ".png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: width,
            TargetHeight: height,
            GenerationWidth: plan.GenerationWidth,
            GenerationHeight: plan.GenerationHeight,
            CustomId: "custom-" + requestKey,
            Status: GenerationItemStatus.Ready,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CandidateId: candId,
            ProviderRawPath: setRawPathOnJob ? rawPath : null,
            StagedOutputPath: normalizedPath,
            RawSha256: rawSha,
            NormalizedSha256: normSha);

        return (job, normalizedPath, rawPath);
    }

    [Fact]
    public void Ready_Valid_PassesVerification()
    {
        var (job, path, _) = StageTestCandidate("fp1", "k1", "cand-valid", 512, 512, Color.Green);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Candidate);
        Assert.Equal(path, result.Candidate.ImagePath);
        Assert.Equal("cand-valid", result.Candidate.Metadata.CandidateId);
    }

    [Fact]
    public void Ready_WithoutRawAuthority_FailsClosed()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-no-raw-authority", 512, 512, Color.Green, setRawPathOnJob: false);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("raw_authority_missing", result.ErrorCode);
    }

    [Fact]
    public void Ready_FileMissing_FailsClosed()
    {
        var (job, path, _) = StageTestCandidate("fp1", "k1", "cand-nofile", 512, 512, Color.Green);
        File.Delete(path);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("staged_file_missing", result.ErrorCode);
    }

    [Fact]
    public void Ready_MetadataMissing_FailsClosed()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-nometa", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-nometa.metadata.json");
        File.Delete(metaPath);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("metadata_missing_or_corrupt", result.ErrorCode);
    }

    [Fact]
    public void Ready_MetadataCorrupt_FailsClosed()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-corruptmeta", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-corruptmeta.metadata.json");
        File.WriteAllText(metaPath, "NOT_JSON_AT_ALL");

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("metadata_missing_or_corrupt", result.ErrorCode);
    }

    [Fact]
    public void Ready_HashMismatch_FailsClosed()
    {
        var (job, path, _) = StageTestCandidate("fp1", "k1", "cand-hashmismatch", 512, 512, Color.Green);
        // Alter image file on disk
        var tamperedPng = CreateTestPng(512, 512, Color.Red);
        File.WriteAllBytes(path, tamperedPng);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("file_hash_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Ready_WrongDimensions_FailsClosed()
    {
        // Image created at 256x256 but target resolution requested is 512x512
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-wrongdim", 256, 256, Color.Green);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("resolution_mismatch", result.ErrorCode);
    }

    [Fact]
    public void VerifyCandidate_InvalidPngHeader_ReturnsInvalid()
    {
        var (job, stagedPath, _) = StageTestCandidate("fp1", "k1", "cand-badhdr", 512, 512, Color.Green);

        // Replace file content with non-PNG bytes (e.g. BMP magic bytes) and match SHA
        var fakeBytes = new byte[100];
        fakeBytes[0] = (byte)'B';
        fakeBytes[1] = (byte)'M';
        File.WriteAllBytes(stagedPath, fakeBytes);

        var newSha = ComputeSha(fakeBytes);
        var metaPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, "cand-badhdr.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        var updatedMeta = meta with { NormalizedSha256 = newSha };
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(updatedMeta));

        var updatedJob = job with { NormalizedSha256 = newSha };

        var result = _verifier.VerifyCandidate(updatedJob, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("invalid_png_header", result.ErrorCode);
    }

    [Fact]
    public void Ready_Invalid_NeverCommits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? messageShown = null;
                MainForm.MessageBoxProvider = (_, msg, title, _, _) => { messageShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                // Setup manifest in form
                var manifestPath = Path.Combine(_tempDir, "manifest.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_k1.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, _) = StageTestCandidate(fp, rk, "cand-fail-commit", 512, 512, Color.Green, setRawPathOnJob: false);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                // Tamper with the file to corrupt hash
                File.WriteAllBytes(path, CreateTestPng(512, 512, Color.Magenta));

                // Click Ready row -> verification should fail, unload image, mark job UncertainAfterInterruption (no raw authority)
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.Null(form.ActiveApiCandidateMetadata);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
                Assert.NotNull(messageShown);
                Assert.Contains("failed verification", messageShown);

                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updatedJob.Status);
                Assert.Equal("candidate_verification_failed_no_raw_authority", updatedJob.ErrorCode);

                // Attempt commit: nothing should be committed to asset directory
                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();

                var committedFiles = Directory.GetFiles(assetRoot, "*.*", SearchOption.AllDirectories);
                Assert.Empty(committedFiles);

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
    public void Ready_Valid_LoadsMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_valid");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_valid.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_valid.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_valid.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, _) = StageTestCandidate(fp, rk, "cand-load-valid", 512, 512, Color.Green);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                // Click Ready row -> verification should pass and load candidate into Main
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);
                Assert.Equal(path, form.GetSelectedImage(ImageSlot.Main));
                Assert.Equal("cand-load-valid", form.ActiveApiCandidateMetadata.CandidateId);

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

    private static void EnsureOpenAiApiTemplate(TestWorkspace workspace)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "provider_templates", "OpenAI API.md");
        if (!File.Exists(src))
        {
            src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AssetProvenanceHelper", "provider_templates", "OpenAI API.md"));
        }
        File.Copy(src, Path.Combine(workspace.ProviderTemplates, "OpenAI API.md"), overwrite: true);
    }

    [Fact]
    public void Candidate_MetadataProviderMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-prov", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-prov.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { Provider = "OtherProvider" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("provider_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataModelMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-model", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-model.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { Model = "other-model" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("model_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataModeMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-mode", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-mode.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { Mode = "batch" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("mode_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataCustomIdMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-cid", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-cid.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { CustomId = "other-cid" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("custom_id_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataTargetResolutionMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-tres", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-tres.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { TargetResolution = "1024x1024" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("target_resolution_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataProviderResolutionMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-pres", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-pres.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { ProviderResolution = "1024x1024" }));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("provider_resolution_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataRequestIdMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-reqid", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-reqid.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { ProviderRequestId = "req_meta" }));

        var jobWithReq = job with { ProviderRequestId = "req_job" };
        var result = _verifier.VerifyCandidate(jobWithReq, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("provider_request_id_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_MetadataBatchIdMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-batchid", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-batchid.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { Mode = "batch", BatchId = "batch_remote_meta" }));

        var batchJob = job with { Mode = GenerationMode.Batch, ProviderBatchId = "batch_remote_job" };
        var result = _verifier.VerifyCandidate(batchJob, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("provider_batch_id_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_RawPathMismatch_Rejected()
    {
        var (job, _, _) = StageTestCandidate("fp1", "k1", "cand-rawpath", 512, 512, Color.Green);
        var wrongRawPath = Path.Combine(_tempDir, "wrong.raw.png");
        File.WriteAllBytes(wrongRawPath, CreateTestPng(512, 512, Color.Green));
        var mutatedJob = job with { ProviderRawPath = wrongRawPath };

        var result = _verifier.VerifyCandidate(mutatedJob, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("raw_path_invalid", result.ErrorCode);
    }

    [Fact]
    public void Candidate_RawHashMismatch_Rejected()
    {
        var (job, _, rawPath) = StageTestCandidate("fp1", "k1", "cand-rawhash", 512, 512, Color.Green);
        File.WriteAllBytes(rawPath, CreateTestPng(512, 512, Color.Blue));

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("raw_hash_mismatch", result.ErrorCode);
    }

    [Fact]
    public void Candidate_FullValidBundle_Passes()
    {
        var (job, path, rawPath) = StageTestCandidate("fp1", "k1", "cand-fullvalid", 512, 512, Color.Green, setRawPathOnJob: true);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-fullvalid.metadata.json");
        var meta = System.Text.Json.JsonSerializer.Deserialize<ApiCandidateMetadata>(File.ReadAllText(metaPath))!;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta with { ProviderRequestId = "req_123" }));
        var jobWithReq = job with { ProviderRequestId = "req_123" };

        var result = _verifier.VerifyCandidate(jobWithReq, 512, 512);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Candidate);
        Assert.Equal("req_123", result.Candidate.Metadata.ProviderRequestId);
    }

    [Fact]
    public void Ready_FinalTampered_RawValid_RebuildsLocally()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_rebuild");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_rebuild.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_rebuild.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_rebuild.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Rebuild test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-rebuild", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                // Tamper with final PNG so verification fails, but raw file is pristine
                File.WriteAllBytes(path, CreateTestPng(512, 512, Color.Magenta));

                // Click Ready row -> verification detects tampered final, but raw authority exists, so it rebuilds locally and loads into Main!
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);
                Assert.NotNull(form.GetSelectedImage(ImageSlot.Main));
                Assert.Equal("cand-rebuild", form.ActiveApiCandidateMetadata.CandidateId);

                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.Ready, updatedJob.Status);

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
    public void Ready_MetadataMissing_RawValid_RebuildsLocally()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_nometa_rebuild");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_nometa.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_nometa.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_nometa.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-nometa-rebuild", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                // Delete metadata json
                var metaPath = Path.Combine(_stagingService.GetItemDirectory(fp, rk), "cand-nometa-rebuild.metadata.json");
                File.Delete(metaPath);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);
                Assert.NotNull(form.GetSelectedImage(ImageSlot.Main));

                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.Ready, updatedJob.Status);

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
    public void Ready_RawMissing_FinalInvalid_BecomesUncertain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_noraw_unc");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_noraw_unc.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_noraw_unc.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_noraw.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-noraw-unc", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                // Corrupt final AND delete raw
                File.WriteAllBytes(path, CreateTestPng(512, 512, Color.Magenta));
                File.Delete(rawPath);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.Null(form.ActiveApiCandidateMetadata);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));

                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updatedJob.Status);
                Assert.Equal("candidate_verification_failed_no_raw_authority", updatedJob.ErrorCode);

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
    public void CommitVerificationFailure_RawValid_NoRemoteRegeneration()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            EnsureOpenAiApiTemplate(workspace);
            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_commit_fail");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_commit_fail.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_commit_fail.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_commit_fail.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-commit-fail", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                // Load candidate into Main
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();
                Assert.NotNull(form.ActiveApiCandidateMetadata);

                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                Assert.NotNull(chkNoRef);
                chkNoRef.Checked = true;

                // Now corrupt final PNG before commit
                File.WriteAllBytes(path, CreateTestPng(512, 512, Color.Magenta));

                // Click Main Image button to trigger commit
                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();
                Application.DoEvents();

                // Candidate was unloaded and commit was blocked
                Assert.Null(form.ActiveApiCandidateMetadata);
                var committedFiles = Directory.GetFiles(assetRoot, "*.*", SearchOption.AllDirectories);
                Assert.Empty(committedFiles);

                // Because raw authority was valid, local recovery rebuilt candidate to Ready!
                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.Ready, updatedJob.Status);

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
    public void ApiCandidate_OpenAiTemplateMissing_CommitBlocked()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            EnsureOpenAiApiTemplate(workspace);

            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_tpl_missing");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_tpl_missing.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? errorShown = null;
                MainForm.MessageBoxProvider = (_, msg, title, _, _) => { errorShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_tpl_missing.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_tpl_missing.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-tpl-missing", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);

                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                Assert.NotNull(chkNoRef);
                chkNoRef.Checked = true;

                // Delete OpenAI API.md from provider templates AFTER candidate is loaded
                var openAiTpl = Path.Combine(workspace.ProviderTemplates, "OpenAI API.md");
                if (File.Exists(openAiTpl)) File.Delete(openAiTpl);

                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();
                Application.DoEvents();

                Assert.NotNull(errorShown);
                Assert.Contains("OpenAI API provider template is missing or invalid", errorShown);

                var committedFiles = Directory.GetFiles(assetRoot, "*.*", SearchOption.AllDirectories);
                Assert.Empty(committedFiles);

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
    public void ApiCandidate_OpenAiTemplateInvalid_CommitBlocked()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            EnsureOpenAiApiTemplate(workspace);

            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_tpl_invalid");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_tpl_invalid.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? errorShown = null;
                MainForm.MessageBoxProvider = (_, msg, title, _, _) => { errorShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_tpl_invalid.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_tpl_invalid.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-tpl-invalid", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);

                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                Assert.NotNull(chkNoRef);
                chkNoRef.Checked = true;

                // Make OpenAI API.md invalid AFTER candidate is loaded
                var openAiTpl = Path.Combine(workspace.ProviderTemplates, "OpenAI API.md");
                File.WriteAllText(openAiTpl, "INVALID TEMPLATE WITHOUT REQUIRED HEADERS OR TAGS");

                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();
                Application.DoEvents();

                Assert.NotNull(errorShown);
                Assert.Contains("OpenAI API provider template is missing or invalid", errorShown);

                var committedFiles = Directory.GetFiles(assetRoot, "*.*", SearchOption.AllDirectories);
                Assert.Empty(committedFiles);

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
    public void ApiCandidate_ProviderDropdownChanged_StillUsesOpenAiApiTemplate()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            EnsureOpenAiApiTemplate(workspace);

            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_tpl_dropdown");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_tpl_dropdown.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = Path.Combine(_tempDir, "manifest_tpl_dropdown.json");
                File.WriteAllText(manifestPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "asset_dropdown.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Test prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                btnImport.PerformClick();

                var currentManifest = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(currentManifest);
                var fp = currentManifest.ManifestFingerprint;
                var rk = currentManifest.Items[0].RequestKey;

                var (job, path, rawPath) = StageTestCandidate(fp, rk, "cand-dropdown", 512, 512, Color.Green, setRawPathOnJob: true);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.NotNull(form.ActiveApiCandidateMetadata);

                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                Assert.NotNull(chkNoRef);
                chkNoRef.Checked = true;

                // Deliberately select another provider in UI dropdown, e.g. "ChatGPT.md"
                form.SelectProviderByFileName("ChatGPT.md");

                // Commit candidate
                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();
                Application.DoEvents();

                var mdFiles = Directory.GetFiles(assetRoot, "*.md", SearchOption.AllDirectories);
                Assert.NotEmpty(mdFiles);
                var provenanceContent = File.ReadAllText(mdFiles[0]);

                // Must use OpenAI API template authority, NOT ChatGPT!
                Assert.Contains("Generation channel: OpenAI API", provenanceContent);
                Assert.Contains("API Provider: OpenAI", provenanceContent);
                Assert.DoesNotContain("Provider: ChatGPT", provenanceContent);

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
    public void ManualCandidate_ProviderDropdownStillWorks()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            EnsureOpenAiApiTemplate(workspace);

            var settings = workspace.CreateSettings();
            var assetRoot = Path.Combine(_tempDir, "assets_manual_dropdown");
            Directory.CreateDirectory(assetRoot);
            settings.AssetRootFolder = assetRoot;

            var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_manual_dropdown.json"));

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                generationJobStore: jobStore,
                stagingService: _stagingService);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                // Select ChatGPT in dropdown
                form.SelectProviderByFileName("ChatGPT.md");

                // Put manual image in Main slot
                var manualImagePath = Path.Combine(_tempDir, "manual_input.png");
                File.WriteAllBytes(manualImagePath, CreateTestPng(512, 512, Color.Blue));
                form.SetSelectedImage(ImageSlot.Main, manualImagePath);

                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                Assert.NotNull(txtPrompt);
                txtPrompt.Text = "Manual prompt text";

                var txtAssetFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
                Assert.NotNull(txtAssetFolder);
                txtAssetFolder.Text = "manual_asset";

                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                Assert.NotNull(chkNoRef);
                chkNoRef.Checked = true;

                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;
                Assert.NotNull(btnMain);
                btnMain.PerformClick();
                Application.DoEvents();

                var mdFiles = Directory.GetFiles(assetRoot, "*.md", SearchOption.AllDirectories);
                Assert.NotEmpty(mdFiles);
                var provenanceContent = File.ReadAllText(mdFiles[0]);

                Assert.Contains("Provider: ChatGPT", provenanceContent);
                Assert.DoesNotContain("Generation channel: OpenAI API", provenanceContent);

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
