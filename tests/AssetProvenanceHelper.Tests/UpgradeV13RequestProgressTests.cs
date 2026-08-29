#nullable enable
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13RequestProgressTests
{
    private static string WriteState(
        string path,
        string json)
    {
        var directory =
            Path.GetDirectoryName(path)!;

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            path,
            json);

        return path;
    }

    [Fact]
    public void MissingStateReturnsEmpty()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        var keys =
            service.LoadForManifest(
                "fingerprint");

        Assert.Empty(keys);
    }

    [Fact]
    public void SameFingerprintRestoresKeys()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save(
            "fingerprint-A",
            new[] { "key1", "key2" });

        var keys =
            service.LoadForManifest(
                "fingerprint-A");

        Assert.Equal(
            new HashSet<string>
            {
                "key1",
                "key2"
            },
            keys);
    }

    [Fact]
    public void DifferentFingerprintReturnsEmpty()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save(
            "fingerprint-A",
            new[] { "key1" });

        var keys =
            service.LoadForManifest(
                "fingerprint-B");

        Assert.Empty(keys);
    }

    [Fact]
    public void SaveIsAtomic()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save(
            "fingerprint-A",
            new[] { "key1" });

        Assert.True(File.Exists(workspace.RequestProgressPath));

        var leftovers =
            Directory
                .GetFiles(
                    Path.GetDirectoryName(workspace.RequestProgressPath)!,
                    "*.tmp");

        Assert.Empty(leftovers);

        var content =
            File.ReadAllText(
                workspace.RequestProgressPath);

        Assert.Contains("key1", content);
    }

    [Fact]
    public void DuplicateKeysDeduplicated()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save(
            "fingerprint-A",
            new[] { "key1", "key1", "key2" });

        var keys =
            service.LoadForManifest(
                "fingerprint-A");

        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void CorruptStateThrowsOnLoad()
    {
        using var workspace = new TestWorkspace();

        WriteState(
            workspace.RequestProgressPath,
            "{ not valid json !!!");

        var service =
            workspace.CreateRequestProgressService();

        Assert.Throws<InvalidDataException>(
            () =>
                service.LoadForManifest(
                    "fingerprint"));
    }

    [Fact]
    public void WhitespaceKeysAreIgnored()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save(
            "fingerprint-A",
            new[] { "key1", "   " });

        var keys =
            service.LoadForManifest(
                "fingerprint-A");

        Assert.Single(keys);
        Assert.Contains("key1", keys);
    }

    [Fact]
    public void DoneRequestStaysDoneAfterReimport()
    {
        using var workspace = new TestWorkspace();

        var progress =
            workspace.CreateRequestProgressService();

        var manifestService =
            new AssetRequestManifestService(
                workspace.CreateValidationService());

        var manifestPath =
            Path.Combine(
                workspace.Root,
                "manifest.json");

        File.WriteAllText(
            manifestPath,
            """
            {
              "manifestVersion": 1,
              "assets": [
                { "filename": "a.png", "resolution": "10x10", "prompt": "p1" },
                { "filename": "b.png", "resolution": "20x20", "prompt": "p2" }
              ]
            }
            """);

        var manifest =
            manifestService.Load(
                manifestPath,
                workspace.CreateSettings().AcceptedExtensions);

        progress.Save(
            manifest.ManifestFingerprint,
            new[] { manifest.Items[0].RequestKey });

        var restored =
            progress.LoadForManifest(
                manifest.ManifestFingerprint);

        Assert.Contains(
            manifest.Items[0].RequestKey,
            restored);
        Assert.DoesNotContain(
            manifest.Items[1].RequestKey,
            restored);
    }
}