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
                    context.Prompt,

                ["<<<API_CANDIDATE_ID>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiCandidateId) ? AppConstants.NotRecordedValue : context.ApiCandidateId,

                ["<<<API_PROVIDER>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiProvider) ? AppConstants.NotRecordedValue : context.ApiProvider,

                ["<<<API_MODEL>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiModel) ? AppConstants.NotRecordedValue : context.ApiModel,

                ["<<<API_MODE>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiMode) ? AppConstants.NotRecordedValue : context.ApiMode,

                ["<<<API_CUSTOM_ID>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiCustomId) ? AppConstants.NotRecordedValue : context.ApiCustomId,

                ["<<<API_TARGET_RESOLUTION>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiTargetResolution) ? AppConstants.NotRecordedValue : context.ApiTargetResolution,

                ["<<<API_PROVIDER_RESOLUTION>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiProviderResolution) ? AppConstants.NotRecordedValue : context.ApiProviderResolution,

                ["<<<API_RAW_SHA256>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiRawSha256) ? AppConstants.NotRecordedValue : context.ApiRawSha256,

                ["<<<API_NORMALIZED_SHA256>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiNormalizedSha256) ? AppConstants.NotRecordedValue : context.ApiNormalizedSha256,

                ["<<<API_PROVIDER_REQUEST_ID>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiProviderRequestId) ? AppConstants.NotRecordedValue : context.ApiProviderRequestId,

                ["<<<API_BATCH_ID>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiBatchId) ? AppConstants.NotRecordedValue : context.ApiBatchId,

                ["<<<API_CREATED_AT_UTC>>>"] =
                    string.IsNullOrWhiteSpace(context.ApiCreatedAtUtc) ? AppConstants.NotRecordedValue : context.ApiCreatedAtUtc
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