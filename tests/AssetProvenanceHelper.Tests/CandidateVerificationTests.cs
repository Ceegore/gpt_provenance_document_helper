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

    private (GenerationItemRecord Job, string NormalizedPath) StageTestCandidate(
        string manifestFp,
        string requestKey,
        string candId,
        int width,
        int height,
        Color color)
    {
        var rawBytes = CreateTestPng(width, height, color);
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
            ProviderResolution: $"{width}x{height}",
            RawSha256: rawSha,
            NormalizedSha256: normSha,
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var normalizedPath = _stagingService.SaveCandidate(manifestFp, requestKey, candId, rawBytes, normBytes, metadata);

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
            GenerationWidth: width,
            GenerationHeight: height,
            CustomId: "custom-" + requestKey,
            Status: GenerationItemStatus.Ready,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            CandidateId: candId,
            StagedOutputPath: normalizedPath,
            RawSha256: rawSha,
            NormalizedSha256: normSha);

        return (job, normalizedPath);
    }

    [Fact]
    public void Ready_Valid_PassesVerification()
    {
        var (job, path) = StageTestCandidate("fp1", "k1", "cand-valid", 512, 512, Color.Green);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Candidate);
        Assert.Equal(path, result.Candidate.ImagePath);
        Assert.Equal("cand-valid", result.Candidate.Metadata.CandidateId);
    }

    [Fact]
    public void Ready_FileMissing_FailsClosed()
    {
        var (job, path) = StageTestCandidate("fp1", "k1", "cand-nofile", 512, 512, Color.Green);
        File.Delete(path);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("staged_file_missing", result.ErrorCode);
    }

    [Fact]
    public void Ready_MetadataMissing_FailsClosed()
    {
        var (job, _) = StageTestCandidate("fp1", "k1", "cand-nometa", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-nometa.metadata.json");
        File.Delete(metaPath);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("metadata_missing_or_corrupt", result.ErrorCode);
    }

    [Fact]
    public void Ready_MetadataCorrupt_FailsClosed()
    {
        var (job, _) = StageTestCandidate("fp1", "k1", "cand-corruptmeta", 512, 512, Color.Green);
        var metaPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), "cand-corruptmeta.metadata.json");
        File.WriteAllText(metaPath, "NOT_JSON_AT_ALL");

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("metadata_missing_or_corrupt", result.ErrorCode);
    }

    [Fact]
    public void Ready_HashMismatch_FailsClosed()
    {
        var (job, path) = StageTestCandidate("fp1", "k1", "cand-hashmismatch", 512, 512, Color.Green);
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
        var (job, _) = StageTestCandidate("fp1", "k1", "cand-wrongdim", 256, 256, Color.Green);

        var result = _verifier.VerifyCandidate(job, 512, 512);

        Assert.False(result.IsValid);
        Assert.Equal("resolution_mismatch", result.ErrorCode);
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

                var (job, path) = StageTestCandidate(fp, rk, "cand-fail-commit", 512, 512, Color.Green);
                jobStore.UpsertItem(job);

                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;
                Assert.NotNull(lvQueue);

                // Tamper with the file to corrupt hash
                File.WriteAllBytes(path, CreateTestPng(512, 512, Color.Magenta));

                // Click Ready row -> verification should fail, unload image, mark job FailedPermanent
                form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
                Application.DoEvents();

                Assert.Null(form.ActiveApiCandidateMetadata);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
                Assert.NotNull(messageShown);
                Assert.Contains("failed verification", messageShown);

                var updatedJob = jobStore.GetItem(fp, rk);
                Assert.NotNull(updatedJob);
                Assert.Equal(GenerationItemStatus.FailedPermanent, updatedJob.Status);

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

                var (job, path) = StageTestCandidate(fp, rk, "cand-load-valid", 512, 512, Color.Green);
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
}
