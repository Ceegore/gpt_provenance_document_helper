using System.Security.Cryptography;
using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class AssetProcessorService
{
    internal void CopyFileWithoutOverwrite(
        string source,
        string destination)
    {
        if (File.Exists(destination))
        {
            throw new IOException(
                $"Destination file already exists: {destination}");
        }

        File.Copy(
            source,
            destination,
            overwrite: false);
    }

    internal static void WriteTextDurablyToReservedPath(
        string path,
        string content)
    {
        if (File.Exists(path))
        {
            throw new IOException(
                $"Target staging file already exists: {path}");
        }

        var directory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                $"Could not determine directory for '{path}'.");

        Directory.CreateDirectory(directory);

        using var stream =
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

        OnReservedTextStagingOpenedHook?.Invoke(path);

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    internal void WriteTextAtomic(
        string targetPath,
        string content)
    {
        if (File.Exists(targetPath))
        {
            throw new IOException(
                $"Destination file already exists: {targetPath}");
        }

        var directory =
            Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                $"Could not determine directory for '{targetPath}'.");

        Directory.CreateDirectory(
            directory);

        var tempPath =
            Path.Combine(
                directory,
                $".__write_{Guid.NewGuid():N}.tmp");

        try
        {
            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
            using (
                var writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
            {
                writer.Write(
                    content);

                writer.Flush();

                stream.Flush(
                    true);
            }

            File.Move(
                tempPath,
                targetPath,
                overwrite: false);
        }
        catch
        {
            TryDeleteFile(
                tempPath);

            throw;
        }
    }

    private static void TryDeleteFile(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Only used for cleanup of a temporary file while
            // preserving the original exception.
        }
    }

    // BUG-R16-001: Verify file hash ownership before allowing deletion
    private bool TryVerifyFileHashOwnership(
        string path,
        string expectedHash)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var currentHash = ComputeSha256(path);
            return string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // BUG-R12-001 & BUG-R13-001: Delete file only after verifying path safety and exact hash ownership immediately before deletion (after hook)
    private bool TryDeleteHashOwnedFileWithError(
        string path,
        string expectedHash,
        string description,
        Func<ValidationResult> validatePathSafety,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedHash);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(validatePathSafety);
        ArgumentNullException.ThrowIfNull(errors);

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            // Test/external-race boundary must occur BEFORE the final path and ownership verification.
            OnBeforeDeleteFileHook?.Invoke(path);

            var pathSafety = validatePathSafety();
            if (!pathSafety.IsValid)
            {
                errors.Add(
                    $"{description} at '{path}' was preserved because path safety changed before deletion: {string.Join("; ", pathSafety.Errors)}");

                return false;
            }

            if (!File.Exists(path))
            {
                return true;
            }

            var actualHash = ComputeSha256(path);

            if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{description} at '{path}' changed before deletion. File preserved.");

                return false;
            }

            File.Delete(path);

            return true;
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete {description} '{path}': {ex.Message}");

            return false;
        }
    }

    private static void TryDeleteFileWithError(
        string path,
        ICollection<string> errors)
    {
        try
        {
            if (File.Exists(path))
            {
                OnBeforeDeleteFileHook?.Invoke(path);
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete file '{path}': {ex.Message}");
        }
    }

    // BUG-R13-001: Delete empty directory with path safety recheck after hook
    private static void TryDeleteEmptyDirectoryWithError(
        string path,
        Func<ValidationResult> validatePathSafety,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(validatePathSafety);
        ArgumentNullException.ThrowIfNull(errors);

        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            if (Directory
                .EnumerateFileSystemEntries(path)
                .Any())
            {
                return;
            }

            OnBeforeDeleteDirectoryHook?.Invoke(path);

            var pathSafety = validatePathSafety();
            if (!pathSafety.IsValid)
            {
                errors.Add(
                    $"Directory at '{path}' was not deleted because path safety changed: {string.Join("; ", pathSafety.Errors)}");
                return;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            if (Directory
                .EnumerateFileSystemEntries(path)
                .Any())
            {
                return;
            }

            if (ValidationService.IsReparsePoint(path))
            {
                errors.Add(
                    $"Directory at '{path}' was not deleted because it is a reparse point.");
                return;
            }

            Directory.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete empty directory '{path}': {ex.Message}");
        }
    }

    // BUG-R12-001 & BUG-R13-001: Restore file only after verifying path safety and exact hash ownership immediately before restore (after hook)
    private bool TryRestoreHashOwnedFileWithError(
        string backupPath,
        string destinationPath,
        string expectedHash,
        string description,
        Func<ValidationResult> validatePathSafety,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(backupPath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(expectedHash);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(validatePathSafety);
        ArgumentNullException.ThrowIfNull(errors);

        try
        {
            if (!File.Exists(backupPath))
            {
                errors.Add(
                    $"{description} backup is missing: {backupPath}");

                return false;
            }

            if (File.Exists(destinationPath))
            {
                errors.Add(
                    $"Could not restore {description}: destination already exists: {destinationPath}");

                return false;
            }

            OnBeforeRestoreFileHook?.Invoke(
                backupPath,
                destinationPath);

            var pathSafety = validatePathSafety();
            if (!pathSafety.IsValid)
            {
                errors.Add(
                    $"{description} was not restored because path safety changed before restore: {string.Join("; ", pathSafety.Errors)}");

                return false;
            }

            if (!File.Exists(backupPath))
            {
                errors.Add(
                    $"{description} backup disappeared before restore.");

                return false;
            }

            if (File.Exists(destinationPath))
            {
                errors.Add(
                    $"Could not restore {description}: destination appeared before restore: {destinationPath}");

                return false;
            }

            var actualHash = ComputeSha256(backupPath);

            if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{description} backup changed before restore. Backup preserved.");

                return false;
            }

            File.Move(
                backupPath,
                destinationPath,
                overwrite: false);

            return true;
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not restore {description}: {ex.Message}");

            return false;
        }
    }

    // BUG-R13-001: Validate full session path hierarchy and reparse states for destructive operations
    private ValidationResult ValidateSessionDestructivePathSafety(AssetSession session)
    {
        var pathSafety = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        if (!pathSafety.IsValid)
        {
            return pathSafety;
        }

        if (ValidationService.IsReparsePoint(session.AssetFolder))
        {
            return ValidationResult.Failure("Asset folder is a reparse point.");
        }

        var refFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        if (Directory.Exists(refFolder) && ValidationService.IsReparsePoint(refFolder))
        {
            return ValidationResult.Failure("Reference folder is a reparse point.");
        }

        var ingameFolder = session.GetIngameFolderPath();
        if (Directory.Exists(ingameFolder) && ValidationService.IsReparsePoint(ingameFolder))
        {
            return ValidationResult.Failure("Ingame folder is a reparse point.");
        }

        return ValidationResult.Success();
    }
}
