using System.Drawing;
using System.Windows.Forms;

namespace AssetProvenanceHelper.Dialogs;

public sealed class TwoChoiceDialog : Form
{
    private bool _primarySelected;

    internal TwoChoiceDialog(
        string title,
        string message,
        string primaryText,
        string secondaryText)
    {
        Text =
            title;

        StartPosition =
            FormStartPosition.CenterParent;

        FormBorderStyle =
            FormBorderStyle.FixedDialog;

        MinimizeBox =
            false;

        MaximizeBox =
            false;

        ShowInTaskbar =
            false;

        ControlBox =
            false;

        Width =
            560;

        Height =
            230;

        var root =
            new TableLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                ColumnCount =
                    1,

                RowCount =
                    2,

                Padding =
                    new Padding(16)
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100));

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        var messageLabel =
            new Label
            {
                Text =
                    message,

                Dock =
                    DockStyle.Fill,

                AutoSize =
                    false
            };

        var buttons =
            new FlowLayoutPanel
            {
                Dock =
                    DockStyle.Fill,

                FlowDirection =
                    FlowDirection.RightToLeft,

                AutoSize =
                    true
            };

        var primary =
            new Button
            {
                Text =
                    primaryText,

                AutoSize =
                    true,

                Padding =
                    new Padding(
                        10,
                        4,
                        10,
                        4)
            };

        var secondary =
            new Button
            {
                Text =
                    secondaryText,

                AutoSize =
                    true,

                Padding =
                    new Padding(
                        10,
                        4,
                        10,
                        4)
            };

        primary.Click +=
            (_, _) =>
            {
                _primarySelected =
                    true;

                DialogResult =
                    DialogResult.OK;

                Close();
            };

        secondary.Click +=
            (_, _) =>
            {
                _primarySelected =
                    false;

                DialogResult =
                    DialogResult.Cancel;

                Close();
            };

        buttons.Controls.Add(
            primary);

        buttons.Controls.Add(
            secondary);

        root.Controls.Add(
            messageLabel,
            0,
            0);

        root.Controls.Add(
            buttons,
            0,
            1);

        Controls.Add(
            root);

        AcceptButton =
            primary;

        CancelButton =
            secondary;
    }

    [ThreadStatic]
    internal static Func<IWin32Window, string, string, string, string, bool>? CustomChoiceProvider;

    public static bool ShowChoice(
        IWin32Window owner,
        string title,
        string message,
        string primaryText,
        string secondaryText)
    {
        if (CustomChoiceProvider is not null)
        {
            return CustomChoiceProvider(
                owner,
                title,
                message,
                primaryText,
                secondaryText);
        }

        using var dialog =
            new TwoChoiceDialog(
                title,
                message,
                primaryText,
                secondaryText);

        dialog.ShowDialog(
            owner);

        return dialog._primarySelected;
    }
}
