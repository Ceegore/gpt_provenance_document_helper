#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13ProviderSessionTests
{
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

        Assert.Contains("Provider: ChatGPT", result);
        Assert.Contains("Date: 2026-08-26", result);
        Assert.Contains("File: reference-image.png", result);
        Assert.Contains("Asset: asset_ui_screen_settings", result);
        Assert.Contains("Project: Roswell", result);
        Assert.Contains($"Role: {AppConstants.ReferenceRoleLabel}", result);
        Assert.Contains($"Workflow: {AppConstants.ReferenceAssistedWorkflowLabel}", result);
        Assert.Contains("Reference: reference-image.png", result);
        Assert.Contains($"Prompt:\n{AppConstants.NotRecordedValue}", result);
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

        Assert.Contains("File: main.png", result);
        Assert.Contains("Reference: reference-image.png", result);
        Assert.Contains("Prompt:\nexact prompt", result);
        Assert.Contains($"Role: {AppConstants.FinalRoleLabel}", result);
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