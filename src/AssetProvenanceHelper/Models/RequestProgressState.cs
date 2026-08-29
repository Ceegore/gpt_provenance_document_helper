namespace AssetProvenanceHelper.Models;

public sealed class RequestProgressState
{
    public int SchemaVersion { get; set; } =
        1;

    public string ManifestFingerprint { get; set; } =
        string.Empty;

    public List<string> CompletedRequestKeys { get; set; } =
        new();
}