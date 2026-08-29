#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13ProviderSessionTests
{
    /// <summary>
    /// Raw string literals embed whatever line endings are present in the source
    /// file on disk, which depends on the checkout's line-ending normalization
    /// (autocrlf, .gitattributes). Semantic assertions about template mapping
    /// should not depend on that, so normalize before checking multi-line
    /// substrings. Line-ending preservation itself is a separate, explicit
    /// contract tested in Render_PreservesLfTemplateLineEndings and
    /// Render_PreservesCrLfTemplateLineEndings below.
    /// </summary>
    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n");

    private static ProviderTemplateSnapshot CreateSnapshot()
    {
        var content =
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

        return new ProviderTemplateSnapshot
        {
            FileName = "ChatGPT.md",
            DisplayName = "ChatGPT",
            Content = content,
            ContentSha256 =
                ProviderTemplateRules.ComputeContentSha256(
                    content)
        };
    }

    [Fact]
    public void RenderReferenceMapsAllNineValues()
    {
        var snapshot = CreateSnapshot();

        var result =
            ProviderTemplateRenderer.Render(
                snapshot,
                new ProviderRenderContext
                {
                    Provider = "ChatGPT",
                    Date = "2026-08-26",
                    Filename = "reference-image.png",
                    AssetName = "asset_ui_screen_settings",
                    Project = "Roswell",
                    Role = AppConstants.ReferenceRoleLabel,
                    Workflow = AppConstants.ReferenceAssistedWorkflowLabel,
                    ReferenceFilename = "reference-image.png",
                    Prompt = AppConstants.NotRecordedValue
                });

        var normalized = Normalize(result);

        Assert.Contains("Provider: ChatGPT", normalized);
        Assert.Contains("Date: 2026-08-26", normalized);
        Assert.Contains("File: reference-image.png", normalized);
        Assert.Contains("Asset: asset_ui_screen_settings", normalized);
        Assert.Contains("Project: Roswell", normalized);
        Assert.Contains($"Role: {AppConstants.ReferenceRoleLabel}", normalized);
        Assert.Contains($"Workflow: {AppConstants.ReferenceAssistedWorkflowLabel}", normalized);
        Assert.Contains("Reference: reference-image.png", normalized);
        Assert.Contains($"Prompt:\n{AppConstants.NotRecordedValue}", normalized);
    }

    [Fact]
    public void RenderFinalRefAssistedMapsPromptAndReference()
    {
        var snapshot = CreateSnapshot();

        var result =
            ProviderTemplateRenderer.Render(
                snapshot,
                new ProviderRenderContext
                {
                    Provider = "ChatGPT",
                    Date = "2026-08-27",
                    Filename = "main.png",
                    AssetName = "asset1",
                    Project = "Roswell",
                    Role = AppConstants.FinalRoleLabel,
                    Workflow = AppConstants.ReferenceAssistedWorkflowLabel,
                    ReferenceFilename = "reference-image.png",
                    Prompt = "exact prompt"
                });

        var normalized = Normalize(result);

        Assert.Contains("File: main.png", normalized);
        Assert.Contains("Reference: reference-image.png", normalized);
        Assert.Contains("Prompt:\nexact prompt", normalized);
        Assert.Contains($"Role: {AppConstants.FinalRoleLabel}", normalized);
    }

    /// <summary>
    /// Builds a minimal-but-valid template containing all 9 required tags, joined
    /// with an explicitly chosen line ending (never a raw string literal, whose
    /// embedded newlines depend on the source file's own checkout encoding).
    /// </summary>
    private static ProviderTemplateSnapshot CreateSnapshotWithLineEnding(
        string newLine)
    {
        var content = string.Join(
            newLine,
            "Provider: <<<PROVIDER>>>",
            "Date: <<<DATE>>>",
            "File: <<<FILENAME>>>",
            "Asset: <<<ASSET_NAME>>>",
            "Project: <<<PROJECT>>>",
            "Role: <<<ROLE>>>",
            "Workflow: <<<WORKFLOW>>>",
            "Reference: <<<REFERENCE_FILENAME>>>",
            "Prompt:",
            "<<<PROMPT>>>");

        return new ProviderTemplateSnapshot
        {
            FileName = "Test.md",
            DisplayName = "Test",
            Content = content,
            ContentSha256 = ProviderTemplateRules.ComputeContentSha256(content)
        };
    }

    private static ProviderRenderContext CreateContext(string prompt) =>
        new()
        {
            Provider = "ChatGPT",
            Date = "2026-08-26",
            Filename = "main.png",
            AssetName = "asset1",
            Project = "project1",
            Role = AppConstants.FinalRoleLabel,
            Workflow = AppConstants.NoReferenceWorkflowLabel,
            ReferenceFilename = AppConstants.NotRecordedValue,
            Prompt = prompt
        };

    [Fact]
    public void Render_PreservesLfTemplateLineEndings()
    {
        var snapshot = CreateSnapshotWithLineEnding("\n");

        var result = ProviderTemplateRenderer.Render(
            snapshot,
            CreateContext("value"));

        Assert.Contains("Prompt:\nvalue", result);
        Assert.DoesNotContain("\r\n", result);
    }

    [Fact]
    public void Render_PreservesCrLfTemplateLineEndings()
    {
        var snapshot = CreateSnapshotWithLineEnding("\r\n");

        var result = ProviderTemplateRenderer.Render(
            snapshot,
            CreateContext("value"));

        Assert.Contains("Prompt:\r\nvalue", result);
    }

    [Fact]
    public void RenderFinalNoReferenceReferenceNotRecorded()
    {
        var snapshot = CreateSnapshot();

        var result =
            ProviderTemplateRenderer.Render(
                snapshot,
                new ProviderRenderContext
                {
                    Provider = "ChatGPT",
                    Date = "2026-08-27",
                    Filename = "main.png",
                    AssetName = "asset1",
                    Project = "Roswell",
                    Role = AppConstants.FinalRoleLabel,
                    Workflow = AppConstants.NoReferenceWorkflowLabel,
                    ReferenceFilename = AppConstants.NotRecordedValue,
                    Prompt = "exact prompt"
                });

        Assert.Contains($"Reference: {AppConstants.NotRecordedValue}", result);
        Assert.Contains($"Workflow: {AppConstants.NoReferenceWorkflowLabel}", result);
    }

    [Fact]
    public void RenderDoesNotProcessTagsInsideInsertedPrompt()
    {
        var snapshot = CreateSnapshot();

        var result =
            ProviderTemplateRenderer.Render(
                snapshot,
                new ProviderRenderContext
                {
                    Provider = "Test",
                    Date = "2026-08-26",
                    Filename = "main.png",
                    AssetName = "asset1",
                    Project = "project1",
                    Role = AppConstants.FinalRoleLabel,
                    Workflow = AppConstants.NoReferenceWorkflowLabel,
                    ReferenceFilename = AppConstants.NotRecordedValue,
                    Prompt =
                        "Keep literal <<<DATE>>> and <<<PROVIDER>>>."
                });

        Assert.Contains(
            "Keep literal <<<DATE>>> and <<<PROVIDER>>>.",
            result);
    }

    [Fact]
    public void SameTagReplacedTwice()
    {
        var content =
            """
            A: <<<PROVIDER>>>
            B: <<<PROVIDER>>>
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

        var snapshot =
            new ProviderTemplateSnapshot
            {
                FileName = "Test.md",
                DisplayName = "Test",
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
                    Provider = "ChatGPT",
                    Date = "2026-08-26",
                    Filename = "main.png",
                    AssetName = "asset1",
                    Project = "project1",
                    Role = AppConstants.FinalRoleLabel,
                    Workflow = AppConstants.NoReferenceWorkflowLabel,
                    ReferenceFilename = AppConstants.NotRecordedValue,
                    Prompt = "prompt"
                });

        Assert.Contains("A: ChatGPT", result);
        Assert.Contains("B: ChatGPT", result);
        Assert.Equal(
            2,
            result.Split("ChatGPT").Length - 1);
    }

    [Fact]
    public void OriginalTemplateContentUnchangedAfterRender()
    {
        var snapshot = CreateSnapshot();

        var original =
            snapshot.Content;

        _ = ProviderTemplateRenderer.Render(
            snapshot,
            new ProviderRenderContext
            {
                Provider = "ChatGPT",
                Date = "2026-08-26",
                Filename = "main.png",
                AssetName = "asset1",
                Project = "project1",
                Role = AppConstants.FinalRoleLabel,
                Workflow = AppConstants.NoReferenceWorkflowLabel,
                ReferenceFilename = AppConstants.NotRecordedValue,
                Prompt = "prompt"
            });

        Assert.Equal(original, snapshot.Content);
    }

    [Fact]
    public void NewReferenceWithProviderIsSchema3WithSnapshot()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_one",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        Assert.Equal(3, session.SchemaVersion);
        Assert.NotNull(session.ProviderTemplate);
        Assert.Equal("ChatGPT", session.ProviderTemplate!.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(session.ReferenceProvenanceHash));
    }

    [Fact]
    public void NewNoReferenceWithProviderIsSchema3WithSnapshot()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("main.png");

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "asset_two",
            source,
            "prompt",
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        Assert.Equal(3, session.SchemaVersion);
        Assert.NotNull(session.ProviderTemplate);
        Assert.False(string.IsNullOrWhiteSpace(session.MainProvenanceHash));
    }

    [Fact]
    public void LegacyCreationWithoutProviderIsSchema2()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_three",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, session.SchemaVersion);
        Assert.Null(session.ProviderTemplate);
    }

    [Fact]
    public void LegacyNoReferenceWithoutProviderIsSchema2()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("main.png");

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "asset_four",
            source,
            "prompt",
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, session.SchemaVersion);
        Assert.Null(session.ProviderTemplate);
    }

    [Fact]
    public void DeletedProviderFileDoesNotBreakActiveSession()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_del",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        File.Delete(workspace.ChatGptProviderTemplatePath);

        var exact = validation.ValidateExactReferenceOutput(session, templateService);
        Assert.True(exact.IsValid);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        processor.PrepareMainCommit(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "prompt",
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        var committed = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "prompt",
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("main.png", committed);
    }

    [Fact]
    public void ModifiedProviderFileDoesNotChangeActiveSession()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_mod",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        // Modify the on-disk template after the session snapshot exists.
        workspace.WriteValidProviderTemplate();
        File.AppendAllText(
            workspace.ChatGptProviderTemplatePath,
            Environment.NewLine + "TAMPERED");

        var exact = validation.ValidateExactReferenceOutput(session, templateService);
        Assert.True(exact.IsValid);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        processor.PrepareMainCommit(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "prompt",
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        var committed = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "prompt",
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("main.png", committed);
    }

    [Fact]
    public void ReferenceReplacementPreservesProviderAndRequestKey()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var requestKey =
            AssetRequestManifestService.ComputeRequestKey(
                "asset_ui.webp",
                "1920x1080",
                "exact prompt");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_rr",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot(),
            requestKey);

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("reference2.png");

        var transaction = processor.CreateReferenceReplacementTransaction(
            session,
            settings.AcceptedExtensions,
            replacement,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, transaction.OldSession.SchemaVersion);
        Assert.Equal(3, transaction.NewSession.SchemaVersion);
        Assert.Equal("ChatGPT", transaction.OldSession.ProviderTemplate!.DisplayName);
        Assert.Equal("ChatGPT", transaction.NewSession.ProviderTemplate!.DisplayName);
        Assert.Equal(requestKey, transaction.OldSession.SourceRequestKey);
        Assert.Equal(requestKey, transaction.NewSession.SourceRequestKey);
        Assert.Equal(
            session.ProviderTemplate!.ContentSha256,
            transaction.NewSession.ProviderTemplate!.ContentSha256);
    }

    [Fact]
    public void ReplacementProvenanceUsesSessionSnapshot()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_rr2",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("reference2.png");

        var transaction = processor.CreateReferenceReplacementTransaction(
            session,
            settings.AcceptedExtensions,
            replacement,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        var expected =
            workspace.CreateTemplateService().RenderReferenceForSession(
                transaction.NewSession,
                transaction.NewSession.ReferenceFilename,
                transaction.NewSession.ReferenceProcessedAt);

        var expectedHash =
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    new System.Text.UTF8Encoding(false).GetBytes(expected)))
                .ToLowerInvariant();

        Assert.Equal(
            expectedHash,
            transaction.NewSession.ReferenceProvenanceHash);
    }

    [Fact]
    public void LegacySchema2SessionValidationStillWorks()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = workspace.CreateAssetProcessor().CreateReferenceSession(
            settings,
            "asset_legacy",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        workspace.CreateAssetProcessor().ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var exact = validation.ValidateExactReferenceOutput(session, templateService);
        Assert.True(exact.IsValid);
    }

    [Fact]
    public void ArbitraryTemplatePassesExactValidation()
    {
        using var workspace = new TestWorkspace();

        var content =
            """
            ## Completely Custom Heading

            foo <<<PROJECT>>>

            date=<<<DATE>>>

            PROVIDER [[[ <<<PROVIDER>>> ]]]

            FILE <<<FILENAME>>>

            asset <<<ASSET_NAME>>>

            ROLE <<<ROLE>>>

            MODE <<<WORKFLOW>>>

            REF <<<REFERENCE_FILENAME>>>

            PROMPT
            <<<PROMPT>>>
            """;

        var snapshot =
            new ProviderTemplateSnapshot
            {
                FileName = "Custom.md",
                DisplayName = "Custom",
                Content = content,
                ContentSha256 =
                    ProviderTemplateRules.ComputeContentSha256(
                        content)
            };

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_custom",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            snapshot);

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var exact = validation.ValidateExactReferenceOutput(session, templateService);
        Assert.True(exact.IsValid);
    }

    [Fact]
    public void SessionAwareRenderingFallbackForSchema2()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_fb",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        var rendered =
            workspace.CreateTemplateService().RenderReferenceForSession(
                session,
                session.ReferenceFilename,
                session.ReferenceProcessedAt);

        Assert.Contains("Asset ID:", rendered);
        Assert.Contains(session.ReferenceFilename, rendered);
    }

    [Fact]
    public void ProviderReferencePromptIsNotRecorded()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");

        var session = processor.CreateReferenceSession(
            settings,
            "asset_np",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var provenance =
            File.ReadAllText(
                session.ReferenceProvenancePath);

        Assert.Contains(
            AppConstants.NotRecordedValue,
            provenance);
    }

    [Fact]
    public void ProviderFinalProvenanceContainsExactPrompt()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("reference.png");
        const string prompt =
            "Generate a UI screen with settings panels.";

        var session = processor.CreateReferenceSession(
            settings,
            "asset_fp",
            source,
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            CreateSnapshot());

        processor.ProcessReference(
            session,
            settings,
            source,
            session.ReferenceProcessedAt);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });

        processor.PrepareMainCommit(
            session,
            settings.AcceptedExtensions,
            mainSource,
            prompt,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            prompt,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        var provenance =
            File.ReadAllText(
                Path.Combine(
                    session.AssetFolder,
                    AppConstants.FinalProvenanceFileName));

        Assert.Contains(prompt, provenance);
    }
}