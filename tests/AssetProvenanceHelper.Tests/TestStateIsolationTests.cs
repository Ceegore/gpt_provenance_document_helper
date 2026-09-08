using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Guards the assembly-wide redirect in <see cref="TestAppState"/>. Without it a
/// MainForm built with the optional state services omitted reads, recovers
/// against, and can discard the operator's real application state.
/// </summary>
public sealed class TestStateIsolationTests
{
    private static string RealUserStateDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ceegore",
            "AssetProvenanceHelper");

    [Fact]
    public void StateDirectory_NeverResolvesToTheRealUserFolder()
    {
        Assert.NotEqual(
            Path.GetFullPath(RealUserStateDirectory),
            Path.GetFullPath(AppBootstrap.GetStateDirectory()));
    }

    [Fact]
    public void ActiveWorkspace_OwnsTheStateDirectory_AndReleasesItOnDispose()
    {
        string workspaceState;
        using (var workspace = new TestWorkspace())
        {
            workspaceState = workspace.StateDirectory;
            Assert.Equal(
                Path.GetFullPath(workspaceState),
                Path.GetFullPath(AppBootstrap.GetStateDirectory()));
        }

        Assert.NotEqual(
            Path.GetFullPath(workspaceState),
            Path.GetFullPath(AppBootstrap.GetStateDirectory()));
        Assert.NotEqual(
            Path.GetFullPath(RealUserStateDirectory),
            Path.GetFullPath(AppBootstrap.GetStateDirectory()));
    }

    [Fact]
    public void ServicesThatFallBackToAppBootstrap_StayInsideTheWorkspace()
    {
        using var workspace = new TestWorkspace();

        var staging = new GeneratedImageStagingService();
        var pixelExactState = AppBootstrap.GetPixelExactBatchStatePath(AppBootstrap.GetStateDirectory());

        Assert.StartsWith(
            Path.GetFullPath(workspace.StateDirectory),
            Path.GetFullPath(staging.BaseStagingPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetFullPath(workspace.StateDirectory),
            Path.GetFullPath(pixelExactState),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainFormWithoutOptionalStateServices_WritesNothingIntoTheRealFolder()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                MainForm.ConfirmBoxProvider = (_, _, _, _, _) => DialogResult.OK;
                MainForm.OpenFolderProvider = _ => { };
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                using var workspace = new TestWorkspace();
                var realFolderBefore = SnapshotRealUserFolder();

                using (var form = new MainForm(
                    workspace.CreateSettings(),
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    workspace.CreateSessionService()))
                {
                    Assert.Equal(
                        Path.GetFullPath(workspace.StateDirectory),
                        Path.GetFullPath(AppBootstrap.GetStateDirectory()));
                }

                Assert.Equal(realFolderBefore, SnapshotRealUserFolder());
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.ConfirmBoxProvider = null;
                MainForm.OpenFolderProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    /// <summary>Path plus last-write time of everything in the real folder, so a
    /// stray create, rewrite or delete by a MainForm shows up as a difference.</summary>
    private static string SnapshotRealUserFolder()
    {
        if (!Directory.Exists(RealUserStateDirectory))
        {
            return string.Empty;
        }

        var entries = Directory
            .EnumerateFileSystemEntries(RealUserStateDirectory, "*", SearchOption.AllDirectories)
            .Select(entry => entry + "|" + File.GetLastWriteTimeUtc(entry).ToString("O"))
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", entries);
    }
}
