#nullable enable
using System.Text;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13ProviderTemplateTests
{
    [Fact]
    public void ValidTemplateLoads()
    {
        using var workspace = new TestWorkspace();

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Empty(catalog.Errors);
        Assert.Single(catalog.Templates);
        Assert.Equal("ChatGPT", catalog.Templates[0].DisplayName);
        Assert.Equal("ChatGPT.md", catalog.Templates[0].FileName);
    }

    [Fact]
    public void UnderscoreTemplateIsIgnored()
    {
        using var workspace = new TestWorkspace();

        workspace.WriteValidProviderTemplate("_TEMPLATE.md");

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.Single(catalog.Templates);
        Assert.Equal("ChatGPT", catalog.Templates[0].DisplayName);
    }

    [Theory]
    [InlineData("<<<PROVIDER>>>")]
    [InlineData("<<<DATE>>>")]
    [InlineData("<<<FILENAME>>>")]
    [InlineData("<<<ASSET_NAME>>>")]
    [InlineData("<<<PROJECT>>>")]
    [InlineData("<<<ROLE>>>")]
    [InlineData("<<<WORKFLOW>>>")]
    [InlineData("<<<REFERENCE_FILENAME>>>")]
    [InlineData("<<<PROMPT>>>")]
    public void MissingRequiredTagRejected(string missingTag)
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var withoutTag =
            content.Replace(
                missingTag,
                "REMOVED",
                StringComparison.Ordinal);

        var result =
            ProviderTemplateRules.ValidateContent(
                "Broken.md",
                withoutTag);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    missingTag,
                    StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownTagRejected()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Model: <<<MODEL>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var result =
            ProviderTemplateRules.ValidateContent(
                "Bad.md",
                content);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    "<<<MODEL>>>",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void LowercaseTagRejected()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<date>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var result =
            ProviderTemplateRules.ValidateContent(
                "Bad.md",
                content);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    "<<<DATE>>>",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedDelimiterRejected()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            Broken: <<<DATE>>
            """;

        var result =
            ProviderTemplateRules.ValidateContent(
                "Bad.md",
                content);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error =>
                error.Contains(
                    "malformed",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateRequiredTagsAllowed()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Provider again: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var result =
            ProviderTemplateRules.ValidateContent(
                "Dup.md",
                content);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ArbitraryMarkdownHeadingsAllowed()
    {
        var content =
            """
            ## Completely Custom Heading

            foo <<<PROJECT>>>

            date=<<<DATE>>>

            PROVIDER [[[ <<<PROVIDER>>> ]]]

            FILE <<<FILENAME>>>

            asset <<<ASSET_NAME>>>

            ROLE <<<ROLE>>>

            MODE <<<WORKFLOW>>>

            REF <<<REFERENCE_FILENAME>>>

            PROMPT
            <<<PROMPT>>>
            """;

        var result =
            ProviderTemplateRules.ValidateContent(
                "Custom.md",
                content);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Utf8BomAllowed()
    {
        using var workspace = new TestWorkspace();

        var valid =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var bytes =
            new byte[]
            {
                0xEF, 0xBB, 0xBF
            }
            .Concat(
                new UTF8Encoding(false).GetBytes(valid))
            .ToArray();

        File.WriteAllBytes(
            Path.Combine(
                workspace.ProviderTemplates,
                "Gemini.md"),
            bytes);

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Equal(2, catalog.Templates.Count);
        Assert.Contains(
            catalog.Templates,
            template =>
                template.DisplayName == "Gemini");
    }

    [Fact]
    public void Utf16Rejected()
    {
        using var workspace = new TestWorkspace();

        var valid =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        File.WriteAllBytes(
            Path.Combine(
                workspace.ProviderTemplates,
                "BadUtf16.md"),
            new UTF8Encoding(true).GetPreamble()
                .Concat(
                    Encoding.Unicode.GetBytes(valid))
                .ToArray());

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Single(catalog.Templates);
        Assert.Contains(
            catalog.Errors,
            error =>
                error.Contains(
                    "BadUtf16.md",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidUtf8Rejected()
    {
        using var workspace = new TestWorkspace();

        var bytes =
            new UTF8Encoding(false).GetBytes(
                """
                Provider: <<<PROVIDER>>>
                Date: <<<DATE>>>
                File: <<<FILENAME>>>
                Asset: <<<ASSET_NAME>>>
                Project: <<<PROJECT>>>
                Role: <<<ROLE>>>
                Workflow: <<<WORKFLOW>>>
                Reference: <<<REFERENCE_FILENAME>>>
                Prompt:
                <<<PROMPT>>>
                """);

        var corrupt =
            bytes.Take(bytes.Length - 4)
                .Concat(new byte[] { 0xFF, 0xFE, 0x80, 0xC0 })
                .ToArray();

        File.WriteAllBytes(
            Path.Combine(
                workspace.ProviderTemplates,
                "Corrupt.md"),
            corrupt);

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Single(catalog.Templates);
        Assert.Contains(
            catalog.Errors,
            error =>
                error.Contains(
                    "Corrupt.md",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedTemplateRejected()
    {
        using var workspace = new TestWorkspace();

        var hugePrompt =
            new string(
                'x',
                ProviderTemplateRules.MaxTemplateBytes);

        var content =
            $"""
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            {hugePrompt}
            """;

        File.WriteAllText(
            Path.Combine(
                workspace.ProviderTemplates,
                "Huge.md"),
            content,
            new UTF8Encoding(false));

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Single(catalog.Templates);
        Assert.Contains(
            catalog.Errors,
            error =>
                error.Contains(
                    "Huge.md",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void OneBadFileDoesNotSuppressGoodFile()
    {
        using var workspace = new TestWorkspace();

        File.WriteAllText(
            Path.Combine(
                workspace.ProviderTemplates,
                "Broken.md"),
            "no tags at all",
            new UTF8Encoding(false));

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.True(catalog.HasUsableTemplates);
        Assert.Single(catalog.Templates);
        Assert.Equal("ChatGPT", catalog.Templates[0].DisplayName);
        Assert.Contains(
            catalog.Errors,
            error =>
                error.Contains(
                    "Broken.md",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void AlphabeticalDropdownOrderDeterministic()
    {
        using var workspace = new TestWorkspace();

        workspace.WriteValidProviderTemplate("Gemini.md");
        workspace.WriteValidProviderTemplate("Claude.md");
        workspace.WriteValidProviderTemplate("alpha.md");

        var catalog = workspace.CreateProviderTemplateCatalogService().Load();

        Assert.Equal(
            new[] { "alpha", "ChatGPT", "Claude", "Gemini" },
            catalog.Templates
                .Select(template => template.DisplayName)
                .ToArray());
    }

    [Fact]
    public void ProviderContentHashDeterministic()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            """;

        var first =
            ProviderTemplateRules.ComputeContentSha256(
                content);

        var second =
            ProviderTemplateRules.ComputeContentSha256(
                content);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void SnapshotValidationRejectsBadSnapshot()
    {
        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new AssetProvenanceHelper.Models.ProviderTemplateSnapshot
                {
                    FileName = "Bad.md",
                    DisplayName = "Bad",
                    Content = "no tags",
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            "no tags")
                });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void SnapshotValidationAcceptsValidSnapshot()
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var result =
            ProviderTemplateRules.ValidateSnapshot(
                new AssetProvenanceHelper.Models.ProviderTemplateSnapshot
                {
                    FileName = "Good.md",
                    DisplayName = "Good",
                    Content = content,
                    ContentSha256 =
                        ProviderTemplateRules.ComputeContentSha256(
                            content)
                });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MissingDirectoryProducesErrorNotTemplates()
    {
        var catalog =
            new ProviderTemplateCatalogService(
                    Path.Combine(
                        Path.GetTempPath(),
                        "AssetProvenanceHelperTests",
                        "nonexistent-" + Guid.NewGuid().ToString("N")))
                .Load();

        Assert.False(catalog.HasUsableTemplates);
        Assert.NotEmpty(catalog.Errors);
    }

    [Fact]
    public void TemplateDirectory_ReturnsConstructorPath()
    {
        var directory =
            Path.Combine(
                Path.GetTempPath(),
                "AssetProvenanceHelperTests",
                "provider-catalog-" + Guid.NewGuid().ToString("N"));

        var catalog = new ProviderTemplateCatalogService(directory);

        Assert.Equal(directory, catalog.TemplateDirectory);
    }

    [Fact]
    public void ProviderTemplateRenderer_WithEmptyApiTags_RendersNotRecorded()
    {
        var templateContent =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Candidate: <<<API_CANDIDATE_ID>>>
            Model: <<<API_MODEL>>>
            Prompt:
            <<<PROMPT>>>
            """;

        var snapshot = new AssetProvenanceHelper.Models.ProviderTemplateSnapshot
        {
            FileName = "OpenAI API.md",
            DisplayName = "OpenAI API",
            Content = templateContent,
            ContentSha256 = ProviderTemplateRules.ComputeContentSha256(templateContent)
        };

        var context = new AssetProvenanceHelper.Models.ProviderRenderContext
        {
            Provider = "OpenAI API",
            Date = "2026-09-03",
            Filename = "test.png",
            AssetName = "test",
            Project = "Demo",
            Role = "Final",
            Workflow = "Direct",
            ReferenceFilename = "none",
            Prompt = "A cat"
        };

        var rendered = ProviderTemplateRenderer.Render(snapshot, context);

        Assert.Contains("Candidate: not recorded", rendered);
        Assert.Contains("Model: not recorded", rendered);
    }
}