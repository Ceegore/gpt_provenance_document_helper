#nullable enable
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Feature v1.4.0: Keep Settings + Variants Mode. See docs/plans/_looi1.md.
/// </summary>
public class FeatureV14VariantsAndKeepSettingsTests
{
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateProductionForm(TestWorkspace workspace, AppSettings? settings = null)
    {
        return new MainForm(
            settings ?? workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService(),
            workspace.CreateProviderTemplateCatalogService(),
            workspace.CreateRecentDocumentHistoryService(),
            workspace.CreateRequestProgressService());
    }

    private static T FindControl<T>(MainForm form, string name)
        where T : Control
    {
        var control = form.Controls.Find(name, true).FirstOrDefault();
        Assert.NotNull(control);
        return Assert.IsType<T>(control);
    }

    private static object? InvokePrivate(MainForm form, string method, params object?[] args)
    {
        var m = typeof(MainForm).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(m is not null, $"Method '{method}' not found.");
        try
        {
            return m!.Invoke(form, args.Length == 0 ? null : args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static object? GetPrivateField(MainForm form, string name)
    {
        var f = typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(f is not null, $"Field '{name}' not found.");
        return f!.GetValue(form);
    }

    private static void SetPrivateField(MainForm form, string name, object? value)
    {
        var f = typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(f is not null, $"Field '{name}' not found.");
        f!.SetValue(form, value);
    }

    private static void SetVariants(MainForm form, int count)
    {
        var cmb = FindControl<ComboBox>(form, "cmbVariants");
        cmb.SelectedIndex = count;
    }

    private static void SetKeepSettings(MainForm form, bool value)
    {
        FindControl<CheckBox>(form, "chkKeepSettings").Checked = value;
    }

    private static void InstallSafeSeams()
    {
        MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
        TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
    }

    private static void ClearSeams()
    {
        MainForm.MessageBoxProvider = null;
        MainForm.OpenFolderProvider = null;
        MainForm.OpenFileDialogProvider = null;
        MainForm.FolderBrowserDialogProvider = null;
        TwoChoiceDialog.CustomChoiceProvider = null;
        AssetProcessorService.OnFileCopiedHook = null;
        AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        ValidationService.FileAttributesProvider = null;
    }

    /// <summary>Creates `count` valid images, oldest first (index 0 is oldest).</summary>
    private static List<string> CreateOrderedImages(TestWorkspace workspace, int count, string prefix = "img")
    {
        var paths = new List<string>();
        var baseTime = DateTime.UtcNow.AddHours(-2);
        for (var i = 0; i < count; i++)
        {
            var path = workspace.CreateImage($"{prefix}{i}.png", new byte[] { (byte)(i + 1), 0x10 });
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i * 10));
            paths.Add(path);
        }

        return paths;
    }

