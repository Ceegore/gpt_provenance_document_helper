namespace AssetProvenanceHelper.Models;

public enum AssetWorkflowMode
{
    ReferenceAssisted = 0,
    NoReference = 1
}

public enum CancelPhase
{
    None = 0,
    Prepared = 1,
    FilesRenamed = 2
}

public sealed class AssetSession
{
    public AssetWorkflowMode WorkflowMode { get; set; } = AssetWorkflowMode.ReferenceAssisted;

    public string ProjectName { get; set; } = string.Empty;

    public string AssetRootFolder { get; set; } = string.Empty;

    public string AssetFolderName { get; set; } = string.Empty;

    public string AssetFolder { get; set; } = string.Empty;

    public string ReferenceSourcePath { get; set; } = string.Empty;

    public string ReferenceDestinationPath { get; set; } = string.Empty;

    public string ReferenceFilename { get; set; } = string.Empty;

    public string ReferenceProvenancePath { get; set; } = string.Empty;

    public string ReferenceHash { get; set; } = string.Empty;

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

    public string? IngameFilename { get; set; }

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
        if (!string.IsNullOrWhiteSpace(IngameFilename))
        {
            return IngameFilename;
        }

        if (string.IsNullOrWhiteSpace(AssetFolderName)
            || string.IsNullOrWhiteSpace(MainFilename))
        {
            return string.Empty;
        }

        return AssetFolderName
            + Path.GetExtension(MainFilename);
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
        IngameFilename = null;
        MainPrompt = null;
        MainProcessedAt = null;
        MainHash = null;
        MainTransactionId = null;
    }
}
