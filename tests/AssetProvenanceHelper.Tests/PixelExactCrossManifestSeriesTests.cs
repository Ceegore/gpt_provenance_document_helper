#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// A canonical Pixel-Exact series may be split over several manifest parts, so
/// the target row of a later output index is legitimately absent. These tests
/// pin the split-series behaviour: commit every bindable phase, defer the rest,
/// close the journal, and hand the operator its next prompt.
/// </summary>
public sealed class PixelExactCrossManifestSeriesTests
{
    private const string SplitSeriesManifest = """
        { "manifestVersion": 2, "assets": [
          { "filename": "series_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed. FLOWMETA: SERIE=gas_station_zapfsaeule; SERIENGROESSE=4; NEXT=Ref3. PROZESSMARKER: Einzeln" },
          { "filename": "series_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection. FLOWMETA: SERIE=gas_station_zapfsaeule; OUTPUT_COUNT=3. PROZESSMARKER: Ref3" },
          { "filename": "series_state_two.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map two. FLOWMETA: SERIE=gas_station_zapfsaeule; OUTPUT_INDEX=2; MASTER=Ref3. PROZESSMARKER: AusRef3" },
          { "filename": "next_single.png", "resolution": "512x512", "alpha": "not_required", "prompt": "The next ordinary single request." }
        ] }
        """;

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    private static void InstallSafeSeams(List<string> messages)
    {
        MainForm.MessageBoxProvider = (_, text, _, _, _) => messages.Add(text);
        MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
        MainForm.OpenFolderProvider = _ => { };
        TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
    }

    private static void ClearSeams()
    {
        MainForm.MessageBoxProvider = null;
        MainForm.ConfirmBoxProvider = null;
        MainForm.OpenFolderProvider = null;
        MainForm.OpenFileDialogProvider = null;
        TwoChoiceDialog.CustomChoiceProvider = null;
    }

    private static PixelExactBatchStateService CreateBatchStateService(TestWorkspace workspace) =>
        new(
            Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
            Path.Combine(workspace.Root, "pixel-exact-staging"));

    private static MainForm CreateForm(TestWorkspace workspace, PixelExactBatchStateService batchState) => new(
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
        batchState);

    private static T FindControl<T>(MainForm form, string name)
        where T : Control
    {
        var control = form.Controls.Find(name, true).FirstOrDefault();
        Assert.NotNull(control);
        return Assert.IsType<T>(control);
    }

