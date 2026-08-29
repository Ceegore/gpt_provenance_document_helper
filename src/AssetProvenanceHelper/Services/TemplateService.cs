using System.Text;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class TemplateService
{
    private static readonly string[] ReferenceTokens =
    {
        "{{REFERENCE_FILENAME}}",
        "{{PROJECT}}",
        "{{GENERATION_DATE}}"
    };

    private static readonly string[] FinalTokens =
    {
        "{{FINAL_FILENAME}}",
        "{{REFERENCE_FILENAME}}",
        "{{PROJECT}}",
        "{{GENERATION_DATE}}",
        "{{PROMPT}}"
    };

    private static readonly string[] FinalNoReferenceTokens =
    {
        "{{FINAL_FILENAME}}",
        "{{PROJECT}}",
        "{{GENERATION_DATE}}",
        "{{PROMPT}}"
    };

    private static readonly Regex TokenRegex =
        new(
            @"\{\{[^{}\r\n]+\}\}",
            RegexOptions.Compiled);

    private readonly string _referenceTemplatePath;
    private readonly string _finalTemplatePath;
    private readonly string? _finalNoReferenceTemplatePath;

    public TemplateService(
        string referenceTemplatePath,
        string finalTemplatePath,
        string? finalNoReferenceTemplatePath = null)
    {
        _referenceTemplatePath =
            referenceTemplatePath;

        _finalTemplatePath =
            finalTemplatePath;

        _finalNoReferenceTemplatePath =
            finalNoReferenceTemplatePath;
    }

    public ValidationResult ValidateTemplates()
    {
        var errors =
            new List<string>();

        ValidateTemplate(
            _referenceTemplatePath,
            ReferenceTokens,
            errors);

        ValidateTemplate(
            _finalTemplatePath,
            FinalTokens,
            errors);

        if (!string.IsNullOrWhiteSpace(_finalNoReferenceTemplatePath))
        {
            ValidateTemplate(
                _finalNoReferenceTemplatePath,
                FinalNoReferenceTokens,
                errors);
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public string RenderReference(
        string referenceFilename,
        string project,
        string generationDate)
    {
        var template =
            LoadValidatedTemplate(
                _referenceTemplatePath,
                ReferenceTokens);

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["{{REFERENCE_FILENAME}}"] =
                    referenceFilename,

                ["{{PROJECT}}"] =
                    project,

                ["{{GENERATION_DATE}}"] =
                    generationDate
            };

        return RenderSinglePass(
            template,
            values);
    }

    public string RenderFinal(
        string finalFilename,
        string referenceFilename,
        string project,
        string generationDate,
        string prompt)
    {
        var template =
            LoadValidatedTemplate(
                _finalTemplatePath,
                FinalTokens);

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["{{FINAL_FILENAME}}"] =
                    finalFilename,

                ["{{REFERENCE_FILENAME}}"] =
                    referenceFilename,

                ["{{PROJECT}}"] =
                    project,

                ["{{GENERATION_DATE}}"] =
                    generationDate,

                ["{{PROMPT}}"] =
                    prompt
            };

        return RenderSinglePass(
            template,
            values);
    }

    public string RenderFinalNoReference(
        string finalFilename,
        string project,
        string generationDate,
        string prompt)
    {
        if (string.IsNullOrWhiteSpace(_finalNoReferenceTemplatePath))
        {
            throw new InvalidOperationException(
                "No-reference template path is not configured.");
        }

        var template =
            LoadValidatedTemplate(
                _finalNoReferenceTemplatePath,
                FinalNoReferenceTokens);

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["{{FINAL_FILENAME}}"] =
                    finalFilename,

                ["{{PROJECT}}"] =
                    project,

                ["{{GENERATION_DATE}}"] =
                    generationDate,

                ["{{PROMPT}}"] =
                    prompt
            };

        return RenderSinglePass(
            template,
            values);
    }

    public string RenderReferenceForSession(
        AssetSession session,
        string referenceFilename,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(session);

        var generationDate =
            processedAt.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);

        if (session.ProviderTemplate is null)
        {
            return RenderReference(
                referenceFilename,
                session.ProjectName,
                generationDate);
        }

        return ProviderTemplateRenderer.Render(
            session.ProviderTemplate,
            new ProviderRenderContext
            {
                Provider =
                    session.ProviderTemplate.DisplayName,

                Date =
                    generationDate,

                Filename =
                    referenceFilename,

                AssetName =
                    session.AssetFolderName,

                Project =
                    session.ProjectName,

                Role =
                    AppConstants.ReferenceRoleLabel,

                Workflow =
                    AppConstants.ReferenceAssistedWorkflowLabel,

                ReferenceFilename =
                    referenceFilename,

                Prompt =
                    AppConstants.NotRecordedValue
            });
    }

    public string RenderFinalForSession(
        AssetSession session,
        string mainFilename,
        string prompt,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(session);

        var generationDate =
            processedAt.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);

        if (session.ProviderTemplate is null)
        {
            return session.WorkflowMode switch
            {
                AssetWorkflowMode.ReferenceAssisted =>
                    RenderFinal(
                        mainFilename,
                        session.ReferenceFilename,
                        session.ProjectName,
                        generationDate,
                        prompt),

                AssetWorkflowMode.NoReference =>
                    RenderFinalNoReference(
                        mainFilename,
                        session.ProjectName,
                        generationDate,
                        prompt),

                _ =>
                    throw new InvalidDataException(
                        $"Unsupported workflow mode: {session.WorkflowMode}")
            };
        }

        var workflow =
            session.WorkflowMode switch
            {
                AssetWorkflowMode.ReferenceAssisted =>
                    AppConstants.ReferenceAssistedWorkflowLabel,

                AssetWorkflowMode.NoReference =>
                    AppConstants.NoReferenceWorkflowLabel,

                _ =>
                    throw new InvalidDataException(
                        $"Unsupported workflow mode: {session.WorkflowMode}")
            };

        var referenceFilename =
            session.WorkflowMode
            == AssetWorkflowMode.ReferenceAssisted
                ? session.ReferenceFilename
                : AppConstants.NotRecordedValue;

        return ProviderTemplateRenderer.Render(
            session.ProviderTemplate,
            new ProviderRenderContext
            {
                Provider =
                    session.ProviderTemplate.DisplayName,

                Date =
                    generationDate,

                Filename =
                    mainFilename,

                AssetName =
                    session.AssetFolderName,

                Project =
                    session.ProjectName,

                Role =
                    AppConstants.FinalRoleLabel,

                Workflow =
                    workflow,

                ReferenceFilename =
                    referenceFilename,

                Prompt =
                    prompt
            });
    }

    private static string RenderSinglePass(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        return TokenRegex.Replace(
            template,
            match =>
            {
                if (!values.TryGetValue(
                        match.Value,
                        out var value))
                {
                    throw new InvalidDataException(
                        $"Unexpected template token {match.Value}.");
                }

                return value;
            });
    }

    private string LoadValidatedTemplate(
        string path,
        IReadOnlyCollection<string> requiredTokens)
    {
        var errors =
            new List<string>();

        ValidateTemplate(
            path,
            requiredTokens,
            errors);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    errors));
        }

        return File.ReadAllText(
            path,
            Encoding.UTF8);
    }

    private static void ValidateTemplate(
        string path,
        IReadOnlyCollection<string> allowedAndRequiredTokens,
        ICollection<string> errors)
    {
        if (!File.Exists(path))
        {
            errors.Add(
                $"Template does not exist: {path}");

            return;
        }

        string text;

        try
        {
            text =
                File.ReadAllText(
                    path,
                    Encoding.UTF8);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not read template '{path}': {ex.Message}");

            return;
        }

        foreach (var required in allowedAndRequiredTokens)
        {
            if (!text.Contains(
                    required,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"Template '{path}' does not contain required token {required}.");
            }
        }

        var allowed =
            new HashSet<string>(
                allowedAndRequiredTokens,
                StringComparer.Ordinal);

        foreach (Match match in TokenRegex.Matches(text))
        {
            if (!allowed.Contains(match.Value))
            {
                errors.Add(
                    $"Template '{path}' contains unknown token {match.Value}.");
            }
        }
    }
}
