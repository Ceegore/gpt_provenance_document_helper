using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class TemplateServiceBranchTests
{
    [Fact]
    public void ValidateTemplates_DetectsMissingTemplates()
    {
        using var workspace = new TestWorkspace();
        var emptyTemplateDir = Path.Combine(workspace.Root, "empty_templates");
        Directory.CreateDirectory(emptyTemplateDir);

        var refPath = Path.Combine(emptyTemplateDir, "reference.md");
        var finalPath = Path.Combine(emptyTemplateDir, "final.md");

        var service = new TemplateService(refPath, finalPath);
        var result = service.ValidateTemplates();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reference.md"));
        Assert.Contains(result.Errors, e => e.Contains("final.md"));
    }

    [Fact]
    public void RenderReference_WithValidCustomTemplate_ProducesExpectedOutput()
    {
        using var workspace = new TestWorkspace();
        var templateDir = Path.Combine(workspace.Root, "custom_templates");
        Directory.CreateDirectory(templateDir);

        var refPath = Path.Combine(templateDir, "reference.md");
        var finalPath = Path.Combine(templateDir, "final.md");

        File.WriteAllText(
            refPath,
            "# Custom Ref\nAsset ID: {{REFERENCE_FILENAME}}\nProject: {{PROJECT}}\nGeneration date: {{GENERATION_DATE}}\n");

        File.WriteAllText(
            finalPath,
            "# Custom Final\nAsset ID: {{FINAL_FILENAME}}\nReference asset: {{REFERENCE_FILENAME}}\nProject: {{PROJECT}}\nGeneration date: {{GENERATION_DATE}}\nPrompt: \"{{PROMPT}}\"\n");

        var service = new TemplateService(refPath, finalPath);
        var refOutput = service.RenderReference("ref.png", "MyProj", "2026-08-17");

        Assert.Contains("Asset ID: ref.png", refOutput);
        Assert.Contains("Project: MyProj", refOutput);
        Assert.Contains("Generation date: 2026-08-17", refOutput);

        var finalOutput = service.RenderFinal("main.png", "ref.png", "MyProj", "2026-08-17", "A beautiful forest");
        Assert.Contains("Asset ID: main.png", finalOutput);
        Assert.Contains("A beautiful forest", finalOutput);
    }
}
