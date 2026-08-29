#nullable enable
using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Paranoid branch-level verification of the v1.3 services. Every test here
/// drives a branch that the main regression suite does not reach.
/// </summary>
public class UpgradeV13ParanoidServiceTests
{
    private const string ValidTemplateContent =
        """
        Provider: <<<PROVIDER>>>
        Date: <<<DATE>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Reference: <<<REFERENCE_FILENAME>>>
        Prompt:
        <<<PROMPT>>>
        """;

    // ---- ProviderTemplateRules uncovered branches ----

    [Fact]
    public void Rules_EmptyContentFailsEarly()
    {
        var result =
            ProviderTemplateRules.ValidateContent(
                "Empty.md",
                "   ");

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rules_WhitespaceOnlyContentFailsEarly()
    {
        var result =
            ProviderTemplateRules.ValidateContent(
                "Empty.md",
                "\r\n\t ");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rules_OversizedContentDirect()
    {
        var content =
            ValidTemplateContent
            + new string('x', ProviderTemplateRules.MaxTemplateBytes);

        var result =
            ProviderTemplateRules.ValidateContent(
                "Huge.md",
                content);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("byte UTF-8 limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotMissingFileName()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = string.Empty,
                    DisplayName = "X",
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            ValidTemplateContent)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("FileName is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotFileNameContainsPath()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = @"folder\ChatGPT.md",
                    DisplayName = "X",
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            ValidTemplateContent)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("only a filename", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotFileNameNotMd()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = "ChatGPT.txt",
                    DisplayName = "X",
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            ValidTemplateContent)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("must use .md", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotMissingDisplayName()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = "ChatGPT.md",
                    DisplayName = string.Empty,
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            ValidTemplateContent)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("DisplayName is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotMissingHash()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = "ChatGPT.md",
                    DisplayName = "ChatGPT",
                    Content = ValidTemplateContent,
                    ContentSha256 = string.Empty
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("ContentSha256 is missing or invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotHashMismatch()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = "ChatGPT.md",
                    DisplayName = "ChatGPT",
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        new string('a', 64)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("does not match ContentSha256", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotHashNotHex()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new ProviderTemplateSnapshot
                {
                    FileName = "ChatGPT.md",
                    DisplayName = "ChatGPT",
                    Content = ValidTemplateContent,
                    ContentSha256 =
                        new string('z', 64)
                });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("ContentSha256 is missing or invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_SnapshotNullThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => ProviderTemplateRules.ValidateSnapshot(null!));
    }

    [Fact]
    public void Rules_ComputeHashIsLowerCaseHex()
    {
        var hash =
            ProviderTemplateRules.ComputeContentSha256(
                ValidTemplateContent);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.True(hash.All(Uri.IsHexDigit));
    }

    // ---- ProviderTemplateCatalogService uncovered branches ----

    [Fact]
    public void Catalog_PureUtf16LeFileRejected()
    {
        using var workspace = new TestWorkspace();

        var bytes =
            new byte[] { 0xFF, 0xFE }
            .Concat(
                Encoding.Unicode.GetBytes(ValidTemplateContent))
            .ToArray();

        File.WriteAllBytes(
            Path.Combine(
                workspace.ProviderTemplates,
                "Utf16Only.md"),
            bytes);

        var result =
            workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(result.HasUsableTemplates);
        Assert.Single(result.Templates);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Utf16Only.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_EmptyFileIgnored()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            Path.Combine(
                workspace.ProviderTemplates,
                "Empty.md"),
            string.Empty);

        var result =
            workspace.CreateProviderTemplateCatalogService().Load();

        Assert.Single(result.Templates);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Empty.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_SameDisplayNameViaCaseIsSingleFileOnWindows()
    {
        // On Windows the filesystem is case-insensitive: writing "chatgpt.md"
        // when "ChatGPT.md" exists overwrites the same file. The catalog must
        // therefore still produce exactly one template and no false errors.
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            Path.Combine(
                workspace.ProviderTemplates,
                "chatgpt.md"),
            ValidTemplateContent);

        var result =
            workspace.CreateProviderTemplateCatalogService().Load();

        Assert.Single(result.Templates);
        Assert.Equal("ChatGPT", result.Templates[0].DisplayName);
    }

    [Fact]
    public void Catalog_OversizedFileIgnoredBeforeDecode()
    {
        using var workspace = new TestWorkspace();

        var bytes =
            new byte[ProviderTemplateRules.MaxTemplateBytes + 100];

        bytes[0] = (byte)'x';

        File.WriteAllBytes(
            Path.Combine(
                workspace.ProviderTemplates,
                "Oversized.md"),
            bytes);

        var result =
            workspace.CreateProviderTemplateCatalogService().Load();

        Assert.Single(result.Templates);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Oversized.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_UnreadableFileIgnored()
    {
        using var workspace = new TestWorkspace();

        var lockedPath =
            Path.Combine(
                workspace.ProviderTemplates,
                "Locked.md");

        File.WriteAllText(
            lockedPath,
            ValidTemplateContent);

        // Hold an exclusive lock so ReadAllBytes fails mid-scan.
        using (var handle =
               new FileStream(
                   lockedPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var result =
                workspace.CreateProviderTemplateCatalogService().Load();

            Assert.True(result.HasUsableTemplates);
            Assert.Single(result.Templates);
            Assert.Contains(
                result.Errors,
                error => error.Contains("Locked.md", StringComparison.Ordinal));
        }
    }

    // ---- ProviderTemplateRenderer uncovered branches ----

    [Fact]
    public void Renderer_InvalidSnapshotThrows()
    {
        var snapshot =
            new ProviderTemplateSnapshot
            {
                FileName = "Bad.md",
                DisplayName = "Bad",
                Content = "no tags",
                ContentSha256 =
                    ProviderTemplateRules.ComputeContentSha256("no tags")
            };

        Assert.Throws<InvalidDataException>(
            () =>
                ProviderTemplateRenderer.Render(
                    snapshot,
                    new ProviderRenderContext
                    {
                        Provider = "P",
                        Date = "2026-08-26",
                        Filename = "f.png",
                        AssetName = "a",
                        Project = "pr",
                        Role = "r",
                        Workflow = "w",
                        ReferenceFilename = "rf",
                        Prompt = "prompt"
                    }));
    }

    [Fact]
    public void Renderer_NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(
            () => ProviderTemplateRenderer.Render(null!, new ProviderRenderContext()));

        Assert.Throws<ArgumentNullException>(
            () => ProviderTemplateRenderer.Render(
                new ProviderTemplateSnapshot(),
                null!));
    }

    [Fact]
    public void Renderer_RendersEveryTagInOrder()
    {
        var content =
            "<<<PROVIDER>>> <<<DATE>>> <<<FILENAME>>> <<<ASSET_NAME>>> <<<PROJECT>>> <<<ROLE>>> <<<WORKFLOW>>> <<<REFERENCE_FILENAME>>> <<<PROMPT>>>";

        var snapshot =
            new ProviderTemplateSnapshot
            {
                FileName = "Full.md",
                DisplayName = "Full",
                Content = content,
                ContentSha256 =
                    ProviderTemplateRules.ComputeContentSha256(
                        content)
            };

        var result =
            ProviderTemplateRenderer.Render(
                snapshot,
                new ProviderRenderContext
                {
                    Provider = "P",
                    Date = "2026-08-26",
                    Filename = "f.png",
                    AssetName = "a",
                    Project = "pr",
                    Role = "r",
                    Workflow = "w",
                    ReferenceFilename = "rf",
                    Prompt = "prompt"
                });

        Assert.Equal(
            "P 2026-08-26 f.png a pr r w rf prompt",
            result);
    }

    // ---- AssetRequestManifestService uncovered branches ----

    [Fact]
    public void Manifest_EmptyPathThrows()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        "   ",
                        workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void Manifest_EmptyFileThrows()
    {
        using var workspace = new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Root,
                "empty.json");

        File.WriteAllText(path, string.Empty);

        Assert.Throws<InvalidDataException>(
            () =>
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        path,
                        workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void Manifest_JsonNullLiteralThrows()
    {
        using var workspace = new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Root,
                "null.json");

        File.WriteAllText(path, "null");

        Assert.Throws<InvalidDataException>(
            () =>
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        path,
                        workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void Manifest_FilenameWithControlCharacterRejected()
    {
        using var workspace = new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Root,
                "control.json");

        File.WriteAllText(
            path,
            """
            {
              "manifestVersion": 1,
              "assets": [
                { "filename": "bad\u0001name.png", "resolution": "10x10", "prompt": "p" }
              ]
            }
            """);

        var ex =
            Assert.Throws<InvalidDataException>(
                () =>
                    new AssetRequestManifestService(
                            workspace.CreateValidationService())
                        .Load(
                            path,
                            workspace.CreateSettings().AcceptedExtensions));

        Assert.Contains("Asset #1", ex.Message);
    }

    [Fact]
    public void Manifest_OversizedPromptRejected()
    {
        using var workspace = new TestWorkspace();

        var hugePrompt =
            new string('p', 1_000_001);

        var json =
            "{\"manifestVersion\":1,\"assets\":[{\"filename\":\"a.png\",\"resolution\":\"10x10\",\"prompt\":\""
            + hugePrompt
            + "\"}]}";

        var path =
            Path.Combine(
                workspace.Root,
                "huge-prompt.json");

        File.WriteAllText(path, json);

        var ex =
            Assert.Throws<InvalidDataException>(
                () =>
                    new AssetRequestManifestService(
                            workspace.CreateValidationService())
                        .Load(
                            path,
                            workspace.CreateSettings().AcceptedExtensions));

        Assert.Contains("Asset #1", ex.Message);
    }

    [Fact]
    public void Manifest_OversizedFileRejected()
    {
        using var workspace = new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Root,
                "oversized.json");

        // 32 MiB + 1 byte of padding.
        using (var stream = File.Create(path))
        {
            var header =
                Encoding.UTF8.GetBytes(
                    "{\"manifestVersion\":1,\"assets\":[{\"filename\":\"a.png\",\"resolution\":\"10x10\",\"prompt\":\"");
            stream.Write(header, 0, header.Length);

            var padding = new byte[32L * 1024L * 1024L - header.Length + 1];
            Array.Fill(padding, (byte)'x');
            stream.Write(padding, 0, padding.Length);

            var footer =
                Encoding.UTF8.GetBytes("\"}]}");
            stream.Write(footer, 0, footer.Length);
        }

        var ex =
            Assert.Throws<InvalidDataException>(
                () =>
                    new AssetRequestManifestService(
                            workspace.CreateValidationService())
                        .Load(
                            path,
                            workspace.CreateSettings().AcceptedExtensions));

        Assert.Contains("byte limit", ex.Message);
    }

    [Fact]
    public void Manifest_InvalidUtf8JsonRejected()
    {
        using var workspace = new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Root,
                "bad-utf8.json");

        File.WriteAllBytes(
            path,
            new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0xFF, 0xFE, 0x7D });

        Assert.ThrowsAny<Exception>(
            () =>
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        path,
                        workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void Manifest_RequestKeyIsOrderedByContent()
    {
        var keyA =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "10x10",
                "p");

        var keyB =
            AssetRequestManifestService.ComputeRequestKey(
                "A.png",
                "10x10",
                "p");

        // Filename comparison is case-insensitive.
        Assert.Equal(keyA, keyB);
    }

    // ---- RequestProgressService uncovered branches ----

    [Fact]
    public void Progress_FilePathPropertyReturnsConfiguredPath()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        Assert.Equal(
            workspace.RequestProgressPath,
            service.FilePath);
    }

    [Fact]
    public void Progress_SaveOverwriteSucceeds()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save("fp-1", new[] { "k1" });
        service.Save("fp-2", new[] { "k2", "k3" });

        var keys =
            service.LoadForManifest("fp-2");

        Assert.Equal(2, keys.Count);
        Assert.Contains("k2", keys);

        // Old fingerprint's keys are gone.
        Assert.Empty(service.LoadForManifest("fp-1"));
    }

    [Fact]
    public void Progress_AtomicTempFileIsNotLeftBehindOnSuccess()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRequestProgressService();

        service.Save("fp", new[] { "k1" });

        var directory =
            Path.GetDirectoryName(workspace.RequestProgressPath)!;

        var leftovers =
            Directory.GetFiles(directory, "*.tmp");

        Assert.Empty(leftovers);
    }

    // ---- RecentDocumentHistoryService uncovered branches ----

    [Fact]
    public void History_JsonNullLiteralThrows()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            workspace.RecentDocumentsPath,
            "null");

