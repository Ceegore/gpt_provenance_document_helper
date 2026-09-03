using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiProvenanceRenderingTests : IDisposable
{
    private readonly string _tempDir;

    public ApiProvenanceRenderingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_prov_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static byte[] CreateTestPng(int width, int height, Color color)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public void OpenAiApiTemplate_ValidatesSuccessfully()
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "provider_templates", "OpenAI API.md");
        if (!File.Exists(templatePath))
        {
            // Fallback for direct source test runs
            templatePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AssetProvenanceHelper", "provider_templates", "OpenAI API.md"));
        }

        Assert.True(File.Exists(templatePath), $"OpenAI API.md template not found at {templatePath}");

        var content = File.ReadAllText(templatePath);
        var validation = ProviderTemplateRules.ValidateContent("OpenAI API.md", content);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void ProviderTemplateRenderer_RendersApiTagsAccurately()
    {
        var templateContent = """
        # Provenance
        Provider: <<<PROVIDER>>>
        Date: <<<DATE>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Ref: <<<REFERENCE_FILENAME>>>
        Prompt: <<<PROMPT>>>
        Candidate: <<<API_CANDIDATE_ID>>>
        Model: <<<API_MODEL>>>
        Mode: <<<API_MODE>>>
        CustomId: <<<API_CUSTOM_ID>>>
        RawSha: <<<API_RAW_SHA256>>>
        NormSha: <<<API_NORMALIZED_SHA256>>>
        ReqId: <<<API_PROVIDER_REQUEST_ID>>>
        BatchId: <<<API_BATCH_ID>>>
        """;

        var snapshot = new ProviderTemplateSnapshot
        {
            FileName = "OpenAI API.md",
            DisplayName = "OpenAI API",
            Content = templateContent,
            ContentSha256 = ProviderTemplateRules.ComputeContentSha256(templateContent)
        };

        var context = new ProviderRenderContext
        {
            Provider = "OpenAI API",
            Date = "2026-09-03",
            Filename = "hero.png",
            AssetName = "hero",
            Project = "GameAssetProject",
            Role = "Main",
            Workflow = "No Reference",
            ReferenceFilename = "-",
            Prompt = "Hero prompt",
            ApiCandidateId = "cand-12345",
            ApiModel = "gpt-image-2",
            ApiMode = "batch",
            ApiCustomId = "aph-1234567890ab-1234567890abcdef",
            ApiRawSha256 = "raw-sha-hash-1111",
            ApiNormalizedSha256 = "norm-sha-hash-2222",
            ApiProviderRequestId = "req-9999",
            ApiBatchId = "batch-8888"
        };

        var rendered = ProviderTemplateRenderer.Render(snapshot, context);

        Assert.Contains("Candidate: cand-12345", rendered);
        Assert.Contains("Model: gpt-image-2", rendered);
        Assert.Contains("Mode: batch", rendered);
        Assert.Contains("CustomId: aph-1234567890ab-1234567890abcdef", rendered);
        Assert.Contains("RawSha: raw-sha-hash-1111", rendered);
        Assert.Contains("NormSha: norm-sha-hash-2222", rendered);
        Assert.Contains("ReqId: req-9999", rendered);
        Assert.Contains("BatchId: batch-8888", rendered);
    }

    [Fact]
    public void MainForm_MainCommit_WithApiCandidate_EmbedsProvenance()
    {
        RunOnSta(() =>
        {
            MainForm.OpenFolderProvider = _ => { };
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            try
            {
                using var workspace = new TestWorkspace();
                var templateSrc = Path.Combine(AppContext.BaseDirectory, "provider_templates", "OpenAI API.md");
                if (!File.Exists(templateSrc))
                {
                    templateSrc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AssetProvenanceHelper", "provider_templates", "OpenAI API.md"));
                }
                File.WriteAllText(Path.Combine(workspace.ProviderTemplates, "OpenAI API.md"), File.ReadAllText(templateSrc));

                var settings = workspace.CreateSettings();
                var assetRoot = Path.Combine(_tempDir, "assets");
                Directory.CreateDirectory(assetRoot);
                settings.AssetRootFolder = assetRoot;
                settings.SelectedProviderTemplateFileName = "OpenAI API.md";

                var jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));

                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    workspace.CreateSessionService(),
                    providerTemplateCatalogService: workspace.CreateProviderTemplateCatalogService(),
                    generationJobStore: jobStore);

                form.Show();

                // Setup staged image and candidate metadata
                var stagedImage = Path.Combine(_tempDir, "staged.png");
                File.WriteAllBytes(stagedImage, CreateTestPng(512, 512, Color.Magenta));

                var metadata = new ApiCandidateMetadata(
                    CandidateId: "cand-test-777",
                    Provider: "OpenAI API",
                    Model: "gpt-image-2",
                    Mode: "direct",
                    CustomId: "aph-test-custom-id",
                    TargetResolution: "512x512",
                    ProviderResolution: "816x816",
                    RawSha256: "raw-hash-abc",
                    NormalizedSha256: "norm-hash-def",
                    NormalizedImagePath: stagedImage,
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    ProviderRequestId: "req-direct-007");

                // Setup staged candidate in form
                form.SetSelectedImage(ImageSlot.Main, stagedImage);
                typeof(MainForm).GetField("_activeApiCandidateMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(form, metadata);

                var txtAsset = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                var cmbProvider = form.Controls.Find("cmbProvider", true).FirstOrDefault() as ComboBox;
                var btnMain = form.Controls.Find("btnMainImage", true).FirstOrDefault() as Button;

                Assert.NotNull(txtAsset);
                Assert.NotNull(txtPrompt);
                Assert.NotNull(chkNoRef);
                Assert.NotNull(cmbProvider);
                Assert.NotNull(btnMain);

                for (var i = 0; i < cmbProvider.Items.Count; i++)
                {
                    if (cmbProvider.Items[i]?.ToString()?.Contains("OpenAI API") == true)
                    {
                        cmbProvider.SelectedIndex = i;
                        break;
                    }
                }

                txtAsset.Text = "mage_staff";
                txtPrompt.Text = "A glowing crystal magic staff";
                chkNoRef.Checked = true; // No-reference mode

                var chkDirect = form.Controls.Find("chkDirectMode", true).FirstOrDefault() as CheckBox;
                if (chkDirect != null) chkDirect.Checked = false;

                // Commit Main image
                typeof(MainForm).GetMethod("HandleMainImageEntryPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(form, null);
                Application.DoEvents();

                var assetDir = Path.Combine(assetRoot, "mage_staff");
                Assert.True(Directory.Exists(assetDir));

                var provFiles = Directory.GetFiles(assetDir, "*.md");
                Assert.Single(provFiles);

                var provContent = File.ReadAllText(provFiles[0]);
                Assert.Contains("mage_staff", provContent);
                Assert.Contains("cand-test-777", provContent);
                Assert.Contains("gpt-image-2", provContent);
                Assert.Contains("raw-hash-abc", provContent);
                Assert.Contains("norm-hash-def", provContent);

                form.Close();
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }
}
