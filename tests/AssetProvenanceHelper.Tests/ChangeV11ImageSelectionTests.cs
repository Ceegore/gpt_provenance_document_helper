#nullable enable
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public class ChangeV11ImageSelectionTests
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

    [Fact]
    public void ReferenceRefresh_DoesNotChangeMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Main, main1);
            Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));

            form.RefreshImageSelection(ImageSlot.Reference);

            Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));
        });
    }

    [Fact]
    public void MainRefresh_DoesNotChangeReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Reference, ref1);
            Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));

            form.RefreshImageSelection(ImageSlot.Main);

            Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));
        });
    }

    [Fact]
    public void ReferenceChoose_DoesNotChangeMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Main, main1);

            MainForm.OpenFileDialogProvider = (owner, initialDir) => ref1;
            try
            {
                form.ChooseImageFile(ImageSlot.Reference);
                Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));
                Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void MainChoose_DoesNotChangeReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            MainForm.OpenFileDialogProvider = (owner, initialDir) => main1;
            try
            {
                form.ChooseImageFile(ImageSlot.Main);
                Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));
                Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ReferenceDrop_DoesNotChangeMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Main, main1);

            var dataObj = new DataObject(DataFormats.FileDrop, new string[] { ref1 });
            var dragEventArgs = new DragEventArgs(dataObj, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);

            var dropMethod = typeof(MainForm).GetMethod("ImageDrop_DragDrop", BindingFlags.NonPublic | BindingFlags.Instance);
            dropMethod?.Invoke(form, new object[] { ImageSlot.Reference, dragEventArgs });

            Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));
            Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));
        });
    }

    [Fact]
    public void MainDrop_DoesNotChangeReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            var dataObj = new DataObject(DataFormats.FileDrop, new string[] { main1 });
            var dragEventArgs = new DragEventArgs(dataObj, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);

            var dropMethod = typeof(MainForm).GetMethod("ImageDrop_DragDrop", BindingFlags.NonPublic | BindingFlags.Instance);
            dropMethod?.Invoke(form, new object[] { ImageSlot.Main, dragEventArgs });

            Assert.Equal(ref1, form.GetSelectedImage(ImageSlot.Reference));
            Assert.Equal(main1, form.GetSelectedImage(ImageSlot.Main));
        });
    }

    [Fact]
    public void CTA_DoesNotImplicitlyRefresh()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var newDownload = workspace.CreateImage("new_download.png", new byte[] { 99, 99, 99 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            // With no slot selected, HandleReference will not implicitly pick new_download.png
            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleRefMethod?.Invoke(form, null);

            // Reference was not created because candidate was empty
            Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
        });
    }

    [Fact]
    public void ReferenceReplacement_ClearsMainAndPrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
            var mainCandidate = workspace.CreateImage("main_candidate.png", new byte[] { 7, 8, 9 });

            var session = processor.ProcessReference(settings, "asset_repl_test", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "Old prompt to be cleared";

            form.SetSelectedImage(ImageSlot.Reference, ref2);
            form.SetSelectedImage(ImageSlot.Main, mainCandidate);

            Dialogs.TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Confirm replace

            var replaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
            replaceMethod?.Invoke(form, null);

            Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;

            Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
            Assert.Null(form.GetSelectedImage(ImageSlot.Main));
            Assert.Empty(txtPrompt.Text);
        });
    }
}
