#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Paranoid branch verification of the hash-owned filesystem helpers.
/// These are the last line of defense before any destructive operation.
/// </summary>
public class UpgradeV13ParanoidFileOpsTests
{
    private static ValidationResult Ok() =>
        ValidationResult.Success();

    private static ValidationResult Fail() =>
        ValidationResult.Failure("path safety violated");

    [Fact]
    public void Copy_RefusesExistingDestination()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = workspace.CreateImage("src.png", new byte[] { 1 });
        var dest = Path.Combine(workspace.Root, "dest.png");
        File.WriteAllBytes(dest, new byte[] { 2 });

        Assert.Throws<IOException>(
            () => processor.CopyFileWithoutOverwrite(source, dest));
    }

    [Fact]
    public void Copy_HappyPathCopiesBytes()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = workspace.CreateImage("src.png", new byte[] { 1 });
        var dest = Path.Combine(workspace.Root, "dest.png");

        processor.CopyFileWithoutOverwrite(source, dest);

        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void WriteTextDurable_RefusesExistingPath()
    {
        using var workspace = new TestWorkspace();

        var path = Path.Combine(workspace.Root, "existing.tmp");
        File.WriteAllText(path, "x");

        Assert.Throws<IOException>(
            () => AssetProcessorService.WriteTextDurablyToReservedPath(path, "content"));
    }

    [Fact]
    public void WriteTextDurable_NoDirectoryThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => AssetProcessorService.WriteTextDurablyToReservedPath(string.Empty, "content"));
    }

    [Fact]
    public void WriteTextDurable_HappyPathWritesExactBytes()
    {
        using var workspace = new TestWorkspace();

        var path = Path.Combine(workspace.Root, "sub", "staged.tmp");

        AssetProcessorService.WriteTextDurablyToReservedPath(path, "exact content");

        Assert.Equal("exact content", File.ReadAllText(path));
    }

    [Fact]
    public void WriteTextAtomic_RefusesExistingDestination()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var target = Path.Combine(workspace.Root, "target.txt");
        File.WriteAllText(target, "x");

        Assert.Throws<IOException>(
            () => processor.WriteTextAtomic(target, "content"));
    }

    [Fact]
    public void WriteTextAtomic_NoDirectoryThrows()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        Assert.Throws<InvalidOperationException>(
            () => processor.WriteTextAtomic(string.Empty, "content"));
    }

    [Fact]
    public void WriteTextAtomic_HappyPathLeavesNoTempFiles()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var target = Path.Combine(workspace.Root, "target.txt");

        processor.WriteTextAtomic(target, "atomic content");

        Assert.Equal("atomic content", File.ReadAllText(target));

        var leftovers =
            Directory.GetFiles(
                workspace.Root,
                ".__write_*.tmp");

        Assert.Empty(leftovers);
    }

    [Fact]
    public void DeleteHashOwned_MissingFileSucceeds()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var errors = new List<string>();

        var result = processor.TryDeleteHashOwnedFileWithError(
            Path.Combine(workspace.Root, "missing.png"),
            new string('a', 64),
            "test file",
            Ok,
            errors);

        Assert.True(result);
        Assert.Empty(errors);
    }

    [Fact]
    public void DeleteHashOwned_HashMismatchPreservesFile()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var path = Path.Combine(workspace.Root, "file.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var errors = new List<string>();

        var result = processor.TryDeleteHashOwnedFileWithError(
            path,
            new string('a', 64),
            "test file",
            Ok,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("preserved", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DeleteHashOwned_PathSafetyFailurePreservesFile()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var path = Path.Combine(workspace.Root, "file.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var actualHash =
            ValidationService.ComputeSha256(path);

        var errors = new List<string>();

        var result = processor.TryDeleteHashOwnedFileWithError(
            path,
            actualHash,
            "test file",
            Fail,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("path safety", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DeleteHashOwned_HappyPathDeletes()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var path = Path.Combine(workspace.Root, "file.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var actualHash =
            ValidationService.ComputeSha256(path);

        var errors = new List<string>();

        var result = processor.TryDeleteHashOwnedFileWithError(
            path,
            actualHash,
            "test file",
            Ok,
            errors);

        Assert.True(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteEmptyDirectory_NonEmptyDirectorySkipped()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var dir = Path.Combine(workspace.Root, "nonempty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");

        var errors = new List<string>();

        AssetProcessorService.TryDeleteEmptyDirectoryWithError(dir, Ok, errors);

        Assert.Empty(errors);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteEmptyDirectory_PathSafetyFailurePreservesDirectory()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var dir = Path.Combine(workspace.Root, "empty");
        Directory.CreateDirectory(dir);

        var errors = new List<string>();

        AssetProcessorService.TryDeleteEmptyDirectoryWithError(dir, Fail, errors);

        Assert.Contains(errors, e => e.Contains("path safety", StringComparison.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteEmptyDirectory_MissingDirectorySucceeds()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var errors = new List<string>();

        AssetProcessorService.TryDeleteEmptyDirectoryWithError(
            Path.Combine(workspace.Root, "missing"),
            Ok,
            errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void DeleteEmptyDirectory_HappyPathDeletes()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var dir = Path.Combine(workspace.Root, "empty2");
        Directory.CreateDirectory(dir);

        var errors = new List<string>();

        AssetProcessorService.TryDeleteEmptyDirectoryWithError(dir, Ok, errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void RestoreHashOwned_MissingBackupReportsError()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var errors = new List<string>();

        var result = processor.TryRestoreHashOwnedFileWithError(
            Path.Combine(workspace.Root, "missing-backup"),
            Path.Combine(workspace.Root, "dest"),
            new string('a', 64),
            "test file",
            Ok,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestoreHashOwned_DestinationExistsReportsError()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var backup = Path.Combine(workspace.Root, "backup");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(backup, new byte[] { 1 });
        File.WriteAllBytes(dest, new byte[] { 2 });

        var errors = new List<string>();

        var result = processor.TryRestoreHashOwnedFileWithError(
            backup,
            dest,
            ValidationService.ComputeSha256(backup),
            "test file",
            Ok,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreHashOwned_HashMismatchPreservesBackup()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var backup = Path.Combine(workspace.Root, "backup");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(backup, new byte[] { 1 });

        var errors = new List<string>();

        var result = processor.TryRestoreHashOwnedFileWithError(
            backup,
            dest,
            new string('a', 64),
            "test file",
            Ok,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("changed before restore", StringComparison.Ordinal));
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void RestoreHashOwned_PathSafetyFailurePreservesBackup()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var backup = Path.Combine(workspace.Root, "backup");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(backup, new byte[] { 1 });

        var errors = new List<string>();

        var result = processor.TryRestoreHashOwnedFileWithError(
            backup,
            dest,
            ValidationService.ComputeSha256(backup),
            "test file",
            Fail,
            errors);

        Assert.False(result);
        Assert.Contains(errors, e => e.Contains("path safety", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void RestoreHashOwned_HappyPathMovesBackup()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var backup = Path.Combine(workspace.Root, "backup");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(backup, new byte[] { 1 });

        var errors = new List<string>();

        var result = processor.TryRestoreHashOwnedFileWithError(
            backup,
            dest,
            ValidationService.ComputeSha256(backup),
            "test file",
            Ok,
            errors);

        Assert.True(result);
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void MoveHashOwned_MissingSourceThrows()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        Assert.Throws<IOException>(
            () => processor.MoveHashOwnedFileWithoutOverwrite(
                Path.Combine(workspace.Root, "missing"),
                Path.Combine(workspace.Root, "dest"),
                new string('a', 64),
                "test file",
                Ok));
    }

    [Fact]
    public void MoveHashOwned_ExistingDestinationThrows()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = Path.Combine(workspace.Root, "source");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(source, new byte[] { 1 });
        File.WriteAllBytes(dest, new byte[] { 2 });

        Assert.Throws<IOException>(
            () => processor.MoveHashOwnedFileWithoutOverwrite(
                source,
                dest,
                ValidationService.ComputeSha256(source),
                "test file",
                Ok));
    }

    [Fact]
    public void MoveHashOwned_PathSafetyFailureThrows()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = Path.Combine(workspace.Root, "source");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(source, new byte[] { 1 });

        Assert.Throws<InvalidDataException>(
            () => processor.MoveHashOwnedFileWithoutOverwrite(
                source,
                dest,
                ValidationService.ComputeSha256(source),
                "test file",
                Fail));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void MoveHashOwned_HashMismatchThrows()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = Path.Combine(workspace.Root, "source");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(source, new byte[] { 1 });

        Assert.Throws<InvalidDataException>(
            () => processor.MoveHashOwnedFileWithoutOverwrite(
                source,
                dest,
                new string('a', 64),
                "test file",
                Ok));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void MoveHashOwned_HappyPathMoves()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();

        var source = Path.Combine(workspace.Root, "source");
        var dest = Path.Combine(workspace.Root, "dest");

        File.WriteAllBytes(source, new byte[] { 1 });

        processor.MoveHashOwnedFileWithoutOverwrite(
            source,
            dest,
            ValidationService.ComputeSha256(source),
            "test file",
            Ok);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(dest));
    }

[Fact]
    public void ValidateSessionDestructivePathSafety_ReparseAssetFolderFails()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_reparse",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        // Simulate a reparse point via the test hook.
        ValidationService.FileAttributesProvider =
            path => FileAttributes.ReparsePoint;

        try
        {
            var result = processor.ValidateSessionDestructivePathSafety(session);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    private static System.Reflection.MethodInfo TryDeleteFileMethod() =>
        typeof(AssetProcessorService).GetMethod(
            "TryDeleteFile",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!;

    [Fact]
    public void TryDeleteFile_ExistingUnlockedFile_DeletesIt()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "temp.tmp");
        File.WriteAllText(path, "x");

        TryDeleteFileMethod().Invoke(null, new object[] { path });

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDeleteFile_NonExistentFile_NoOp()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "does-not-exist.tmp");

        var exception = Record.Exception(
            () => TryDeleteFileMethod().Invoke(null, new object[] { path }));

        Assert.Null(exception);
    }

    [Fact]
    public void TryDeleteFile_LockedFile_SwallowsExceptionAndLeavesFileInPlace()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "locked.tmp");
        File.WriteAllText(path, "x");

        using var lockStream =
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var exception = Record.Exception(
            () => TryDeleteFileMethod().Invoke(null, new object[] { path }));

        Assert.Null(exception);
        Assert.True(File.Exists(path));
    }

    // MoveHashOwnedFileWithoutOverwrite is the central forward-move primitive
    // that virtually all canonical promotion eventually depends on. It
    // deliberately re-checks source/destination existence and source hash
    // AFTER OnBeforeHashOwnedMoveHook fires, specifically to catch a race
    // between its first check and the actual move. These two tests use that
    // hook to simulate exactly that race on each side.

    [Fact]
    public void MoveHashOwnedFileWithoutOverwrite_SourceDisappearsAfterInitialCheck_ThrowsAndDoesNotCreateDestination()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var sourcePath = Path.Combine(workspace.Root, "source.bin");
        var destinationPath = Path.Combine(workspace.Root, "destination.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(sourcePath, bytes);
        var expectedHash = ValidationService.ComputeSha256(sourcePath);

        AssetProcessorService.OnBeforeHashOwnedMoveHook = (src, dest) =>
        {
            // Simulate another actor deleting the source between the
            // method's first existence check and its final move.
            File.Delete(src);
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.MoveHashOwnedFileWithoutOverwrite(
                    sourcePath,
                    destinationPath,
                    expectedHash,
                    "test file",
                    ValidationResult.Success));

            Assert.Contains("disappeared before move", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }

        Assert.False(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public void MoveHashOwnedFileWithoutOverwrite_DestinationAppearsAfterInitialCheck_ThrowsAndPreservesForeignDestination()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var sourcePath = Path.Combine(workspace.Root, "source.bin");
        var destinationPath = Path.Combine(workspace.Root, "destination.bin");
        var sourceBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(sourcePath, sourceBytes);
        var expectedHash = ValidationService.ComputeSha256(sourcePath);

        var foreignDestinationBytes = new byte[] { 9, 9, 9 };

        AssetProcessorService.OnBeforeHashOwnedMoveHook = (src, dest) =>
        {
            // Simulate another actor creating the destination between the
            // method's first existence check and its final move.
            File.WriteAllBytes(dest, foreignDestinationBytes);
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.MoveHashOwnedFileWithoutOverwrite(
                    sourcePath,
                    destinationPath,
                    expectedHash,
                    "test file",
                    ValidationResult.Success));

            Assert.Contains("appeared before move", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }

        // The source is untouched (move never happened)...
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));

        // ...and the racing foreign destination content survives exactly as
        // it was, rather than being silently overwritten.
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(foreignDestinationBytes, File.ReadAllBytes(destinationPath));
    }

    // EnsureOldProvenanceByteAuthority hydrates a missing raw provenance hash
    // for legacy (schema 2) sessions from either a backup or canonical
    // provenance file. The happy repair path is already covered elsewhere;
    // these four cover the less-covered failure arms: corrupt/unreadable
    // candidates and the case where neither exists. In every failure case,
    // OldSession.ReferenceProvenanceHash must remain untouched (null) - no
    // destructive mutation on a failed hydration.

    private static AssetSession CreateLegacyOldSession(TestWorkspace workspace, string provenancePath) =>
        new()
        {
            SchemaVersion = 2,
            ProjectName = "TestProject",
            ReferenceFilename = "reference.png",
            ReferenceProcessedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ReferenceProvenancePath = provenancePath,
            ReferenceProvenanceHash = null,
            ProviderTemplate = null,
            AssetFolder = workspace.Root
        };

    [Fact]
    public void EnsureOldProvenanceByteAuthority_CorruptBackup_FailsWithoutMutatingSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var canonicalPath = Path.Combine(workspace.Root, "reference-provenance.md");
        var backupPath = Path.Combine(workspace.Root, "reference-provenance.backup.md");
        File.WriteAllText(backupPath, "this does not match any tool-generated provenance template");

        var oldSession = CreateLegacyOldSession(workspace, canonicalPath);

        var transaction = new ReferenceReplacementTransaction
        {
            TransactionId = new string('a', 32),
            OldSession = oldSession,
            NewSession = oldSession,
            BackupReferencePath = Path.Combine(workspace.Root, "reference.backup.png"),
            BackupProvenancePath = backupPath
        };

        var result = processor.EnsureOldProvenanceByteAuthority(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("legacy backup", StringComparison.OrdinalIgnoreCase));
        Assert.Null(oldSession.ReferenceProvenanceHash);
    }

    [Fact]
    public void EnsureOldProvenanceByteAuthority_UnreadableBackup_FailsWithoutMutatingSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var canonicalPath = Path.Combine(workspace.Root, "reference-provenance.md");
        var backupPath = Path.Combine(workspace.Root, "reference-provenance.backup.md");
        File.WriteAllText(backupPath, "content irrelevant - will be locked");

        var oldSession = CreateLegacyOldSession(workspace, canonicalPath);

        var transaction = new ReferenceReplacementTransaction
        {
            TransactionId = new string('a', 32),
            OldSession = oldSession,
            NewSession = oldSession,
            BackupReferencePath = Path.Combine(workspace.Root, "reference.backup.png"),
            BackupProvenancePath = backupPath
        };

        using (var lockStream = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = processor.EnsureOldProvenanceByteAuthority(transaction);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("legacy backup", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Null(oldSession.ReferenceProvenanceHash);
    }

    [Fact]
    public void EnsureOldProvenanceByteAuthority_NoBackupButCorruptCanonical_FailsWithoutMutatingSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var canonicalPath = Path.Combine(workspace.Root, "reference-provenance.md");
        File.WriteAllText(canonicalPath, "this does not match any tool-generated provenance template");

        var oldSession = CreateLegacyOldSession(workspace, canonicalPath);

        var transaction = new ReferenceReplacementTransaction
        {
            TransactionId = new string('a', 32),
            OldSession = oldSession,
            NewSession = oldSession,
            BackupReferencePath = Path.Combine(workspace.Root, "reference.backup.png"),
            // No backup file exists at this path - candidate 1 is absent.
            BackupProvenancePath = Path.Combine(workspace.Root, "does-not-exist.backup.md")
        };

        var result = processor.EnsureOldProvenanceByteAuthority(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("legacy canonical", StringComparison.OrdinalIgnoreCase));
        Assert.Null(oldSession.ReferenceProvenanceHash);
    }

    [Fact]
    public void EnsureOldProvenanceByteAuthority_NeitherCandidatePresent_FailsWithoutMutatingSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var canonicalPath = Path.Combine(workspace.Root, "reference-provenance.md");
        var oldSession = CreateLegacyOldSession(workspace, canonicalPath);

        var transaction = new ReferenceReplacementTransaction
        {
            TransactionId = new string('a', 32),
            OldSession = oldSession,
            NewSession = oldSession,
            BackupReferencePath = Path.Combine(workspace.Root, "reference.backup.png"),
            BackupProvenancePath = Path.Combine(workspace.Root, "does-not-exist.backup.md")
        };

        var result = processor.EnsureOldProvenanceByteAuthority(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Could not locate", StringComparison.OrdinalIgnoreCase));
        Assert.Null(oldSession.ReferenceProvenanceHash);
    }

    // RequireMainStagingAuthority re-verifies each deterministically-staged
    // file's hash immediately before canonical Main promotion
    // (AssetProcessorService.Main.cs ~1086-1156). OnBeforeMainStagingAuthorityGate
    // fires right after staging completes and right before that re-check, so
    // it is the seam for proving each staged file's corruption independently
    // blocks promotion - with zero new canonical output and the transaction
    // left in a recoverable (IsMainCommitting still true) state.

    [Fact]
    public void RequireMainStagingAuthority_StagedMainCorrupted_BlocksPromotionAndPreservesRecoverableState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_stage_main", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
            File.WriteAllBytes(s.GetMainTempImagePath(), new byte[] { 99, 99, 99 });

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt));
            Assert.Contains("Main staging image", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }

        Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)));
        Assert.False(File.Exists(session.GetIngameImagePath()));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(session.IsMainCommitting);
    }

    [Fact]
    public void RequireMainStagingAuthority_StagedIngameCorrupted_BlocksPromotionAndPreservesRecoverableState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_stage_ingame", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
            File.WriteAllBytes(s.GetMainTempIngamePath(), new byte[] { 99, 99, 99 });

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt));
            Assert.Contains("Ingame staging image", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }

        Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)));
        Assert.False(File.Exists(session.GetIngameImagePath()));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(session.IsMainCommitting);
    }

    [Fact]
    public void RequireMainStagingAuthority_StagedProvenanceCorrupted_BlocksPromotionAndPreservesRecoverableState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_stage_prov", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
            File.WriteAllText(s.GetMainTempProvenancePath(), "tampered provenance content");

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt));
            Assert.Contains("Main staging provenance", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }

        Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)));
        Assert.False(File.Exists(session.GetIngameImagePath()));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(session.IsMainCommitting);
    }
}

