using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class BatchSubmissionCheckpointTests : IDisposable
{
    private readonly string _tempDir;

    public BatchSubmissionCheckpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_batch_checkpoint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    private sealed class ControllableBatchProvider : IImageGenerationProvider
    {
        private readonly GenerationJobStore _jobStore;

        public ControllableBatchProvider(GenerationJobStore jobStore)
        {
            _jobStore = jobStore;
        }

        public string ProviderId => "OpenAI";

        public ProviderCapabilities GetCapabilities(string model) =>
            new(SupportsTextToImage: true,
                SupportsBatch: true,
                SupportsTransparentBackground: false,
                SupportsReferenceImages: false,
                SupportsArbitrarySize: false);

        public int UploadCallCount;
        public int CreateBatchCallCount;
        public bool ThrowOnUpload;
        public bool ThrowOnCreateBatch;
        public string UploadedFileId = "file-input-123";
        public string CreatedBatchId = "batch-remote-456";
        public string? InputFileIdSeenDuringCreateBatch;

        public Task<string> UploadBatchInputFileAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default)
        {
            UploadCallCount++;
            if (ThrowOnUpload)
            {
                throw new HttpRequestException("Upload failed network error");
            }
            return Task.FromResult(UploadedFileId);
        }

        public Task<BatchSubmissionResult> CreateBatchAsync(string inputFileId, string apiKey, CancellationToken cancellationToken = default)
        {
            CreateBatchCallCount++;
            // Checkpoint verification: Has ProviderInputFileId been persisted to disk BEFORE CreateBatch is called?
            var batchInStore = _jobStore.Load().Batches.FirstOrDefault();
            InputFileIdSeenDuringCreateBatch = batchInStore?.ProviderInputFileId;

            if (ThrowOnCreateBatch)
            {
                throw new InvalidOperationException("Account quota exceeded for batch creation");
            }
            return Task.FromResult(new BatchSubmissionResult(inputFileId, CreatedBatchId, 1, DateTimeOffset.UtcNow));
        }

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default) =>
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

    private (MainForm Form, ControllableBatchProvider Provider, GenerationJobStore JobStore) CreateTestForm(TestWorkspace workspace)
    {
        var settings = workspace.CreateSettings();
        var secrets = new FakeSecretStore();
        var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs_" + Guid.NewGuid().ToString("N") + ".json"));
        var provider = new ControllableBatchProvider(jobStore);

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

    private string CreateTestManifest()
    {
        var manifestPath = Path.Combine(_tempDir, "manifest_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(manifestPath, """
        {
          "manifestVersion": 2,
          "assets": [
            {
              "filename": "hero.png",
              "resolution": "512x512",
              "alpha": "not_required",
              "prompt": "Test batch prompt"
            }
          ]
        }
        """);
        return manifestPath;
    }

    [Fact]
    public void BatchSubmission_PersistsInputFileId_BeforeBatchCreate()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);

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
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();
                btnQueue.PerformClick();

                // Wait for async submission to finish
                for (var i = 0; i < 50 && provider.CreateBatchCallCount == 0; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.UploadCallCount);
                Assert.Equal(1, provider.CreateBatchCallCount);

                // Provider checked that InputFileId was persisted to disk BEFORE CreateBatch was executed
                Assert.Equal("file-input-123", provider.InputFileIdSeenDuringCreateBatch);

                var state = jobStore.Load();
                var batch = Assert.Single(state.Batches);
                Assert.Equal("Submitted", batch.Status);
                Assert.Equal("file-input-123", batch.ProviderInputFileId);
                Assert.Equal("batch-remote-456", batch.ProviderBatchId);

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
    public void BatchSubmission_InputFilePersistFails_NeverCallsBatchCreate()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? messageShown = null;
                MainForm.MessageBoxProvider = (_, msg, _, _, _) => { messageShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();

                // Inject failure when persisting ProviderInputFileId
                GenerationJobStore.OnBeforeSaveCoreForTests = state =>
                {
                    if (state.Batches.Any(b => !string.IsNullOrEmpty(b.ProviderInputFileId) && string.IsNullOrEmpty(b.ProviderBatchId)))
                    {
                        throw new IOException("Simulated disk error when saving ProviderInputFileId");
                    }
                };

                btnQueue.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.UploadCallCount);
                // CreateBatch MUST NEVER be called if saving input file ID failed!
                Assert.Equal(0, provider.CreateBatchCallCount);

                Assert.NotNull(messageShown);
                Assert.Contains("Batch creation was aborted", messageShown);

                form.Close();
            }
            finally
            {
                GenerationJobStore.OnBeforeSaveCoreForTests = null;
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void BatchSubmission_BatchCreateFails_RetainsInputFileId()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);
            provider.ThrowOnCreateBatch = true;

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? messageShown = null;
                MainForm.MessageBoxProvider = (_, msg, _, _, _) => { messageShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();
                btnQueue.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.UploadCallCount);
                Assert.Equal(1, provider.CreateBatchCallCount);

                var state = jobStore.Load();
                var batch = Assert.Single(state.Batches);
                Assert.Equal("FailedLocal", batch.Status);
                Assert.Equal("file-input-123", batch.ProviderInputFileId);
                Assert.Null(batch.ProviderBatchId);

                Assert.NotNull(messageShown);
                Assert.Contains("file-input-123", messageShown);

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
    public void BatchSubmission_BatchIdPersistFails_NeverAutoResubmits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? messageShown = null;
                MainForm.MessageBoxProvider = (_, msg, _, _, _) => { messageShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();

                // Inject failure when persisting ProviderBatchId
                GenerationJobStore.OnBeforeSaveCoreForTests = state =>
                {
                    if (state.Batches.Any(b => !string.IsNullOrEmpty(b.ProviderBatchId)))
                    {
                        throw new IOException("Simulated disk error when saving ProviderBatchId");
                    }
                };

                btnQueue.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                // Exactly 1 upload and 1 create - NEVER auto-resubmitted
                Assert.Equal(1, provider.UploadCallCount);
                Assert.Equal(1, provider.CreateBatchCallCount);

                Assert.NotNull(messageShown);
                Assert.Contains("Local recovery state could not be saved", messageShown);

                form.Close();
            }
            finally
            {
                GenerationJobStore.OnBeforeSaveCoreForTests = null;
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void BatchSubmission_UploadFails_MarksFailedLocal()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);
            provider.ThrowOnUpload = true;

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                string? messageShown = null;
                MainForm.MessageBoxProvider = (_, msg, _, _, _) => { messageShown = msg; };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var manifestPath = CreateTestManifest();
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault() as Button;
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();
                btnQueue.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.UploadCallCount);
                Assert.Equal(0, provider.CreateBatchCallCount);

                var state = jobStore.Load();
                var batch = Assert.Single(state.Batches);
                Assert.Equal("FailedLocal", batch.Status);
                Assert.Null(batch.ProviderInputFileId);
                Assert.Null(batch.ProviderBatchId);

                Assert.NotNull(messageShown);
                Assert.Contains("Failed to upload batch input file", messageShown);

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
    public void BatchIdPersistFailure_SecondSubmissionIsBlocked()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var (form, provider, jobStore) = CreateTestForm(workspace);

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
                var btnQueue = form.Controls.Find("btnQueueProductionBatch", true).FirstOrDefault() as Button;
                Assert.NotNull(btnImport);
                Assert.NotNull(btnQueue);

                btnImport.PerformClick();

                GenerationJobStore.OnBeforeSaveCoreForTests = state =>
                {
                    if (state.Batches.Any(batch => batch.ProviderBatchId == provider.CreatedBatchId))
                    {
                        throw new IOException("Simulated persistence failure when saving ProviderBatchId");
                    }
                };

                btnQueue.PerformClick();

                // Wait for async submission to attempt and fail persisting ProviderBatchId
                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.Equal(1, provider.CreateBatchCallCount);

                // Clear the persistence failure hook
                GenerationJobStore.OnBeforeSaveCoreForTests = null;

                // Attempt a second submission - MUST be blocked because items are BatchQueued/Uncertain
                btnQueue.PerformClick();

                for (var i = 0; i < 50; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                // Critical assertion: CreateBatch must NOT have been called a second time
                Assert.Equal(1, provider.CreateBatchCallCount);

                var state = jobStore.Load();
                var item = Assert.Single(state.Items);
                Assert.Contains(item.Status, new[]
                {
                    GenerationItemStatus.BatchQueued,
                    GenerationItemStatus.UncertainAfterInterruption
                });
                Assert.NotEqual(GenerationItemStatus.Pending, item.Status);

                form.Close();
            }
            finally
            {
                GenerationJobStore.OnBeforeSaveCoreForTests = null;
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }
}
