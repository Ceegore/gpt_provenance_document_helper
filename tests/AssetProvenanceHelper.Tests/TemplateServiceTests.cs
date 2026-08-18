using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class TemplateServiceTests
{
    [Fact]
    public void ValidTemplates_PassValidation()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            workspace.CreateTemplateService();

        var result =
            service.ValidateTemplates();

        Assert.True(
            result.IsValid,
            string.Join(
                Environment.NewLine,
                result.Errors));
    }

    [Fact]
    public void MissingReferenceToken_FailsValidation()
    {
        using var workspace =
            new TestWorkspace();

        File.WriteAllText(
            workspace.ReferenceTemplatePath,
            """
            {{PROJECT}}
            {{GENERATION_DATE}}
            """);

        var service =
            workspace.CreateTemplateService();

        var result =
            service.ValidateTemplates();

        Assert.False(
            result.IsValid);
    }

    [Fact]
    public void UnknownToken_FailsValidation()
    {
        using var workspace =
            new TestWorkspace();

        File.AppendAllText(
            workspace.ReferenceTemplatePath,
            Environment.NewLine
            + "{{UNKNOWN_TOKEN}}");

        var service =
            workspace.CreateTemplateService();

        var result =
            service.ValidateTemplates();

        Assert.False(
            result.IsValid);
    }

    [Fact]
    public void RenderReference_ReplacesRequiredValues()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            workspace.CreateTemplateService();

        var rendered =
            service.RenderReference(
                "reference.png",
                "SpellQuake",
                "2026-08-17");

        Assert.Contains(
            "reference.png",
            rendered);

        Assert.Contains(
            "SpellQuake",
            rendered);

        Assert.Contains(
            "2026-08-17",
            rendered);

        Assert.Contains(
            "STATIC_REFERENCE_MARKER",
            rendered);
    }

    [Fact]
    public void RenderFinal_PreservesDynamicValuesWithoutRecursiveTokenReplacement()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            workspace.CreateTemplateService();

        const string project =
            "SpellQuake {{PROMPT}}";

        const string prompt =
            "erste Zeile\nzweite \"Zeile\" äöü 日本語 {{PROJECT}}";

        var rendered =
            service.RenderFinal(
                "main.png",
                "reference.png",
                project,
                "2026-08-17",
                prompt);

        Assert.Contains(
            project,
            rendered);

        Assert.Contains(
            prompt,
            rendered);

        Assert.Contains(
            "STATIC_FINAL_MARKER",
            rendered);
    }
}
