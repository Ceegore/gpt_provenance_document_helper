#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Guards the v1.4.0 layout defect: the single-line field hosts stretched to their
/// full table row, rendering as large grey blocks and starving the Reference/Main
/// cards of vertical space so they were cut off at the default window size.
/// </summary>
public class LayoutV140Tests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)));
        if (error != null) { throw new AggregateException(error); }
    }

    private static MainForm CreateForm(TestWorkspace w) =>
        new(
            w.CreateSettings(),
            w.CreateSettingsService(),
            w.CreateImageFinder(),
            w.CreateTemplateService(),
            w.CreateValidationService(),
            w.CreateAssetProcessor(),
            w.CreateSessionService(),
            w.CreateProviderTemplateCatalogService(),
            w.CreateRecentDocumentHistoryService(),
            w.CreateRequestProgressService());

    /// <summary>
    /// True when the window actually got (near enough) the size we asked for. A
    /// constrained CI display can clamp it, and geometry assertions against a
    /// clamped window test the runner, not the layout.
    /// </summary>
    private static bool ReachedRequestedSize(MainForm form, System.Drawing.Size requested) =>
        form.Width >= requested.Width - 8 && form.Height >= requested.Height - 8;

    private static T Find<T>(MainForm f, string name) where T : Control
    {
        var c = f.Controls.Find(name, true).FirstOrDefault();
        Assert.NotNull(c);
        return Assert.IsType<T>(c);
    }

    /// <summary>
    /// Each single-line input's coloured host must hug its control. Anything much
    /// taller is the grey-block defect returning.
    /// </summary>
    [Theory]
    [InlineData("txtDownloadFolder")]
    [InlineData("txtAssetRoot")]
    [InlineData("txtAssetFolderName")]
    public void SingleLineFieldHosts_HugTheirControl(string inputName)
    {
        RunOnSta(() =>
        {
            using var w = new TestWorkspace();
            using var form = CreateForm(w);
            form.Show();
            try
            {
                var input = Find<TextBox>(form, inputName);
                var host = input.Parent;
                Assert.NotNull(host);

                // Host = control height + 2px padding top/bottom. Allow generous
                // slack for DPI, but nothing like a full stretched row.
                Assert.True(
                    host!.Height <= input.PreferredHeight + 16,
                    $"{inputName} host is {host.Height}px tall for a {input.PreferredHeight}px control - grey block defect.");
            }
            finally { form.Hide(); }
        });
    }

    /// <summary>
    /// Usability bar at the MINIMUM supported size: the workflow must remain
    /// reachable through the workspace scrollbar, rather than silently clipping
    /// lower controls. The preferred layout intentionally exceeds this height.
    /// </summary>
    [Fact]
    public void AllKeyControlsRemainReachableAtMinimumWindowSize()
    {
        RunOnSta(() =>
        {
            using var w = new TestWorkspace();
            using var form = CreateForm(w);
            form.Size = form.MinimumSize;
            form.Show();
            form.PerformLayout();
            try
            {
                if (!ReachedRequestedSize(form, form.MinimumSize))
                {
                    // Headless/low-resolution agent: the window never got the size
                    // under test, so any geometry assertion here would be noise.
                    return;
                }

                string[] mustBeVisible =
                {
                    "txtDownloadFolder", "txtAssetRoot", "cmbProvider", "txtAssetFolderName",
                    "chkNoReference", "chkDirectMode", "chkKeepSettings", "cmbVariants",
                    "btnRefreshReference", "btnChooseReference", "btnReference",
                    "btnRefreshMain", "btnChooseMain", "txtPrompt", "btnMainImage",
                    "btnOpenAssetFolder", "btnCancel", "lvRecentDocuments"
                };

                Assert.True(form.AutoScroll);
                Assert.True(form.VerticalScroll.Visible);

                foreach (var name in mustBeVisible)
                {
                    var c = form.Controls.Find(name, true).FirstOrDefault();
                    Assert.True(c is not null, $"Control '{name}' not found.");
                    Assert.True(c!.Height > 0 && c.Width > 0, $"'{name}' collapsed to zero size.");
                }
            }
            finally { form.Hide(); }
        });
    }

    /// <summary>
    /// At the DEFAULT window size the cards - the actual workspace - must get a
    /// comfortable share of the window rather than being squeezed by chrome.
    /// </summary>
    [Fact]
    public void CardsGetTheMostSpaceAtDefaultWindowSize()
    {
        RunOnSta(() =>
        {
            using var w = new TestWorkspace();
            using var form = CreateForm(w);
            var requested = form.Size;
            form.Show();
            form.PerformLayout();
            try
            {
                if (!ReachedRequestedSize(form, requested))
                {
                    return;
                }

                var cards = Find<TableLayoutPanel>(form, "pnlCardsContainer");
                var settings = Find<GroupBox>(form, "grpSettings");
                var status = Find<GroupBox>(form, "grpStatus");

                Assert.True(cards.Height >= 260,
                    $"Cards are only {cards.Height}px at the default size.");
                Assert.True(cards.Height > settings.Height,
                    $"Settings ({settings.Height}px) takes more room than the workspace ({cards.Height}px).");
                Assert.True(cards.Height > status.Height,
                    $"Status ({status.Height}px) takes more room than the workspace ({cards.Height}px).");
            }
            finally { form.Hide(); }
        });
    }

    /// <summary>
    /// The settings block is chrome, not the workspace: it must not eat a large
    /// share of the window.
    /// </summary>
    [Fact]
    public void SettingsGroupDoesNotDominateTheWindow()
    {
        RunOnSta(() =>
        {
            using var w = new TestWorkspace();
            using var form = CreateForm(w);
            form.Size = form.MinimumSize;
            form.Show();
            form.PerformLayout();
            try
            {
                var settings = Find<GroupBox>(form, "grpSettings");
                Assert.True(settings.Height <= form.ClientSize.Height * 0.30,
                    $"Settings group is {settings.Height}px of {form.ClientSize.Height}px client height.");
            }
            finally { form.Hide(); }
        });
    }
}
