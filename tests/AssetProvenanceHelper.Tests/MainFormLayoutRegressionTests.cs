#nullable enable
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Numeric guards for layout defects found in a visual audit of v1.5.3. Each
/// one failed before its fix, and each describes a defect that is invisible to
/// behavioural tests because nothing throws - the window merely renders wrong.
///
/// To re-run that audit, set APH_SHOT_DIR to an output folder and the capture
/// test at the bottom writes a PNG of every state; it is skipped otherwise, so
/// it costs nothing in CI.
/// </summary>
public sealed class MainFormLayoutRegressionTests
{
    private static void RunOnSta(Action action)
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
                action();
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
                MainForm.OpenFileDialogProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)));
        if (error is not null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateShownForm(TestWorkspace workspace)
    {
        var form = new MainForm(
            workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService(),
            workspace.CreateProviderTemplateCatalogService(),
            workspace.CreateRecentDocumentHistoryService(),
            workspace.CreateRequestProgressService(),
            null,
            null,
            null,
            null,
            workspace.CreateRequestQueueStateService(),
            workspace.CreatePixelExactBatchStateService());
        form.Size = new Size(1500, 1020);
        form.Show();
        Settle();
        return form;
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }
    }

    private static T Find<T>(Control root, string name)
        where T : Control
    {
        var found = root.Controls.Find(name, true).FirstOrDefault();
        Assert.True(found is not null, $"Control '{name}' not found.");
        return Assert.IsAssignableFrom<T>(found);
    }

    /// <summary>
    /// The Current Asset group and its Fill-docked child sized themselves from
    /// each other and settled 88px too tall. Every one of those pixels came out
    /// of the image cards, which are the only Percent row below it.
    /// </summary>
    [Theory]
    [InlineData(1500, 1020)]
    [InlineData(1120, 760)]
    [InlineData(1040, 640)]
    public void CurrentAssetGroup_HasNoDeadSpaceAndNeverOverlapsTheCards(int width, int height)
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateShownForm(workspace);
            form.Size = new Size(width, height);
            Settle();

            var group = Find<GroupBox>(form, "grpCurrentAsset");
            var modes = Find<FlowLayoutPanel>(form, "pnlModeFlow");

            var modesBottomInGroup = group.RectangleToClient(modes.RectangleToScreen(modes.ClientRectangle)).Bottom;
            var slack = group.ClientSize.Height - modesBottomInGroup;
            Assert.True(
                slack < 40,
                $"Current Asset has {slack}px of dead space under the mode row at {width}x{height} (group {group.Height}px).");

            // The group's height is set from its content, so it must still end
            // above the cards row rather than bleeding over the Main Image
            // caption underneath it.
            var cards = Find<Control>(form, "pnlCardsContainer");
            var groupBottom = group.RectangleToScreen(group.ClientRectangle).Bottom;
            var cardsTop = cards.RectangleToScreen(cards.ClientRectangle).Top;
            Assert.True(
                groupBottom <= cardsTop,
                $"Current Asset overlaps the cards by {groupBottom - cardsTop}px at {width}x{height}.");
        });
    }

    /// <summary>
    /// The prompt buttons used to live inside a percent-sized container. When
    /// that row was squeezed the container overlapped its own AutoSize rows,
    /// drawing the buttons through the prompt text and, when squeezed further,
    /// giving them zero height.
    /// </summary>
    [Theory]
    [InlineData(1500, 1020)]
    [InlineData(1120, 760)]
    [InlineData(1040, 640)]
    public void PromptButtons_StayVisibleAndClearOfThePromptBox(int width, int height)
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateShownForm(workspace);
            form.Size = new Size(width, height);
            Settle();

            var buttons = Find<FlowLayoutPanel>(form, "pnlPromptButtons");
            var paste = Find<Button>(form, "btnPasteClipboard");
            var promptHost = Find<TextBox>(form, "txtPrompt").Parent!;

            Assert.True(buttons.Height > 0, $"Prompt button row collapsed to {buttons.Height}px at {width}x{height}.");
            Assert.True(paste.Height > 0, $"Paste button collapsed to {paste.Height}px at {width}x{height}.");

            var buttonRect = buttons.RectangleToScreen(buttons.ClientRectangle);
            var promptRect = promptHost.RectangleToScreen(promptHost.ClientRectangle);
            Assert.False(
                buttonRect.IntersectsWith(promptRect),
                $"Prompt buttons overlap the prompt box at {width}x{height}: {buttonRect} vs {promptRect}.");
        });
    }

    /// <summary>
    /// The overlay was docked Fill next to a Top-docked workspace, so it only
    /// received the leftover strip below it - 61px against a 560px content
    /// panel, which clipped every line of help text away.
    /// </summary>
    [Fact]
    public void HelpOverlay_CoversTheClientAreaAndShowsItsContent()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateShownForm(workspace);

            typeof(MainForm)
                .GetMethod("ShowHelpOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, null);
            Settle();

            var overlay = Find<Control>(form, "helpOverlay");
            Assert.True(overlay.Visible, "Help overlay is not visible after ShowHelpOverlay.");
            Assert.Equal(form.ClientRectangle, overlay.Bounds);

            var content = overlay.Controls.Cast<Control>().OrderByDescending(c => c.Height).First();
            Assert.True(
                content.Bottom <= overlay.ClientSize.Height,
                $"Help content ({content.Bounds}) is clipped by the overlay ({overlay.ClientSize}).");
        });
    }

    /// <summary>
    /// A 380px queue column left the Asset column ~150px, which truncated every
    /// real asset name to the same shared prefix.
    /// </summary>
    [Fact]
    public void RequestQueue_AssetColumnFitsRealAssetNamesWithoutHorizontalScrolling()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateShownForm(workspace);

            var queue = Find<ListView>(form, "lvRequestQueue");
            var assetColumn = queue.Columns[1].Width;
            var total = queue.Columns.Cast<ColumnHeader>().Sum(c => c.Width);

            using var graphics = queue.CreateGraphics();
            var sampleWidth = graphics.MeasureString("asset_card_gas_station_probenlager_029", queue.Font).Width;

            Assert.True(
                assetColumn >= sampleWidth,
                $"Asset column is {assetColumn}px but a real asset name needs {(int)sampleWidth}px.");
            Assert.True(
                total <= queue.ClientSize.Width,
                $"Queue columns total {total}px against a {queue.ClientSize.Width}px client area, forcing a horizontal scrollbar.");
        });
    }

    /// <summary>Opt-in visual audit; skipped unless APH_SHOT_DIR is set.</summary>
    [Fact]
    public void CaptureScreenStatesForVisualAudit()
    {
        var directory = Environment.GetEnvironmentVariable("APH_SHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        RunOnSta(() =>
        {
            Directory.CreateDirectory(directory);
            using var workspace = new TestWorkspace();
            using var form = CreateShownForm(workspace);
            GuiCapture.Save(form, Path.Combine(directory, "10-idle.png"));

            var manifestPath = Path.Combine(workspace.Root, "audit-manifest.json");
            File.WriteAllText(manifestPath, BuildAuditManifest());
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            typeof(MainForm)
                .GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, null);
            Settle();
            GuiCapture.Save(form, Path.Combine(directory, "11-queue-imported.png"));

            var queue = Find<ListView>(form, "lvRequestQueue");
            form.HandleRequestQueueItemActivate(queue.Items[1]);
            Settle();
            GuiCapture.Save(form, Path.Combine(directory, "12-collection-row-active.png"));

            foreach (var size in new[] { new Size(1120, 760), new Size(1040, 640) })
            {
                form.Size = size;
                Settle();
                GuiCapture.Save(form, Path.Combine(directory, $"13-window-{size.Width}x{size.Height}.png"));
            }

            form.Size = new Size(1500, 1020);
            Settle();
            typeof(MainForm)
                .GetMethod("ShowHelpOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, null);
            Settle();
            GuiCapture.Save(form, Path.Combine(directory, "14-help-overlay.png"));
        });
    }

    private static string BuildAuditManifest()
    {
        var rows = new List<string>
        {
            Row("asset_card_bureau_field_lab_001.png", "Seed. FLOWMETA: SERIE=bureau_field_lab_dekontamination; SERIENGROESSE=4; NEXT=Ref3. PROZESSMARKER: Einzeln"),
            Row("asset_card_bureau_field_lab_013.png", "Collection. FLOWMETA: SERIE=bureau_field_lab_dekontamination; OUTPUT_COUNT=3. PROZESSMARKER: Ref3"),
            Row("asset_card_bureau_field_lab_025.png", "Map two. FLOWMETA: SERIE=bureau_field_lab_dekontamination; OUTPUT_INDEX=2; MASTER=Ref3. PROZESSMARKER: AusRef3"),
            Row("asset_card_bureau_field_lab_037.png", "Map three. FLOWMETA: SERIE=bureau_field_lab_dekontamination; OUTPUT_INDEX=3; MASTER=Ref3. PROZESSMARKER: AusRef3")
        };
        for (var i = 0; i < 30; i++)
        {
            rows.Add(Row($"asset_card_gas_station_probenlager_{i:D3}.png", $"An ordinary single request number {i}."));
        }
        return "{ \"manifestVersion\": 2, \"assets\": [" + string.Join(",", rows) + "] }";

        static string Row(string file, string prompt) =>
            $"{{ \"filename\": \"{file}\", \"resolution\": \"768x1024\", \"alpha\": \"not_required\", \"prompt\": \"{prompt}\" }}";
    }
}
