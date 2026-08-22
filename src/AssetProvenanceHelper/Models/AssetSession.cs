using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Models;

public enum AssetWorkflowMode
{
    ReferenceAssisted = 0,
    NoReference = 1
}

public enum ReferenceCommitPhase
{
    None = 0,
    Prepared = 1
}

public enum CancelPhase
{
    None = 0,
    Prepared = 1,
    FilesRenamed = 2
}

public sealed class AssetSession
{
    /// <summary>Schema marker for conservative session migrations.</summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>When this helper recorded the reference, not when it was generated.</summary>
    public DateTimeOffset? ReferenceRecordedAt { get; set; }

    /// <summary>User-declared generation timestamp; null means not recorded.</summary>
    public DateTimeOffset? ReferenceGenerationAt { get; set; }

    /// <summary>When this helper recorded the final asset, not when it was generated.</summary>
    public DateTimeOffset? MainRecordedAt { get; set; }

    /// <summary>User-declared generation timestamp; null means not recorded.</summary>
    public DateTimeOffset? MainGenerationAt { get; set; }

    public AssetWorkflowMode WorkflowMode { get; set; } = AssetWorkflowMode.ReferenceAssisted;

    public ReferenceCommitPhase ReferenceCommitPhase { get; set; } = ReferenceCommitPhase.None;

    public string? ReferenceTransactionId { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    public string AssetRootFolder { get; set; } = string.Empty;

    public string AssetFolderName { get; set; } = string.Empty;

    public string AssetFolder { get; set; } = string.Empty;

    public string ReferenceSourcePath { get; set; } = string.Empty;

    public string ReferenceDestinationPath { get; set; } = string.Empty;

    public string ReferenceFilename { get; set; } = string.Empty;

    public string ReferenceProvenancePath { get; set; } = string.Empty;

    public string ReferenceHash { get; set; } = string.Empty;

    public string? ReferenceProvenanceHash { get; set; }

    public DateTimeOffset ReferenceProcessedAt { get; set; }

    public bool WasAssetFolderCreatedByTool { get; set; }

    public bool WasReferenceFolderCreatedByTool { get; set; }

    public bool WasIngameFolderCreatedByTool { get; set; }

    /// <summary>
    /// BUG-002: Set to <see langword="true"/> immediately before the first
    /// irreversible Main-image write begins and persisted to session.json.
    /// On the next startup this flag lets the recovery logic distinguish
    /// "crash during Main commit" from "normal unfinished Reference session".
    /// </summary>
    public bool IsMainCommitting { get; set; }

    public string? MainFilename { get; set; }

    public string? MainProvenanceHash { get; set; }

    public string? MainPrompt { get; set; }

    public DateTimeOffset? MainProcessedAt { get; set; }

    /// <summary>
    /// BUG-R2-006: Expected SHA-256 hash of the committed Main image to verify
    /// byte integrity during crash recovery.
    /// </summary>
    public string? MainHash { get; set; }

    /// <summary>
    /// BUG-R4-001: Explicit persistent transaction phase for cancellation.
    /// </summary>
    public CancelPhase CancelPhase { get; set; } = CancelPhase.None;

    /// <summary>
    /// BUG-R4-001 & BUG-R4-002: 32-character hexadecimal identifier for cancellation temp files.
    /// </summary>
    public string? CancellationId { get; set; }

    /// <summary>
    /// BUG-R9-002: 32-character hexadecimal identifier for Main commit transaction temp files.
    /// </summary>
    public string? MainTransactionId { get; set; }

    public string GetReferenceTempImagePath()
    {
        if (string.IsNullOrWhiteSpace(ReferenceTransactionId) || string.IsNullOrWhiteSpace(AssetFolder) || string.IsNullOrWhiteSpace(ReferenceFilename))
        {
            return string.Empty;
        }

        return Path.Combine(
            AssetFolder,
            AppConstants.ReferenceFolderName,
            $".__reference_{ReferenceTransactionId}{Path.GetExtension(ReferenceFilename)}");
    }

    public string GetReferenceTempProvenancePath()
    {
        if (string.IsNullOrWhiteSpace(ReferenceTransactionId) || string.IsNullOrWhiteSpace(AssetFolder))
        {
            return string.Empty;
        }

        return Path.Combine(
            AssetFolder,
            AppConstants.ReferenceFolderName,
            $".__reference_provenance_{ReferenceTransactionId}.tmp");
    }

    public string GetCancelTempReferencePath()
    {
        if (string.IsNullOrWhiteSpace(CancellationId) || string.IsNullOrWhiteSpace(ReferenceDestinationPath))
        {
            return string.Empty;
        }

        return ReferenceDestinationPath + "." + CancellationId + ".canceling";
    }

    public string GetCancelTempProvenancePath()
    {
        if (string.IsNullOrWhiteSpace(CancellationId) || string.IsNullOrWhiteSpace(ReferenceProvenancePath))
        {
            return string.Empty;
        }

        return ReferenceProvenancePath + "." + CancellationId + ".canceling";
    }

    public string GetMainTempImagePath()
    {
        if (string.IsNullOrWhiteSpace(MainTransactionId) || string.IsNullOrWhiteSpace(AssetFolder) || string.IsNullOrWhiteSpace(MainFilename))
        {
            return string.Empty;
        }

        return Path.Combine(AssetFolder, $".main-{MainTransactionId}{Path.GetExtension(MainFilename)}");
    }

    public string GetMainTempProvenancePath()
    {
        if (string.IsNullOrWhiteSpace(MainTransactionId) || string.IsNullOrWhiteSpace(AssetFolder))
        {
            return string.Empty;
        }

        return Path.Combine(AssetFolder, $".main-{MainTransactionId}.md.tmp");
    }

    public string GetIngameFolderPath()
    {
        if (string.IsNullOrWhiteSpace(AssetFolder))
        {
            return string.Empty;
        }

        return Path.Combine(
            AssetFolder,
            AppConstants.IngameFolderName);
    }

    public string GetIngameFilename()
    {
        if (string.IsNullOrWhiteSpace(AssetFolderName)
            || string.IsNullOrWhiteSpace(MainFilename))
        {
            return string.Empty;
        }

        return AssetNaming.BuildIngameFilename(
            AssetFolderName,
            MainFilename);
    }

    public string GetIngameImagePath()
    {
        var folder = GetIngameFolderPath();
        var filename = GetIngameFilename();

        if (string.IsNullOrWhiteSpace(folder)
            || string.IsNullOrWhiteSpace(filename))
        {
            return string.Empty;
        }

        return Path.Combine(folder, filename);
    }

    public string GetMainTempIngamePath()
    {
        if (string.IsNullOrWhiteSpace(MainTransactionId)
            || string.IsNullOrWhiteSpace(MainFilename)
            || string.IsNullOrWhiteSpace(AssetFolder))
        {
            return string.Empty;
        }

        return Path.Combine(
            GetIngameFolderPath(),
            $".main-ingame-{MainTransactionId}{Path.GetExtension(MainFilename)}");
    }

    public void ResetMainCommitMetadata()
    {
        IsMainCommitting = false;
        MainFilename = null;
        MainProvenanceHash = null;
        MainPrompt = null;
        MainProcessedAt = null;
        MainHash = null;
        MainTransactionId = null;
    }
}
