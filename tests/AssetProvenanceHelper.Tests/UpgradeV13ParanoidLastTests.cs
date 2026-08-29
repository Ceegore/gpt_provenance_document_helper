#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Absolute final line-gap tests.
/// </summary>
public class UpgradeV13ParanoidLastTests
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

    private static MainForm CreateForm(
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
    public void Queue_ActivationBlockedForDifferentRequestWhileReferenceReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_bound.webp",
                    "1920x1080",
                    "bound prompt");

            var refImage =
                workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_bound",
                    refImage,
                    DateTimeOffset.Now,
                    providerTemplate: null,
                    sourceRequestKey: requestKey);

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            var manifestPath =
                Path.Combine(
                    workspace.Root,
                    "manifest.json");

            File.WriteAllText(
                manifestPath,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "asset_bound.webp", "resolution": "1920x1080", "prompt": "bound prompt" },
                    { "filename": "other.webp", "resolution": "10x10", "prompt": "other prompt" }
                  ]
                }
                """);

            using var form = CreateForm(workspace, settings);

            var sessionField =
                typeof(MainForm).GetField(
                    "_currentSession",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var stateField =
                typeof(MainForm).GetField(
                    "_state",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var applyState =
                typeof(MainForm).GetMethod(
                    "ApplyState",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            applyState!.Invoke(form, null);

            form.ClipboardWriter = _ => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            var blocked = false;

            MainForm.MessageBoxProvider =
                (_, text, caption, _, _) =>
                {
                    blocked =
                        caption == "Request selection blocked"
                        && text.Contains(
                            "Finish or cancel",
                            StringComparison.Ordinal);
                };

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                // The bound row is active; clicking the OTHER row is blocked.
                var activate =
                    typeof(MainForm).GetMethod(
                        "HandleRequestQueueItemActivate",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                activate!.Invoke(form, new object[] { lv.Items[1] });

                Assert.True(blocked);

                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                // The originally bound request stays active.
                Assert.NotNull(activeRequest);
                Assert.Equal(requestKey, activeRequest!.RequestKey);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ReferenceReplaceButtonClickWhileReferenceReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();
            var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.ProcessReference(
                settings,
                "asset_replace_btn",
                refImage,
                DateTimeOffset.Now);

            using var form = CreateForm(workspace, settings);
            form.Show();

            var sessionField =
                typeof(MainForm).GetField(
                    "_currentSession",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var stateField =
                typeof(MainForm).GetField(
                    "_state",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var applyState =
                typeof(MainForm).GetMethod(
                    "ApplyState",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            applyState!.Invoke(form, null);

            // No replacement candidate selected -> warning path.
            var warningShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    warningShown =
                        caption == "Replace Reference";
                };

            try
            {
                var btnReference =
                    FindControl<Button>(form, "btnReference");

                btnReference.PerformClick();

                Assert.True(warningShown);

                // The session must remain untouched.
                var current =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.NotNull(current);
                Assert.Equal(session.ReferenceHash, current!.ReferenceHash);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void CtaPulse_TicksCompleteAndReset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var start =
                typeof(MainForm).GetMethod(
                    "StartCtaPulse",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var btn =
                FindControl<Button>(form, "btnReference");

            start!.Invoke(
                form,
                new object[] { btn, AssetProvenanceHelper.Ui.UiTheme.ReferenceAccent });

            var timer =
                typeof(MainForm).GetField(
                    "_ctaPulseTimer",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as System.Windows.Forms.Timer;

            Assert.NotNull(timer);

            var onTick =
                typeof(System.Windows.Forms.Timer).GetMethod(
                    "OnTick",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            // After 8 ticks the pulse stops and the button turns error red.
            for (var i = 0; i < 8; i++)
            {
                onTick!.Invoke(timer, new object[] { EventArgs.Empty });
            }

            Assert.Null(
                typeof(MainForm).GetField(
                    "_ctaPulseTimer",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form));

            Assert.Null(
                typeof(MainForm).GetField(
                    "_pulsingButton",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form));

            Assert.Equal(
                AssetProvenanceHelper.Ui.UiTheme.Error,
                btn.BackColor);
        });
    }

    [Fact]
    public void ClearValidationVisuals_ResetsHostsAndPulse()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            // Simulate a validation error state.
            var start =
                typeof(MainForm).GetMethod(
                    "StartCtaPulse",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var btn =
                FindControl<Button>(form, "btnReference");

            start!.Invoke(
                form,
                new object[] { btn, AssetProvenanceHelper.Ui.UiTheme.ReferenceAccent });

            var clear =
                typeof(MainForm).GetMethod(
                    "ClearValidationVisuals",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            clear!.Invoke(form, null);

            Assert.Null(
                typeof(MainForm).GetField(
                    "_pulsingButton",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form));

            Assert.Equal(
                AssetProvenanceHelper.Ui.UiTheme.ReferenceAccent,
                btn.BackColor);
        });
    }
}
