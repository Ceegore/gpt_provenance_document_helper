using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Dialogs;

namespace AssetProvenanceHelper.Tests;

public sealed class RequestQueueScrollRetentionTests
{
    [Fact]
    public void SavedKeyStillPresent_AtDifferentIndex_ReturnsThatIndex()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c", "key-d" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, "key-c", savedTopIndex: 0);

        Assert.Equal(2, index);
    }

    [Fact]
    public void SavedKeyRemoved_FallsBackToClampedSavedIndex()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, "key-removed", savedTopIndex: 1);

        Assert.Equal(1, index);
    }

    [Fact]
    public void EmptyList_ReturnsNegativeOne()
    {
        var index = MainForm.ResolveRestoredTopIndex([], "key-a", savedTopIndex: 0);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void NullSavedKey_WithValidSavedIndex_ReturnsClampedIndex()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, savedTopRequestKey: null, savedTopIndex: 1);

        Assert.Equal(1, index);
    }

    [Fact]
    public void EmptySavedKey_WithValidSavedIndex_ReturnsClampedIndex()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, savedTopRequestKey: string.Empty, savedTopIndex: 2);

        Assert.Equal(2, index);
    }

    [Fact]
    public void SavedIndexBeyondEnd_ClampsToLastIndex()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, savedTopRequestKey: null, savedTopIndex: 99);

        Assert.Equal(2, index);
    }

    /// <summary>
    /// The pure helper above only decides *which* row to scroll to. This test
    /// drives a real, shown ListView so the capture/restore round trip itself is
    /// covered: activating a row must not send a long queue back to its top.
    /// </summary>
    [Fact]
    public void ActivatingARow_KeepsTheScrolledQueuePosition()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
            MainForm.OpenFolderProvider = _ => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            try
            {
                var manifest = new StringBuilder("{ \"manifestVersion\": 1, \"assets\": [");
                for (var index = 0; index < 60; index++)
                {
                    manifest.Append(index == 0 ? string.Empty : ",");
                    manifest.Append($"{{ \"filename\": \"row_{index:D3}.png\", \"resolution\": \"512x512\", \"prompt\": \"prompt {index}\" }}");
                }
                manifest.Append("] }");
                var manifestPath = Path.Combine(workspace.Root, "long-queue.json");
                File.WriteAllText(manifestPath, manifest.ToString());

                using var form = new MainForm(
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
                    workspace.CreateRequestQueueStateService(),
                    workspace.CreatePixelExactBatchStateService());
                form.Show();

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, null);

                var queue = Assert.IsType<ListView>(form.Controls.Find("lvRequestQueue", true).Single());
                Assert.Equal(60, queue.Items.Count);

                queue.TopItem = queue.Items[40];
                var scrolledTop = queue.TopItem!.Index;
                Assert.True(scrolledTop > 0, "The queue must be scrollable for this regression to mean anything.");

                form.HandleRequestQueueItemActivate(queue.Items[scrolledTop + 1]);

                Assert.Equal(scrolledTop, queue.TopItem!.Index);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                MainForm.OpenFolderProvider = null;
                MainForm.OpenFileDialogProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    [Fact]
    public void NothingSaved_ReturnsNegativeOne()
    {
        var currentKeys = new[] { "key-a", "key-b", "key-c" };

        var index = MainForm.ResolveRestoredTopIndex(currentKeys, savedTopRequestKey: null, savedTopIndex: -1);

        Assert.Equal(-1, index);
    }
}
