using System.Windows.Forms;
using System.Reflection;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class RequestQueuePersistenceUiTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.OpenFolderProvider = _ => { };
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFolderProvider = null;
                MainForm.OpenFileDialogProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateForm(TestWorkspace workspace) => new(
        workspace.CreateSettings(),
        workspace.CreateSettingsService(),
        workspace.CreateImageFinder(),
        workspace.CreateTemplateService(),
        workspace.CreateValidationService(),
        workspace.CreateAssetProcessor(),
        workspace.CreateSessionService(),
        workspace.CreateProviderTemplateCatalogService(),
        workspace.CreateRecentDocumentHistoryService(),
        workspace.CreateRequestProgressService(),
        null,
        null,
        null,
        null,
        workspace.CreateRequestQueueStateService());

    [Fact]
    public void ImportRestartAndClear_RestoresThenRemovesQueueState()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var manifestPath = Path.Combine(workspace.Root, "manifest.json");
            File.WriteAllText(manifestPath, """
                { "manifestVersion": 1, "assets": [
                  { "filename": "first.png", "resolution": "512x512", "prompt": "first prompt" },
                  { "filename": "second.png", "resolution": "1024x1024", "prompt": "second prompt" }
                ] }
                """);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            using (var initial = CreateForm(workspace))
            {
                typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(initial, null);
                Assert.Equal(2, Assert.IsType<ListView>(initial.Controls.Find("lvRequestQueue", true).Single()).Items.Count);
            }

            File.Delete(manifestPath);
            using var restored = CreateForm(workspace);
            var queue = Assert.IsType<ListView>(restored.Controls.Find("lvRequestQueue", true).Single());
            Assert.Equal(2, queue.Items.Count);
            Assert.Contains("restored", Assert.IsType<Label>(restored.Controls.Find("lblRequestSource", true).Single()).Text);

            typeof(MainForm).GetMethod("HandleClearRequestQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(restored, null);
            Assert.Empty(queue.Items);
            Assert.False(File.Exists(workspace.RequestQueueStatePath));
            Assert.False(File.Exists(workspace.RequestProgressPath));
        });
    }
}
