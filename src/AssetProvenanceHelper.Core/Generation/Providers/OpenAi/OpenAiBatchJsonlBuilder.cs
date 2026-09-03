using System.Text;
using System.Text.Json;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public static class OpenAiBatchJsonlBuilder
{
    private const string TargetEndpoint = "/v1/images/generations";
    private const string HttpMethod = "POST";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static byte[] Build(IReadOnlyList<ImageGenerationSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0)
        {
            throw new ArgumentException("Cannot build batch JSONL with zero specifications.", nameof(specs));
        }

        var expectedModel = specs[0].Model;
        var seenCustomIds = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();

        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.CustomId))
            {
                throw new ArgumentException("Every specification must have a valid CustomId.", nameof(specs));
            }

            if (!seenCustomIds.Add(spec.CustomId))
            {
                throw new ArgumentException($"Duplicate CustomId detected: '{spec.CustomId}'.", nameof(specs));
            }

            if (!string.Equals(spec.Model, expectedModel, StringComparison.Ordinal))
            {
                throw new ArgumentException($"All batch items must use the same model. Found '{spec.Model}' and '{expectedModel}'.", nameof(specs));
            }

            if (spec.AlphaRequirement == AlphaRequirement.Required)
            {
                throw new InvalidOperationException($"Cannot include asset '{spec.AssetName}' in batch: model '{spec.Model}' does not support required alpha.");
            }

            var requestLine = new OpenAiBatchJsonlRequestLine(
                CustomId: spec.CustomId,
                Method: HttpMethod,
                Url: TargetEndpoint,
                Body: new OpenAiImageGenerationRequest(
                    Model: spec.Model,
                    Prompt: spec.Prompt,
                    Size: $"{spec.GenerationWidth}x{spec.GenerationHeight}",
                    Quality: spec.Quality,
                    N: 1,
                    OutputFormat: "png",
                    Background: "opaque"));

            var lineJson = JsonSerializer.Serialize(requestLine, JsonOptions);

            // Validate the line can be deserialized back locally
            var validated = JsonSerializer.Deserialize<OpenAiBatchJsonlRequestLine>(lineJson, JsonOptions);
            if (validated == null || validated.CustomId != spec.CustomId)
            {
                throw new InvalidOperationException($"Failed local roundtrip validation for CustomId '{spec.CustomId}'.");
            }

            sb.Append(lineJson).Append('\n');
        }

        // Return UTF-8 bytes without BOM
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }
}
