#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Valid-state validation paths: a fully valid replacement transaction and
/// journal must pass every branch of the structural validators.
/// </summary>
public class UpgradeV13ParanoidValidPathTests
{
    [Fact]
    public void ValidMaterializedReplacementTransactionPassesValidation()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_valid_tx",
            refImage,
            DateTimeOffset.Now);

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var newSource = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            newSource,
            DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(transaction, settings.AcceptedExtensions);

        var result = validation.ValidateReferenceReplacementTransaction(transaction);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidJournalPassesValidation()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_valid_journal",
            refImage,
            DateTimeOffset.Now);

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var newSource = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            newSource,
            DateTimeOffset.Now);

        var journal = transaction.ToJournal(ReferenceReplacementPhase.Prepared);

        var result = validation.ValidateReferenceReplacementJournal(journal);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidPreparedSessionWithProviderPasses()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();
        var provider = catalog.Templates.Single();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_valid_prepared",
            refImage,
            DateTimeOffset.Now,
            provider.CreateSnapshot());

        var result = validation.ValidatePreparedReferenceSession(session);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidSessionCommonWithProviderPasses()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();
        var provider = catalog.Templates.Single();

        var requestKey =
            AssetRequestManifestService.ComputeRequestKey(
                "asset_vp.webp",
                "1920x1080",
                "prompt");

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_vp",
            refImage,
            DateTimeOffset.Now,
            provider.CreateSnapshot(),
            requestKey);

        processor.ProcessReference(session, settings, refImage, session.ReferenceProcessedAt);

        // Full session validation must pass for a schema-3 provider session.
        var result = validation.ValidateSession(session);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidExactOutputWithProviderPasses()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();
        var provider = catalog.Templates.Single();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_exact_provider",
            refImage,
            DateTimeOffset.Now,
            provider.CreateSnapshot());

        processor.ProcessReference(session, settings, refImage, session.ReferenceProcessedAt);

        var result = validation.ValidateExactReferenceOutput(session, templateService);

        Assert.True(result.IsValid);
    }
}
