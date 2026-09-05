namespace AssetProvenanceHelper.Models;

public sealed record ApiPreflightIssue(
    string RequestKey,
    string FileName,
    string Code,
    string Message);

public sealed record ApiPreflightResult(
    IReadOnlyList<AssetRequestItem> Eligible,
    IReadOnlyList<AssetRequestItem> BlockedAlpha,
    IReadOnlyList<ApiPreflightIssue> Errors,
    IReadOnlyList<ApiPreflightIssue> Warnings,
    int TotalPendingCount = 0,
    int AlreadyReadyCount = 0,
    int InFlightCount = 0,
    int UncertainCount = 0);
