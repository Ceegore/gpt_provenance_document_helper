#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public class ChangeV11RecoveryTests
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    [Fact]
    public void StartupRecovery_CompletedNoReferenceAsset_UserDeletesRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var mainSource = workspace.CreateImage("hero.png", new byte[] { 10, 20, 30 });
            var now = DateTimeOffset.Now;
            var session = processor.CreateNoReferenceMainSession(settings, "hero_asset", mainSource, "hero prompt", now);
            sessionService.Save(session);

            processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "hero prompt", session.MainProcessedAt!.Value);

            // Re-save session to simulate interruption right before session deletion
            sessionService.Save(session);
            Assert.True(sessionService.Exists());

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true; // Delete record

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            TwoChoiceDialog.CustomChoiceProvider = null;

            Assert.False(sessionService.Exists());
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, "hero.png")));
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "hero_asset.png")));
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        });
    }

    [Fact]
    public void StartupRecovery_CompletedNoReferenceAsset_UserExits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var mainSource = workspace.CreateImage("hero.png", new byte[] { 10, 20, 30 });
            var now = DateTimeOffset.Now;
            var session = processor.CreateNoReferenceMainSession(settings, "hero_asset2", mainSource, "hero prompt", now);
            sessionService.Save(session);

            processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "hero prompt", session.MainProcessedAt!.Value);

            // Re-save session
            sessionService.Save(session);
            Assert.True(sessionService.Exists());

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => false; // Exit

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            TwoChoiceDialog.CustomChoiceProvider = null;

            Assert.True(sessionService.Exists());
        });
    }

    [Fact]
    public void StartupRecovery_IncompleteNoReferenceMain_RollsBack()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var mainSource = workspace.CreateImage("hero_inc.png", new byte[] { 10, 20, 30 });
            var session = processor.CreateNoReferenceMainSession(settings, "hero_inc_asset", mainSource, "hero prompt", DateTimeOffset.Now);
            sessionService.Save(session);

            // Only create root main file, leave ingame and final provenance missing (incomplete commit)
            Directory.CreateDirectory(session.AssetFolder);
            var rootMain = Path.Combine(session.AssetFolder, "hero_inc.png");
            File.Copy(mainSource, rootMain, true);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            Assert.False(sessionService.Exists());
            Assert.False(File.Exists(rootMain));
        });
    }

    [Fact]
    public void StartupRecovery_CorruptedNoReferenceSession_PromptsUser()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var sessionService = workspace.CreateSessionService();

            File.WriteAllText(sessionService.SessionFilePath, "{ invalid json corrupt");

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true; // Delete broken record

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            TwoChoiceDialog.CustomChoiceProvider = null;

            Assert.False(sessionService.Exists());
        });
    }
}
