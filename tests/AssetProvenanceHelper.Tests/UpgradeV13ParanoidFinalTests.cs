#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Final gap-closing tests for the last reachable branch lines.
/// </summary>
public class UpgradeV13ParanoidFinalTests
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
    public void ReferenceButtonClickTriggersHandleReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            using var form = CreateForm(workspace);
            form.Show();

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_btn_click";

            form.SetSelectedImage(ImageSlot.Reference, refImage);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            try
            {
                var btnReference =
                    FindControl<Button>(form, "btnReference");

                btnReference.PerformClick();

                var session =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetSession;

                Assert.NotNull(session);
                Assert.Equal("asset_btn_click", session!.AssetFolderName);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void CtaPulseStartsAndStops()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var start =
                typeof(MainForm).GetMethod(
                    "StartCtaPulse",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var stop =
                typeof(MainForm).GetMethod(
                    "StopCtaPulse",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var btn =
                FindControl<Button>(form, "btnReference");

            start!.Invoke(
                form,
                new object[] { btn, UiTheme.ReferenceAccent });

            var pulsingButton =
                typeof(MainForm).GetField(
                    "_pulsingButton",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as Button;

            Assert.Same(btn, pulsingButton);

            var timer =
                typeof(MainForm).GetField(
                    "_ctaPulseTimer",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as System.Windows.Forms.Timer;

            Assert.NotNull(timer);

            stop!.Invoke(form, null);

            Assert.Null(
                typeof(MainForm).GetField(
                    "_pulsingButton",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form));
        });
    }

    [Fact]
    public void SessionValidation_AssetFolderEscapesRootRejected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var session = new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ProjectName = "P",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_x",
            AssetFolder = Path.Combine(workspace.Root, "outside")
        };

        var result = validation.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("does not match", StringComparison.Ordinal)
                || e.Contains("not a direct child", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionValidation_MissingRootRejected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var session = new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ProjectName = "P",
            AssetRootFolder = Path.Combine(workspace.Root, "missing-root"),
            AssetFolderName = "asset_x",
            AssetFolder = Path.Combine(workspace.Root, "missing-root", "asset_x")
        };

        var result = validation.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionValidation_RootReparsePointRejected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var session = new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ProjectName = "P",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_x",
            AssetFolder = Path.Combine(workspace.Assets, "asset_x")
        };

        ValidationService.FileAttributesProvider =
            path => FileAttributes.ReparsePoint;

        try
        {
            var result = validation.ValidateSession(session);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                e => e.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void SessionValidation_ReferenceHashMismatchDetected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var session = new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ProjectName = "P",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_x",
            AssetFolder = Path.Combine(workspace.Assets, "asset_x"),
            ReferenceProcessedAt = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            ReferenceFilename = "ref.png",
            ReferenceHash = new string('a', 64),
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_x", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_x", "reference", AppConstants.ReferenceProvenanceFileName),
            CancelPhase = CancelPhase.None
        };

        // Real files with a DIFFERENT actual hash than the recorded authority.
        var folder = Path.GetDirectoryName(session.ReferenceDestinationPath)!;
        Directory.CreateDirectory(folder);

        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 1, 2, 3 });
        File.WriteAllText(session.ReferenceProvenancePath, "whatever");

        var result = validation.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("does not match the current reference image", StringComparison.Ordinal));
    }

    [Fact]
    public void CompleteAsset_IngameHashMismatchDetected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var templateService =
            workspace.CreateTemplateService();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "asset_ingame_bad",
            source,
            "prompt",
            DateTimeOffset.Now);

        // Build a complete-looking asset with tampered ingame copy.
        Directory.CreateDirectory(session.AssetFolder);
        File.Copy(source, Path.Combine(session.AssetFolder, "main.png"));

        var ingame = session.GetIngameFolderPath();
        Directory.CreateDirectory(ingame);

        var ingameName = session.GetIngameFilename();
        File.WriteAllBytes(
            Path.Combine(ingame, ingameName),
            new byte[] { 9, 9, 9 });

        File.WriteAllText(
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName),
            "x");

        var result = validation.ValidateCompleteAsset(
            session,
            Path.Combine(session.AssetFolder, "main.png"),
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName),
            "main.png",
            "2026-08-27",
            "prompt",
            templateService,
            session.MainHash);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("Ingame image", StringComparison.Ordinal));
    }

    [Fact]
    public void MainDestination_IngameReparseRejected()
    {
        using var workspace = new TestWorkspace();

        var validation =
            workspace.CreateValidationService();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_ingame_reparse",
            refSource,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

        var ingame = session.GetIngameFolderPath();
        Directory.CreateDirectory(ingame);

        ValidationService.FileAttributesProvider =
            path => path == ingame
                ? FileAttributes.ReparsePoint
                : FileAttributes.Normal;

        try
        {
            var result = validation.ValidateMainDestinationAvailability(
                session,
                settings.AcceptedExtensions,
                Path.Combine(workspace.Downloads, "main.png"));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                e => e.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }
}

