using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiStagingAndSecretStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ApiStagingAndSecretStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_store_test_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void StagingService_RespectsStateDirectoryOverride()
    {
        var customStateDir = Path.Combine(_tempDir, "custom_state");
        Directory.CreateDirectory(customStateDir);

        var prevOverride = AppBootstrap.StateDirectoryOverride;
        AppBootstrap.StateDirectoryOverride = () => customStateDir;
        try
        {
            var staging = new GeneratedImageStagingService();
            Assert.Equal(Path.Combine(customStateDir, "generated"), staging.BaseStagingPath);
        }
        finally
        {
            AppBootstrap.StateDirectoryOverride = prevOverride;
        }
    }

    [Fact]
    public void DpapiSecretStore_RespectsStateDirectoryOverride()
    {
        var customStateDir = Path.Combine(_tempDir, "custom_state");
        Directory.CreateDirectory(customStateDir);

        var prevOverride = AppBootstrap.StateDirectoryOverride;
        AppBootstrap.StateDirectoryOverride = () => customStateDir;
        try
        {
            var store = new DpapiSecretStore();
            Assert.Equal(Path.Combine(customStateDir, "secrets.dat"), store.StoragePath);
        }
        finally
        {
            AppBootstrap.StateDirectoryOverride = prevOverride;
        }
    }

    [Fact]
    public void SettingsDialog_LoadsAndSavesOpenAiModel()
    {
        var secretStore = new InMemorySecretStore();
        var settings = new AppSettings
        {
            OpenAiModel = "gpt-image-2"
        };

        using var dialog = new SettingsDialog(settings, secretStore);
        var cmbModel = dialog.Controls.Find("cmbModel", true).OfType<System.Windows.Forms.ComboBox>().FirstOrDefault();
        Assert.NotNull(cmbModel);
        Assert.Equal("gpt-image-2", cmbModel.SelectedItem);

        var btnOk = dialog.Controls.Find("btnOk", true).OfType<System.Windows.Forms.Button>().FirstOrDefault();
        Assert.NotNull(btnOk);
        btnOk.PerformClick();

        Assert.Equal("gpt-image-2", settings.OpenAiModel);
    }

    [Fact]
    public void CorruptSecretStore_OpenSettings_ShowsErrorNoCrash()
    {
        RunOnSta(() =>
        {
            var storePath = Path.Combine(_tempDir, "corrupt_secrets_nocrash.dat");
            File.WriteAllBytes(storePath, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03]);
            var secretStore = new DpapiSecretStore(storePath);

            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                secretStore: secretStore);

            string? errorShown = null;
            MainForm.MessageBoxProvider = (_, msg, title, _, _) => { errorShown = msg; };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.No;

            try
            {
                form.Show();
                var btnSettings = form.Controls.Find("btnSettings", true).FirstOrDefault() as Button;
                Assert.NotNull(btnSettings);

                btnSettings.PerformClick();

                Assert.NotNull(errorShown);
                Assert.Contains("The encrypted API secret store is corrupt or cannot be decrypted", errorShown);

                form.Close();
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
            }
        });
    }

    [Fact]
    public void CorruptSecretStore_IsNotOverwritten()
    {
        RunOnSta(() =>
        {
            var storePath = Path.Combine(_tempDir, "corrupt_secrets_not_overwritten.dat");
            byte[] corruptBytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];
            File.WriteAllBytes(storePath, corruptBytes);
            var secretStore = new DpapiSecretStore(storePath);

            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                secretStore: secretStore);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.No;

            try
            {
                form.Show();
                var btnSettings = form.Controls.Find("btnSettings", true).FirstOrDefault() as Button;
                Assert.NotNull(btnSettings);
                btnSettings.PerformClick();

                var currentBytes = File.ReadAllBytes(storePath);
                Assert.Equal(corruptBytes, currentBytes);

                form.Close();
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
            }
        });
    }

    [Fact]
    public void CorruptSecretStore_UserCancelsReset_FilePreserved()
    {
        RunOnSta(() =>
        {
            var storePath = Path.Combine(_tempDir, "corrupt_secrets_cancel.dat");
            byte[] corruptBytes = [0xAA, 0xBB, 0xCC, 0xDD];
            File.WriteAllBytes(storePath, corruptBytes);
            var secretStore = new DpapiSecretStore(storePath);

            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                secretStore: secretStore);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.No;

            try
            {
                form.Show();
                var btnSettings = form.Controls.Find("btnSettings", true).FirstOrDefault() as Button;
                Assert.NotNull(btnSettings);
                btnSettings.PerformClick();

                Assert.True(File.Exists(storePath));
                Assert.Equal(corruptBytes, File.ReadAllBytes(storePath));

                form.Close();
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
            }
        });
    }

    [Fact]
    public void CorruptSecretStore_UserConfirmsReset_FileDeleted()
    {
        RunOnSta(() =>
        {
            var storePath = Path.Combine(_tempDir, "corrupt_secrets_confirm.dat");
            File.WriteAllBytes(storePath, [0xAA, 0xBB, 0xCC, 0xDD]);
            var secretStore = new DpapiSecretStore(storePath);

            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                secretStore: secretStore);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.Yes;

            try
            {
                form.Show();
                var btnSettings = form.Controls.Find("btnSettings", true).FirstOrDefault() as Button;
                Assert.NotNull(btnSettings);
                btnSettings.PerformClick();

                Assert.False(File.Exists(storePath));

                form.Close();
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
            }
        });
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public string? LoadSecret(string name) => _secrets.TryGetValue(name, out var val) ? val : null;
        public void SaveSecret(string name, string secret) => _secrets[name] = secret;
        public void DeleteSecret(string name) => _secrets.Remove(name);
    }
}
