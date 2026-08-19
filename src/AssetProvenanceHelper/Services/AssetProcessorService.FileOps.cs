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

    // BUG-R16-001: Verify text file content ownership before allowing deletion
    private static bool TryVerifyTextFileOwnership(
        string path,
        string expectedContent)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var currentContent = File.ReadAllText(path, new UTF8Encoding(false));
            return string.Equals(currentContent, expectedContent, StringComparison.Ordinal);
        }
        catch
        {
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

    private static void TryDeleteEmptyDirectoryWithError(
        string path,
        ICollection<string> errors)
    {
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
            Directory.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete empty directory '{path}': {ex.Message}");
        }
    }

    private static void TryRestoreFileWithError(
        string backupPath,
        string destinationPath,
        string description,
        ICollection<string> errors)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            if (File.Exists(destinationPath))
            {
                errors.Add(
                    $"Could not restore {description}: destination already exists: {destinationPath}");

                return;
            }

            File.Move(
                backupPath,
                destinationPath,
                overwrite: false);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not restore {description}: {ex.Message}");
        }
    }
}
