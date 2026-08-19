using System.Security.Cryptography;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class AssetProcessorService
{
    [ThreadStatic]
    internal static Action<string, string>? OnFileCopiedHook;

    [ThreadStatic]
    internal static Action<string>? OnMainPromotedHook;

    [ThreadStatic]
    internal static Action<string>? OnIngameTempCopiedHook;

    [ThreadStatic]
    internal static Action<string>? OnIngamePromotedHook;

    [ThreadStatic]
    internal static Action<string, string>? OnPrepareReplacementOldBackedUpHook;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnRollbackReferenceReplacementInvoked;

    [ThreadStatic]
    internal static Action? OnPreparedReferenceAuthorityVerifiedHook;

    [ThreadStatic]
    internal static Action<string>? OnReservedTextStagingOpenedHook;

    [ThreadStatic]
    internal static Action<AssetSession>? OnBeforeMainStagingAuthorityGate;

    [ThreadStatic]
    internal static Action<AssetSession>? OnBeforeInitialReferenceStagingAuthorityGate;

    [ThreadStatic]
    internal static Action<string>? OnBeforeDeleteFileHook;

    [ThreadStatic]
    internal static Action<string>? OnBeforeDeleteDirectoryHook;

    [ThreadStatic]
    internal static Action<AssetSession>? OnBeforeRollbackMainFinalPathGate;

    [ThreadStatic]
    internal static Action<AssetSession>? OnBeforeRollbackReferenceFinalPathGate;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnBeforeRollbackReferenceReplacementFinalPathGate;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnBeforeReplacementCleanupFinalPathGate;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnBeforeReplacementFinalPathGate;

    [ThreadStatic]
    internal static Action<string, string>? OnBeforeRestoreFileHook;

    [ThreadStatic]
    internal static Action<ReferenceReplacementTransaction>? OnBeforeBackupOldReferenceFinalAuthorityGate;

    private readonly TemplateService _templateService;
    private readonly ValidationService _validationService;

    public AssetProcessorService(
        TemplateService templateService,
        ValidationService validationService)
    {
        _templateService =
            templateService;

        _validationService =
            validationService;
    }

    public string ComputeSha256(
        string filePath)
    {
        using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        var hash =
            SHA256.HashData(
                stream);

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }
}
