using System.Drawing;
using FileViewer.Controls;

namespace FileViewer.Forms;

/// <summary>
/// Dialog for configuring logical CPU affinity for a process.
/// </summary>
public sealed class AffinityDialog : Form
{
    private readonly CheckedListBox _clbCores;
    private readonly CheckBox _chkSelectAll;
    private readonly Button _btnOk;
    private readonly Button _btnCancel;

    public long SelectedMask { get; private set; }

    public AffinityDialog(string processName, int pid, long currentMask)
    {
        Text = $"CPU Affinity - {processName} (PID {pid})";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 360);

        var lblHeader = new Label
        {
            Text = "Which logical processors can run this process?",
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 10, 12, 4),
            AutoEllipsis = true
        };

        _chkSelectAll = new CheckBox
        {
            Text = "Select all processors",
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(12, 4, 12, 0),
            UseVisualStyleBackColor = true
        };

        _clbCores = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            MultiColumn = Environment.ProcessorCount > 8,
            ColumnWidth = 110
        };

        int cpuCount = Math.Min(64, Environment.ProcessorCount);
        long effectiveMask = currentMask <= 0 ? ((1L << Math.Min(cpuCount, 63)) - 1) : currentMask;

        bool allChecked = true;
        for (int i = 0; i < cpuCount; i++)
        {
            bool isSet = (effectiveMask & (1L << i)) != 0;
            if (!isSet)
            {
                allChecked = false;
            }
            _clbCores.Items.Add($"CPU {i}", isSet);
        }

        _chkSelectAll.Checked = allChecked;
        _chkSelectAll.CheckedChanged += (_, _) =>
        {
            bool target = _chkSelectAll.Checked;
            for (int i = 0; i < _clbCores.Items.Count; i++)
            {
                _clbCores.SetItemChecked(i, target);
            }
        };

        var pnlListContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 4, 12, 8)
        };
        pnlListContainer.Controls.Add(_clbCores);

        var pnlButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 8)
        };

        _btnCancel = new Button
        {
            Text = "Cancel",
            Width = 90,
            Height = 28,
            DialogResult = DialogResult.Cancel,
            UseVisualStyleBackColor = true
        };

        _btnOk = new Button
        {
            Text = "OK",
            Width = 90,
            Height = 28,
            UseVisualStyleBackColor = true
        };
        _btnOk.Click += BtnOk_Click;

        pnlButtons.Controls.Add(_btnCancel);
        pnlButtons.Controls.Add(_btnOk);

        Controls.Add(pnlListContainer);
        Controls.Add(_chkSelectAll);
        Controls.Add(lblHeader);
        Controls.Add(pnlButtons);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
        WorkspaceTheme.Apply(this);
        WorkspaceTheme.StyleButton(_btnOk, primary: true);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        long mask = 0;
        for (int i = 0; i < _clbCores.Items.Count; i++)
        {
            if (_clbCores.GetItemChecked(i))
            {
                mask |= 1L << i;
            }
        }

        if (mask == 0)
        {
            MessageBox.Show(
                this,
                "Select at least one logical processor.",
                "Invalid Affinity",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SelectedMask = mask;
        DialogResult = DialogResult.OK;
        Close();
    }
}
