using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiSettingsSecretUxTests
{
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

        public string? LoadSecret(string name) =>
            Secrets.TryGetValue(name, out var val) ? val : null;

        public void SaveSecret(string name, string secret) =>
            Secrets[name] = secret;

        public void DeleteSecret(string name) =>
            Secrets.Remove(name);
    }

    [Fact]
    public void SettingsDialog_ExistingSecret_LeavesTextboxBlankWithPlaceholder()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-existing-secret-123");

            using var dialog = new SettingsDialog(settings, secrets);
            dialog.Show();

            var txtApiKey = dialog.Controls.Find("txtApiKey", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtApiKey);

            // Rule 2: txtApiKey.Text remains empty ("") when a secret exists
            Assert.Equal(string.Empty, txtApiKey.Text);

            // Rule 1: txtApiKey.PlaceholderText indicates key is configured
            Assert.Contains("Key configured", txtApiKey.PlaceholderText);

            dialog.Close();
        });
    }

    [Fact]
    public void SettingsDialog_BlankSubmit_PreservesExistingSecret()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-keep-me-intact");

            using var dialog = new SettingsDialog(settings, secrets);
            dialog.Show();

            var txtApiKey = dialog.Controls.Find("txtApiKey", true).FirstOrDefault() as TextBox;
            var btnOk = dialog.Controls.Find("btnOk", true).FirstOrDefault() as Button;
            Assert.NotNull(txtApiKey);
            Assert.NotNull(btnOk);

            // User enters nothing (leaves textbox blank) and clicks OK
            Assert.Equal(string.Empty, txtApiKey.Text);
            btnOk.PerformClick();

            // Rule 3: Existing secret remains untouched!
            Assert.Equal("sk-keep-me-intact", secrets.LoadSecret(SettingsDialog.OpenAiApiKeySecretName));

            dialog.Close();
        });
    }

    [Fact]
    public void SettingsDialog_ExplicitNewKey_OverwritesSecret()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-old-secret");

            using var dialog = new SettingsDialog(settings, secrets);
            dialog.Show();

            var txtApiKey = dialog.Controls.Find("txtApiKey", true).FirstOrDefault() as TextBox;
            var btnOk = dialog.Controls.Find("btnOk", true).FirstOrDefault() as Button;
            Assert.NotNull(txtApiKey);
            Assert.NotNull(btnOk);

            // User explicitly enters a new secret and submits
            txtApiKey.Text = "sk-brand-new-secret-456";
            btnOk.PerformClick();

            // Rule 3: Explicit new text overwrites the stored secret
            Assert.Equal("sk-brand-new-secret-456", secrets.LoadSecret(SettingsDialog.OpenAiApiKeySecretName));

            dialog.Close();
        });
    }

    [Fact]
    public void SettingsDialog_ExplicitDelete_RemovesSecretAfterConfirmation()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings();
            var secrets = new FakeSecretStore();
            secrets.SaveSecret(SettingsDialog.OpenAiApiKeySecretName, "sk-to-be-deleted");

            try
            {
                var confirmShown = false;
                SettingsDialog.ConfirmBoxProvider = (_, msg, title, _, _) =>
                {
                    confirmShown = true;
                    Assert.Contains("delete", msg, StringComparison.OrdinalIgnoreCase);
                    return DialogResult.Yes;
                };

                using var dialog = new SettingsDialog(settings, secrets);
                dialog.Show();

                var btnDelete = dialog.Controls.Find("btnDeleteApiKey", true).FirstOrDefault() as Button
                    ?? dialog.Controls.Find("btnDeleteKey", true).FirstOrDefault() as Button;
                Assert.NotNull(btnDelete);

                btnDelete.PerformClick();

                Assert.True(confirmShown);
                // Secret was deleted after confirmation
                Assert.Null(secrets.LoadSecret(SettingsDialog.OpenAiApiKeySecretName));

                dialog.Close();
            }
            finally
            {
                SettingsDialog.ConfirmBoxProvider = null;
            }
        });
    }
}