    private static string WriteManifest(TestWorkspace workspace, string json, string fileName = "manifest.json")
    {
        var path = Path.Combine(workspace.Root, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static void SetupNoReferenceBatch(MainForm form, string baseName, string prompt)
    {
        FindControl<CheckBox>(form, "chkNoReference").Checked = true;
        FindControl<TextBox>(form, "txtAssetFolderName").Text = baseName;
        FindControl<TextBox>(form, "txtPrompt").Text = prompt;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ======================================================================
    // Keep Settings (KS-1 .. KS-11)
    // ======================================================================

    [Fact]
    public void KS1_KeepSettingsOff_CompletionClearsPromptAndName()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var main = workspace.CreateImage("main.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_ks1", "my prompt");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Equal(string.Empty, FindControl<TextBox>(form, "txtAssetFolderName").Text);
                Assert.Equal(string.Empty, FindControl<TextBox>(form, "txtPrompt").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS2_KeepSettingsOn_CompletionPreservesPromptAndName()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);
                var main = workspace.CreateImage("main.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_ks2", "my prompt");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Equal("asset_ks2", FindControl<TextBox>(form, "txtAssetFolderName").Text);
                Assert.Equal("my prompt", FindControl<TextBox>(form, "txtPrompt").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS3_KeepSettingsOn_CompletionStillClearsImageSelectionsAndReferenceLabel()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);
                var main = workspace.CreateImage("main.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_ks3", "my prompt");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
                Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
                Assert.Equal("Saved reference: none", FindControl<Label>(form, "lblReference").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS4_KeepSettingsOn_CancelPreservesPromptAndName()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_ks4", refImg, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            SetPrivateField(form, "_currentSession", session);
            InvokePrivate(form, "ApplyState");

            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_ks4";
                FindControl<TextBox>(form, "txtPrompt").Text = "kept prompt";

                InvokePrivate(form, "HandleCancel");

                Assert.Equal("asset_ks4", FindControl<TextBox>(form, "txtAssetFolderName").Text);
                Assert.Equal("kept prompt", FindControl<TextBox>(form, "txtPrompt").Text);
                Assert.Null(GetPrivateField(form, "_currentSession"));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS5_KeepSettingsOn_ImportRequestStillClearsEverything()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var manifestPath = WriteManifest(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p1" }
                  ]
                }
                """);

            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "leftover_name";
                FindControl<TextBox>(form, "txtPrompt").Text = "leftover_prompt";

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

                InvokePrivate(form, "HandleImportRequest");

                Assert.Equal(string.Empty, FindControl<TextBox>(form, "txtAssetFolderName").Text);
                Assert.Equal(string.Empty, FindControl<TextBox>(form, "txtPrompt").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS6_KeepSettingsOn_ClearPromptButtonStillClears()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            form.Show();
            SetKeepSettings(form, true);
            FindControl<TextBox>(form, "txtPrompt").Text = "some prompt";

            FindControl<Button>(form, "btnClearPrompt").PerformClick();

            Assert.Equal(string.Empty, FindControl<TextBox>(form, "txtPrompt").Text);
        });
    }

    [Fact]
    public void KS7_KeepSettingsEnabled_RoundTripsThroughSettingsService()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSettingsService();

        var settings = service.CreateDefaults();
        settings.DownloadFolder = workspace.Downloads;
        settings.AssetRootFolder = workspace.Assets;
        settings.KeepSettingsEnabled = true;
        service.Save(settings);

        var loaded = service.Load();
        Assert.True(loaded.KeepSettingsEnabled);
    }

    [Fact]
    public void KS8_PreFeatureSettingsJson_LoadsWithKeepSettingsFalse()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSettingsService();

        File.WriteAllText(
            workspace.SettingsPath,
            $$"""
            {
              "DownloadFolder": {{System.Text.Json.JsonSerializer.Serialize(workspace.Downloads)}},
              "AssetRootFolder": {{System.Text.Json.JsonSerializer.Serialize(workspace.Assets)}}
            }
            """);

        var loaded = service.Load();
        Assert.False(loaded.KeepSettingsEnabled);
    }

    [Fact]
    public void KS9_KeepSettingsOn_QueueBoundCompletion_ActiveRequestClearedAndDoneRowNotReactivated()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var manifestPath = WriteManifest(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "asset_ks9.png", "resolution": "10x10", "prompt": "queued prompt" }
                  ]
                }
                """);

            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);

                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                InvokePrivate(form, "HandleImportRequest");

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                InvokePrivate(form, "HandleRequestQueueItemActivate", lv.Items[0]);

                Assert.NotNull(GetPrivateField(form, "_activeRequest"));

                var main = workspace.CreateImage("main.png", new byte[] { 9 });
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Null(GetPrivateField(form, "_activeRequest"));
                Assert.Equal("Done", lv.Items[0].SubItems[0].Text);

                // Re-activating the now-Done row must not rebind it.
                InvokePrivate(form, "HandleRequestQueueItemActivate", lv.Items[0]);
                Assert.Null(GetPrivateField(form, "_activeRequest"));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS10_KeepSettingsOn_ClickingMainImageAgainWithUnchangedNameFailsSafely()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                SetKeepSettings(form, true);
                var main = workspace.CreateImage("main.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_ks10", "my prompt");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                var assetFolder = Path.Combine(workspace.Assets, "asset_ks10");
                Assert.True(File.Exists(Path.Combine(assetFolder, AppConstants.FinalProvenanceFileName)));

                // Name/prompt were kept; select the same image again and retry.
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                // The completed asset must remain intact and untouched.
                Assert.True(File.Exists(Path.Combine(assetFolder, AppConstants.FinalProvenanceFileName)));
                Assert.True(File.Exists(Path.Combine(assetFolder, "main.png")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void KS11_VariantsCountPreservedOnlyWhenKeepSettingsOn()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var formOn = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                SetKeepSettings(formOn, true);
                SetVariants(formOn, 3);
                var mains = CreateOrderedImages(workspace, 3);
                SetupNoReferenceBatch(formOn, "asset_ks11a", "prompt");

                InvokePrivate(formOn, "HandleMainImageEntryPoint");

                Assert.Equal(3, FindControl<ComboBox>(formOn, "cmbVariants").SelectedIndex);
            }
            finally
            {
                ClearSeams();
            }

            using var formOff = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                SetVariants(formOff, 2);
                var mains = CreateOrderedImages(workspace, 2, "img2_");
                SetupNoReferenceBatch(formOff, "asset_ks11b", "prompt");

                InvokePrivate(formOff, "HandleMainImageEntryPoint");

                Assert.Equal(0, FindControl<ComboBox>(formOff, "cmbVariants").SelectedIndex);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    // ======================================================================
    // Variants — naming (VN-1 .. VN-7)
    // ======================================================================

    [Theory]
    [InlineData(1, "A")]
    [InlineData(2, "B")]
    [InlineData(10, "J")]
    public void VN1_GetVariantSuffix_MapsToLetters(int n, string expected)
    {
        Assert.Equal(expected, AssetNaming.GetVariantSuffix(n));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void VN2_GetVariantSuffix_ThrowsOutOfRange(int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetNaming.GetVariantSuffix(n));
    }

    [Fact]
    public void VN3_BuildVariantAssetName_AppendsSuffix()
    {
        Assert.Equal("image1A", AssetNaming.BuildVariantAssetName("image1", 1));
    }

    [Fact]
    public void VN4_BuildVariantAssetName_TrimsWhitespace()
    {
        Assert.Equal("image1A", AssetNaming.BuildVariantAssetName("  image1  ", 1));
    }

    [Fact]
    public void VN5_AllTenDerivedNames_PassValidateAssetName()
    {
        var validation = new ValidationService();
        for (var i = 1; i <= 10; i++)
        {
            var name = AssetNaming.BuildVariantAssetName("image1", i);
            var result = validation.ValidateAssetName(name, AppConstants.DefaultImageExtensions);
            Assert.True(result.IsValid, $"'{name}' should be valid: {string.Join(";", result.Errors)}");
        }
    }

    [Fact]
    public void VN6_Ordering_OldestOfNBecomesVariantA()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var mains = CreateOrderedImages(workspace, 3); // [0]=oldest .. [2]=newest
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vn6", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                var hashOldest = Sha256Of(mains[0]);
                var hashNewest = Sha256Of(mains[2]);

                var aMain = Directory.GetFiles(Path.Combine(workspace.Assets, "asset_vn6A"))
                    .Single(p => Path.GetExtension(p) == ".png");
                var cMain = Directory.GetFiles(Path.Combine(workspace.Assets, "asset_vn6C"))
                    .Single(p => Path.GetExtension(p) == ".png");

                Assert.Equal(hashOldest, Sha256Of(aMain));
                Assert.Equal(hashNewest, Sha256Of(cMain));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VN7_FindLatestImages_TieBreakIsDeterministic()
    {
        using var workspace = new TestWorkspace();
        var finder = workspace.CreateImageFinder();
        var settings = workspace.CreateSettings();

        var same = DateTime.UtcNow;
        var a = workspace.CreateImage("a.png", new byte[] { 1 });
        var b = workspace.CreateImage("b.png", new byte[] { 2 });
        File.SetLastWriteTimeUtc(a, same);
        File.SetLastWriteTimeUtc(b, same);
        File.SetCreationTimeUtc(a, same);
        File.SetCreationTimeUtc(b, same);

        var first = finder.FindLatestImages(settings, 2);
        var second = finder.FindLatestImages(settings, 2);

        Assert.Equal(first, second);
        Assert.Equal(2, first.Distinct().Count());
    }

    // ======================================================================
    // Variants — preflight (VP-1 .. VP-5)
    // ======================================================================

    [Fact]
    public void VP1_NotEnoughImages_AbortsWithNothingWritten()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            var messages = new List<string>();
            MainForm.MessageBoxProvider = (_, text, _, _, _) => messages.Add(text);
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vp1", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, m => m.Contains("Variants is set to 3") && m.Contains("only 2"));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp1A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VP2_OneInvalidImage_AbortsWholeBatch()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var mains = CreateOrderedImages(workspace, 3);
                File.WriteAllBytes(mains[1], new byte[] { 0x00, 0x01 }); // corrupt the middle image
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vp2", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp2A")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp2C")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VP3_NoReference_LaterVariantFolderExists_AbortsBeforeAIsCreated()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                Directory.CreateDirectory(Path.Combine(workspace.Assets, "asset_vp3B"));

                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vp3", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp3A")),
                    "Preflight must run before any variant is created.");
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VP4_ReferenceAssisted_VariantAFolderExisting_DoesNotAbort()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 4); // 1 reference + 3 mains
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vp4";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);

                InvokePrivate(form, "HandleReference");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp4A", "reference")));

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp4A")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp4B")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp4C")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VP5_InvalidDownloadFolder_AbortsWithFieldHighlighted()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                FindControl<TextBox>(form, "txtDownloadFolder").Text = Path.Combine(workspace.Root, "missing_dl");
                SetVariants(form, 2);
                SetupNoReferenceBatch(form, "asset_vp5", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vp5A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    // ======================================================================
    // Variants — No-Reference execution (VE-1 .. VE-9)
    // ======================================================================

    [Fact]
    public void VE1_ThreeVariants_HappyPath_ProducesThreeCompleteAssets()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_ve1", "shared prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                foreach (var suffix in new[] { "A", "B", "C" })
                {
                    var folder = Path.Combine(workspace.Assets, $"asset_ve1{suffix}");
                    Assert.True(Directory.Exists(folder));
                    Assert.True(File.Exists(Path.Combine(folder, AppConstants.FinalProvenanceFileName)));
                    var ingameFolder = Path.Combine(folder, "ingame");
                    Assert.True(Directory.Exists(ingameFolder));
                    Assert.Single(Directory.GetFiles(ingameFolder));
                }
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE2_EachVariantsProvenanceNamesItsOwnAsset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                SetupNoReferenceBatch(form, "asset_ve2", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                var provA = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve2A", AppConstants.FinalProvenanceFileName));
                var provB = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve2B", AppConstants.FinalProvenanceFileName));

                Assert.Contains("asset_ve2A", provA);
                Assert.Contains("asset_ve2B", provB);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE3_AllVariantsShareIdenticalPrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                SetupNoReferenceBatch(form, "asset_ve3", "the shared prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                var provA = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve3A", AppConstants.FinalProvenanceFileName));
                var provB = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve3B", AppConstants.FinalProvenanceFileName));

                Assert.Contains("the shared prompt", provA);
                Assert.Contains("the shared prompt", provB);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE4_AllVariantsCarrySameGenerationDate()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                SetupNoReferenceBatch(form, "asset_ve4", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                var provA = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve4A", AppConstants.FinalProvenanceFileName));
                var provB = File.ReadAllText(Path.Combine(workspace.Assets, "asset_ve4B", AppConstants.FinalProvenanceFileName));

                var dateA = provA.Split("Date:")[1].Split('\n')[0].Trim();
                var dateB = provB.Split("Date:")[1].Split('\n')[0].Trim();

                Assert.Equal(dateA, dateB);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE5_NoSessionJsonRemainsAfterSuccessfulBatch()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                SetupNoReferenceBatch(form, "asset_ve5", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(File.Exists(workspace.SessionPath));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE6_AtMostOneSessionJsonExistsAtAnyInstant()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            var sawMoreThanOne = false;
            try
            {
                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_ve6", "prompt");

                MainForm.OnVariantCommittedHook = (_, _) =>
                {
                    // No-Reference mode never keeps a durable session between variants.
                    if (File.Exists(workspace.SessionPath))
                    {
                        sawMoreThanOne = true;
                    }
                };

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(sawMoreThanOne);
            }
            finally
            {
                MainForm.OnVariantCommittedHook = null;
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE7_VariantsOne_ProducesSingleAssetNamedWithSuffixA()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 1);
                SetVariants(form, 1);
                SetupNoReferenceBatch(form, "asset_ve7", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_ve7A")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_ve7")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE8_VariantsNone_ProducesByteForByteTodaysBehavior()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var main = workspace.CreateImage("main.png", new byte[] { 1, 2 });
                SetVariants(form, 0);
                SetupNoReferenceBatch(form, "asset_ve8", "prompt");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_ve8")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_ve8A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VE9_TenVariants_WorksEndToEnd()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 10);
                SetVariants(form, 10);
                SetupNoReferenceBatch(form, "asset_ve9", "prompt");

                InvokePrivate(form, "HandleMainImageEntryPoint");

                foreach (var i in Enumerable.Range(1, 10))
                {
                    var suffix = AssetNaming.GetVariantSuffix(i);
                    Assert.True(
                        Directory.Exists(Path.Combine(workspace.Assets, $"asset_ve9{suffix}")),
                        $"Variant {suffix} must exist.");
                }
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    // ======================================================================
    // Variants — Reference-assisted execution (VR-1 .. VR-11)
    // ======================================================================

    [Fact]
    public void VR1_ReferenceClickWithVariants_CreatesVariantAFolderNotBaseFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr1";
                form.SetSelectedImage(ImageSlot.Reference, refImg);

                InvokePrivate(form, "HandleReference");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr1A", "reference")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr1")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR2_ReferenceClickWithVariantsNone_StillCreatesBaseFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
                SetVariants(form, 0);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr2";
                form.SetSelectedImage(ImageSlot.Reference, refImg);

                InvokePrivate(form, "HandleReference");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr2", "reference")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR3_ThreeVariants_HappyPath_EachHasOwnReferenceCopyAndProvenance()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 4); // 1 ref + 3 mains
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr3";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                var refHash = Sha256Of(images[0]);

                foreach (var suffix in new[] { "A", "B", "C" })
                {
                    var folder = Path.Combine(workspace.Assets, $"asset_vr3{suffix}");
                    Assert.True(Directory.Exists(folder));
                    Assert.True(File.Exists(Path.Combine(folder, AppConstants.FinalProvenanceFileName)));
                    Assert.True(Directory.Exists(Path.Combine(folder, "ingame")));

                    var refFolder = Path.Combine(folder, "reference");
                    Assert.True(Directory.Exists(refFolder));
                    var refFile = Directory.GetFiles(refFolder).Single(p => Path.GetExtension(p) == ".png");
                    Assert.Equal(refHash, Sha256Of(refFile));
                    Assert.True(File.Exists(Path.Combine(refFolder, AppConstants.ReferenceProvenanceFileName)));
                }
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR5_EachVariantsReferenceProvenanceNamesItsOwnAsset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 3); // 1 ref + 2 mains
                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr5";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                var refProvB = File.ReadAllText(
                    Path.Combine(workspace.Assets, "asset_vr5B", "reference", AppConstants.ReferenceProvenanceFileName));

                Assert.Contains("asset_vr5B", refProvB);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR6_AllReferenceDocumentsCarrySameGenerationDate()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 3);
                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr6";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                var provA = File.ReadAllText(Path.Combine(workspace.Assets, "asset_vr6A", "reference", AppConstants.ReferenceProvenanceFileName));
                var provB = File.ReadAllText(Path.Combine(workspace.Assets, "asset_vr6B", "reference", AppConstants.ReferenceProvenanceFileName));

                var dateA = provA.Split("Date:")[1].Split('\n')[0].Trim();
                var dateB = provB.Split("Date:")[1].Split('\n')[0].Trim();

                Assert.Equal(dateA, dateB);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR7_VariantA_ReusesExistingSession_NoSecondReferenceCopy()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 3); // 1 ref + 2 mains
                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr7";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                var refFileA = Path.Combine(workspace.Assets, "asset_vr7A", "reference", "img0.png");
                var mtimeBefore = File.GetLastWriteTimeUtc(refFileA);
                var hashBefore = Sha256Of(refFileA);

                var copyCount = 0;
                AssetProcessorService.OnFileCopiedHook = (_, _) => copyCount++;

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                // Exactly 2 copies expected for the whole 2-variant batch: one
                // reference replication for variant B, one main copy for variant B.
                // Variant A's main copy also happens (it was not yet committed),
                // so total = 1 (A main) + 1 (B reference) + 1 (B main) = 3.
                Assert.Equal(3, copyCount);
                Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(refFileA));
                Assert.Equal(hashBefore, Sha256Of(refFileA));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR8_ValidateExactReferenceOutput_PassesForEveryVariant()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();

            var results = new Dictionary<string, ValidationResult>(StringComparer.Ordinal);
            var templates = workspace.CreateTemplateService();
            var validation = workspace.CreateValidationService();

            SessionService.OnBeforeSaveSessionHook = s =>
            {
                if (s.WorkflowMode == AssetWorkflowMode.ReferenceAssisted
                    && s.ReferenceCommitPhase == ReferenceCommitPhase.None
                    && !s.IsMainCommitting)
                {
                    results[s.AssetFolderName] = validation.ValidateExactReferenceOutput(s, templates);
                }
            };

            try
            {
                var images = CreateOrderedImages(workspace, 4);
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr8";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                foreach (var suffix in new[] { "A", "B", "C" })
                {
                    var name = $"asset_vr8{suffix}";
                    Assert.True(results.ContainsKey(name), $"No stable reference snapshot captured for {name}");
                    Assert.True(results[name].IsValid, $"{name}: {string.Join(";", results[name].Errors)}");
                }
            }
            finally
            {
                SessionService.OnBeforeSaveSessionHook = null;
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR9_VariantsComboDisabledWhileReferenceReady_SelectionNotReset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
                SetVariants(form, 4);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr9";
                form.SetSelectedImage(ImageSlot.Reference, refImg);

                InvokePrivate(form, "HandleReference");

                var cmb = FindControl<ComboBox>(form, "cmbVariants");
                Assert.False(cmb.Enabled);
                Assert.Equal(4, cmb.SelectedIndex);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR10_SessionNotVariantA_RefusesBatch()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 2);

                // Create the reference while Variants was "none" -> base session "asset_vr10".
                SetVariants(form, 0);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr10";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr10")));

                // Now switch Variants on for the Main click without a new Reference.
                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Main, images[1]);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr10A")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr10B")));
                // The pre-existing, legitimate reference-only session must remain untouched.
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr10", "reference")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VR11_VariantsWithNoActiveReferenceSession_NothingWritten()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            var messages = new List<string>();
            MainForm.MessageBoxProvider = (_, text, _, _, _) => messages.Add(text);
            try
            {
                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vr11";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, m => m.Contains("No active reference session exists."));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vr11A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    // ======================================================================
    // Variants — failure and reuse warning (VF-1 .. VF-9)
    // ======================================================================

    [Fact]
    public void VF1_VF2_VF3_VariantBFails_AStaysComplete_CNeverAttempted_NoOrphans_RequestStaysPending()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var manifestPath = WriteManifest(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "asset_vf1.png", "resolution": "10x10", "prompt": "queued prompt" }
                  ]
                }
                """);

            InstallSafeSeams();
            try
            {
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                InvokePrivate(form, "HandleImportRequest");
                var lv = FindControl<ListView>(form, "lvRequestQueue");
                InvokePrivate(form, "HandleRequestQueueItemActivate", lv.Items[0]);

                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;

                var copyCount = 0;
                AssetProcessorService.OnFileCopiedHook = (_, _) =>
                {
                    copyCount++;
                    if (copyCount == 2)
                    {
                        throw new InvalidOperationException("Induced failure for variant B.");
                    }
                };

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf1A")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "asset_vf1A", AppConstants.FinalProvenanceFileName)));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf1C")));

                // No orphaned session.json / temp files anywhere under the asset root.
                Assert.False(File.Exists(workspace.SessionPath));
                var tempFiles = Directory.GetFiles(workspace.Assets, ".main-*", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(workspace.Assets, ".__new_*", SearchOption.AllDirectories));
                Assert.Empty(tempFiles);

                // Request stays Pending.
                Assert.NotEqual("Done", lv.Items[0].SubItems[0].Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF4_FullBatchSuccess_QueueRequestMarkedDoneExactlyOnce()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var manifestPath = WriteManifest(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "asset_vf4.png", "resolution": "10x10", "prompt": "queued prompt" }
                  ]
                }
                """);

            InstallSafeSeams();
            try
            {
                MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
                InvokePrivate(form, "HandleImportRequest");
                var lv = FindControl<ListView>(form, "lvRequestQueue");
                InvokePrivate(form, "HandleRequestQueueItemActivate", lv.Items[0]);

                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;

                var doneCount = 0;
                MainForm.OnVariantCommittedHook = (_, _) =>
                {
                    if (lv.Items[0].SubItems[0].Text == "Done")
                    {
                        doneCount++;
                    }
                };

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Equal("Done", lv.Items[0].SubItems[0].Text);
                Assert.Equal(0, doneCount); // Not marked Done until AFTER the last variant.
            }
            finally
            {
                MainForm.OnVariantCommittedHook = null;
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF5_CriticalFailure_ClosesForm_AStaysCommitted_CNeverCreated()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vf5", "prompt");

                var mainCallCount = 0;
                AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
                {
                    mainCallCount++;
                    if (mainCallCount == 2)
                    {
                        ValidationService.FileAttributesProvider = path =>
                        {
                            if (ValidationService.PathsEqual(path, s.GetIngameFolderPath()))
                            {
                                return FileAttributes.Directory | FileAttributes.ReparsePoint;
                            }

                            return File.GetAttributes(path);
                        };
                    }
                };

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(form.IsDisposed);
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf5A")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf5C")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF6_ReferenceReplicationFails_ARemainsComplete_BFullyRolledBack_CNeverAttempted()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 4); // 1 ref + 3 mains
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vf6";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, images[0]);
                InvokePrivate(form, "HandleReference");

                var copyCount = 0;
                AssetProcessorService.OnFileCopiedHook = (_, _) =>
                {
                    copyCount++;
                    if (copyCount == 2)
                    {
                        // Copy #1 is variant A's Main image; copy #2 is variant B's
                        // reference replication - fail exactly that one.
                        throw new InvalidOperationException("Induced reference replication failure.");
                    }
                };

                form.SetSelectedImage(ImageSlot.Main, images[1]);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf6A")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "asset_vf6A", AppConstants.FinalProvenanceFileName)));

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf6B")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf6C")));
                Assert.False(File.Exists(workspace.SessionPath));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF7_ReuseWarning_ChoosingCancel_CommitsNothing()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var main = workspace.CreateImage("reused.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_vf7_first", "prompt");
                form.SetSelectedImage(ImageSlot.Main, main);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf7_first")));

                CreateOrderedImages(workspace, 2, "extra_"); // pad so the batch has 3 candidates, incl. the reused one
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vf7_second", "prompt2");

                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => false; // Cancel

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf7_secondA")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF8_ReuseWarning_ChoosingProcessAgain_ProceedsNormally()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var main = workspace.CreateImage("reused2.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_vf8_first", "prompt");
                form.SetSelectedImage(ImageSlot.Main, main);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                CreateOrderedImages(workspace, 2, "extra_");
                SetVariants(form, 3);
                SetupNoReferenceBatch(form, "asset_vf8_second", "prompt2");

                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true; // Process Again

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf8_secondA")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf8_secondC")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VF9_NoReuseWarningOnSingleAssetPath()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            var dialogShown = false;
            try
            {
                var main = workspace.CreateImage("reused3.png", new byte[] { 1 });
                SetupNoReferenceBatch(form, "asset_vf9_first", "prompt");
                form.SetSelectedImage(ImageSlot.Main, main);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) =>
                {
                    dialogShown = true;
                    return true;
                };

                // Variants stays "none": select the SAME already-committed source again.
                SetupNoReferenceBatch(form, "asset_vf9_second", "prompt2");
                form.SetSelectedImage(ImageSlot.Main, main);

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(dialogShown);
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vf9_second")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    // ======================================================================
    // Variants — UI state (VU-1 .. VU-7)
    // ======================================================================

    [Fact]
    public void VU1_VariantsComboEnabledInIdle_BothModes()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var cmb = FindControl<ComboBox>(form, "cmbVariants");
            Assert.True(cmb.Enabled);

            FindControl<CheckBox>(form, "chkNoReference").Checked = true;
            InvokePrivate(form, "ApplyState");
            Assert.True(cmb.Enabled);
        });
    }

    [Fact]
    public void VU2_VariantsComboDisabledInReferenceReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var refImg = workspace.CreateImage("ref.png", new byte[] { 1 });
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vu2";
                form.SetSelectedImage(ImageSlot.Reference, refImg);
                InvokePrivate(form, "HandleReference");

                Assert.False(FindControl<ComboBox>(form, "cmbVariants").Enabled);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VU3_DirectNoReferenceWithVariants_BypassesTryAutoSelectLatestMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            var messages = new List<string>();
            MainForm.MessageBoxProvider = (_, text, _, _, _) => messages.Add(text);
            try
            {
                FindControl<CheckBox>(form, "chkDirectMode").Checked = true;
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;
                SetVariants(form, 3);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vu3";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                // No images in the download folder at all.

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.Contains(messages, m => m.Contains("Variants is set to 3"));
                Assert.DoesNotContain(messages, m => m.Contains("No supported image was found"));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VU4_DirectReferenceAssistedWithVariants_ResolvesNPlusOneOldestAsReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var images = CreateOrderedImages(workspace, 3); // 1 ref + 2 mains
                FindControl<CheckBox>(form, "chkDirectMode").Checked = true;
                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vu4";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vu4A", "reference")));
                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vu4B")));

                var refFile = Directory.GetFiles(Path.Combine(workspace.Assets, "asset_vu4A", "reference"))
                    .Single(p => Path.GetExtension(p) == ".png");
                Assert.Equal(Sha256Of(images[0]), Sha256Of(refFile));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VU5_DirectReferenceAssistedWithVariantsNone_ByteForByteTodaysBehavior()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var old = workspace.CreateImage("old.png", new byte[] { 1 });
                var reference = workspace.CreateImage("reference.png", new byte[] { 2 });
                var main = workspace.CreateImage("main.png", new byte[] { 3 });
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-30));
                File.SetLastWriteTimeUtc(reference, DateTime.UtcNow.AddMinutes(-10));
                File.SetLastWriteTimeUtc(main, DateTime.UtcNow);

                FindControl<CheckBox>(form, "chkDirectMode").Checked = true;
                SetVariants(form, 0);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_vu5";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_vu5")));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_vu5A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VU6_MainRefreshWithVariantsActive_ShowsBatchLabel_ReferenceRefreshUnaffected()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 3);
                SetVariants(form, 3);

                InvokePrivate(form, "RefreshImageSelection", ImageSlot.Main);

                var label = FindControl<Label>(form, "lblMainSelectedImage");
                Assert.Contains("3 variants", label.Text);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));

                var refImg = workspace.CreateImage("aaa_ref.png", new byte[] { 9 });
                File.SetLastWriteTimeUtc(refImg, DateTime.UtcNow.AddMinutes(1));
                InvokePrivate(form, "RefreshImageSelection", ImageSlot.Reference);

                Assert.NotNull(form.GetSelectedImage(ImageSlot.Reference));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VU7_GetSelectedVariantCount_MatchesComboIndex()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            SetVariants(form, 0);
            Assert.Equal(0, (int)InvokePrivate(form, "GetSelectedVariantCount")!);

            SetVariants(form, 7);
            Assert.Equal(7, (int)InvokePrivate(form, "GetSelectedVariantCount")!);
        });
    }

    // ======================================================================
    // Additional coverage: defensive branches
    // ======================================================================

    [Fact]
    public void TryResolveVariantAssetNames_InvalidBaseName_ReturnsNullWithoutTouchingDisk()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var result = InvokePrivate(form, "TryResolveVariantAssetNames", "bad|name*", 2, false);

                Assert.Null(result);
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "bad|name*A")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VariantBatch_EmptyPrompt_FailsValidationBeforeAnyResolution()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 2);
                SetVariants(form, 2);
                FindControl<CheckBox>(form, "chkNoReference").Checked = true;
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_emptyprompt";
                FindControl<TextBox>(form, "txtPrompt").Text = string.Empty;

                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_emptypromptA")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void RefreshMainVariantBatchSelection_NotEnoughImages_LeavesLabelUnset()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                CreateOrderedImages(workspace, 1);
                SetVariants(form, 3);

                InvokePrivate(form, "RefreshImageSelection", ImageSlot.Main);

                Assert.Equal("Selected: none", FindControl<Label>(form, "lblMainSelectedImage").Text);
            }
            finally
            {
                ClearSeams();
            }
        });
    }

    [Fact]
    public void VariantBatch_ReferenceAssisted_PrepareMainCommitFails_ReportsErrorAndStopsBatch()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            InstallSafeSeams();
            try
            {
                var refImg = workspace.CreateImage("ref.png", new byte[] { 5, 5, 5 });
                File.SetLastWriteTimeUtc(refImg, DateTime.UtcNow.AddMinutes(-30));

                SetVariants(form, 2);
                FindControl<TextBox>(form, "txtAssetFolderName").Text = "asset_prepfail";
                FindControl<TextBox>(form, "txtPrompt").Text = "prompt";
                form.SetSelectedImage(ImageSlot.Reference, refImg);
                InvokePrivate(form, "HandleReference");

                // Variant A's main is normal; variant B's "main" is byte-identical to
                // the reference, which PrepareMainCommit explicitly rejects.
                var mainA = workspace.CreateImage("mainA.png", new byte[] { 1, 2 });
                File.SetLastWriteTimeUtc(mainA, DateTime.UtcNow.AddMinutes(-10));
                var mainBIdenticalToRef = workspace.CreateImage("mainB.png", new byte[] { 5, 5, 5 });
                File.SetLastWriteTimeUtc(mainBIdenticalToRef, DateTime.UtcNow);

                form.SetSelectedImage(ImageSlot.Main, mainA);
                InvokePrivate(form, "HandleMainImageEntryPoint");

                Assert.True(Directory.Exists(Path.Combine(workspace.Assets, "asset_prepfailA")));
                Assert.True(File.Exists(Path.Combine(workspace.Assets, "asset_prepfailA", AppConstants.FinalProvenanceFileName)));
                Assert.False(Directory.Exists(Path.Combine(workspace.Assets, "asset_prepfailC")));
            }
            finally
            {
                ClearSeams();
            }
        });
    }
}
