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

    public static readonly IReadOnlyList<string> DefaultImageExtensions =
        new[]
        {
            ".png",
            ".webp",
            ".jpg",
            ".jpeg"
        };
}
