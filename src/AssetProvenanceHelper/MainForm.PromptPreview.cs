#nullable enable
using System.Drawing;

namespace AssetProvenanceHelper;

partial class MainForm
{
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

    }
}
