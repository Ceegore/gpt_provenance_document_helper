using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiCandidateDirectPrecedenceTests : IDisposable
{
    private readonly string _tempDir;

    public ApiCandidateDirectPrecedenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_prec_test_" + Guid.NewGuid().ToString("N"));
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

    private static string CreateTestPng(string path, int width, int height, Color color)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        bmp.Save(path, ImageFormat.Png);
        return ComputeSha256(path);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [Fact]
    public void ApiCandidate_LegacyDirectChecked_CommitsApiCandidate_NotNewerDownload()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var downloadFolder = Path.Combine(_tempDir, "downloads");
            var assetRoot = Path.Combine(_tempDir, "assets");
            Directory.CreateDirectory(downloadFolder);
            Directory.CreateDirectory(assetRoot);

            settings.DownloadFolder = downloadFolder;
            settings.AssetRootFolder = assetRoot;

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var stagedCandidatePath = Path.Combine(_tempDir, "staged_cand.png");
                var candidateHash = CreateTestPng(stagedCandidatePath, 512, 512, Color.Blue);

                // Create a newer image in downloads folder
                Thread.Sleep(50);
                var newerDownloadPath = Path.Combine(downloadFolder, "newer_download.png");
                var downloadHash = CreateTestPng(newerDownloadPath, 512, 512, Color.Yellow);
                File.SetLastWriteTimeUtc(newerDownloadPath, DateTime.UtcNow.AddMinutes(5));

                var metadata = new ApiCandidateMetadata(
                    CandidateId: "cand-priority-1",
                    Provider: "OpenAI API",
                    Model: "gpt-image-2",
                    Mode: "direct",
                    CustomId: "aph-custom-1",
                    TargetResolution: "512x512",
                    ProviderResolution: "816x816",
                    RawSha256: "raw-hash",
                    NormalizedSha256: candidateHash,
                    NormalizedImagePath: stagedCandidatePath,
                    CreatedAtUtc: DateTimeOffset.UtcNow,
                    ProviderRequestId: "req-1");

                // Set active candidate metadata and image slot
                form.SetSelectedImage(ImageSlot.Main, stagedCandidatePath);
                typeof(MainForm).GetField("_activeApiCandidateMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(form, metadata);

                var chkDirect = form.Controls.Find("chkDirectMode", true).FirstOrDefault() as CheckBox;
                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                var txtAsset = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                var cmbProvider = form.Controls.Find("cmbProvider", true).FirstOrDefault() as ComboBox;

                Assert.NotNull(chkDirect);
                Assert.NotNull(chkNoRef);
                Assert.NotNull(txtAsset);
                Assert.NotNull(txtPrompt);
                Assert.NotNull(cmbProvider);

                chkDirect.Checked = true; // Legacy Direct mode is ON!
                chkNoRef.Checked = true;
                txtAsset.Text = "priority_asset";
                txtPrompt.Text = "Priority test prompt";

                for (var i = 0; i < cmbProvider.Items.Count; i++)
                {
                    if (cmbProvider.Items[i]?.ToString()?.Contains("OpenAI API") == true)
                    {
                        cmbProvider.SelectedIndex = i;
                        break;
                    }
                }

                // Trigger Main Image Entry Point
                typeof(MainForm).GetMethod("HandleMainImageEntryPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(form, null);
                Application.DoEvents();

                var assetDir = Path.Combine(assetRoot, "priority_asset");
                Assert.True(Directory.Exists(assetDir), "Committed asset directory was not created.");

                var pngFiles = Directory.GetFiles(assetDir, "*.png");
                Assert.Single(pngFiles);

                // Verify the committed file is the candidate (Blue), NOT the newer download (Yellow)
                var committedHash = ComputeSha256(pngFiles[0]);
                Assert.Equal(candidateHash, committedHash);
                Assert.NotEqual(downloadHash, committedHash);

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

    [Fact]
    public void NoApiCandidate_LegacyDirectChecked_OldBehaviorUnchanged()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var downloadFolder = Path.Combine(_tempDir, "downloads2");
            var assetRoot = Path.Combine(_tempDir, "assets2");
            Directory.CreateDirectory(downloadFolder);
            Directory.CreateDirectory(assetRoot);

            settings.DownloadFolder = downloadFolder;
            settings.AssetRootFolder = assetRoot;

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            try
            {
                MainForm.OpenFolderProvider = _ => { };
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                form.Show();

                var downloadPath = Path.Combine(downloadFolder, "auto_download.png");
                var downloadHash = CreateTestPng(downloadPath, 512, 512, Color.Green);

                var chkDirect = form.Controls.Find("chkDirectMode", true).FirstOrDefault() as CheckBox;
                var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
                var txtAsset = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;

                Assert.NotNull(chkDirect);
                Assert.NotNull(chkNoRef);
                Assert.NotNull(txtAsset);
                Assert.NotNull(txtPrompt);

                chkDirect.Checked = true;
                chkNoRef.Checked = true;
                txtAsset.Text = "legacy_asset";
                txtPrompt.Text = "Legacy direct prompt";

                // No API candidate active
                Assert.Null(form.ActiveApiCandidateMetadata);

                // Trigger Main Image Entry Point -> should auto-select download
                typeof(MainForm).GetMethod("HandleMainImageEntryPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(form, null);
                Application.DoEvents();

                var assetDir = Path.Combine(assetRoot, "legacy_asset");
                Assert.True(Directory.Exists(assetDir));

                var pngFiles = Directory.GetFiles(assetDir, "*.png");
                Assert.Single(pngFiles);

                var committedHash = ComputeSha256(pngFiles[0]);
                Assert.Equal(downloadHash, committedHash);

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
