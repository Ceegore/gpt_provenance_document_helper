using AssetProvenanceHelper;

namespace AssetProvenanceHelper.Tests;

public sealed class AssetProcessorServiceTests
{
    [Fact]
    public void ReferenceWorkflow_CreatesExpectedFiles()
    {
        using var workspace =
            new TestWorkspace();

        var source =
            workspace.CreateImage(
                "ChatGPT Image reference.png",
                new byte[]
                {
                    1,
                    2,
                    3
                });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var timestamp =
            new DateTimeOffset(
                2026,
                8,
                17,
                10,
                30,
                0,
                TimeSpan.FromHours(2));

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                source,
                timestamp);

        Assert.True(
            File.Exists(
                session.ReferenceDestinationPath));

        Assert.True(
            File.Exists(
                session.ReferenceProvenancePath));

        Assert.True(
            File.Exists(
                source));

        var provenance =
            File.ReadAllText(
                session.ReferenceProvenancePath);

        Assert.Contains(
            session.ReferenceFilename,
            provenance);

        Assert.Contains(
            session.ProjectName,
            provenance);

        Assert.Contains(
            "Generation date: 2026-08-17",
            provenance);

        Assert.False(
            string.IsNullOrWhiteSpace(
                session.ReferenceHash));
    }

    [Fact]
    public void ReferenceWorkflow_DoesNotOverwriteExistingReference()
    {
        using var workspace =
            new TestWorkspace();

        var source =
            workspace.CreateImage(
                "reference.png");

        var assetFolder =
            Path.Combine(
                workspace.Assets,
                "asset1");

        var referenceFolder =
            Path.Combine(
                assetFolder,
                AppConstants.ReferenceFolderName);

        Directory.CreateDirectory(
            referenceFolder);

        File.WriteAllBytes(
            Path.Combine(
                referenceFolder,
                "reference.png"),
            new byte[]
            {
                9
            });

        var processor =
            workspace.CreateAssetProcessor();

        Assert.Throws<IOException>(
            () =>
                processor.ProcessReference(
                    workspace.CreateSettings(),
                    "asset1",
                    source,
                    DateTimeOffset.Now));
    }

    [Fact]
    public void MainWorkflow_CreatesFinalAsset()
    {
        using var workspace =
            new TestWorkspace();

        var reference =
            workspace.CreateImage(
                "reference.png",
                new byte[]
                {
                    1,
                    2,
                    3
                });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                reference,
                DateTimeOffset.Now);

        var main =
            workspace.CreateImage(
                "main.png",
                new byte[]
                {
                    4,
                    5,
                    6
                });

        const string prompt =
            "bitte gib mir 4 varianten davon";

        var filename =
            processor.ProcessMainImage(
                session,
                settings.AcceptedExtensions,
                main,
                prompt,
                DateTimeOffset.Now);

        var mainDestination =
            Path.Combine(
                session.AssetFolder,
                filename);

        var finalProvenance =
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName);

        Assert.True(
            File.Exists(
                mainDestination));

        Assert.True(
            File.Exists(
                finalProvenance));

        Assert.True(
            File.Exists(
                main));

        var text =
            File.ReadAllText(
                finalProvenance);

        Assert.Contains(
            filename,
            text);

        Assert.Contains(
            session.ReferenceFilename,
            text);

        Assert.Contains(
            prompt,
            text);
    }

    [Fact]
    public void MainWorkflow_RejectsIdenticalReferenceBytes()
    {
        using var workspace =
            new TestWorkspace();

        var bytes =
            new byte[]
            {
                1,
                2,
                3,
                4
            };

        var reference =
            workspace.CreateImage(
                "reference.png",
                bytes);

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                reference,
                DateTimeOffset.Now);

        var main =
            workspace.CreateImage(
                "main.png",
                bytes);

        Assert.Throws<InvalidOperationException>(
            () =>
                processor.ProcessMainImage(
                    session,
                    settings.AcceptedExtensions,
                    main,
                    "prompt",
                    DateTimeOffset.Now));

        Assert.False(
            File.Exists(
                Path.Combine(
                    session.AssetFolder,
                    "main.png")));

        Assert.True(
            File.Exists(
                session.ReferenceDestinationPath));
    }

    [Fact]
    public void RollbackMain_RemovesOnlyMainArtifacts()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var reference =
            workspace.CreateImage(
                "reference.png",
                new byte[]
                {
                    1
                });

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                reference,
                DateTimeOffset.Now);

        var unrelated =
            Path.Combine(
                session.AssetFolder,
                "unrelated.txt");

        File.WriteAllText(
            unrelated,
            "keep");

        var main =
            workspace.CreateImage(
                "main.png",
                new byte[]
                {
                    2
                });

        var filename =
            processor.ProcessMainImage(
                session,
                settings.AcceptedExtensions,
                main,
                "prompt",
                DateTimeOffset.Now);

        var rollback =
            processor.RollbackMain(
                session,
                filename);

        Assert.True(
            rollback.IsValid);

        Assert.False(
            File.Exists(
                Path.Combine(
                    session.AssetFolder,
                    filename)));

        Assert.False(
            File.Exists(
                Path.Combine(
                    session.AssetFolder,
                    AppConstants.FinalProvenanceFileName)));

        Assert.True(
            File.Exists(
                session.ReferenceDestinationPath));

        Assert.True(
            File.Exists(
                unrelated));
    }

    [Fact]
    public void ReplaceReference_CanBeRolledBack()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var oldSource =
            workspace.CreateImage(
                "old.png",
                new byte[]
                {
                    1
                });

        var oldSession =
            processor.ProcessReference(
                settings,
                "asset1",
                oldSource,
                DateTimeOffset.Now);

        var oldHash =
            oldSession.ReferenceHash;

        var newSource =
            workspace.CreateImage(
                "new.png",
                new byte[]
                {
                    2
                });

        var transaction =
            processor.PrepareReferenceReplacement(
                oldSession,
                settings.AcceptedExtensions,
                newSource,
                DateTimeOffset.Now);

        Assert.True(
            File.Exists(
                transaction.NewSession.ReferenceDestinationPath));

        var rollback =
            processor.RollbackReferenceReplacement(
                transaction);

        Assert.True(
            rollback.IsValid);

        Assert.True(
            File.Exists(
                oldSession.ReferenceDestinationPath));

        Assert.True(
            File.Exists(
                oldSession.ReferenceProvenancePath));

        Assert.Equal(
            oldHash,
            oldSession.ReferenceHash);
    }

    [Fact]
    public void ReplaceReference_CommitRemovesBackups()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var oldSource =
            workspace.CreateImage(
                "old.png",
                new byte[]
                {
                    1
                });

        var oldSession =
            processor.ProcessReference(
                settings,
                "asset1",
                oldSource,
                DateTimeOffset.Now);

        var newSource =
            workspace.CreateImage(
                "new.png",
                new byte[]
                {
                    2
                });

        var transaction =
            processor.PrepareReferenceReplacement(
                oldSession,
                settings.AcceptedExtensions,
                newSource,
                DateTimeOffset.Now);

        var commit =
            processor.CommitReferenceReplacement(
                transaction);

        Assert.True(
            commit.IsValid);

        Assert.False(
            File.Exists(
                transaction.BackupReferencePath));

        Assert.False(
            File.Exists(
                transaction.BackupProvenancePath));

        Assert.True(
            File.Exists(
                transaction.NewSession.ReferenceDestinationPath));
    }
}
