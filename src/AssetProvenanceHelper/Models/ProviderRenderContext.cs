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
}