namespace AssetProvenanceHelper.Models;

public sealed class ProviderTemplateDefinition
{
    public required string FileName { get; init; }

    public required string DisplayName { get; init; }

    public required string FullPath { get; init; }

    public required string ContentSha256 { get; init; }

    public required string Content { get; init; }

    public bool IsSessionSnapshot { get; init; }

    public ProviderTemplateSnapshot CreateSnapshot()
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

    public static ProviderTemplateDefinition FromSnapshot(
        ProviderTemplateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ProviderTemplateDefinition
        {
            FileName =
                snapshot.FileName,

            DisplayName =
                snapshot.DisplayName
                + " (session snapshot)",

            FullPath =
                string.Empty,

            ContentSha256 =
                snapshot.ContentSha256,

            Content =
                snapshot.Content,

            IsSessionSnapshot =
                true
        };
    }

    public override string ToString()
    {
        return DisplayName;
    }
}