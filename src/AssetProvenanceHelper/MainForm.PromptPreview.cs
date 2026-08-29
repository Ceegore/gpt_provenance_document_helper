#nullable enable
using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private PromptPreviewOverlayControl _promptOverlay = null!;
    private System.Windows.Forms.Timer? _promptOverlayTimer;

    internal static Func<Point>? CursorPositionProvider;

    internal static string BuildPromptPreview(
        string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return "No prompt stored.";
        }

        var wasTruncated =
            prompt.Length > 100;

        var slice =
            wasTruncated
                ? prompt[..100]
                : prompt;

        var display =
            slice
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');

        return wasTruncated
            ? display + "..."
            : display;
    }

    private void UpdatePromptPreview()
    {
        if (IsDisposed
            || Disposing
            || lblPromptPreview.IsDisposed)
        {
            return;
        }

        lblPromptPreview.Text =
            BuildPromptPreview(
                txtPrompt.Text);

        lblPromptPreview.ForeColor =
            string.IsNullOrEmpty(txtPrompt.Text)
                ? Color.Gray
                : SystemColors.ControlText;

        if (_promptOverlay is not null
            && _promptOverlay.Visible)
        {
            _promptOverlay.SetPromptText(txtPrompt.Text);
        }
    }

    private void BuildPromptOverlay()
    {
        _promptOverlay = new PromptPreviewOverlayControl();
        _promptOverlay.Name = "promptOverlay";
        Controls.Add(_promptOverlay);
        _promptOverlay.BringToFront();

        _promptOverlay.CloseRequested += (_, _) => HidePromptOverlay();

        lblPromptPreview.MouseEnter += (_, _) => ShowPromptOverlay();
        lblPromptPreview.MouseLeave += (_, _) => StartPromptOverlayCloseTimer();

        _promptOverlayTimer = components != null
            ? new System.Windows.Forms.Timer(components)
            : new System.Windows.Forms.Timer();
        _promptOverlayTimer.Interval = 100;
        _promptOverlayTimer.Tick += (_, _) =>
        {
            if (IsCursorOverPreviewOrOverlay())
            {
                return;
            }

            HidePromptOverlay();
        };
    }

    private void ShowPromptOverlay()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        _promptOverlay.SetPromptText(txtPrompt.Text);
        PositionPromptOverlay();

        _promptOverlay.ShowOverlay();

        if (_promptOverlayTimer is not null)
        {
            _promptOverlayTimer.Stop();
            _promptOverlayTimer.Start();
        }
    }

    private void StartPromptOverlayCloseTimer()
    {
        if (_promptOverlayTimer is null)
        {
            return;
        }

        _promptOverlayTimer.Stop();
        _promptOverlayTimer.Start();
    }

    private void HidePromptOverlay()
    {
        if (_promptOverlayTimer is not null)
        {
            _promptOverlayTimer.Stop();
        }

        _promptOverlay.HideOverlay();
    }

    private bool IsCursorOverPreviewOrOverlay()
    {
        var cursor = CursorPositionProvider?.Invoke() ?? Cursor.Position;

        var previewBounds =
            lblPromptPreview.RectangleToScreen(
                lblPromptPreview.ClientRectangle);

        if (previewBounds.Contains(cursor))
        {
            return true;
        }

        if (_promptOverlay is not null
            && _promptOverlay.Visible)
        {
            var overlayBounds =
                _promptOverlay.RectangleToScreen(
                    _promptOverlay.ClientRectangle);

            if (overlayBounds.Contains(cursor))
            {
                return true;
            }
        }

        return false;
    }

    private void PositionPromptOverlay()
    {
        var previewScreen =
            lblPromptPreview.RectangleToScreen(
                lblPromptPreview.ClientRectangle);

        var overlaySize =
            _promptOverlay.GetPreferredSize();

        var formWidth =
            ClientSize.Width;

        var formHeight =
            ClientSize.Height;

        var width =
            Math.Min(
                overlaySize.Width,
                formWidth - 40);

        var height =
            Math.Min(
                overlaySize.Height,
                formHeight - 40);

        var previewClient =
            PointToClient(
                new Point(
                    previewScreen.Left,
                    previewScreen.Bottom));

        var left =
            Math.Max(
                8,
                Math.Min(
                    previewClient.X,
                    formWidth - width - 8));

        var top =
            Math.Max(
                8,
                Math.Min(
                    previewClient.Y + 4,
                    formHeight - height - 8));

        _promptOverlay.SetBounds(
            left,
            top,
            width,
            height);
    }
}