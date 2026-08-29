#nullable enable
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Paranoid verification of session validation and cancellation branch logic.
/// These are the safety-critical guards for transaction/recovery behavior.
/// </summary>
public class UpgradeV13ParanoidSessionTests
{
    private static AssetSession CreateBaseSession(TestWorkspace workspace)
    {
        return new AssetSession
        {
            SchemaVersion = 2,
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ProjectName = "Project",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_x",
            AssetFolder = Path.Combine(workspace.Assets, "asset_x"),
            ReferenceProcessedAt = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            ReferenceHash = new string('a', 64),
            ReferenceProvenanceHash = new string('b', 64),
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_x", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_x", "reference", AppConstants.ReferenceProvenanceFileName),
            ReferenceFilename = "ref.png"
        };
    }

    // ---------- NoReference session validation branches ----------

    [Fact]
    public void NoReference_CancelPhaseAndReferenceFieldsRejected()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);
        session.WorkflowMode = AssetWorkflowMode.NoReference;
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('1', 32);
        session.ReferenceSourcePath = "C:\\ref.png";
        session.ReferenceDestinationPath = "C:\\dest.png";
        session.ReferenceFilename = "ref.png";
        session.ReferenceProvenancePath = "C:\\prov.md";
        session.ReferenceHash = new string('a', 64);
        session.ReferenceProcessedAt = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        session.IsMainCommitting = false;

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CancelPhase", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("CancellationId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceSourcePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceDestinationPath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceFilename", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProvenancePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceHash", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProcessedAt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("IsMainCommitting", StringComparison.Ordinal));
    }

    [Fact]
    public void NoReference_MissingAssetFolderRejectedUnlessToolCreated()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);
        session.WorkflowMode = AssetWorkflowMode.NoReference;
        session.IsMainCommitting = true;
        session.WasAssetFolderCreatedByTool = false;

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("does not exist", StringComparison.Ordinal));

        // When the tool created it, a missing folder is acceptable.
        session.WasAssetFolderCreatedByTool = true;
        session.CancelPhase = CancelPhase.None;
        session.CancellationId = null;
        session.ReferenceSourcePath = string.Empty;
        session.ReferenceDestinationPath = string.Empty;
        session.ReferenceFilename = string.Empty;
        session.ReferenceProvenancePath = string.Empty;
        session.ReferenceHash = string.Empty;
        session.ReferenceProvenanceHash = null;
        session.ReferenceProcessedAt = default;
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        session.MainHash = new string('f', 64);
        session.MainTransactionId = new string('9', 32);

        var result2 =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.True(result2.IsValid);
    }

    // ---------- Reference session validation branches ----------

    [Fact]
    public void Reference_MissingProcessedAtAndBadFilenameRejected()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);
        session.ReferenceProcessedAt = default;
        session.ReferenceFilename = @"folder\ref.png";

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProcessedAt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("only a filename", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_InvalidHashRejected()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);
        session.ReferenceHash = "not-a-hash";

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ReferenceHash", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_CancelNoneWithMissingFilesRejected()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reference image does not exist", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("reference provenance does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_UnknownCancelPhaseRejected()
    {
        using var workspace = new TestWorkspace();

        var session = CreateBaseSession(workspace);
        session.CancelPhase = (CancelPhase)99;
        session.CancellationId = new string('1', 32);

        var result =
            workspace.CreateValidationService().ValidateSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reference_PreparedCancelInconsistentStatesRejected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('1', 32);

        // Neither original nor temp provenance exists -> inconsistent.
        var result = validation.ValidateSession(session);
        Assert.False(result.IsValid);

        // FilesRenamed phase with originals still present -> rejected.
        session.CancelPhase = CancelPhase.FilesRenamed;
        session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_x", "reference", "ref.png");
        Directory.CreateDirectory(Path.GetDirectoryName(session.ReferenceDestinationPath)!);
        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 1, 2, 3 });

        var result2 = validation.ValidateSession(session);
        Assert.False(result2.IsValid);
    }

    [Fact]
    public void Reference_MainCommitMetadataValidation()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.IsMainCommitting = true;

        // Missing Main metadata pieces.
        var result = validation.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MainFilename", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("MainPrompt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("MainProcessedAt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("MainHash", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("MainTransactionId", StringComparison.Ordinal));

        // MainTransactionId set while not committing -> rejected.
        var idle = CreateBaseSession(workspace);
        idle.IsMainCommitting = false;
        idle.MainTransactionId = new string('2', 32);
        var result2 = validation.ValidateSession(idle);
        Assert.False(result2.IsValid);
        Assert.Contains(result2.Errors, e => e.Contains("MainTransactionId", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_ProvenanceContentHashMismatchDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var session = CreateBaseSession(workspace);
        session.ProviderTemplate = null;

        // Write a provenance file that does not match the expected content.
        var folder = Path.Combine(workspace.Assets, "asset_x", "reference");
        Directory.CreateDirectory(folder);
        session.ReferenceDestinationPath = Path.Combine(folder, "ref.png");
        session.ReferenceProvenancePath = Path.Combine(folder, AppConstants.ReferenceProvenanceFileName);
        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 1, 2, 3 });
        session.ReferenceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();

        File.WriteAllText(session.ReferenceProvenancePath, "totally wrong content");

        var legacy = validation.ValidateReferenceProvenanceContent(session, session.ReferenceProvenancePath);
        Assert.False(legacy.IsValid);

        // Provider path: hash authority missing.
        var providerSession = CreateBaseSession(workspace);
        providerSession.ProviderTemplate = new ProviderTemplateSnapshot
        {
            FileName = "X.md",
            DisplayName = "X",
            ContentSha256 = new string('c', 64),
            Content = "x"
        };
        providerSession.ReferenceProvenanceHash = null;

        var providerMissing = validation.ValidateReferenceProvenanceContent(providerSession, session.ReferenceProvenancePath);
        Assert.False(providerMissing.IsValid);

        // Provider path: stored hash differs from actual file hash.
        providerSession.ReferenceProvenanceHash = new string('d', 64);
        var providerMismatch = validation.ValidateReferenceProvenanceContent(providerSession, session.ReferenceProvenancePath);
        Assert.False(providerMismatch.IsValid);
    }

    [Fact]
    public void Final_ProvenanceContentBranches()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.WorkflowMode = AssetWorkflowMode.NoReference;

        var provPath = Path.Combine(workspace.Assets, "asset_x", AppConstants.FinalProvenanceFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(provPath)!);
        File.WriteAllText(provPath, "wrong");

        // Legacy: missing expected strings.
        var legacy = validation.ValidateFinalProvenanceContent(
            session,
            provPath,
            "main.png",
            "2026-08-26",
            "prompt");

        Assert.False(legacy.IsValid);

        // Provider: hash authority missing.
        session.ProviderTemplate = new ProviderTemplateSnapshot
        {
            FileName = "X.md",
            DisplayName = "X",
            ContentSha256 = new string('c', 64),
            Content = "x"
        };
        session.MainProvenanceHash = null;

        var providerMissing = validation.ValidateFinalProvenanceContent(
            session,
            provPath,
            "main.png",
            "2026-08-26",
            "prompt");

        Assert.False(providerMissing.IsValid);

        // Provider: hash mismatch.
        session.MainProvenanceHash = new string('e', 64);
        var providerMismatch = validation.ValidateFinalProvenanceContent(
            session,
            provPath,
            "main.png",
            "2026-08-26",
            "prompt");

        Assert.False(providerMismatch.IsValid);

        // Missing file.
        var missing = validation.ValidateFinalProvenanceContent(
            session,
            Path.Combine(workspace.Assets, "nope", "x.md"),
            "main.png",
            "2026-08-26",
            "prompt");

        Assert.False(missing.IsValid);
    }

    [Fact]
    public void CompleteAsset_VerifiesAllArtifacts()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var session = CreateBaseSession(workspace);
        session.WorkflowMode = AssetWorkflowMode.NoReference;
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        session.MainHash = new string('f', 64);
        session.MainTransactionId = new string('9', 32);
        session.MainProvenanceHash = new string('0', 64);

        // NoReference with missing main image and missing ingame.
        var result = validation.ValidateCompleteAsset(
            session,
            Path.Combine(session.AssetFolder, "main.png"),
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName),
            "main.png",
            "2026-08-27",
            "prompt",
            templateService,
            session.MainHash);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Main image does not exist", StringComparison.Ordinal));

        // Reference-assisted with broken reference provenance.
        var refSession = CreateBaseSession(workspace);
        var folder = Path.Combine(workspace.Assets, "asset_x", "reference");
        Directory.CreateDirectory(folder);
        refSession.ReferenceDestinationPath = Path.Combine(folder, "ref.png");
        refSession.ReferenceProvenancePath = Path.Combine(folder, AppConstants.ReferenceProvenanceFileName);
        File.WriteAllBytes(refSession.ReferenceDestinationPath, new byte[] { 1, 2, 3 });
        refSession.ReferenceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
        File.WriteAllText(refSession.ReferenceProvenancePath, "wrong content");

        var refResult = validation.ValidateCompleteAsset(
            refSession,
            Path.Combine(refSession.AssetFolder, "main.png"),
            Path.Combine(refSession.AssetFolder, AppConstants.FinalProvenanceFileName),
            "main.png",
            "2026-08-27",
            "prompt",
            templateService);

        Assert.False(refResult.IsValid);
    }

    [Fact]
    public void MainDestinationAvailability_DetectsConflicts()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var session = CreateBaseSession(workspace);

        var assetFolder = Path.Combine(workspace.Assets, "asset_x");
        Directory.CreateDirectory(assetFolder);
        session.AssetFolder = assetFolder;

        // Existing root main image.
        File.WriteAllBytes(Path.Combine(assetFolder, "main.png"), new byte[] { 1 });
        var result = validation.ValidateMainDestinationAvailability(
            session,
            workspace.CreateSettings().AcceptedExtensions,
            Path.Combine(workspace.Downloads, "main.png"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already exists", StringComparison.Ordinal));

        // Existing ingame variant.
        var ingame = Path.Combine(assetFolder, AppConstants.IngameFolderName);
        Directory.CreateDirectory(ingame);
        File.WriteAllBytes(Path.Combine(ingame, "asset_x.png"), new byte[] { 1 });
        var result2 = validation.ValidateMainDestinationAvailability(
            session,
            workspace.CreateSettings().AcceptedExtensions,
            Path.Combine(workspace.Downloads, "other.png"));

        Assert.False(result2.IsValid);
        Assert.Contains(result2.Errors, e => e.Contains("ingame asset variant", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Replacement validation branches ----------

    [Fact]
    public void Replacement_NullTransactionThrowsArgumentNull()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<ArgumentNullException>(
            () =>
                workspace.CreateValidationService()
                    .ValidateReferenceReplacementTransaction(null!));

        Assert.Throws<ArgumentNullException>(
            () =>
                workspace.CreateValidationService()
                    .ValidateReferenceReplacementJournal(null!));
    }

    [Fact]
    public void Replacement_SessionMismatchesDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // Real, fully validated sessions so path checks pass first.
        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_x",
            refImage,
            DateTimeOffset.Now);

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            replacement,
            DateTimeOffset.Now);

        // Mutate the new session's project name to force a mismatch that
        // survives path validation.
        transaction.NewSession.ProjectName = "DifferentProject";

        var result = validation.ValidateReferenceReplacementTransaction(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ProjectName do not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Replacement_ProviderSnapshotMismatchDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();
        var provider = catalog.Templates.Single();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_x",
            refImage,
            DateTimeOffset.Now,
            provider.CreateSnapshot());

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            replacement,
            DateTimeOffset.Now);

        // Tamper with the new session's snapshot copy.
        transaction.NewSession.ProviderTemplate!.DisplayName = "TAMPERED";

        var result = validation.ValidateReferenceReplacementTransaction(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ProviderTemplate snapshots do not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Replacement_SourceRequestKeyMismatchDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var requestKey =
            AssetRequestManifestService.ComputeRequestKey(
                "asset_key.webp",
                "1920x1080",
                "prompt");

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_x",
            refImage,
            DateTimeOffset.Now,
            providerTemplate: null,
            sourceRequestKey: requestKey);

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            replacement,
            DateTimeOffset.Now);

        transaction.NewSession.SourceRequestKey = new string('2', 64);

        var result = validation.ValidateReferenceReplacementTransaction(transaction);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SourceRequestKey values do not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Journal_StructuralViolationsDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var oldSession = CreateBaseSession(workspace);
        var newSession = CreateBaseSession(workspace);

        var journal = new ReferenceReplacementJournal
        {
            TransactionId = new string('7', 32),
            Phase = (ReferenceReplacementPhase)99,
            OldSession = oldSession,
            NewSession = newSession,
            BackupReferencePath = oldSession.ReferenceDestinationPath + "." + new string('7', 32) + ".old",
            BackupProvenancePath = oldSession.ReferenceProvenancePath + "." + new string('7', 32) + ".old",
            TempNewReferencePath = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName, ".__new_reference_" + new string('7', 32) + ".png"),
            TempNewProvenancePath = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName, ".__new_provenance_" + new string('7', 32) + ".tmp")
        };

        var result = validation.ValidateReferenceReplacementJournal(journal);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Unknown replacement phase", StringComparison.Ordinal));

        // Missing sessions.
        var journal2 = new ReferenceReplacementJournal
        {
            TransactionId = new string('7', 32),
            Phase = ReferenceReplacementPhase.Prepared,
            OldSession = null!,
            NewSession = null!
        };

        var result2 = validation.ValidateReferenceReplacementJournal(journal2);
        Assert.False(result2.IsValid);
        Assert.Contains(result2.Errors, e => e.Contains("missing", StringComparison.OrdinalIgnoreCase));

        // Wrong workflow mode.
        var journal3 = new ReferenceReplacementJournal
        {
            TransactionId = new string('7', 32),
            Phase = ReferenceReplacementPhase.Prepared,
            OldSession = oldSession,
            NewSession = newSession
        };
        newSession.WorkflowMode = AssetWorkflowMode.NoReference;

        var result3 = validation.ValidateReferenceReplacementJournal(journal3);
        Assert.False(result3.IsValid);
        Assert.Contains(result3.Errors, e => e.Contains("ReferenceAssisted", StringComparison.Ordinal));
    }

    [Fact]
    public void Journal_BackupPathMismatchesDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.CreateReferenceSession(
            settings,
            "asset_x",
            refImage,
            DateTimeOffset.Now);

        processor.ProcessReference(oldSession, settings, refImage, oldSession.ReferenceProcessedAt);

        var replacement = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            replacement,
            DateTimeOffset.Now);

        var journal = transaction.ToJournal(ReferenceReplacementPhase.Prepared);

        journal.BackupReferencePath = "C:\\wrong-path.old";
        journal.BackupProvenancePath = "C:\\wrong-path2.old";
        journal.TempNewReferencePath = "C:\\wrong-path3.png";
        journal.TempNewProvenancePath = "C:\\wrong-path4.tmp";

        var result = validation.ValidateReferenceReplacementJournal(journal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BackupReferencePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("BackupProvenancePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("TempNewReferencePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("TempNewProvenancePath", StringComparison.Ordinal));
    }

    // ---------- Prepared session validation branches ----------

    [Fact]
    public void PreparedReference_StructuralViolationsDetected()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.ReferenceCommitPhase = ReferenceCommitPhase.Prepared;
        session.ReferenceTransactionId = new string('5', 32);
        session.ReferenceProvenanceHash = new string('b', 64);

        var invalid = CreateBaseSession(workspace);
        invalid.ReferenceCommitPhase = ReferenceCommitPhase.None;
        invalid.ReferenceTransactionId = null;
        invalid.ReferenceProvenanceHash = null;
        invalid.ReferenceHash = "bad";
        invalid.CancelPhase = CancelPhase.Prepared;
        invalid.IsMainCommitting = true;
        invalid.ProjectName = string.Empty;
        invalid.ReferenceProcessedAt = default;

        var result = validation.ValidatePreparedReferenceSession(invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Prepared", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceTransactionId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ProjectName", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProcessedAt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("CancelPhase", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Main commit", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceHash", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ReferenceProvenanceHash", StringComparison.Ordinal));
    }

    [Fact]
    public void PreparedReference_ValidSessionPasses()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.ReferenceCommitPhase = ReferenceCommitPhase.Prepared;
        session.ReferenceTransactionId = new string('5', 32);
        session.ReferenceProvenanceHash = new string('b', 64);

        var result = validation.ValidatePreparedReferenceSession(session);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void PreparedReference_ProviderMetadataValidated()
    {
        using var workspace = new TestWorkspace();

        var validation = workspace.CreateValidationService();

        var session = CreateBaseSession(workspace);
        session.ReferenceCommitPhase = ReferenceCommitPhase.Prepared;
        session.ReferenceTransactionId = new string('5', 32);
        session.ReferenceProvenanceHash = new string('b', 64);
        session.SchemaVersion = 3;

        // Schema 3 without a provider snapshot is invalid.
        var result = validation.ValidatePreparedReferenceSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing ProviderTemplate", StringComparison.Ordinal));
    }

    // ---------- SessionService cancellation branches ----------

    [Fact]
    public void Cancel_InvalidCancellationIdRejected()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_cancel_id",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = "short";

        var service = workspace.CreateSessionService();

        Assert.Throws<InvalidDataException>(
            () => service.Cancel(session));
    }

    [Fact]
    public void Cancel_AmbiguousProvenanceStateRejected()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_cancel_amb",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        // Both original and temp provenance exist -> ambiguous.
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('3', 32);

        var tempProv = session.GetCancelTempProvenancePath();
        Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
        File.Copy(session.ReferenceProvenancePath, tempProv);

        var service = workspace.CreateSessionService();

        Assert.Throws<IOException>(
            () => service.Cancel(session));
    }

    [Fact]
    public void Cancel_MissingProvenanceStateRejected()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_cancel_miss",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        // Neither original nor temp provenance exists.
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('3', 32);
        File.Delete(session.ReferenceProvenancePath);

        var service = workspace.CreateSessionService();

        Assert.Throws<IOException>(
            () => service.Cancel(session));
    }

    [Fact]
    public void Cancel_TamperedTempProvenancePreserved()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_cancel_tamper",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        // Simulate an interrupted cancellation: provenance already moved.
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('3', 32);

        var tempProv = session.GetCancelTempProvenancePath();
        var tempRef = session.GetCancelTempReferencePath();

        Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
        File.Move(session.ReferenceProvenancePath, tempProv);

        // Tamper with the temp provenance so ownership can no longer be proven.
        File.WriteAllText(tempProv, "TAMPERED");

        var service = workspace.CreateSessionService();

        var ex = Assert.ThrowsAny<Exception>(
            () => service.Cancel(session));

        // The tampered file must NOT be deleted.
        Assert.True(File.Exists(tempProv));
    }

    [Fact]
    public void Cancel_ResumeAfterProvenanceMoved()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.CreateReferenceSession(
            settings,
            "asset_cancel_resume",
            source,
            DateTimeOffset.Now);

        processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

        // Interrupted cancellation: provenance moved, reference still original.
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('3', 32);

        var tempProv = session.GetCancelTempProvenancePath();
        Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
        File.Move(session.ReferenceProvenancePath, tempProv);

        var service = workspace.CreateSessionService();

        service.Cancel(session);

        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));
        Assert.False(File.Exists(tempProv));
        Assert.False(service.Exists());
    }
}