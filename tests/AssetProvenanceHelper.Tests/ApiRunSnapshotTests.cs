using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiRunSnapshotTests : IDisposable
{
    private readonly string _tempDir;

    public ApiRunSnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_snapshot_test_" + Guid.NewGuid().ToString("N"));
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

    private sealed class BlockingFakeProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public readonly List<ImageGenerationSpec> DirectRequests = [];
        public TaskCompletionSource<bool> AllowResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default)
        {
            DirectRequests.Add(spec);
            await AllowResponse.Task;
            var bytes = CreateTestPng(spec.GenerationWidth, spec.GenerationHeight, Color.DarkBlue);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return new ImageGenerationCandidate(
                CandidateId: Guid.NewGuid().ToString("N"),
                CustomId: spec.CustomId,
                RawBytes: bytes,
                RawSha256: sha,
                ProviderWidth: spec.GenerationWidth,
                ProviderHeight: spec.GenerationHeight,
                ProviderRequestId: "req-block-123");
        }

#pragma warning disable CS0618
        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
#pragma warning restore CS0618

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult completedBatch, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void DirectRun_CapturesManifestAndSettingsSnapshot_AndBlocksImportDuringRun()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            settings.OpenAiModel = "gpt-image-2";
            settings.DirectImageQuality = "medium";

            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-test-key");

            var provider = new BlockingFakeProvider();
            var jobStorePath = Path.Combine(_tempDir, "jobs.json");
            var jobStore = new GenerationJobStore(jobStorePath);

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
                string? lastMessageBoxText = null;
                MainForm.MessageBoxProvider = (_, msg, title, _, _) => { lastMessageBoxText = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestAPath = Path.Combine(_tempDir, "manifestA.json");
                File.WriteAllText(manifestAPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "itemA.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Item A prompt"
                    }
                  ]
                }
                """);

                var manifestBPath = Path.Combine(_tempDir, "manifestB.json");
                File.WriteAllText(manifestBPath, """
                {
                  "manifestVersion": 2,
                  "assets": [
                    {
                      "filename": "itemB.png",
                      "resolution": "512x512",
                      "alpha": "not_required",
                      "prompt": "Item B prompt"
                    }
                  ]
                }
                """);

                MainForm.OpenFileDialogProvider = (_, _) => manifestAPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnGenerate = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                var lvQueue = form.Controls.Find("lvRequestQueue", true).FirstOrDefault() as ListView;

                Assert.NotNull(btnImport);
                Assert.NotNull(btnGenerate);
                Assert.NotNull(lvQueue);

                btnImport.PerformClick();
                Assert.Single(lvQueue.Items);

                var manifestA = typeof(MainForm).GetField("_currentManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form) as AssetRequestManifest;
                Assert.NotNull(manifestA);
                var fingerprintA = manifestA.ManifestFingerprint;

                // Start generation (which blocks in provider)
                btnGenerate.PerformClick();

                // Wait until provider sees request
                var waitCount = 0;
                while (provider.DirectRequests.Count == 0 && waitCount++ < 50)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }

                Assert.Single(provider.DirectRequests);
                var capturedSpec = provider.DirectRequests[0];
                Assert.Equal(fingerprintA, capturedSpec.ManifestFingerprint);
                Assert.Equal("gpt-image-2", capturedSpec.Model);
                Assert.Equal("medium", capturedSpec.Quality);

                // Verify UI buttons disabled while run active
                Assert.False(btnImport.Enabled, "btnImportRequest should be disabled while generation is active.");
                Assert.False(btnGenerate.Enabled, "btnGenerateNow should be disabled while generation is active.");

                // Attempt programmatic import of manifest B while run is active
                MainForm.OpenFileDialogProvider = (_, _) => manifestBPath;
                typeof(MainForm).GetMethod("HandleImportRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(form, null);

                Assert.NotNull(lastMessageBoxText);
                Assert.Contains("Wait until the local operation has finished", lastMessageBoxText);

                // Change settings mid-run
                settings.OpenAiModel = "dall-e-3";
                settings.DirectImageQuality = "hd";

                // Unblock provider
                provider.AllowResponse.SetResult(true);

                // Wait for completion
                waitCount = 0;
                while (lvQueue.Items[0].Text != "Ready" && waitCount++ < 50)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }

                Assert.Equal("Ready", lvQueue.Items[0].Text);

                // Verify job in store was saved under Fingerprint A with original model & quality
                var state = jobStore.Load();
                var itemsForA = state.Items.Where(i => i.ManifestFingerprint == fingerprintA).ToList();
                Assert.Single(itemsForA);
                var record = itemsForA[0];
                Assert.Equal(fingerprintA, record.ManifestFingerprint);
                Assert.Equal("gpt-image-2", record.Model);
                Assert.Equal("medium", record.Quality);
                Assert.Equal(GenerationItemStatus.Ready, record.Status);

                // And no items under any other fingerprint
                Assert.All(state.Items, item => Assert.Equal(fingerprintA, item.ManifestFingerprint));

                // Once run finishes, import is re-enabled
                Assert.True(btnImport.Enabled, "btnImportRequest should be re-enabled after generation completes.");

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
