using System.Globalization;
using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ValidationServiceBranchTests
{
    [Fact]
    public void ValidateSettings_EdgeCases_AreDetected()
    {
        var service = new ValidationService();

        // Null / empty project name
        var s1 = new AppSettings { ProjectName = "", DownloadFolder = @"C:\Downloads", AssetRootFolder = @"C:\Assets", AcceptedExtensions = new List<string> { ".png" } };
        Assert.False(service.ValidateSettings(s1).IsValid);

        // Empty extensions
        var s2 = new AppSettings { ProjectName = "Proj", DownloadFolder = @"C:\Downloads", AssetRootFolder = @"C:\Assets", AcceptedExtensions = new List<string>() };
        Assert.False(service.ValidateSettings(s2).IsValid);

        // Non-existent directories
        var s3 = new AppSettings { ProjectName = "Proj", DownloadFolder = @"Z:\NonExistent_Downloads_123", AssetRootFolder = @"Z:\NonExistent_Assets_123", AcceptedExtensions = new List<string> { ".png" } };
        var r3 = service.ValidateSettings(s3);
        Assert.False(r3.IsValid);
        Assert.Contains(r3.Errors, e => e.Contains("Download Folder does not exist"));
        Assert.Contains(r3.Errors, e => e.Contains("Asset Root Folder does not exist"));

        // Same directory for download and asset
        using var workspace = new TestWorkspace();
        var s4 = new AppSettings { ProjectName = "Proj", DownloadFolder = workspace.Downloads, AssetRootFolder = workspace.Downloads, AcceptedExtensions = new List<string> { ".png" } };
        var r4 = service.ValidateSettings(s4);
        Assert.False(r4.IsValid);
        Assert.Contains(r4.Errors, e => e.Contains("cannot be the same directory"));

        // Subdirectory relationship
        var subDir = Path.Combine(workspace.Downloads, "subdir");
        Directory.CreateDirectory(subDir);
        var s5 = new AppSettings { ProjectName = "Proj", DownloadFolder = workspace.Downloads, AssetRootFolder = subDir, AcceptedExtensions = new List<string> { ".png" } };
        var r5 = service.ValidateSettings(s5);
        Assert.False(r5.IsValid);
        Assert.Contains(r5.Errors, e => e.Contains("inside"));
    }

    [Fact]
    public void ValidateSession_MissingFields_ReturnsExpectedErrors()
    {
        var service = new ValidationService();

        var emptySession = new AssetSession();
        var result = service.ValidateSession(emptySession);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ProjectName is missing"));
        Assert.Contains(result.Errors, e => e.Contains("AssetRootFolder is missing"));
        Assert.Contains(result.Errors, e => e.Contains("AssetFolderName is missing"));
        Assert.Contains(result.Errors, e => e.Contains("AssetFolder is missing"));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProcessedAt is missing"));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceFilename is missing"));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceHash is missing"));
    }

    [Fact]
    public void ValidateSession_InvalidHash_ReturnsError()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_hash_test", refSource, DateTimeOffset.Now);

        // Invalid hash format (not 64 hex chars)
        session.ReferenceHash = "not_a_valid_hash";
        var service = workspace.CreateValidationService();
        var result = service.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SHA-256 hexadecimal value"));

        // 64 chars but non-hex characters
        session.ReferenceHash = new string('Z', 64);
        var resultNonHex = service.ValidateSession(session);
        Assert.False(resultNonHex.IsValid);
        Assert.Contains(resultNonHex.Errors, e => e.Contains("SHA-256 hexadecimal value"));
    }

    [Fact]
    public void ValidateSession_ReferenceFilenameWithPath_ReturnsError()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_fn_test", refSource, DateTimeOffset.Now);
        session.ReferenceFilename = @"sub\ref.png";

        var service = workspace.CreateValidationService();
        var result = service.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must contain only a filename"));
    }

    [Fact]
    public void ValidateSession_MismatchedAssetFolder_ReturnsError()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_mis_test", refSource, DateTimeOffset.Now);
        session.AssetFolder = Path.Combine(workspace.Assets, "some_other_folder");

        var service = workspace.CreateValidationService();
        var result = service.ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not match AssetRootFolder + AssetFolderName"));
    }

    [Fact]
    public void ValidateReferenceOutput_MissingElementsInProvenance_Fails()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_prov_test", refSource, DateTimeOffset.Now);

        // Overwrite provenance with content missing project name
        File.WriteAllText(session.ReferenceProvenancePath, "Asset ID: ref.png\nGeneration date: 2026-08-17\n", Encoding.UTF8);

        var service = workspace.CreateValidationService();
        var result = service.ValidateReferenceOutput(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("expected Project value"));
    }

    [Fact]
    public void ValidateImageFile_HandlesLockedOrMissingFiles()
    {
        var service = new ValidationService();

        // Non-existent image
        var missingResult = service.ValidateImageFile(@"C:\NonExistent_Image_12345.png", new[] { ".png" });
        Assert.False(missingResult.IsValid);
        Assert.Contains(missingResult.Errors, e => e.Contains("does not exist"));

        // Valid file
        using var workspace = new TestWorkspace();
        var validPath = workspace.CreateImage("test.png", new byte[] { 1, 2, 3 });
        var validResult = service.ValidateImageFile(validPath, new[] { ".png" });
        Assert.True(validResult.IsValid);
    }
}
