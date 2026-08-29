namespace AssetProvenanceHelper.Models;

public sealed class ProviderCatalogResult
{
    public List<ProviderTemplateDefinition> Templates { get; }
        = new();

    public List<string> Errors { get; }
        = new();

    public bool HasUsableTemplates =>
        Templates.Count > 0;
}