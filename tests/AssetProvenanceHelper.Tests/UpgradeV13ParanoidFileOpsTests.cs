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
}

