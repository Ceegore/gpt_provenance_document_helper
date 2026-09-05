namespace AssetProvenanceHelper;

public static class AppConstants
{
    public const string ReferenceFolderName = "reference";
    public const string IngameFolderName = "ingame";

    public const string ReferenceProvenanceFileName =
        "license.txt — AI Reference Asset.md";

    public const string FinalProvenanceFileName =
        "license.txt — Final AI-Generated Asset.md";

    public const string SettingsFileName = "settings.json";
    public const string SessionFileName = "session.json";
    public const string ReferenceReplacementFileName = "reference-replacement.json";

    public const string ProviderTemplateFolderName =
        "provider_templates";

    public const string DefaultProviderTemplateFileName =
        "ChatGPT.md";

    public const string RecentDocumentsFileName =
        "recent-documents.json";

    public const string RequestProgressFileName =
        "request-progress.json";

    public const string RequestQueueStateFileName =
        "request-queue-state.json";

    public const string ReferenceRoleLabel =
        "Intermediate reference image";

    public const string FinalRoleLabel =
        "Final production asset";

    public const string ReferenceAssistedWorkflowLabel =
        "reference-assisted";

    public const string NoReferenceWorkflowLabel =
        "no-reference";

    public const string NotRecordedValue =
        "not recorded";

    public const int MaxVariantCount = 10;

    public static readonly IReadOnlyList<string> DefaultImageExtensions =
        new[]
        {
            ".png",
            ".webp",
            ".jpg",
            ".jpeg"
        };
}
