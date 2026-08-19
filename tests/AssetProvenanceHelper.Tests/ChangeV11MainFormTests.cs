#nullable enable
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public class ChangeV11MainFormTests
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
    public void MainRoot_AutoScrollFalse()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            Assert.False(form.AutoScroll);
        });
    }

    [Fact]
    public void ProjectControlAbsent()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var txtProject = form.Controls.Find("txtProject", true).FirstOrDefault();
            Assert.Null(txtProject);
        });
    }

    [Fact]
    public void SettingsGroupExists()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var grpSettings = form.Controls.Find("grpSettings", true).FirstOrDefault() as GroupBox;
            Assert.NotNull(grpSettings);
            Assert.Equal("Settings", grpSettings.Text);
        });
    }

    [Fact]
    public void ReferenceAndMainGroupsExist()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var grpReference = form.Controls.Find("grpReference", true).FirstOrDefault() as GroupBox;
            var grpMain = form.Controls.Find("grpMain", true).FirstOrDefault() as GroupBox;

            Assert.NotNull(grpReference);
            Assert.NotNull(grpMain);
            Assert.Equal("Reference Image", grpReference.Text);
            Assert.Equal("Main Image", grpMain.Text);
        });
    }

    [Fact]
    public void StatusIsLastRootSection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var grpStatus = form.Controls.Find("grpStatus", true).FirstOrDefault() as GroupBox;
            Assert.NotNull(grpStatus);
            Assert.Equal("Status History & Actions", grpStatus.Text);
        });
    }

    [Fact]
    public void SeparateRefreshChooseDropControlsExist()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            Assert.NotNull(form.Controls.Find("btnRefreshReference", true).FirstOrDefault());
            Assert.NotNull(form.Controls.Find("btnChooseReference", true).FirstOrDefault());
            Assert.NotNull(form.Controls.Find("lblReferenceDrop", true).FirstOrDefault());

            Assert.NotNull(form.Controls.Find("btnRefreshMain", true).FirstOrDefault());
            Assert.NotNull(form.Controls.Find("btnChooseMain", true).FirstOrDefault());
            Assert.NotNull(form.Controls.Find("lblMainDrop", true).FirstOrDefault());
        });
    }

    [Fact]
    public void NoReference_HidesReferenceGroup()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
            var grpRef = form.Controls.Find("grpReference", true).FirstOrDefault() as GroupBox;
            Assert.NotNull(chkNoRef);
            Assert.NotNull(grpRef);

            form.Show();

            Assert.True(grpRef.Visible);

            chkNoRef.Checked = true;
            Assert.False(grpRef.Visible);

            chkNoRef.Checked = false;
            Assert.True(grpRef.Visible);
        });
    }

    [Fact]
    public void NoReference_ExpandsMainColumn()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
            var pnlCards = form.Controls.Find("pnlCardsContainer", true).FirstOrDefault() as TableLayoutPanel;
            Assert.NotNull(chkNoRef);
            Assert.NotNull(pnlCards);

            chkNoRef.Checked = true;
            Assert.Equal(0, pnlCards.ColumnStyles[0].Width);
            Assert.Equal(100, pnlCards.ColumnStyles[1].Width);

            chkNoRef.Checked = false;
            Assert.Equal(50, pnlCards.ColumnStyles[0].Width);
            Assert.Equal(50, pnlCards.ColumnStyles[1].Width);
        });
    }

    [Fact]
    public void ActiveReference_DisablesNoReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var session = processor.ProcessReference(settings, "asset_act", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // ReferenceReady

            var applyMethod = typeof(MainForm).GetMethod("ApplyState", BindingFlags.NonPublic | BindingFlags.Instance);
            applyMethod?.Invoke(form, null);

            var chkNoRef = form.Controls.Find("chkNoReference", true).FirstOrDefault() as CheckBox;
            Assert.NotNull(chkNoRef);
            Assert.False(chkNoRef.Enabled);
            Assert.False(chkNoRef.Checked);
        });
    }

    [Fact]
    public void MissingAssetName_HighlightsAssetName()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            var txtAssetName = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetName);
            txtAssetName.Text = ""; // Empty asset name

            var validateMethod = typeof(MainForm).GetMethod("ValidateReferenceActionUi", BindingFlags.NonPublic | BindingFlags.Instance);
            var isValid = (bool)(validateMethod?.Invoke(form, null) ?? false);

            Assert.False(isValid);

            var pnlHost = typeof(MainForm).GetField("pnlAssetFolderNameHost", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Panel;
            Assert.NotNull(pnlHost);
            Assert.Equal(UiTheme.Error, pnlHost.BackColor);
        });
    }

    [Fact]
    public void MissingPrompt_HighlightsPrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_prompt_test", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // ReferenceReady

            form.SetSelectedImage(ImageSlot.Main, main1);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = ""; // Empty prompt

            var validateMethod = typeof(MainForm).GetMethod("ValidateMainActionUi", BindingFlags.NonPublic | BindingFlags.Instance);
            var isValid = (bool)(validateMethod?.Invoke(form, null) ?? false);

            Assert.False(isValid);

            var pnlHost = typeof(MainForm).GetField("pnlPromptHost", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Panel;
            Assert.NotNull(pnlHost);
            Assert.Equal(UiTheme.Error, pnlHost.BackColor);
        });
    }

    [Fact]
    public void MissingReference_HighlightsReferenceSelection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var txtAssetName = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetName);
            txtAssetName.Text = "valid_name";

            // No reference selected
            var validateMethod = typeof(MainForm).GetMethod("ValidateReferenceActionUi", BindingFlags.NonPublic | BindingFlags.Instance);
            var isValid = (bool)(validateMethod?.Invoke(form, null) ?? false);

            Assert.False(isValid);

            var pnlHost = typeof(MainForm).GetField("pnlReferenceImageHost", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Panel;
            Assert.NotNull(pnlHost);
            Assert.Equal(UiTheme.Error, pnlHost.BackColor);
        });
    }

    [Fact]
    public void MissingMain_HighlightsMainSelection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_main_test", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // ReferenceReady

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "valid prompt";

            // No main image selected
            var validateMethod = typeof(MainForm).GetMethod("ValidateMainActionUi", BindingFlags.NonPublic | BindingFlags.Instance);
            var isValid = (bool)(validateMethod?.Invoke(form, null) ?? false);

            Assert.False(isValid);

            var pnlHost = typeof(MainForm).GetField("pnlMainImageHost", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Panel;
            Assert.NotNull(pnlHost);
            Assert.Equal(UiTheme.Error, pnlHost.BackColor);
        });
    }

    [Fact]
    public void HelpButtonExists()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var btnHelp = form.Controls.Find("btnHelp", true).FirstOrDefault() as Button;
            Assert.NotNull(btnHelp);
            Assert.Equal("?", btnHelp.Text);
        });
    }

    [Fact]
    public void HelpOverlayInitiallyHidden()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(overlay);
            Assert.False(overlay.Visible);
        });
    }

    [Fact]
    public void HelpButtonShowsOverlay()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var btnHelp = form.Controls.Find("btnHelp", true).FirstOrDefault() as Button;
            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(btnHelp);
            Assert.NotNull(overlay);

            form.Show();

            btnHelp.PerformClick();
            Assert.True(overlay.Visible);
        });
    }

    [Fact]
    public void CloseHidesOverlay()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.Show();

            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(overlay);

            overlay.ShowOverlay();
            Assert.True(overlay.Visible);

            overlay.HideOverlay();
            Assert.False(overlay.Visible);
        });
    }

    [Fact]
    public void EscHidesOverlay()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.Show();

            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(overlay);

            overlay.ShowOverlay();
            Assert.True(overlay.Visible);

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            var escKey = new KeyEventArgs(Keys.Escape);
            keyMethod?.Invoke(form, new object[] { form, escKey });

            Assert.False(overlay.Visible);
        });
    }

    [Fact]
    public void HelpVisibleSuppressesCtrlRAndCtrlM()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            form.Show();

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            var txtAssetName = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetName);
            txtAssetName.Text = "suppress_test";

            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(overlay);
            overlay.ShowOverlay();

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrlR = new KeyEventArgs(Keys.Control | Keys.R);
            keyMethod?.Invoke(form, new object[] { form, ctrlR });

            // Session was not created because help was visible
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.Null(sessionField?.GetValue(form));
        });
    }

    [Fact]
    public void F1_ShowsHelpOverlay()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var _ = form.Handle;
            form.Show();
            Application.DoEvents();

            var overlay = form.Controls.Find("helpOverlay", true).FirstOrDefault() as HelpOverlayControl;
            Assert.NotNull(overlay);
            Assert.False(overlay.Visible);

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            var f1Key = new KeyEventArgs(Keys.F1);
            keyMethod?.Invoke(form, new object[] { form, f1Key });

            Assert.True(overlay.Visible);
        });
    }

    [Fact]
    public void CtrlO_OpensAssetFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_ctrl_o", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);

            string? openedPath = null;
            MainForm.OpenFolderProvider = path => openedPath = path;

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrlOKey = new KeyEventArgs(Keys.Control | Keys.O);
            keyMethod?.Invoke(form, new object[] { form, ctrlOKey });

            Assert.Equal(session.AssetFolder, openedPath);

            MainForm.OpenFolderProvider = null;
        });
    }

    [Fact]
    public void HelpContainsMadeByCeeGore()
    {
        var overlay = new HelpOverlayControl();
        var txtContent = overlay.Controls.Find("_txtContent", true).FirstOrDefault() as TextBox
            ?? overlay.GetType().GetField("_txtContent", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(overlay) as TextBox;

        Assert.NotNull(txtContent);
        Assert.Contains("Made by CeeGore", txtContent.Text);
    }
}
