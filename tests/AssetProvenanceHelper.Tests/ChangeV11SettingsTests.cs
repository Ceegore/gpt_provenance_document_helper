using System.Text.Json;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ChangeV11SettingsTests
{
    [Fact]
    public void LegacySettings_WithProjectName_LoadsAfterProjectRemoval()
    {
        using var workspace =
            new TestWorkspace();

        var json = $$"""
        {
          "ProjectName": "OldGame",
          "DownloadFolder": "{{workspace.Downloads.Replace("\\", "\\\\")}}",
          "AssetRootFolder": "{{workspace.Assets.Replace("\\", "\\\\")}}",
          "AcceptedExtensions": [".png", ".webp", ".jpg", ".jpeg"]
        }
        """;

        File.WriteAllText(
            workspace.SettingsPath,
            json);

        var loaded =
            workspace
                .CreateSettingsService()
                .Load();

        Assert.Equal(workspace.Downloads, loaded.DownloadFolder);
        Assert.Equal(workspace.Assets, loaded.AssetRootFolder);
        Assert.Contains(".png", loaded.AcceptedExtensions);

        // Save again and ensure ProjectName is not written to new JSON
        workspace.CreateSettingsService().Save(loaded);
        var savedJson = File.ReadAllText(workspace.SettingsPath);
        Assert.DoesNotContain("ProjectName", savedJson);
    }

    [Fact]
    public void ProcessingSettings_AllowsEmptyDownloadFolder()
    {
        using var workspace =
            new TestWorkspace();

        var validationService =
            workspace.CreateValidationService();

        var settings =
            new AppSettings
            {
                DownloadFolder = string.Empty,
                AssetRootFolder = workspace.Assets,
                AcceptedExtensions = new List<string> { ".png", ".jpg" }
            };

        var result =
            validationService.ValidateProcessingSettings(settings);

        Assert.True(
            result.IsValid,
            string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RefreshValidation_RejectsEmptyDownloadFolder()
    {
        using var workspace =
            new TestWorkspace();

        var validationService =
            workspace.CreateValidationService();

        var settings =
            new AppSettings
            {
                DownloadFolder = string.Empty,
                AssetRootFolder = workspace.Assets
            };

        var result =
            validationService.ValidateDownloadFolder(settings);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("Image Download Folder must not be empty"));
    }

    [Fact]
    public void RefreshValidation_RejectsNonExistentDownloadFolder()
    {
        using var workspace =
            new TestWorkspace();

        var validationService =
            workspace.CreateValidationService();

        var settings =
            new AppSettings
            {
                DownloadFolder = Path.Combine(workspace.Root, "NonExistentFolder123"),
                AssetRootFolder = workspace.Assets
            };

        var result =
            validationService.ValidateDownloadFolder(settings);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("does not exist"));
    }

    [Fact]
    public void ManualProcessing_CanProceedWithEmptyDownloadFolder()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            new AppSettings
            {
                DownloadFolder = string.Empty,
                AssetRootFolder = workspace.Assets,
                AcceptedExtensions = new List<string> { ".png", ".jpg" }
            };

        var imagePath =
            workspace.CreateImage("manual_ref.png", new byte[] { 1, 2, 3, 4 });

        var session =
            processor.ProcessReference(
                settings,
                "asset_empty_download",
                imagePath,
                DateTimeOffset.Now);

        Assert.NotNull(session);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void ProjectTextbox_DoesNotExistInMainForm()
    {
        RunOnSta(() =>
        {
            using var workspace =
                new TestWorkspace();

            using var form =
                new MainForm(
                    workspace.CreateSettings(),
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    workspace.CreateSessionService());

            var txtProject =
                form.Controls.Find("txtProject", true).FirstOrDefault() as TextBox;

            Assert.Null(txtProject);
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw new Exception("STA test execution failed.", exception);
        }
    }
}