    private static object? InvokePrivate(MainForm form, string method)
    {
        var target = typeof(MainForm).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target is not null, $"Method '{method}' not found.");
        try
        {
            return target!.Invoke(form, null);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }
    }

    private static void ImportManifest(TestWorkspace workspace, MainForm form, string json)
    {
        var path = Path.Combine(workspace.Root, "split-manifest.json");
        File.WriteAllText(path, json);
        MainForm.OpenFileDialogProvider = (_, _) => path;
        InvokePrivate(form, "HandleImportRequest");
    }

    private static ListViewItem QueueRow(MainForm form, string assetName) =>
        FindControl<ListView>(form, "lvRequestQueue").Items
            .Cast<ListViewItem>()
            .Single(row => string.Equals(row.SubItems[1].Text, assetName, StringComparison.Ordinal));

    /// <summary>Writes `count` images whose modification times increase, so the
    /// image finder's newest-first scan is deterministic.</summary>
    private static List<string> CreateOrderedImages(TestWorkspace workspace, string prefix, int count, DateTime baseTimeUtc)
    {
        var paths = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var path = workspace.CreateImage($"{prefix}{index}.png", new byte[] { (byte)(index + 1), 0x20, (byte)prefix.Length });
            File.SetLastWriteTimeUtc(path, baseTimeUtc.AddMinutes(index));
            paths.Add(path);
        }
        return paths;
    }

    private static void CommitSeed(TestWorkspace workspace, MainForm form)
    {
        var seedImage = workspace.CreateImage("seed.png", new byte[] { 0x41 });
        File.SetLastWriteTimeUtc(seedImage, DateTime.UtcNow.AddHours(-3));
        form.HandleRequestQueueItemActivate(QueueRow(form, "series_master"));
        form.SetSelectedImage(ImageSlot.Main, seedImage);
        InvokePrivate(form, "HandleMainImageEntryPoint");
    }

    [Fact]
    public void SplitSeries_CommitsEveryBindablePhase_AndDefersTheMissingTargetRow()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = CreateBatchStateService(workspace);
            var messages = new List<string>();
            InstallSafeSeams(messages);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, SplitSeriesManifest);

                CommitSeed(workspace, form);

                // The seed commit auto-loads the Ref3 collection row.
                Assert.True(FindControl<CheckBox>(form, "chkPixelExact").Checked);
                CreateOrderedImages(workspace, "phase", 3, DateTime.UtcNow.AddMinutes(-30));

                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.DoesNotContain(messages, text => text.Contains("does not contain exactly one target row", StringComparison.Ordinal));
                Assert.Contains(messages, text => text.Contains("2 Pixel-Exact outputs were committed", StringComparison.Ordinal));
                Assert.Contains(messages, text => text.Contains("has no queue row for output 3/3", StringComparison.Ordinal));

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "series_state_one")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "series_state_two")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "series_state_two", AppConstants.FinalProvenanceFileName)));

                var state = batchState.Load();
                Assert.NotNull(state);
                Assert.True(state!.Completed);
                var deferred = Assert.Single(state.Outputs, output => output.DeferredNoTargetRow);
                Assert.Equal(3, deferred.OutputIndex);
                Assert.Equal(PixelExactOutputCommitState.Staged, deferred.State);
                Assert.All(
                    state.Outputs.Where(output => !output.DeferredNoTargetRow),
                    output => Assert.Equal(PixelExactOutputCommitState.QueueCompleted, output.State));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void FinishedCollection_SelectsTheNextOpenRequest_AndCopiesItsPromptToTheClipboard()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = CreateBatchStateService(workspace);
            var messages = new List<string>();
            InstallSafeSeams(messages);
            try
            {
                using var form = CreateForm(workspace, batchState);
                var clipboard = new List<string>();
                form.ClipboardWriter = text => clipboard.Add(text);
                ImportManifest(workspace, form, SplitSeriesManifest);

                CommitSeed(workspace, form);
                CreateOrderedImages(workspace, "phase", 3, DateTime.UtcNow.AddMinutes(-30));

                clipboard.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Equal("The next ordinary single request.", clipboard.LastOrDefault());
                Assert.Equal("next_single", FindControl<TextBox>(form, "txtAssetFolderName").Text);
                Assert.Equal("The next ordinary single request.", FindControl<TextBox>(form, "txtPrompt").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void ContinuationOutputRow_CommitsAsASingleConfirmedAsset_WhenNoCollectionRowIsImportable()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = CreateBatchStateService(workspace);
            var messages = new List<string>();
            InstallSafeSeams(messages);
            try
            {
                using var form = CreateForm(workspace, batchState);
                // The continuation manifest carries only the trailing AusRefN row;
                // its RefN collection row lives in the preceding manifest part.
                ImportManifest(workspace, form, """
                    { "manifestVersion": 2, "assets": [
                      { "filename": "series_state_three.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map three. FLOWMETA: SERIE=gas_station_zapfsaeule; OUTPUT_INDEX=3; MASTER=Ref3. PROZESSMARKER: AusRef3" }
                    ] }
                    """);

                var leftover = workspace.CreateImage("leftover.png", new byte[] { 0x51 });
                form.HandleRequestQueueItemActivate(QueueRow(form, "series_state_three"));
                form.SetSelectedImage(ImageSlot.Main, leftover);

                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.DoesNotContain(messages, text => text.Contains("filled automatically by its preceding RefN", StringComparison.Ordinal));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "series_state_three")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "series_state_three", AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void OutputRow_StillRefusesAManualCommit_WhileItsCollectionRowIsOpen()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = CreateBatchStateService(workspace);
            var messages = new List<string>();
            InstallSafeSeams(messages);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, SplitSeriesManifest);

                var image = workspace.CreateImage("stray.png", new byte[] { 0x61 });
                form.HandleRequestQueueItemActivate(QueueRow(form, "series_state_two"));
                form.SetSelectedImage(ImageSlot.Main, image);

                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, text => text.Contains("filled automatically by its preceding RefN", StringComparison.Ordinal));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "series_state_two")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void AmbiguousSeriesMetadata_StillFailsClosed_WithoutWritingAnyAsset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = CreateBatchStateService(workspace);
            var messages = new List<string>();
            InstallSafeSeams(messages);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, """
                    { "manifestVersion": 2, "assets": [
                      { "filename": "amb_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed. FLOWMETA: SERIE=ambiguous_series; SERIENGROESSE=3; NEXT=Ref2. PROZESSMARKER: Einzeln" },
                      { "filename": "amb_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection. FLOWMETA: SERIE=ambiguous_series; OUTPUT_COUNT=2. PROZESSMARKER: Ref2" },
                      { "filename": "amb_state_two_a.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map A. FLOWMETA: SERIE=ambiguous_series; OUTPUT_INDEX=2; MASTER=Ref2. PROZESSMARKER: AusRef2" },
                      { "filename": "amb_state_two_b.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map B. FLOWMETA: SERIE=ambiguous_series; OUTPUT_INDEX=2; MASTER=Ref2. PROZESSMARKER: AusRef2" }
                    ] }
                    """);

                var seedImage = workspace.CreateImage("amb_seed.png", new byte[] { 0x71 });
                File.SetLastWriteTimeUtc(seedImage, DateTime.UtcNow.AddHours(-3));
                form.HandleRequestQueueItemActivate(QueueRow(form, "amb_master"));
                form.SetSelectedImage(ImageSlot.Main, seedImage);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                CreateOrderedImages(workspace, "amb_phase", 2, DateTime.UtcNow.AddMinutes(-30));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, text => text.Contains("contains 2 target rows for output 2", StringComparison.Ordinal));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "amb_state_one")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "amb_state_two_a")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }
}
