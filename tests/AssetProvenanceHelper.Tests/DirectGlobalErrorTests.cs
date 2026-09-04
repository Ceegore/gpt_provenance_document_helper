using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class DirectGlobalErrorTests : IDisposable
{
    private readonly string _tempDir;

    public DirectGlobalErrorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_direct_err_" + Guid.NewGuid().ToString("N"));
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

    private static byte[] CreateTestPng(int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Green);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private sealed class ControllableDirectProvider : IImageGenerationProvider
    {
        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true, SupportsBatch: true, SupportsTransparentBackground: false, SupportsReferenceImages: false, SupportsArbitrarySize: false);

        public Func<ImageGenerationSpec, Task<ImageGenerationCandidate>>? GenerateFunc;
        public int GenerateCallCount;

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref GenerateCallCount);
            if (GenerateFunc != null)
            {
                return GenerateFunc(spec);
            }
            var rawBytes = CreateTestPng(816, 816);
            var rawSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBytes)).ToLowerInvariant();
            return Task.FromResult(new ImageGenerationCandidate(
                CandidateId: Guid.NewGuid().ToString("N"),
                CustomId: spec.CustomId,
                RawBytes: rawBytes,
                RawSha256: rawSha,
                ProviderWidth: 816,
                ProviderHeight: 816));
        }

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
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
        public string? LoadSecret(string name) => "sk-test-key";
        public void SaveSecret(string name, string secret) { }
        public void DeleteSecret(string name) { }
    }

    private string CreateMultiItemManifest(int count)
    {
        var manifestPath = Path.Combine(_tempDir, $"manifest_{Guid.NewGuid():N}.json");
        var assets = Enumerable.Range(1, count).Select(i => $$"""
        {
          "filename": "asset_{{i}}.png",
          "resolution": "512x512",
          "alpha": "not_required",
          "prompt": "Test asset prompt {{i}}"
        }
        """);

        File.WriteAllText(manifestPath, $$"""
        {
          "manifestVersion": 2,
          "assets": [
            {{string.Join(",\n", assets)}}
          ]
        }
        """);
        return manifestPath;
    }

    private (MainForm Form, ControllableDirectProvider Provider, GenerationJobStore JobStore) CreateTestForm(TestWorkspace workspace)
    {
        var settings = workspace.CreateSettings();
        var secrets = new FakeSecretStore();
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_" + Guid.NewGuid().ToString("N") + ".json"));
        var provider = new ControllableDirectProvider();

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
            generationJobStore: jobStore);

        return (form, provider, jobStore);
    }

    [Fact]
    public void DirectLoop_On401_CancelsRemainingTasks()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);

            provider.GenerateFunc = async _ =>
            {
                await Task.Delay(10);
                throw new OpenAiApiException(HttpStatusCode.Unauthorized, "invalid_api_key", null, "Incorrect API key provided.", "req_1");
            };

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateMultiItemManifest(5);
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnDirect = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnDirect);

                btnImport.PerformClick();
                btnDirect.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                var state = jobStore.Load();
                var failedItem = state.Items.FirstOrDefault(it => it.Status == GenerationItemStatus.FailedPermanent);
                Assert.NotNull(failedItem);
                Assert.Equal("global_direct_error", failedItem.ErrorCode);

                // Any non-failed items must remain Pending or UncertainAfterInterruption (never completed)
                Assert.DoesNotContain(state.Items, it => it.Status == GenerationItemStatus.Ready);

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
    public void DirectLoop_On403_CancelsRemainingTasks()
    {
        var ex = new OpenAiApiException(HttpStatusCode.Forbidden, "permission_denied", null, "Country not supported", null);
        Assert.True(MainForm.IsGlobalDirectError(ex, out var reason));
        Assert.Contains("Forbidden", reason);
    }

    [Fact]
    public void DirectLoop_On404ModelNotFound_CancelsRemainingTasks()
    {
        var ex = new OpenAiApiException(HttpStatusCode.NotFound, "model_not_found", null, "The model gpt-image-2 does not exist", null);
        Assert.True(MainForm.IsGlobalDirectError(ex, out var reason));
        Assert.Contains("404", reason);
    }

    [Fact]
    public void DirectLoop_On429_DoesNotCancelRemainingTasks()
    {
        var ex = new OpenAiApiException(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", null, "Rate limit exceeded", null);
        Assert.False(MainForm.IsGlobalDirectError(ex, out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void DirectLoop_GlobalError_ShowsSingleMessageBox()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, _) = CreateTestForm(workspace);

            var messageBoxCount = 0;
            provider.GenerateFunc = async _ =>
            {
                await Task.Delay(10);
                throw new OpenAiApiException(HttpStatusCode.Unauthorized, "invalid_api_key", null, "Incorrect API key provided.", "req_2");
            };

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { Interlocked.Increment(ref messageBoxCount); };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateMultiItemManifest(4);
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnDirect = form.Controls.Find("btnGenerateNow", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnDirect);

                btnImport.PerformClick();
                btnDirect.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                // Exactly 1 message box displayed to user despite multiple items!
                Assert.Equal(1, messageBoxCount);

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
