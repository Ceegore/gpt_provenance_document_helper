#nullable enable
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// A real 250-row manifest part ends with a run of Pixel-Exact seed rows whose
/// RefN/AusRefN rows all live in the next part. The journal therefore has to
/// hold one receipt per series at the same time. While it held a single slot,
/// the first seed claimed it, every following seed commit was refused, and
/// every following RefN row in the next part failed with "The matching
/// Pixel-Exact seed has not been committed and marked done."
/// </summary>
public sealed class PixelExactMultiSeriesJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aph-pixel-journal-" + Guid.NewGuid().ToString("N")[..8]);

    public PixelExactMultiSeriesJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Two seed rows, then their two collections, in two manifest parts.</summary>
    private const string SeedOnlyManifest = """
        { "manifestVersion": 2, "assets": [
          { "filename": "alpha_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed A. FLOWMETA: SERIE=series_alpha; SERIENGROESSE=3; NEXT=Ref2@part_b. PROZESSMARKER: Einzeln" },
          { "filename": "beta_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed B. FLOWMETA: SERIE=series_beta; SERIENGROESSE=3; NEXT=Ref2@part_b. PROZESSMARKER: Einzeln" }
        ] }
        """;

    private const string CollectionOnlyManifest = """
        { "manifestVersion": 2, "assets": [
          { "filename": "alpha_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection A. FLOWMETA: SERIE=series_alpha; OUTPUT_COUNT=2; REF_ORIGIN=part_a. PROZESSMARKER: Ref2" },
          { "filename": "alpha_state_two.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map A2. FLOWMETA: SERIE=series_alpha; OUTPUT_INDEX=2; MASTER=Ref2@part_b; REFERENZ=part_a. PROZESSMARKER: AusRef2" },
          { "filename": "beta_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection B. FLOWMETA: SERIE=series_beta; OUTPUT_COUNT=2; REF_ORIGIN=part_a. PROZESSMARKER: Ref2" },
          { "filename": "beta_state_two.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map B2. FLOWMETA: SERIE=series_beta; OUTPUT_INDEX=2; MASTER=Ref2@part_b; REFERENZ=part_a. PROZESSMARKER: AusRef2" }
        ] }
        """;

    // ------------------------------------------------------------------ service

    private PixelExactBatchStateService CreateService() => new(
        Path.Combine(_root, "pixel-exact-batch-state.json"),
        Path.Combine(_root, "pixel-exact"));

    private static PixelExactBatchState CreateSeedReceipt(string seriesId, string seedRequestKey, int bundleCount = 2) => new()
    {
        SeriesId = seriesId,
        HasCanonicalSeriesIdentity = true,
        BundleCount = bundleCount,
        TotalPhases = bundleCount + 1,
        SeedManifestFingerprint = new string('a', 64),
        SeedRequestKey = seedRequestKey,
        SeedExpectedSession = new AssetSession()
    };

    [Fact]
    public void Journal_KeepsOneReceiptPerSeries_SoConsecutiveSeedCommitsDoNotEvictEachOther()
    {
        var service = CreateService();

        service.Save(CreateSeedReceipt("series_alpha", "key-alpha"));
        service.Save(CreateSeedReceipt("series_beta", "key-beta"));
        service.Save(CreateSeedReceipt("series_gamma", "key-gamma"));

        Assert.Equal(3, service.LoadAll().Count);
        Assert.Equal("key-alpha", service.Load("series_alpha")!.SeedRequestKey);
        Assert.Equal("key-beta", service.Load("series_beta")!.SeedRequestKey);
        Assert.Equal("series_gamma", service.LoadBySeedRequestKey("key-gamma")!.SeriesId);
        Assert.Null(service.Load("series_delta"));
    }

    [Fact]
    public void Journal_UpsertsTheSameSeries_InsteadOfAppendingASecondEntry()
    {
        var service = CreateService();
        service.Save(CreateSeedReceipt("series_alpha", "key-alpha"));

        var updated = CreateSeedReceipt("series_alpha", "key-alpha");
        updated.CollectionRequestKey = "collection-alpha";
        service.Save(updated);

        var single = Assert.Single(service.LoadAll());
        Assert.Equal("collection-alpha", single.CollectionRequestKey);
    }

    [Fact]
    public void DiscardPendingState_RemovesOnlyTheNamedSeries()
    {
        var service = CreateService();
        service.Save(CreateSeedReceipt("series_alpha", "key-alpha"));
        service.Save(CreateSeedReceipt("series_beta", "key-beta"));

        service.DiscardPendingState("series_alpha");

        Assert.Null(service.Load("series_alpha"));
        Assert.NotNull(service.Load("series_beta"));
    }

    [Fact]
    public void DiscardAll_ClearsEverySeries()
    {
        var service = CreateService();
        service.Save(CreateSeedReceipt("series_alpha", "key-alpha"));
        service.Save(CreateSeedReceipt("series_beta", "key-beta"));

        service.DiscardAll();

        Assert.Empty(service.LoadAll());
        Assert.False(File.Exists(service.StatePath));
    }

    [Fact]
    public void HasPendingState_IgnoresFinishedSeries()
    {
        var service = CreateService();
        var finished = CreateSeedReceipt("series_alpha", "key-alpha");
        finished.Completed = true;
        service.Save(finished);
        Assert.False(service.HasPendingState);

        service.Save(CreateSeedReceipt("series_beta", "key-beta"));
        Assert.True(service.HasPendingState);
    }

    /// <summary>An installed operator state directory still holds the v1 single
    /// batch document. It must keep working and be rewritten as a journal.</summary>
    [Fact]
    public void LegacySingleBatchFile_IsReadAsOneEntry_AndRewrittenAsAJournal()
    {
        var service = CreateService();
        var legacy = CreateSeedReceipt("series_alpha", "key-alpha");
        File.WriteAllText(service.StatePath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));

        var loaded = Assert.Single(service.LoadAll());
        Assert.Equal("series_alpha", loaded.SeriesId);

        service.Save(CreateSeedReceipt("series_beta", "key-beta"));

        Assert.Equal(2, service.LoadAll().Count);
        Assert.Contains("\"Batches\"", File.ReadAllText(service.StatePath), StringComparison.Ordinal);
    }

    /// <summary>Finished batches are retained for the delete/retry path, but a
    /// long run must not grow the journal without bound. Pending work is never
    /// pruned, because it is the only resumable state there is.</summary>
    [Fact]
    public void CompletedBatches_ArePrunedToTheRetentionLimit_WhilePendingOnesSurvive()
    {
        var service = CreateService();
        service.Save(CreateSeedReceipt("pending_series", "key-pending"));

        for (var index = 0; index < PixelExactBatchStateService.MaxRetainedCompletedBatches + 5; index++)
        {
            var finished = CreateSeedReceipt($"done_series_{index:D2}", $"key-done-{index:D2}");
            finished.Completed = true;
            service.Save(finished);
        }

        var all = service.LoadAll();
        Assert.Equal(PixelExactBatchStateService.MaxRetainedCompletedBatches + 1, all.Count);
        Assert.NotNull(service.Load("pending_series"));
        Assert.Null(service.Load("done_series_00"));
        Assert.NotNull(service.Load($"done_series_{PixelExactBatchStateService.MaxRetainedCompletedBatches + 4:D2}"));
    }

    // ------------------------------------------------------------------ MainForm

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    private static void InstallSafeSeams(List<string> messages, List<string> confirmations, Func<string, DialogResult>? confirm = null)
    {
        MainForm.MessageBoxProvider = (_, text, _, _, _) => messages.Add(text);
        MainForm.ConfirmBoxProvider = (_, text, _, _, _) =>
        {
            confirmations.Add(text);
            return confirm?.Invoke(text) ?? DialogResult.OK;
        };
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

    private static object? InvokePrivateWithArgs(MainForm form, string method, params object?[] args)
    {
        var target = typeof(MainForm).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(target is not null, $"Method '{method}' not found.");
        try
        {
            return target!.Invoke(form, args);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }
    }

    private static void ImportManifest(TestWorkspace workspace, MainForm form, string fileName, string json)
    {
        var path = Path.Combine(workspace.Root, fileName);
        File.WriteAllText(path, json);
        MainForm.OpenFileDialogProvider = (_, _) => path;
        InvokePrivate(form, "HandleImportRequest");
    }

    private static ListViewItem QueueRow(MainForm form, string assetName) =>
        FindControl<ListView>(form, "lvRequestQueue").Items
            .Cast<ListViewItem>()
            .Single(row => string.Equals(row.SubItems[1].Text, assetName, StringComparison.Ordinal));

    private static void CommitSeed(TestWorkspace workspace, MainForm form, string assetName, byte marker)
    {
        var seedImage = workspace.CreateImage($"{assetName}_seed.png", new byte[] { marker });
        File.SetLastWriteTimeUtc(seedImage, DateTime.UtcNow.AddHours(-6));
        form.HandleRequestQueueItemActivate(QueueRow(form, assetName));
        form.SetSelectedImage(ImageSlot.Main, seedImage);
        InvokePrivate(form, "HandleMainImageEntryPoint");
    }

    private static void CreateOrderedImages(TestWorkspace workspace, string prefix, int count, DateTime baseTimeUtc)
    {
        for (var index = 0; index < count; index++)
        {
            var path = workspace.CreateImage($"{prefix}{index}.png", new byte[] { (byte)(index + 1), 0x20, (byte)prefix.Length, (byte)prefix[0] });
            File.SetLastWriteTimeUtc(path, baseTimeUtc.AddMinutes(index));
        }
    }

    /// <summary>The exact production shape: both masters are committed from the
    /// part that carries them, then both collections run from the next part.</summary>
    [Fact]
    public void TwoSeedsInOnePart_ThenBothCollectionsInTheNextPart_AllCommitWithoutAMissingSeedError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(messages, confirmations);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, "part-a.json", SeedOnlyManifest);

                CommitSeed(workspace, form, "alpha_master", 0x41);
                messages.Clear();
                CommitSeed(workspace, form, "beta_master", 0x42);

                // The second master used to be refused outright, so its asset was
                // never written and its receipt never reached the journal.
                Assert.DoesNotContain(messages, text => text.Contains("already has a pending Pixel-Exact batch", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, text => text.Contains("Another Pixel-Exact batch is pending", StringComparison.Ordinal));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "alpha_master")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "beta_master")));

                var pending = batchState.LoadAll();
                Assert.Equal(2, pending.Count);
                Assert.All(pending, state => Assert.True(state.SeedCommitted && state.SeedQueueCompleted));

                ImportManifest(workspace, form, "part-b.json", CollectionOnlyManifest);

                CreateOrderedImages(workspace, "alpha_phase", 2, DateTime.UtcNow.AddMinutes(-40));
                form.HandleRequestQueueItemActivate(QueueRow(form, "alpha_state_one"));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.DoesNotContain(messages, text => text.Contains("has not been committed and marked done", StringComparison.Ordinal));
                Assert.Contains(messages, text => text.Contains("2 Pixel-Exact outputs were committed", StringComparison.Ordinal));

                CreateOrderedImages(workspace, "beta_phase", 2, DateTime.UtcNow.AddMinutes(-10));
                form.HandleRequestQueueItemActivate(QueueRow(form, "beta_state_one"));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                // This is the click that produced the reported error dialog.
                Assert.DoesNotContain(messages, text => text.Contains("has not been committed and marked done", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, text => text.Contains("durable Pixel-Exact collection receipt", StringComparison.Ordinal));
                Assert.Contains(messages, text => text.Contains("2 Pixel-Exact outputs were committed", StringComparison.Ordinal));

                foreach (var asset in new[] { "alpha_state_one", "alpha_state_two", "beta_state_one", "beta_state_two" })
                {
                    Assert.True(Directory.Exists(Path.Combine(workspace.Assets, asset)), asset);
                    Assert.True(File.Exists(Path.Combine(workspace.Assets, asset, AppConstants.FinalProvenanceFileName)), asset);
                }
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    /// <summary>Within one manifest the strict order still holds: the master row
    /// is importable and open, so its collection must not run first.</summary>
    [Fact]
    public void CollectionRow_StillRefusesToRun_WhileItsOwnMasterRowIsStillOpen()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(messages, confirmations);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, "single-part.json", """
                    { "manifestVersion": 2, "assets": [
                      { "filename": "solo_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed. FLOWMETA: SERIE=series_solo; SERIENGROESSE=3; NEXT=Ref2. PROZESSMARKER: Einzeln" },
                      { "filename": "solo_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection. FLOWMETA: SERIE=series_solo; OUTPUT_COUNT=2. PROZESSMARKER: Ref2" },
                      { "filename": "solo_state_two.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map two. FLOWMETA: SERIE=series_solo; OUTPUT_INDEX=2; MASTER=Ref2. PROZESSMARKER: AusRef2" }
                    ] }
                    """);

                CreateOrderedImages(workspace, "solo_phase", 2, DateTime.UtcNow.AddMinutes(-20));
                form.HandleRequestQueueItemActivate(QueueRow(form, "solo_state_one"));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, text => text.Contains("Process 'solo_master' first", StringComparison.Ordinal));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "solo_state_one")));
                Assert.Empty(batchState.LoadAll());
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    /// <summary>A series whose master row is in another manifest part and whose
    /// receipt is gone (cleared queue, other machine) is an explicit operator
    /// decision, not a dead end.</summary>
    [Fact]
    public void CollectionRow_WithoutAnyLocalReceipt_CommitsAfterAnExplicitConfirmation()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(messages, confirmations);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, "part-b.json", CollectionOnlyManifest);

                CreateOrderedImages(workspace, "alpha_phase", 2, DateTime.UtcNow.AddMinutes(-40));
                form.HandleRequestQueueItemActivate(QueueRow(form, "alpha_state_one"));
                messages.Clear();
                confirmations.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(confirmations, text => text.Contains("No durable master receipt for series 'series_alpha'", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, text => text.Contains("has not been committed and marked done", StringComparison.Ordinal));
                Assert.Contains(messages, text => text.Contains("2 Pixel-Exact outputs were committed", StringComparison.Ordinal));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_one")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_two")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    /// <summary>
    /// The RefN collection freezes every phase, including the ones this manifest
    /// part cannot bind. When the continuation part is imported, that frozen
    /// image is the one the row wants - the operator should not have to work out
    /// which of many downloads it was.
    /// </summary>
    [Fact]
    public void DeferredPhase_IsOfferedFromTheJournal_AndClosedOutWhenItsContinuationRowCommits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(messages, confirmations);
            try
            {
                using var form = CreateForm(workspace, batchState);
                // Part A binds outputs 1 and 2; output 3 has no row here.
                ImportManifest(workspace, form, "split-a.json", """
                    { "manifestVersion": 2, "assets": [
                      { "filename": "split_master.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Seed. FLOWMETA: SERIE=series_split; SERIENGROESSE=4; NEXT=Ref3. PROZESSMARKER: Einzeln" },
                      { "filename": "split_state_one.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Collection. FLOWMETA: SERIE=series_split; OUTPUT_COUNT=3. PROZESSMARKER: Ref3" },
                      { "filename": "split_state_two.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map two. FLOWMETA: SERIE=series_split; OUTPUT_INDEX=2; MASTER=Ref3. PROZESSMARKER: AusRef3" }
                    ] }
                    """);

                CommitSeed(workspace, form, "split_master", 0x51);
                CreateOrderedImages(workspace, "split_phase", 3, DateTime.UtcNow.AddMinutes(-30));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");
                Assert.Contains(messages, text => text.Contains("has no queue row for output 3/3", StringComparison.Ordinal));

                var deferred = Assert.Single(batchState.Load("series_split")!.Outputs, output => output.DeferredNoTargetRow);
                var deferredFileName = Path.GetFileName(deferred.OriginalSourcePath);

                // A later series must not evict the deferred phase's receipt.
                var finished = CreateSeedReceipt("unrelated_series", "key-unrelated");
                finished.Completed = true;
                batchState.Save(finished);

                ImportManifest(workspace, form, "split-b.json", """
                    { "manifestVersion": 2, "assets": [
                      { "filename": "split_state_three.png", "resolution": "512x512", "alpha": "not_required", "prompt": "Map three. FLOWMETA: SERIE=series_split; OUTPUT_INDEX=3; MASTER=Ref3. PROZESSMARKER: AusRef3" }
                    ] }
                    """);

                form.HandleRequestQueueItemActivate(QueueRow(form, "split_state_three"));
                messages.Clear();
                confirmations.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                // No image was selected by hand: the journal supplied it.
                Assert.Contains(confirmations, text => text.Contains(deferredFileName, StringComparison.Ordinal));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "split_state_three")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "split_state_three", AppConstants.FinalProvenanceFileName)));

                var resolved = batchState.Load("series_split")!.Outputs.Single(output => output.OutputIndex == 3);
                Assert.False(resolved.DeferredNoTargetRow);
                Assert.Equal(PixelExactOutputCommitState.QueueCompleted, resolved.State);
                Assert.Equal("split_state_three", resolved.AssetName);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    /// <summary>
    /// A confirmed continuation collection never has a seed receipt. Requiring
    /// one to reopen it would make its own retry - delete a committed output
    /// with the row's x, then run the RefN row again - permanently unresumable.
    /// </summary>
    [Fact]
    public void ContinuationCollection_IsStillResumable_AfterOneOfItsOutputsIsDeleted()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(messages, confirmations);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, "part-b.json", CollectionOnlyManifest);

                CreateOrderedImages(workspace, "alpha_phase", 2, DateTime.UtcNow.AddMinutes(-40));
                form.HandleRequestQueueItemActivate(QueueRow(form, "alpha_state_one"));
                InvokePrivate(form, "HandleMainImageEntryPoint");
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_two")));

                var journal = batchState.Load("series_alpha")!;
                Assert.False(journal.SeedCommitted);

                InvokePrivateWithArgs(form, "HandleCompletedRequestReset", QueueRow(form, "alpha_state_two"));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_two")));

                form.HandleRequestQueueItemActivate(QueueRow(form, "alpha_state_one"));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.DoesNotContain(messages, text => text.Contains("has not been committed and marked done", StringComparison.Ordinal));
                Assert.DoesNotContain(messages, text => text.Contains("durable Pixel-Exact collection receipt", StringComparison.Ordinal));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_two")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    /// <summary>Retention must not throw away the only record of which download
    /// belongs to a queue row that is still open in a later manifest part.</summary>
    [Fact]
    public void CompletedBatchWithAnUnresolvedDeferredPhase_IsNeverPruned()
    {
        var service = CreateService();
        var withDeferred = CreateSeedReceipt("deferred_series", "key-deferred");
        withDeferred.Completed = true;
        withDeferred.Outputs.Add(new PixelExactStagedOutput
        {
            OutputIndex = 1,
            Phase = 2,
            OriginalSourcePath = Path.Combine(_root, "one.png"),
            StagedPath = Path.Combine(_root, "staged-one.png"),
            Sha256 = new string('b', 64),
            State = PixelExactOutputCommitState.QueueCompleted,
            ManifestFingerprint = new string('c', 64),
            RequestKey = "bound-key",
            AssetName = "bound_asset",
            ExpectedCommitSession = new AssetSession(),
            AssetFolderPath = Path.Combine(_root, "bound_asset")
        });
        withDeferred.Outputs.Add(new PixelExactStagedOutput
        {
            OutputIndex = 2,
            Phase = 3,
            OriginalSourcePath = Path.Combine(_root, "two.png"),
            StagedPath = Path.Combine(_root, "staged-two.png"),
            Sha256 = new string('d', 64),
            DeferredNoTargetRow = true
        });
        withDeferred.BatchId = Guid.NewGuid().ToString("N");
        withDeferred.CollectionGenerationPrompt = "Collection prompt.";
        withDeferred.CollectionGenerationPromptSha256 = Sha256OfText("Collection prompt.");
        service.Save(withDeferred);

        for (var index = 0; index < PixelExactBatchStateService.MaxRetainedCompletedBatches + 5; index++)
        {
            var finished = CreateSeedReceipt($"filler_series_{index:D2}", $"key-filler-{index:D2}");
            finished.Completed = true;
            service.Save(finished);
        }

        Assert.NotNull(service.Load("deferred_series"));
    }

    private static string Sha256OfText(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void CollectionRow_WithoutAnyLocalReceipt_WritesNothingWhenTheConfirmationIsDeclined()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var batchState = new PixelExactBatchStateService(
                Path.Combine(workspace.Root, "pixel-exact-batch-state.json"),
                Path.Combine(workspace.Root, "pixel-exact-staging"));
            var messages = new List<string>();
            var confirmations = new List<string>();
            InstallSafeSeams(
                messages,
                confirmations,
                text => text.Contains("No durable master receipt", StringComparison.Ordinal) ? DialogResult.Cancel : DialogResult.OK);
            try
            {
                using var form = CreateForm(workspace, batchState);
                ImportManifest(workspace, form, "part-b.json", CollectionOnlyManifest);

                CreateOrderedImages(workspace, "alpha_phase", 2, DateTime.UtcNow.AddMinutes(-40));
                form.HandleRequestQueueItemActivate(QueueRow(form, "alpha_state_one"));
                messages.Clear();
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "alpha_state_one")));
                Assert.Empty(batchState.LoadAll());
            }
            finally
            {
                ClearSeams();
            }
        });
    }
}
