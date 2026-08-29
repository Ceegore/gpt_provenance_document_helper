using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public static class ProviderTemplateRenderer
{
    private static readonly Regex TagRegex =
        new(
            @"<<<[^<>\r\n]+>>>",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    public static string Render(
        ProviderTemplateSnapshot snapshot,
        ProviderRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var snapshotValidation =
            ProviderTemplateRules.ValidateSnapshot(
                snapshot);

        if (!snapshotValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    snapshotValidation.Errors));
        }

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["<<<PROVIDER>>>"] =
                    context.Provider,

                ["<<<DATE>>>"] =
                    context.Date,

                ["<<<FILENAME>>>"] =
                    context.Filename,

                ["<<<ASSET_NAME>>>"] =
                    context.AssetName,

                ["<<<PROJECT>>>"] =
                    context.Project,

                ["<<<ROLE>>>"] =
                    context.Role,

                ["<<<WORKFLOW>>>"] =
                    context.Workflow,

                ["<<<REFERENCE_FILENAME>>>"] =
                    context.ReferenceFilename,

                ["<<<PROMPT>>>"] =
                    context.Prompt
            };

        return TagRegex.Replace(
            snapshot.Content,
            match =>
            {
                if (!values.TryGetValue(
                        match.Value,
                        out var value))
                {
                    throw new InvalidDataException(
                        $"Unsupported provider template tag {match.Value}.");
                }

                return value;
            });
    }
}