using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiSettingsUiTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ApiSettingsUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
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

    private sealed class FakeSecretStore : ISecretStore
    {
        public readonly Dictionary<string, string> Secrets = new(StringComparer.Ordinal);

        public string? LoadSecret(string name)
        {
            return Secrets.TryGetValue(name, out var val) ? val : null;
        }

        public void SaveSecret(string name, string secret)
        {
            Secrets[name] = secret;
        }

        public void DeleteSecret(string name)
        {
            Secrets.Remove(name);
        }
    }

    [Fact]
    public void SettingsDialog_SavesAndDeletesKeyViaSecretStore()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings();
            var fakeSecrets = new FakeSecretStore();

            using var dialog = new SettingsDialog(settings, fakeSecrets);
            dialog.Show();

            var txtKey = dialog.Controls.Find("txtApiKey", true).FirstOrDefault() as TextBox;
            var btnSave = dialog.Controls.Find("btnSaveKey", true).FirstOrDefault() as Button;
            var btnDelete = dialog.Controls.Find("btnDeleteKey", true).FirstOrDefault() as Button;

            Assert.NotNull(txtKey);
            Assert.NotNull(btnSave);
            Assert.NotNull(btnDelete);
            Assert.True(txtKey.UseSystemPasswordChar);

            // Test saving key
            txtKey.Text = "sk-test-secret-12345";
            btnSave.PerformClick();

            Assert.Equal("sk-test-secret-12345", fakeSecrets.LoadSecret(SettingsDialog.OpenAiApiKeySecretName));

            // Test deleting key
            btnDelete.PerformClick();
            Assert.Null(fakeSecrets.LoadSecret(SettingsDialog.OpenAiApiKeySecretName));
            Assert.Equal(string.Empty, txtKey.Text);

            dialog.Close();
        });
    }

    [Fact]
    public void SettingsService_Save_DoesNotIncludeApiKey()
    {
        var service = new SettingsService(_settingsPath);
        var settings = new AppSettings
        {
            DirectImageQuality = "high",
            BatchImageQuality = "low",
            DirectStartsPerMinute = 10,
            DirectMaxConcurrency = 8,
            BatchPollSeconds = 45,
            MaxBatchRequestsPerSubmission = 200,
            DirectRetryAttempts = 4
        };

        service.Save(settings);

        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("sk-", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);

        var loaded = service.Load();
        Assert.Equal("high", loaded.DirectImageQuality);
        Assert.Equal("low", loaded.BatchImageQuality);
        Assert.Equal(10, loaded.DirectStartsPerMinute);
        Assert.Equal(8, loaded.DirectMaxConcurrency);
        Assert.Equal(45, loaded.BatchPollSeconds);
        Assert.Equal(200, loaded.MaxBatchRequestsPerSubmission);
        Assert.Equal(4, loaded.DirectRetryAttempts);
    }

    [Fact]
    public void DpapiSecretStore_Roundtrip_EncryptsOnDisk()
    {
        var secretFile = Path.Combine(_tempDir, "secrets.dat");
        var store = new DpapiSecretStore(secretFile);

        store.SaveSecret("test_name", "my-super-secret-value");

        // Plain text must not appear in raw file bytes
        var rawBytes = File.ReadAllBytes(secretFile);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("my-super-secret-value", rawText);

        var loaded = store.LoadSecret("test_name");
        Assert.Equal("my-super-secret-value", loaded);

        store.DeleteSecret("test_name");
        Assert.Null(store.LoadSecret("test_name"));
    }
}
