#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Final micro-batch for the remaining reachable branch lines.
/// </summary>
public class UpgradeV13ParanoidMicroTests
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateForm(TestWorkspace workspace)
    {
        return new MainForm(
            workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService(),
            workspace.CreateProviderTemplateCatalogService(),
            workspace.CreateRecentDocumentHistoryService(),
            workspace.CreateRequestProgressService());
    }

    private static T FindControl<T>(MainForm form, string name)
        where T : Control
    {
        var control =
            form.Controls.Find(name, true)
                .FirstOrDefault();

        Assert.NotNull(control);
        return Assert.IsType<T>(control);
    }

    [Fact]
    public void DirectPair_InvalidDownloadFolderFails()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var txtDownload =
                FindControl<TextBox>(form, "txtDownloadFolder");

            txtDownload.Text =
                Path.Combine(
                    workspace.Root,
                    "missing");

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => errorShown = true;

            try
            {
                var pair =
                    typeof(MainForm).GetMethod(
                        "TrySelectDirectReferencePair",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var result = pair!.Invoke(form, new object[] { 1 });

                Assert.Null(result);
                Assert.True(errorShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void DirectPair_InvalidReferenceImageFails()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var validMain =
                workspace.CreateImage(
                    "main.png",
                    new byte[] { 1, 2, 3 });

            File.SetLastWriteTimeUtc(
                validMain,
                DateTime.UtcNow);

            var invalidRef =
                Path.Combine(
                    workspace.Downloads,
                    "reference.png");

            File.WriteAllBytes(
                invalidRef,
                new byte[] { 0x00, 0x01, 0x02 });

            File.SetLastWriteTimeUtc(
                invalidRef,
                DateTime.UtcNow.AddMinutes(-1));

            using var form = CreateForm(workspace);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption == "Direct Reference image is invalid.";
                };

            try
            {
                var pair =
                    typeof(MainForm).GetMethod(
                        "TrySelectDirectReferencePair",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var result = pair!.Invoke(form, new object[] { 1 });

                Assert.Null(result);
                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_ActivateWithForeignTagIsNoop()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var activate =
                typeof(MainForm).GetMethod(
                    "HandleRequestQueueItemActivate",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            activate!.Invoke(
                form,
                new object[] { new ListViewItem("x") { Tag = "not-an-item" } });

            activate!.Invoke(form, new object?[] { null });

            var activeRequest =
                typeof(MainForm).GetField(
                    "_activeRequest",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form);

            Assert.Null(activeRequest);
        });
    }

    [Fact]
    public void MainForm_ClearPromptButtonClears()
    {
        RunOnSta(() =>
        {
using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            form.Show();

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "some prompt";

            var btnClear =
                FindControl<Button>(form, "btnClearPrompt");

            btnClear.PerformClick();

            Assert.Empty(txtPrompt.Text);
        });
    }

    [Fact]
    public void MainForm_KeyDownWithoutControlReturns()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var keyDown =
                typeof(MainForm).GetMethod(
                    "MainForm_KeyDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            // No Control modifier: nothing happens, no suppression.
            var plainM = new KeyEventArgs(Keys.M);
            keyDown!.Invoke(form, new object[] { form, plainM });

            Assert.False(plainM.SuppressKeyPress);
        });
    }

    [Fact]
    public void ImageSelection_DragEnterWithoutFilesIsNone()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var dragEnter =
                typeof(MainForm).GetMethod(
                    "ImageDrop_DragEnter",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var emptyData =
                new DataObject();

            var drag =
                new DragEventArgs(
                    emptyData,
                    0,
                    0,
                    0,
                    DragDropEffects.Copy,
                    DragDropEffects.None);

            dragEnter!.Invoke(
                form,
                new object?[] { null, drag });

            Assert.Equal(
                DragDropEffects.None,
                drag.Effect);

            var fileData =
                new DataObject(
                    DataFormats.FileDrop,
                    new[] { "C:\\x.png" });

            var dragFiles =
                new DragEventArgs(
                    fileData,
                    0,
                    0,
                    0,
                    DragDropEffects.Copy,
                    DragDropEffects.None);

            dragEnter.Invoke(
                form,
                new object?[] { null, dragFiles });

            Assert.Equal(
                DragDropEffects.Copy,
                dragFiles.Effect);
        });
    }

    [Fact]
    public void ImageSelection_DropSingleValidFileSelects()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var valid =
                workspace.CreateImage(
                    "dropped.png",
                    new byte[] { 1, 2, 3 });

            var drop =
                typeof(MainForm).GetMethod(
                    "ImageDrop_DragDrop",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var data =
                new DataObject(
                    DataFormats.FileDrop,
                    new[] { valid });

            var drag =
                new DragEventArgs(
                    data,
                    0,
                    0,
                    0,
                    DragDropEffects.Copy,
                    DragDropEffects.None);

            drop!.Invoke(
                form,
                new object[]
                {
                    ImageSlot.Main,
                    drag
                });

            Assert.Equal(
                Path.GetFullPath(valid),
                Path.GetFullPath(form.GetSelectedImage(ImageSlot.Main)!));
        });
    }
}
