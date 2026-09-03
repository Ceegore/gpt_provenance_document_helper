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

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public string? LoadSecret(string name) => _secrets.TryGetValue(name, out var val) ? val : null;
        public void SaveSecret(string name, string secret) => _secrets[name] = secret;
        public void DeleteSecret(string name) => _secrets.Remove(name);
    }
}
