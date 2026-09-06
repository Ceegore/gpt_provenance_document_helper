using System.Text.Json.Serialization;

namespace AssetProvenanceHelper.Models;

public sealed class RequestProgressState
{
    public int SchemaVersion { get; set; } =
        2;

    public Dictionary<string, List<string>> CompletedByManifest { get; set; } =
        new(StringComparer.Ordinal);

    // Schema-1 compatibility fields. Schema-2 writes never populate these.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestFingerprint { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CompletedRequestKeys { get; set; }
}
