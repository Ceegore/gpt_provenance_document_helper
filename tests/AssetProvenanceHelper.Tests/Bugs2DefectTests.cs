using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

[Trait("Category", "RecoveryCritical")]
public sealed class Bugs2DefectTests
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
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            thread.Interrupt();
            throw new TimeoutException("STA thread timed out.");
        }

        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [Fact]
    public void R2_001_ValidateReplacementJournal_RejectsInvalidJsonAndPathTraversal()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_001", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var journal = tx.ToJournal(ReferenceReplacementPhase.Prepared);

        // Valid journal validates successfully
        var validResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.True(validResult.IsValid, string.Join(Environment.NewLine, validResult.Errors));

        // Unknown phase
        journal.Phase = (ReferenceReplacementPhase)99;
        var invalidPhaseResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.False(invalidPhaseResult.IsValid);
        Assert.Contains(invalidPhaseResult.Errors, e => e.Contains("unknown", StringComparison.OrdinalIgnoreCase));

        // Path traversal in backup path
        journal.Phase = ReferenceReplacementPhase.Prepared;
        journal.BackupReferencePath = Path.Combine(session.AssetFolder, "..", "escaped.bak");
        var escapeResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.False(escapeResult.IsValid);
        Assert.Contains(escapeResult.Errors, e => e.Contains("deterministic", StringComparison.OrdinalIgnoreCase) || e.Contains("inside", StringComparison.OrdinalIgnoreCase) || e.Contains("parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void R2_002_InterruptionRecovery_HandlesAllEightPhases()
    {
        foreach (var phase in Enum.GetValues<ReferenceReplacementPhase>())
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var sessionService = workspace.CreateSessionService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, $"asset_r2_002_{phase}", ref1, DateTimeOffset.Now);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
            var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

            var journal = tx.ToJournal(phase);
            sessionService.SaveReplacementJournal(journal);
            Assert.True(sessionService.ReplacementJournalExists());

            RunOnSta(() =>
            {
                TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

                try
                {
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
                }
                finally
                {
                    TwoChoiceDialog.CustomChoiceProvider = null;
                    MainForm.MessageBoxProvider = null;
                }
            });
        }
    }

    [Fact]
    public void R2_003_StartupRecovery_PreparedReferenceSession_DoesNotCrashWhenFilesMissing()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_003", ref1, DateTimeOffset.Now);

        session.ReferenceCommitPhase = ReferenceCommitPhase.Prepared;
        session.ReferenceTransactionId = Guid.NewGuid().ToString("N");
        sessionService.Save(session);

        if (File.Exists(session.ReferenceDestinationPath)) File.Delete(session.ReferenceDestinationPath);
        if (File.Exists(session.ReferenceProvenancePath)) File.Delete(session.ReferenceProvenancePath);

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
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
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R2_004_StartupRecovery_MainCompleted_TamperedReferencePreservesMainOutputs()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_004", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 99, 99, 99 });

        var rootMain = Path.Combine(session.AssetFolder, mainFilename);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Assert.True(File.Exists(rootMain));
        Assert.True(File.Exists(finalProv));

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => false;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
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

                Assert.True(File.Exists(rootMain), "Main output must be preserved when reference is tampered");
                Assert.True(File.Exists(finalProv), "Final provenance must be preserved when reference is tampered");
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R2_005_ValidateCompleteAsset_ValidatesViaMainProvenanceHashWhenTemplateChanged()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_005", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "original prompt", DateTimeOffset.Now);

        var rootMain = Path.Combine(session.AssetFolder, mainFilename);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        File.WriteAllText(workspace.FinalTemplatePath, "UPDATED TEMPLATE {{AssetId}} {{GenerationDate}} {{Prompt}}");

        var result = validationService.ValidateCompleteAsset(
            session,
            rootMain,
            finalProv,
            mainFilename,
            session.MainProcessedAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "original prompt",
            templateService,
            session.MainHash);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void R2_006_ProcessMainImage_WithoutPreparedTransaction_ThrowsInvalidOperationException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_006", ref1, DateTimeOffset.Now);

        Assert.False(session.IsMainCommitting);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now));

        Assert.Contains("ProcessMainImage requires a prepared and durably persisted Main transaction", ex.Message);
    }

    [Fact]
    public void R2_006_PrepareMainCommit_IdenticalImageToReference_ThrowsInvalidOperationException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_006_identical", ref1, DateTimeOffset.Now);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.PrepareMainCommit(session, ref1, "prompt", DateTimeOffset.Now));

        Assert.Contains("identical to the reference image", ex.Message);
    }

    [Fact]
    public void R2_010_DropButtons_HaveExpectedText()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();

        RunOnSta(() =>
        {
            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var btnDropReference = (Button)typeof(MainForm).GetField("btnDropReference", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            var btnDropMain = (Button)typeof(MainForm).GetField("btnDropMain", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

            Assert.Equal("Drop file here", btnDropReference.Text);
            Assert.Equal("Drop file here", btnDropMain.Text);
        });
    }
}