        Assert.Throws<InvalidDataException>(
            () =>
                workspace.CreateRecentDocumentHistoryService().Load());
    }

    [Fact]
    public void History_RecordNullEntryThrows()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<ArgumentNullException>(
            () =>
                workspace.CreateRecentDocumentHistoryService()
                    .Record(null!));
    }

    [Fact]
    public void History_RemoveUnderFolderWithNullFolderIsNoop()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        service.Record(
            new RecentDocumentEntry
            {
                Path = Path.Combine(workspace.Assets, "a", "x.md"),
                AssetName = "a",
                Kind = ProvenanceDocumentKind.Final,
                RecordedAt = DateTimeOffset.Now
            });

        service.RemoveEntriesUnderAssetFolder(string.Empty);

        Assert.Single(service.Load());
    }

    [Fact]
    public void History_RemoveUnderFolderIgnoresInvalidEntryPaths()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var state =
            new RecentDocumentHistoryState
            {
                Entries =
                {
                    new RecentDocumentEntry
                    {
                        Path = "not a valid path <>\0",
                        AssetName = "bad",
                        Kind = ProvenanceDocumentKind.Final,
                        RecordedAt = DateTimeOffset.Now
                    }
                }
            };

        var directory =
            Path.GetDirectoryName(workspace.RecentDocumentsPath)!;

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            workspace.RecentDocumentsPath,
            System.Text.Json.JsonSerializer.Serialize(state));

        // Must not throw even though the entry path is invalid.
        service.RemoveEntriesUnderAssetFolder(
            Path.Combine(workspace.Assets, "whatever"));
    }

    [Fact]
    public void History_LoadFiltersBlankPaths()
    {
        using var workspace = new TestWorkspace();

        var state =
            new RecentDocumentHistoryState
            {
                Entries =
                {
                    new RecentDocumentEntry
                    {
                        Path = "   ",
                        AssetName = "blank",
                        Kind = ProvenanceDocumentKind.Final,
                        RecordedAt = DateTimeOffset.Now
                    },
                    new RecentDocumentEntry
                    {
                        Path = Path.Combine(workspace.Assets, "a", "x.md"),
                        AssetName = "a",
                        Kind = ProvenanceDocumentKind.Reference,
                        RecordedAt = DateTimeOffset.Now
                    }
                }
            };

        Directory.CreateDirectory(
            Path.GetDirectoryName(workspace.RecentDocumentsPath)!);

        File.WriteAllText(
            workspace.RecentDocumentsPath,
            System.Text.Json.JsonSerializer.Serialize(state));

        var entries =
            workspace.CreateRecentDocumentHistoryService().Load();

        Assert.Single(entries);
        Assert.Equal("a", entries[0].AssetName);
    }

    [Fact]
    public void History_RecordingKeepsNewestFirstAcrossReload()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateRecentDocumentHistoryService();

        var now =
            new DateTimeOffset(
                2026,
                8,
                27,
                10,
                0,
                0,
                TimeSpan.Zero);

        service.Record(
            new RecentDocumentEntry
            {
                Path = Path.Combine(workspace.Assets, "a", "x.md"),
                AssetName = "a",
                Kind = ProvenanceDocumentKind.Final,
                RecordedAt = now
            });

        service.Record(
            new RecentDocumentEntry
            {
                Path = Path.Combine(workspace.Assets, "b", "y.md"),
                AssetName = "b",
                Kind = ProvenanceDocumentKind.Reference,
                RecordedAt = now.AddMinutes(1)
            });

        var reloaded =
            new RecentDocumentHistoryService(
                    workspace.RecentDocumentsPath)
                .Load();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("b", reloaded[0].AssetName);
        Assert.Equal(ProvenanceDocumentKind.Reference, reloaded[0].Kind);
    }

    // ---- TemplateService bridge uncovered branches ----

    [Fact]
    public void TemplateBridge_RenderReferenceForSessionNullSessionThrows()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<ArgumentNullException>(
            () =>
                workspace.CreateTemplateService()
                    .RenderReferenceForSession(
                        null!,
                        "ref.png",
                        DateTimeOffset.Now));
    }

    [Fact]
    public void TemplateBridge_RenderFinalForSessionNullSessionThrows()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<ArgumentNullException>(
            () =>
                workspace.CreateTemplateService()
                    .RenderFinalForSession(
                        null!,
                        "main.png",
                        "prompt",
                        DateTimeOffset.Now));
    }

    [Fact]
    public void TemplateBridge_ProviderSnapshotNullSessionFallbackLegacy()
    {
        using var workspace = new TestWorkspace();

        var templateService =
            workspace.CreateTemplateService();

        var session =
            new AssetSession
            {
                SchemaVersion = 2,
                WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
                ProjectName = "Legacy",
                AssetFolderName = "asset",
                ReferenceFilename = "ref.png"
            };

        var rendered =
            templateService.RenderReferenceForSession(
                session,
                "ref.png",
                new DateTimeOffset(
                    2026,
                    8,
                    27,
                    0,
                    0,
                    0,
                    TimeSpan.Zero));

        Assert.Contains("Asset ID: ref.png", rendered);
        Assert.Contains("Project: Legacy", rendered);
    }

    [Fact]
    public void TemplateBridge_NoReferenceSessionUsesNoReferenceTemplate()
    {
        using var workspace = new TestWorkspace();

        var templateService =
            workspace.CreateTemplateService();

        var session =
            new AssetSession
            {
                SchemaVersion = 2,
                WorkflowMode = AssetWorkflowMode.NoReference,
                ProjectName = "Legacy",
                AssetFolderName = "asset"
            };

        var rendered =
            templateService.RenderFinalForSession(
                session,
                "main.png",
                "prompt",
                new DateTimeOffset(
                    2026,
                    8,
                    27,
                    0,
                    0,
                    0,
                    TimeSpan.Zero));

        Assert.Contains("Prompt: \"prompt\"", rendered);
        Assert.Contains(
            "STATIC_FINAL_NO_REFERENCE_MARKER",
            rendered);
    }

    [Fact]
    public void TemplateBridge_UnsupportedWorkflowModeThrows()
    {
        using var workspace = new TestWorkspace();

        var templateService =
            workspace.CreateTemplateService();

        var session =
            new AssetSession
            {
                SchemaVersion = 2,
                WorkflowMode = (AssetWorkflowMode)99,
                ProjectName = "Legacy",
                AssetFolderName = "asset"
            };

        Assert.Throws<InvalidDataException>(
            () =>
                templateService.RenderFinalForSession(
                    session,
                    "main.png",
                    "prompt",
                    DateTimeOffset.Now));
    }
}
// [m1]
// [m2]
// [m3]
// [m4]
// [m9]
// [m10]
