using System.Security.Cryptography;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class ValidationService
{
    // BUG-005: Extended with Unicode-superscript COM/LPT variants (COM¹ COM² COM³
    // LPT¹ LPT² LPT³) documented by Microsoft as reserved on Windows.
    private static readonly HashSet<string> ReservedDeviceNames =
        new(
            new[]
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",

                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "COM\u00B9", // COM¹
                "COM\u00B2", // COM²
                "COM\u00B3", // COM³

                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9",
                "LPT\u00B9", // LPT¹
                "LPT\u00B2", // LPT²
                "LPT\u00B3", // LPT³

                "CONIN$",
                "CONOUT$"
            },
            StringComparer.OrdinalIgnoreCase);

    public ValidationResult ValidateSettings(
        AppSettings settings) =>
        ValidateProcessingSettings(settings);

    public ValidationResult ValidateProcessingSettings(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(
                settings.AssetRootFolder))
        {
            errors.Add(
                "Asset Root Folder must not be empty.");
        }
        else if (!Directory.Exists(
                     settings.AssetRootFolder))
        {
            errors.Add(
                $"Asset Root Folder does not exist: {settings.AssetRootFolder}");
        }
        else if (IsReparsePoint(settings.AssetRootFolder)) // BUG-007
        {
            errors.Add(
                "Asset Root Folder is a reparse point (junction or symbolic link) and cannot be used safely.");
        }

        if (settings.AcceptedExtensions is null ||
            settings.AcceptedExtensions.Count == 0)
        {
            errors.Add(
                "Accepted image extensions must not be empty.");
        }
        else
        {
            foreach (var ext in settings.AcceptedExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext) ||
                    !ext.StartsWith('.') ||
                    ext.Length == 1 ||
                    ext.Contains('/') ||
                    ext.Contains('\\') ||
                    ext.Contains(".."))
                {
                    errors.Add(
                        $"Accepted extension '{ext}' is invalid. Extensions must start with '.' and contain no path separators.");
                }
                else if (!AppConstants.DefaultImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"Unsupported image extension configured: {ext}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.DownloadFolder) &&
            Directory.Exists(settings.DownloadFolder) &&
            !string.IsNullOrWhiteSpace(settings.AssetRootFolder) &&
            Directory.Exists(settings.AssetRootFolder))
        {
            if (PathsEqual(settings.DownloadFolder, settings.AssetRootFolder))
            {
                errors.Add(
                    "Download Folder and Asset Root Folder cannot be the same directory.");
            }
            else
            {
                var normDownload =
                    NormalizePath(settings.DownloadFolder)
                    + Path.DirectorySeparatorChar;

                var normAsset =
                    NormalizePath(settings.AssetRootFolder)
                    + Path.DirectorySeparatorChar;

                if (normAsset.StartsWith(
                        normDownload,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Asset Root Folder cannot be inside the Download Folder.");
                }
                else if (normDownload.StartsWith(
                             normAsset,
                             StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Download Folder cannot be inside the Asset Root Folder.");
                }
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateDownloadFolder(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ValidateDownloadFolder(settings.DownloadFolder);
    }

    public ValidationResult ValidateDownloadFolder(
        string? downloadFolder)
    {
        if (string.IsNullOrWhiteSpace(downloadFolder))
        {
            return ValidationResult.Failure(
                "Image Download Folder must not be empty for Refresh/Open Folder.");
        }

        if (!Directory.Exists(downloadFolder))
        {
            return ValidationResult.Failure(
                $"Image Download Folder does not exist: {downloadFolder}");
        }

        return ValidationResult.Success();
    }

    public ValidationResult ValidateAssetRootFolder(
        string? assetRootFolder)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(assetRootFolder))
        {
            errors.Add(
                "Asset Root Folder must not be empty.");
        }
        else if (!Directory.Exists(assetRootFolder))
        {
            errors.Add(
                $"Asset Root Folder does not exist: {assetRootFolder}");
        }
        else if (IsReparsePoint(assetRootFolder))
        {
            errors.Add(
                "Asset Root Folder is a reparse point (junction or symbolic link) and cannot be used safely.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidatePrompt(
        string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ValidationResult.Failure(
                "Final Prompt must not be empty.");
        }

        return ValidationResult.Success();
    }

    public ValidationResult ValidateAssetFolderName(
        string name)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(
                "Asset Folder Name must not be empty.");

            return ValidationResult.Failure(
                errors);
        }

        if (name is "." or "..")
        {
            errors.Add(
                "Asset Folder Name must not be '.' or '..'.");
        }

        if (name.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(char.IsControl))
        {
            errors.Add(
                "Asset Folder Name contains invalid Windows filename characters.");
        }

        if (name.EndsWith(
                " ",
                StringComparison.Ordinal) ||
            name.EndsWith(
                ".",
                StringComparison.Ordinal))
        {
            errors.Add(
                "Asset Folder Name must not end with a space or dot.");
        }

        // BUG-005: Extract the stem before the *first* dot, not the last, so
        // that names like NUL.tar.gz, PRN.foo.bar, COM².webp are all caught.
        // GetFileNameWithoutExtension stops at the last dot only.
        var firstDotIndex =
            name.IndexOf('.');

        var stem =
            (firstDotIndex >= 0 ? name[..firstDotIndex] : name)
                .TrimEnd(' ', '.');

        if (ReservedDeviceNames.Contains(stem))
        {
            errors.Add(
                $"Asset Folder Name uses reserved Windows device name '{stem}'.");
        }

        if (!string.Equals(
                Path.GetFileName(name),
                name,
                StringComparison.Ordinal))
        {
            errors.Add(
                "Asset Folder Name must be a single folder name and must not contain a path.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateAssetName(
        string name,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        var baseValidation =
            ValidateAssetFolderName(name);

        var errors =
            baseValidation.Errors.ToList();

        if (!string.IsNullOrWhiteSpace(name) && acceptedExtensions != null)
        {
            var extension =
                Path.GetExtension(name);

            if (!string.IsNullOrWhiteSpace(extension)
                && acceptedExtensions.Contains(
                    extension,
                    StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    "Asset Name must be entered without an image file extension.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateImageFile(
        string path,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(
                "Image path must not be empty.");

            return ValidationResult.Failure(
                errors);
        }

        if (!File.Exists(path))
        {
            errors.Add(
                $"Image file does not exist: {path}");

            return ValidationResult.Failure(
                errors);
        }

        var extension =
            Path.GetExtension(path);

        if (!acceptedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Unsupported image extension '{extension}'.");
        }

        try
        {
            var info =
                new FileInfo(path);

            if (info.Length <= 0)
            {
                errors.Add(
                    $"Image file is empty: {path}");
            }

            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            if (stream.Length <= 0)
            {
                errors.Add(
                    $"Image file is not readable or contains no data: {path}");
            }
            else
            {
                var header = new byte[12];
                var bytesRead = stream.Read(header, 0, header.Length);

                if (!HasValidMagicBytes(extension, header, bytesRead))
                {
                    errors.Add(
                        $"Image file '{Path.GetFileName(path)}' header does not match expected signature for {extension}.");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Image file cannot be opened for reading: {path}. {ex.Message}");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    private static bool HasValidMagicBytes(string extension, byte[] header, int bytesRead)
    {
        var ext = extension.ToLowerInvariant();
        if (ext == ".png")
        {
            return bytesRead >= 8 &&
                   header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                   header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
        }

        if (ext is ".jpg" or ".jpeg")
        {
            return bytesRead >= 3 &&
                   header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        }

        if (ext == ".webp")
        {
            return bytesRead >= 12 &&
                   header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
                   header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';
        }

        return false;
    }

    [ThreadStatic]
    internal static Func<string, FileAttributes>? FileAttributesProvider;

    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var attributes = FileAttributesProvider is not null
                ? FileAttributesProvider(path)
                : File.GetAttributes(path);

            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Fail closed for safety: if attributes cannot be checked on an existing path (e.g. UnauthorizedAccessException), treat as unsafe
            return true;
        }
    }

    public static string ComputeSha256(
        string filePath)
    {
        using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                // Do not permit a destructive-operation authority check to
                // race a writer, renamer, or deleter through a shared handle.
                FileShare.Read);

        var hash =
            SHA256.HashData(
                stream);

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    private static void RequireContains(
        string source,
        string expected,
        string error,
        ICollection<string> errors)
    {
        if (!source.Contains(
                expected,
                StringComparison.Ordinal))
        {
            errors.Add(
                error);
        }
    }
}
