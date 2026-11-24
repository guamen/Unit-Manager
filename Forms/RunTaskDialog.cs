using System.Drawing;
using FileViewer.Controls;

namespace FileViewer.Forms;

/// <summary>
/// Dialog to launch a new executable or command, optionally elevated.
/// </summary>
public sealed class RunTaskDialog : Form
{
    private readonly TextBox _txtCommand;
    private readonly TextBox _txtArguments;
    private readonly CheckBox _chkElevated;

    public string CommandText => _txtCommand.Text.Trim();

    public string ArgumentsText => _txtArguments.Text.Trim();

    public bool RunAsAdministrator => _chkElevated.Checked;

    public RunTaskDialog()
    {
        Text = "Run New Task";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(480, 210);

        var lblDesc = new Label
        {
            Text = "Enter the name of a program, folder, document, or internet resource for Windows to open.",
            Location = new Point(14, 14),
            Size = new Size(450, 34)
        };

        var lblCmd = new Label
        {
            Text = "Open:",
            Location = new Point(14, 58),
            AutoSize = true
        };

        _txtCommand = new TextBox
        {
            Location = new Point(92, 55),
            Size = new Size(286, 23),
            PlaceholderText = "e.g. cmd.exe, powershell, notepad, regedit"
        };

        var btnBrowse = new Button
        {
            Text = "Browse...",
            Location = new Point(384, 54),
            Size = new Size(80, 25),
            UseVisualStyleBackColor = true
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select Program or File",
                Filter = "Programs (*.exe;*.bat;*.cmd;*.msc)|*.exe;*.bat;*.cmd;*.msc|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtCommand.Text = dlg.FileName;
            }
        };

        var lblArgs = new Label
        {
            Text = "Arguments:",
            Location = new Point(14, 92),
            AutoSize = true
        };

        _txtArguments = new TextBox
        {
            Location = new Point(92, 89),
            Size = new Size(372, 23),
            PlaceholderText = "Optional arguments"
        };

        _chkElevated = new CheckBox
        {
            Text = "Run as administrator",
            Location = new Point(92, 124),
            AutoSize = true,
            UseVisualStyleBackColor = true
        };

        var btnOk = new Button
        {
            Text = "OK",
            Location = new Point(296, 165),
            Size = new Size(80, 28),
            UseVisualStyleBackColor = true
        };
        btnOk.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_txtCommand.Text))
            {
                MessageBox.Show(this, "Enter a valid command or path.", "Required Field", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(384, 165),
            Size = new Size(80, 28),
            DialogResult = DialogResult.Cancel,
            UseVisualStyleBackColor = true
        };

        Controls.AddRange([lblDesc, lblCmd, _txtCommand, btnBrowse, lblArgs, _txtArguments, _chkElevated, btnOk, btnCancel]);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        WorkspaceTheme.Apply(this);
        WorkspaceTheme.StyleButton(btnOk, primary: true);
    }
}
