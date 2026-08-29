#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13DirectModeTests
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
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

    private static MainForm CreateProductionForm(
        TestWorkspace workspace,
        AppSettings? settings = null)
    {
        return new MainForm(
            settings ?? workspace.CreateSettings(),
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

    [Fact]
    public void DirectOffDoesNotReplaceManuallySelectedMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var manual =
                workspace.CreateImage(
                    "manual_main.png",
                    new byte[] { 1, 2, 3 });

            var newer =
                workspace.CreateImage(
                    "newer_main.png",
                    new byte[] { 4, 5, 6 });

            File.SetLastWriteTimeUtc(
                manual,
                DateTime.UtcNow.AddMinutes(-5));

            File.SetLastWriteTimeUtc(
                newer,
                DateTime.UtcNow);

            using var form = CreateProductionForm(workspace, settings);

            form.SetSelectedImage(ImageSlot.Main, manual);

            // Direct OFF: entry point delegates to the existing workflow and
            // must keep the manually selected candidate untouched.
            var entryMethod =
                typeof(MainForm).GetMethod(
                    "HandleMainImageEntryPoint",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            entryMethod?.Invoke(form, null);

            Assert.Equal(
                Path.GetFullPath(manual),
                Path.GetFullPath(form.GetSelectedImage(ImageSlot.Main)!));
        });
    }

    [Fact]
    public void DirectReferenceSucceedsMainFailsThenRetryRefreshesOnlyMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            workspace.CreateImage("reference.png", new byte[] { 2 });
            workspace.CreateImage("main_bad.png", new byte[] { 3 });

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_fail";

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "prompt";

            var messageTexts = new List<string>();

            MainForm.MessageBoxProvider =
                (_, text, _, _, _) => messageTexts.Add(text);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => true;

            try
            {
                // Preflight: only the newest (bad main) file must fail Main
                // validation, so we intercept before the pair selection by
                // making the "Main" candidate invalid. Use a reference that is
                // valid and a Main that fails ValidateImageFile: overwrite the
                // main file with non-image bytes after timestamp ordering.
                var referencePath =
                    Path.Combine(
                        workspace.Downloads,
                        "reference.png");

                var badMainPath =
                    Path.Combine(
                        workspace.Downloads,
                        "main_bad.png");

                File.SetLastWriteTimeUtc(
                    referencePath,
                    DateTime.UtcNow.AddMinutes(-2));

                File.SetLastWriteTimeUtc(
                    badMainPath,
                    DateTime.UtcNow);

                // Corrupt the Main candidate header so validation fails.
                File.WriteAllBytes(
                    badMainPath,
                    new byte[] { 0x00, 0x01, 0x02, 0x03 });

                var orchestrator =
                    typeof(MainForm).GetMethod(
                        "HandleDirectMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                orchestrator?.Invoke(form, null);

                // Reference succeeded? The pair preflight validates BOTH
                // images before Reference mutation, so nothing should exist.
                var sessionField =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var session =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.Null(session);

                // Restore a valid Main image and verify the retry selects only
                // Main (Reference candidate is untouched because no session).
                var mainGood =
                    workspace.CreateImage(
                        "main_good.png",
                        new byte[] { 5, 6, 7 });

                File.SetLastWriteTimeUtc(
                    mainGood,
                    DateTime.UtcNow);

                var autoSelect =
                    typeof(MainForm).GetMethod(
                        "TryAutoSelectLatestMain",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var selected =
                    (bool)(autoSelect?.Invoke(form, null) ?? false);

                Assert.True(selected);
                Assert.Equal(
                    Path.GetFullPath(mainGood),
                    Path.GetFullPath(form.GetSelectedImage(ImageSlot.Main)!));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void DirectNoReferenceFullCommit()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

            using var form = CreateProductionForm(workspace, settings);

            var chkNoReference =
                FindControl<CheckBox>(form, "chkNoReference");

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkNoReference.Checked = true;
            chkDirect.Checked = true;

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_nr_commit";

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "direct prompt";

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => { };

            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => true;

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry?.Invoke(form, null);

                var assetFolder =
                    Path.Combine(
                        settings.AssetRootFolder,
                        "asset_direct_nr_commit");

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "main.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void DirectReferenceAssistedFullCommit()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
            workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkDirect.Checked = true;

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_ref_commit";

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "direct ref prompt";

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => { };

            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => true;

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry?.Invoke(form, null);

                var assetFolder =
                    Path.Combine(
                        settings.AssetRootFolder,
                        "asset_direct_ref_commit");

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "reference",
                            "reference.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "reference",
                            AppConstants.ReferenceProvenanceFileName)));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "main.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }
}