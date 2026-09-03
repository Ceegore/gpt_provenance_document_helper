using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiGenerationBatchTests : IDisposable
{
    private readonly string _tempDir;

    public ApiGenerationBatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_batch_test_" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeBatchProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public readonly List<IReadOnlyList<ImageGenerationSpec>> SubmittedBatches = [];
        public string BatchStatusToReturn = "in_progress";
        public byte[]? CompletedImageBytes { get; set; }

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default)
        {
            SubmittedBatches.Add(specs);
            return Task.FromResult(new BatchSubmissionResult(
                ProviderInputFileId: "file-input-123",
                ProviderBatchId: "batch-prov-123",
                SubmittedCount: specs.Count,
                CreatedAtUtc: DateTimeOffset.UtcNow));
        }

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BatchStatusResult(
                ProviderBatchId: providerBatchId,
                Status: BatchStatusToReturn,
                OutputFileId: BatchStatusToReturn == "completed" ? "file-output-456" : null,
                ErrorFileId: null,
                TotalCount: 1,
                CompletedCount: BatchStatusToReturn == "completed" ? 1 : 0,
                FailedCount: 0));
        }

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult completedBatch, string apiKey, CancellationToken cancellationToken = default)
        {
            var specs = SubmittedBatches.SelectMany(s => s).ToList();
            var items = specs.Select(s => new BatchItemOutput(
                CustomId: s.CustomId,
                IsSuccess: true,
                ImageBytes: CompletedImageBytes ?? CreateTestPng(s.GenerationWidth, s.GenerationHeight, Color.Green),
                StatusCode: 200,
                ErrorCode: null,
                ErrorMessage: null,
                ProviderRequestId: "req-batch-1")).ToList();

            return Task.FromResult(new BatchDownloadResult(completedBatch.ProviderBatchId, items));
        }
    }

    [Fact]
    public void MainForm_ProductionBatch_SubmitsPollsAndStagesReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-test-key");

            var provider = new FakeBatchProvider();
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
                  "filename": "batch_item.png",
                  "resolution": "512x512",
                  "alpha": "not_required",
                  "prompt": "Batch item prompt"
                },
                {
                  "filename": "blocked_item.png",
                  "resolution": "512x512",
                  "alpha": "required",
                  "prompt": "Blocked item prompt"
                }
              ]
            }
            """);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;

            var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
            var btnBatch = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
            var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

            Assert.NotNull(btnImport);
            Assert.NotNull(btnBatch);
            Assert.NotNull(lvQueue);

            btnImport.PerformClick();
            Assert.Equal(2, lvQueue.Items.Count);

            // Execute Queue Production Batch
            btnBatch.PerformClick();

            // Wait a moment for submission
            var waitCount = 0;
            while (provider.SubmittedBatches.Count == 0 && waitCount++ < 50)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }

            Assert.Single(provider.SubmittedBatches);
            Assert.Single(provider.SubmittedBatches[0]);
            Assert.Equal("batch_item.png", provider.SubmittedBatches[0][0].FileName);

            // Item should now display as Batch queued / submitted
            waitCount = 0;
            while (lvQueue.Items[0].Text != "Batch queued" && waitCount++ < 50)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            Assert.Equal("Batch queued", lvQueue.Items[0].Text);
            Assert.Equal("Blocked: alpha", lvQueue.Items[1].Text);

            // Now simulate batch completion on OpenAI side
            provider.BatchStatusToReturn = "completed";
            form.PollActiveBatchesAsync().GetAwaiter().GetResult();
            Application.DoEvents();

            // Item should now be Ready!
            Assert.Equal("Ready", lvQueue.Items[0].Text);

            // Activate Ready row -> loads staged candidate
            form.HandleRequestQueueItemActivate(lvQueue.Items[0]);
            Application.DoEvents();

            Assert.NotNull(form.ActiveApiCandidateMetadata);
            Assert.Equal("batch", form.ActiveApiCandidateMetadata.Mode);
            Assert.Equal("OpenAI", form.ActiveApiCandidateMetadata.Provider);

            form.Close();
        });
    }
}
