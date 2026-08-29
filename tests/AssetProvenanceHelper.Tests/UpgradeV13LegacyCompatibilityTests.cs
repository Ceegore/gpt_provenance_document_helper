#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13LegacyCompatibilityTests
{
    [Fact]
    public void OldSettingsLoadWithNewDefaults()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            workspace.SettingsPath,
            """
            {
              "DownloadFolder": "C:\\Downloads",
              "AssetRootFolder": "C:\\Assets",
              "AcceptedExtensions": [ ".png", ".jpg" ]
            }
            """);

        var settings =
            workspace.CreateSettingsService().Load();

        Assert.Equal(
            AppConstants.DefaultProviderTemplateFileName,
            settings.SelectedProviderTemplateFileName);

        Assert.False(settings.DirectModeEnabled);
        Assert.Equal("C:\\Downloads", settings.DownloadFolder);
    }

    [Fact]
    public void InvalidProviderFileNameNormalizedToDefault()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            workspace.SettingsPath,
            """
            {
              "DownloadFolder": "C:\\Downloads",
              "AssetRootFolder": "C:\\Assets",
              "SelectedProviderTemplateFileName": "..\\evil\\Gemini.md"
            }
            """);

        var settings =
            workspace.CreateSettingsService().Load();

        Assert.Equal(
            AppConstants.DefaultProviderTemplateFileName,
            settings.SelectedProviderTemplateFileName);
    }

    [Fact]
    public void NewSettingsRoundTrip()
    {
        using var workspace = new TestWorkspace();

        var service =
            workspace.CreateSettingsService();

        var settings =
            workspace.CreateSettings();

        settings.SelectedProviderTemplateFileName =
            "Gemini.md";

        settings.DirectModeEnabled = true;

        service.Save(settings);

        var reloaded =
            service.Load();

        Assert.Equal("Gemini.md", reloaded.SelectedProviderTemplateFileName);
        Assert.True(reloaded.DirectModeEnabled);
    }

    [Fact]
    public void LegacySessionSerializationRoundTripPreservesSchema2()
    {
        using var workspace = new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var source =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session =
            processor.CreateReferenceSession(
                settings,
                "asset_legacy_roundtrip",
                source,
                DateTimeOffset.Now);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        var reloaded =
            sessionService.Load();

        Assert.Equal(2, reloaded!.SchemaVersion);
        Assert.Null(reloaded.ProviderTemplate);
        Assert.Null(reloaded.SourceRequestKey);
    }

    [Fact]
    public void Schema3SessionWithoutProviderIsInvalid()
    {
        using var workspace = new TestWorkspace();

        var session =
            new AssetSession
            {
                SchemaVersion = 3,
                WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
                ProjectName = "P",
                AssetRootFolder = workspace.Assets,
                AssetFolderName = "asset_x",
                AssetFolder = Path.Combine(workspace.Assets, "asset_x")
            };

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    "missing ProviderTemplate",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidSourceRequestKeyRejected()
    {
        using var workspace = new TestWorkspace();

        var session =
            new AssetSession
            {
                SchemaVersion = 2,
                WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
                ProjectName = "P",
                AssetRootFolder = workspace.Assets,
                AssetFolderName = "asset_x",
                AssetFolder = Path.Combine(workspace.Assets, "asset_x"),
                SourceRequestKey = "not-a-hash"
            };

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    "SourceRequestKey",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyTemplateServiceMethodsUnchanged()
    {
        using var workspace = new TestWorkspace();

        var templateService =
            workspace.CreateTemplateService();

        var reference =
            templateService.RenderReference(
                "ref.png",
                "Project",
                "2026-08-26");

        var final =
            templateService.RenderFinal(
                "main.png",
                "ref.png",
                "Project",
                "2026-08-26",
                "prompt");

        var finalNoRef =
            templateService.RenderFinalNoReference(
                "main.png",
                "Project",
                "2026-08-26",
                "prompt");

        Assert.Contains("Asset ID: ref.png", reference);
        Assert.Contains("Asset ID: main.png", final);
        Assert.Contains("prompt", finalNoRef);

        Assert.True(templateService.ValidateTemplates().IsValid);
    }

    [Fact]
    public void OldConstructorStyleFormStillWorks()
    {
        // The optional service parameters must keep the old constructor
        // call style source-compatible and produce a working form.
        using var workspace = new TestWorkspace();

        using var form = new MainForm(
            workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService());

        Assert.NotNull(form);
    }

    [Fact]
    public void SettingsServiceCreatesDefaultsWithNewFields()
    {
        using var workspace = new TestWorkspace();

        var defaults =
            workspace.CreateSettingsService().CreateDefaults();

        Assert.Equal(
            AppConstants.DefaultProviderTemplateFileName,
            defaults.SelectedProviderTemplateFileName);

        Assert.False(defaults.DirectModeEnabled);
    }

    [Fact]
    public void Schema3ProviderSessionSurvivesSessionSerialization()
    {
        using var workspace = new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var catalog =
            workspace.CreateProviderTemplateCatalogService()
                .Load();

        var provider =
            catalog.Templates.Single();

        var source =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var requestKey =
            AssetRequestManifestService.ComputeRequestKey(
                "asset_persist.webp",
                "1920x1080",
                "persist prompt");

        var session =
            processor.CreateReferenceSession(
                settings,
                "asset_persist",
                source,
                DateTimeOffset.Now,
                provider.CreateSnapshot(),
                requestKey);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        var reloaded =
            sessionService.Load();

        Assert.Equal(3, reloaded!.SchemaVersion);
        Assert.NotNull(reloaded.ProviderTemplate);
        Assert.Equal("ChatGPT.md", reloaded.ProviderTemplate!.FileName);
        Assert.Equal(
            session.ProviderTemplate!.ContentSha256,
            reloaded.ProviderTemplate.ContentSha256);
        Assert.Equal(
            session.ProviderTemplate.Content,
            reloaded.ProviderTemplate.Content);
        Assert.Equal(requestKey, reloaded.SourceRequestKey);
    }

    [Fact]
    public void LegacySessionWithoutNewFieldsRoundTrips()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            workspace.SessionPath,
            """
            {
              "SchemaVersion": 2,
              "WorkflowMode": 1,
              "ProjectName": "LegacyProject",
              "AssetRootFolder": "C:\\Assets",
              "AssetFolderName": "legacy_asset",
              "AssetFolder": "C:\\Assets\\legacy_asset",
              "IsMainCommitting": true,
              "MainFilename": "main.png",
              "MainPrompt": "legacy prompt",
              "MainProcessedAt": "2026-08-26T12:00:00+02:00",
              "MainHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "MainTransactionId": "0123456789abcdef0123456789abcdef"
            }
            """);

        var session =
            workspace.CreateSessionService().Load();

        Assert.Equal(2, session!.SchemaVersion);
        Assert.Null(session.ProviderTemplate);
        Assert.Null(session.SourceRequestKey);
        Assert.Equal("legacy_asset", session.AssetFolderName);
    }
}