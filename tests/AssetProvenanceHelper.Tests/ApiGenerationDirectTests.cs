using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiGenerationDirectTests : IDisposable
{
    private readonly string _tempDir;

    public ApiGenerationDirectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_direct_test_" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeSecretStore : ISecretStore
    {
        public readonly Dictionary<string, string> Secrets = new(StringComparer.Ordinal);

        public string? LoadSecret(string name) =>
            Secrets.TryGetValue(name, out var val) ? val : null;

        public void SaveSecret(string name, string secret) =>
            Secrets[name] = secret;

        public void DeleteSecret(string name) =>
            Secrets.Remove(name);
    }

    private sealed class FakeProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public readonly List<ImageGenerationSpec> DirectRequests = [];

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default)
        {
            DirectRequests.Add(spec);
            var bytes = CreateTestPng(spec.GenerationWidth, spec.GenerationHeight, Color.Blue);
            return Task.FromResult(new ImageGenerationCandidate(
                CandidateId: Guid.NewGuid().ToString("N"),
                CustomId: spec.CustomId,
                RawBytes: bytes,
                RawSha256: "fake-sha",
                ProviderWidth: spec.GenerationWidth,
                ProviderHeight: spec.GenerationHeight,
                ProviderRequestId: "req-123"));
        }

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult completedBatch, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void ImageNormalizationService_NormalizesToExactDimensions()
    {
        // Target: 512x512, planned generation: 816x816 (aspect 1:1, scaled to min pixels)
        var plan = ImageSizePlanner.Plan(512, 512);
        var rawBytes = CreateTestPng(plan.GenerationWidth, plan.GenerationHeight, Color.Red);

        var result = ImageNormalizationService.Normalize(rawBytes, plan);

        Assert.NotNull(result);
        Assert.Equal(512, result.NormalizedWidth);
        Assert.Equal(512, result.NormalizedHeight);
        Assert.False(string.IsNullOrWhiteSpace(result.RawSha256));
        Assert.False(string.IsNullOrWhiteSpace(result.NormalizedSha256));

        // Re-read resulting PNG
        using var ms = new MemoryStream(result.NormalizedBytes);
        using var img = Image.FromStream(ms);
        Assert.Equal(512, img.Width);
        Assert.Equal(512, img.Height);
    }

    [Fact]
    public void GeneratedImageStagingService_SavesAndLoadsMetadata()
    {
        var stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging"));
        var rawBytes = CreateTestPng(816, 816, Color.Green);
        var normBytes = CreateTestPng(512, 512, Color.Green);
        var metadata = new ApiCandidateMetadata(
            CandidateId: "cand-1",
            Provider: "OpenAI",
            Model: "gpt-image-2",
            Mode: "direct",
            CustomId: "custom-1",
            TargetResolution: "512x512",
            ProviderResolution: "816x816",
            RawSha256: "raw-sha",
            NormalizedSha256: "norm-sha",
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ProviderRequestId: "req-123");

        var path = stagingService.SaveCandidate("fp1", "key1", "cand-1", rawBytes, normBytes, metadata);
        Assert.True(File.Exists(path));

        var loaded = stagingService.LoadMetadata("fp1", "key1", "cand-1");
        Assert.NotNull(loaded);
        Assert.Equal("cand-1", loaded.CandidateId);
        Assert.Equal("OpenAI", loaded.Provider);
        Assert.Equal("gpt-image-2", loaded.Model);
        Assert.Equal("512x512", loaded.TargetResolution);
    }

    [Fact]
    public void MainForm_GenerateNow_PreflightAndStaging_TransitionsToReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-test-key");

            var provider = new FakeProvider();
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

            form.Show();

            // Setup manifest with 1 eligible item and 1 blocked alpha item
            var manifestPath = Path.Combine(_tempDir, "manifest.json");
            File.WriteAllText(manifestPath, """
            {
              "manifestVersion": 2,
              "assets": [
                {
                  "filename": "eligible_item.png",
                  "resolution": "512x512",
                  "alpha": "not_required",
                  "prompt": "Eligible asset prompt"
                },
                {
                  "filename": "blocked_item.png",
                  "resolution": "512x512",
                  "alpha": "required",
                  "prompt": "Blocked asset prompt"
                }
              ]
            }
            """);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;

            var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
            var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
            var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

            Assert.NotNull(btnImport);
            Assert.NotNull(btnGenerate);
            Assert.NotNull(lvQueue);

            btnImport.PerformClick();
            Assert.Equal(2, lvQueue.Items.Count);

            // Execute Generate Now
            btnGenerate.PerformClick();

            // Wait a moment for async completion
            var waitCount = 0;
            while (provider.DirectRequests.Count == 0 && waitCount++ < 50)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }

            // Only the eligible item should have been requested (not the blocked one!)
            Assert.Single(provider.DirectRequests);
            Assert.Equal("eligible_item.png", provider.DirectRequests[0].FileName);

            // Let background task finish staging and UI updates
            waitCount = 0;
            while (lvQueue.Items[0].Text != "Ready" && waitCount++ < 50)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }

            Assert.Equal("Ready", lvQueue.Items[0].Text);
            Assert.Equal("Blocked: alpha", lvQueue.Items[1].Text);

            // Now activate the Ready row -> loads staged candidate into Main image slot
            form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
            Application.DoEvents();

            Assert.NotNull(form.ActiveApiCandidateMetadata);
            Assert.Equal("OpenAI", form.ActiveApiCandidateMetadata.Provider);
            Assert.NotNull(form.GetSelectedImage(ImageSlot.Main));

            // Activating unready row 1 must unload the staged image and metadata
            form.HandleRequestQueueItemActivate(lvQueue.Items[1]);
            Application.DoEvents();

            Assert.Null(form.ActiveApiCandidateMetadata);
            Assert.Null(form.GetSelectedImage(ImageSlot.Main));

            // Reactivating row 0 loads it again
            form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
            Application.DoEvents();
            Assert.NotNull(form.ActiveApiCandidateMetadata);

            // If user manually clears or changes Main slot to another file -> metadata cleared
            form.SetSelectedImage(ImageSlot.Main, null);
            Assert.Null(form.ActiveApiCandidateMetadata);

            form.Close();
        });
    }
}
