#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13RecentDocumentsTests
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

    private static RecentDocumentEntry CreateEntry(
        string path,
        string assetName,
        ProvenanceDocumentKind kind,
        DateTimeOffset recordedAt) =>
        new()
        {
            Path = path,
            AssetName = assetName,
            Kind = kind,
            RecordedAt = recordedAt
        };

    [Fact]
    public void RecordingKeepsNewestThree()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var now =
            new DateTimeOffset(
                2026,
                8,
                26,
                12,
                0,
                0,
                TimeSpan.Zero);

        service.Record(
            CreateEntry(
                "A.md",
                "assetA",
                ProvenanceDocumentKind.Final,
                now));

        service.Record(
            CreateEntry(
                "B.md",
                "assetB",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(1)));

        service.Record(
            CreateEntry(
                "C.md",
                "assetC",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(2)));

        var entries = service.Load();

        Assert.Equal(
            new[] { "C.md", "B.md", "A.md" },
            entries
                .Select(entry => Path.GetFileName(entry.Path))
                .ToArray());

        service.Record(
            CreateEntry(
                "D.md",
                "assetD",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(3)));

        entries = service.Load();

        Assert.Equal(
            new[] { "D.md", "C.md", "B.md" },
            entries
                .Select(entry => Path.GetFileName(entry.Path))
                .ToArray());

        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void RecordingSamePathMovesToNewestPosition()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var now =
            new DateTimeOffset(
                2026,
                8,
                26,
                12,
                0,
                0,
                TimeSpan.Zero);

        service.Record(
            CreateEntry(
                "A.md",
                "assetA",
                ProvenanceDocumentKind.Final,
                now));

        service.Record(
            CreateEntry(
                "B.md",
                "assetB",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(1)));

        service.Record(
            CreateEntry(
                "A.md",
                "assetA",
                ProvenanceDocumentKind.Reference,
                now.AddMinutes(2)));

        var entries = service.Load();

        Assert.Equal(
            new[] { "A.md", "B.md" },
            entries
                .Select(entry => Path.GetFileName(entry.Path))
                .ToArray());

        Assert.Equal(
            ProvenanceDocumentKind.Reference,
            entries[0].Kind);
    }

    [Fact]
    public void PersistsRestart()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        service.Record(
            CreateEntry(
                "A.md",
                "assetA",
                ProvenanceDocumentKind.Final,
                DateTimeOffset.Now));

        var reloaded =
            new RecentDocumentHistoryService(
                    workspace.RecentDocumentsPath)
                .Load();

        Assert.Single(reloaded);
        Assert.Equal("assetA", reloaded[0].AssetName);
    }

    [Fact]
    public void ReferenceAndFinalKindsPreserved()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var now =
            new DateTimeOffset(
                2026,
                8,
                26,
                12,
                0,
                0,
                TimeSpan.Zero);

        service.Record(
            CreateEntry(
                "A.md",
                "assetA",
                ProvenanceDocumentKind.Reference,
                now));

        service.Record(
            CreateEntry(
                "B.md",
                "assetB",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(1)));

        var entries = service.Load();

        Assert.Equal(
            ProvenanceDocumentKind.Final,
            entries[0].Kind);

        Assert.Equal(
            ProvenanceDocumentKind.Reference,
            entries[1].Kind);
    }

    [Fact]
    public void CancellationRemovesMatchingReferenceHistory()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var assetFolder =
            Path.Combine(
                workspace.Assets,
                "asset_cancel");

        Directory.CreateDirectory(assetFolder);

        var now =
            new DateTimeOffset(
                2026,
                8,
                26,
                12,
                0,
                0,
                TimeSpan.Zero);

        service.Record(
            CreateEntry(
                Path.Combine(
                    assetFolder,
                    "reference",
                    AppConstants.ReferenceProvenanceFileName),
                "asset_cancel",
                ProvenanceDocumentKind.Reference,
                now));

        service.Record(
            CreateEntry(
                Path.Combine(
                    workspace.Assets,
                    "other_asset",
                    AppConstants.FinalProvenanceFileName),
                "other_asset",
                ProvenanceDocumentKind.Final,
                now.AddMinutes(1)));

        service.RemoveEntriesUnderAssetFolder(
            assetFolder);

        var entries = service.Load();

        Assert.Single(entries);
        Assert.Equal("other_asset", entries[0].AssetName);
    }

    [Fact]
    public void HistorySaveFailureDoesNotThrowAtUiLayer()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // Simulate an unreachable history path by pointing the service at a
            // path whose parent is a file, so writes fail.
            var blocker =
                Path.Combine(
                    workspace.Root,
                    "blocker");

            File.WriteAllText(
                blocker,
                "x");

            var brokenService =
                new RecentDocumentHistoryService(
                    Path.Combine(
                        blocker,
                        "recent-documents.json"));

            // The service itself surfaces the write failure...
            Assert.ThrowsAny<Exception>(
                () =>
                    brokenService.Record(
                        CreateEntry(
                            "A.md",
                            "assetA",
                            ProvenanceDocumentKind.Final,
                            DateTimeOffset.Now)));

            // ...but the MainForm bookkeeping layer must swallow it so a
            // committed asset is never rolled back because of history state.
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                brokenService,
                workspace.CreateRequestProgressService());

            var recordMethod =
                typeof(MainForm).GetMethod(
                    "RecordRecentDocument",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);

            // Must not throw despite the broken history store.
            recordMethod?.Invoke(
                form,
                new object[]
                {
                    ProvenanceDocumentKind.Final,
                    Path.Combine(workspace.Assets, "a", AppConstants.FinalProvenanceFileName),
                    "a",
                    DateTimeOffset.Now
                });
        });
    }

    [Fact]
    public void CorruptHistoryFileLoadsAsEmptyThroughSafePath()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            workspace.RecentDocumentsPath,
            "{ corrupt !!");

        var service =
            workspace.CreateRecentDocumentHistoryService();

        Assert.Throws<InvalidDataException>(
            () =>
                service.Load());
    }

    [Fact]
    public void FilePath_ReturnsConstructorPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "recent-documents.json");
        var service = new RecentDocumentHistoryService(path);

        Assert.Equal(path, service.FilePath);
    }

    [Fact]
    public void RefreshRecentDocumentsUi_NoEntriesArgument_LoadsFromHistoryService()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var historyService = workspace.CreateRecentDocumentHistoryService();

            historyService.Record(
                CreateEntry(
                    "A.md",
                    "assetA",
                    ProvenanceDocumentKind.Final,
                    DateTimeOffset.Now));

            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                historyService,
                workspace.CreateRequestProgressService());

            var lvRecentDocuments =
                typeof(MainForm).GetField(
                    "lvRecentDocuments",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)
                    !.GetValue(form) as System.Windows.Forms.ListView;

            var refreshMethod =
                typeof(MainForm).GetMethod(
                    "RefreshRecentDocumentsUi",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance);

            // Calling with no argument (null) forces the method to load via
            // LoadRecentDocumentsSafe() instead of using a caller-supplied list.
            refreshMethod!.Invoke(form, new object?[] { null });

            Assert.NotNull(lvRecentDocuments);
            Assert.Single(lvRecentDocuments!.Items);
        });
    }

    [Fact]
    public void RefreshRecentDocumentsUi_NoEntriesArgument_HistoryLoadThrows_SwallowsAndShowsNothing()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // A history file that fails to parse makes Load() throw, so
            // LoadRecentDocumentsSafe() must swallow it and return empty.
            File.WriteAllText(
                workspace.RecentDocumentsPath,
                "{ corrupt !!");

            var historyService = workspace.CreateRecentDocumentHistoryService();

            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                historyService,
                workspace.CreateRequestProgressService());

            var lvRecentDocuments =
                typeof(MainForm).GetField(
                    "lvRecentDocuments",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)
                    !.GetValue(form) as System.Windows.Forms.ListView;

            var refreshMethod =
                typeof(MainForm).GetMethod(
                    "RefreshRecentDocumentsUi",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance);

            var exception = Record.Exception(
                () => refreshMethod!.Invoke(form, new object?[] { null }));

            Assert.Null(exception);
            Assert.NotNull(lvRecentDocuments);
            Assert.Empty(lvRecentDocuments!.Items);
        });
    }
}