namespace AssetProvenanceHelper.Models;

public sealed class ProviderTemplateSnapshot
{
    public string FileName { get; set; } =
        string.Empty;

    public string DisplayName { get; set; } =
        string.Empty;

    public string ContentSha256 { get; set; } =
        string.Empty;

    public string Content { get; set; } =
        string.Empty;

    public ProviderTemplateSnapshot Clone()
    {
        return new ProviderTemplateSnapshot
        {
            FileName =
                FileName,

            DisplayName =
                DisplayName,

            ContentSha256 =
                ContentSha256,

            Content =
                Content
        };
    }
}