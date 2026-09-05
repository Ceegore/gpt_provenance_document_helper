namespace AssetProvenanceHelper.Models;

public sealed class ProviderRenderContext
{
    public string Provider { get; init; } =
        string.Empty;

    public string Date { get; init; } =
        string.Empty;

    public string Filename { get; init; } =
        string.Empty;

    public string AssetName { get; init; } =
        string.Empty;

    public string Project { get; init; } =
        string.Empty;

    public string Role { get; init; } =
        string.Empty;

    public string Workflow { get; init; } =
        string.Empty;

    public string ReferenceFilename { get; init; } =
        string.Empty;

    public string Prompt { get; init; } =
        string.Empty;

    public string ApiCandidateId { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiProvider { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiModel { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiMode { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiCustomId { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiTargetResolution { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiProviderResolution { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiRawSha256 { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiNormalizedSha256 { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiProviderRequestId { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiBatchId { get; init; } =
        AppConstants.NotRecordedValue;

    public string ApiCreatedAtUtc { get; init; } =
        AppConstants.NotRecordedValue;
}