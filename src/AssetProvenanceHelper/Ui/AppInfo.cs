#nullable enable
namespace AssetProvenanceHelper.Ui;

internal static class AppInfo
{
    public const string ProductName =
        "AI Asset Provenance Helper";

    public static string Version =>
        typeof(AppInfo)
            .Assembly
            .GetName()
            .Version?
            .ToString(3)
        ?? "dev";
}
