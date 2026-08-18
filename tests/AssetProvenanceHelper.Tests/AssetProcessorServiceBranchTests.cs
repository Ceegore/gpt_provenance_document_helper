using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class AssetProcessorServiceBranchTests
{
    [Fact]
    public void ProcessMainImage_WithIdenticalImageAsReference_ThrowsInvalidOperationException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refBytes = new byte[] { 10, 20, 30, 40 };
        var refSource = workspace.CreateImage("ref.png", refBytes);
        var session = processor.ProcessReference(settings, "asset_same_test", refSource, DateTimeOffset.Now);

        // Main image with IDENTICAL bytes
        var mainSource = workspace.CreateImage("main.png", refBytes);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "test prompt", DateTimeOffset.Now));

        Assert.Contains("identical to the reference image", ex.Message);
    }

    [Fact]
    public void ProcessMainImage_WithEmptyOrWhitespacePrompt_ThrowsArgumentException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_prompt_test", refSource, DateTimeOffset.Now);
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        Assert.Throws<ArgumentException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "", DateTimeOffset.Now));

        Assert.Throws<ArgumentException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "   ", DateTimeOffset.Now));
    }

    [Fact]
    public void WriteTextAtomic_ExistingFile_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var existingFile = Path.Combine(workspace.Root, "existing.txt");
        File.WriteAllText(existingFile, "content");

        Assert.Throws<IOException>(() =>
            processor.WriteTextAtomic(existingFile, "new content"));
    }

    [Fact]
    public void CopyFileWithoutOverwrite_ExistingDestination_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var src = workspace.CreateImage("src.png", new byte[] { 1 });
        var dst = workspace.CreateImage("dst.png", new byte[] { 2 });

        Assert.Throws<IOException>(() =>
            processor.CopyFileWithoutOverwrite(src, dst));
    }

    [Fact]
    public void PrepareReferenceReplacement_WithExistingDifferentNameDestination_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_replace_test", refSource, DateTimeOffset.Now);

        // Pre-create destination file with different name
        var conflictingPath = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName, "ref2.png");
        File.WriteAllBytes(conflictingPath, new byte[] { 9, 9, 9 });

        var newSource = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        Assert.Throws<IOException>(() =>
            processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, newSource, DateTimeOffset.Now));
    }
}
