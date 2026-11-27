using FileViewer.Controls;
using FileViewer.Models;

namespace FileViewer.Forms;

internal sealed class NetworkDiagnosisDialog : Form
{
    private static readonly Font BoldCellFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly NetworkDiagnosisReport _report;
    private readonly Button _btnCopy = new() { Text = "Copy Report", AutoSize = true };
    private readonly Button _btnSave = new() { Text = "Save Report (.txt)...", AutoSize = true };
    private readonly Button _btnClose = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };

    public NetworkDiagnosisDialog(NetworkDiagnosisReport report)
    {
        _report = report;
        Text = "Network & Internet Diagnosis Report";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1020, 700);
        MinimumSize = new Size(780, 520);
        Padding = new Padding(12);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 1. Executive Summary Card
        var summaryPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 10),
            BorderStyle = BorderStyle.FixedSingle
        };

        var summaryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3
        };

        var lblHeader = new Label
        {
            AutoSize = true,
            Font = WorkspaceTheme.Heading,
            Text = "Diagnostic Verdict",
            Margin = new Padding(0, 0, 0, 4)
        };

        var lblSummary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(940, 0),
            Text = report.ExecutiveSummary,
            Margin = new Padding(0, 0, 0, 4)
        };

        string windowsLine = report.DetectedSlowWindows.Count > 0
            ? $"Detected slow window(s): {string.Join("  |  ", report.DetectedSlowWindows)}"
            : $"Analyzed dataset: {report.TimeRangeDisplay}";
        var lblMeta = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(940, 0),
            ForeColor = SystemInformation.HighContrast ? SystemColors.GrayText : WorkspaceTheme.Muted,
            Text = windowsLine
        };

        summaryLayout.Controls.Add(lblHeader, 0, 0);
        summaryLayout.Controls.Add(lblSummary, 0, 1);
        summaryLayout.Controls.Add(lblMeta, 0, 2);
        summaryPanel.Controls.Add(summaryLayout);

        // 2. Tabbed Details (Findings, Hourly Breakdown, Full Report)
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var tabFindings = new TabPage("Findings & Causes") { Padding = new Padding(6) };
        tabFindings.Controls.Add(BuildFindingsGrid(report.Findings));

        var tabHourly = new TabPage($"Hourly Pattern ({report.HourlyBreakdown.Count}h)") { Padding = new Padding(6) };
        tabHourly.Controls.Add(BuildHourlyGrid(report.HourlyBreakdown));

        var tabText = new TabPage("Live Probe & Full Report") { Padding = new Padding(6) };
        var txtReport = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Text = report.FullTextReport
        };
        tabText.Controls.Add(txtReport);

        tabs.TabPages.AddRange([tabFindings, tabHourly, tabText]);

        // 3. Footer buttons
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        footer.Controls.AddRange([_btnClose, _btnSave, _btnCopy]);

        _btnCopy.Click += (_, _) => CopyReport();
        _btnSave.Click += (_, _) => SaveReport();
        AcceptButton = _btnClose;
        CancelButton = _btnClose;

        root.Controls.Add(summaryPanel, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        WorkspaceTheme.Apply(this);
        WorkspaceTheme.StyleButton(_btnClose, primary: true);
    }

    private static DataGridView BuildFindingsGrid(IReadOnlyList<DiagnosticFinding> findings)
    {
        var grid = CreateBaseGrid();
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Severity", Width = 78 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Category", Width = 165 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Diagnosis", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Evidence", Width = 250, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Recommended Action", Width = 260, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 190 });

        foreach (DiagnosticFinding f in findings)
        {
            int idx = grid.Rows.Add(f.SeverityDisplay, f.Category, f.Title, f.Evidence, f.Recommendation);
            if (!SystemInformation.HighContrast)
            {
                DataGridViewCell sevCell = grid.Rows[idx].Cells[0];
                sevCell.Style.Font = BoldCellFont;
                if (f.Severity == DiagnosticSeverity.Critical)
                {
                    sevCell.Style.ForeColor = Color.FromArgb(185, 28, 28);
                    grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
                }
                else if (f.Severity == DiagnosticSeverity.Warning)
                {
                    sevCell.Style.ForeColor = Color.FromArgb(180, 83, 9);
                    grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(255, 251, 235);
                }
                else
                {
                    sevCell.Style.ForeColor = WorkspaceTheme.Accent;
                }
            }
        }

        return grid;
    }

    private static DataGridView BuildHourlyGrid(IReadOnlyList<HourlyNetworkSummary> hourly)
    {
        var grid = CreateBaseGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Hour Window", Width = 115 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Samples", Width = 72 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Avg Speed", Width = 92 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Min Speed", Width = 92 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local Down", Width = 92 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local Up", Width = 84 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Router Ping", Width = 86 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Internet Ping", Width = 92 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Loss %", Width = 66 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Dominant Process", Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Window Flag", Width = 105 });

        if (hourly.Count == 0)
        {
            grid.Rows.Add("—", "0", "—", "—", "—", "—", "—", "—", "—",
                "Enable the 5-second diagnostic log to collect hourly patterns (e.g., 11:00–16:00).", "No history");
            return grid;
        }

        foreach (HourlyNetworkSummary h in hourly)
        {
            int idx = grid.Rows.Add(
                h.HourRangeDisplay,
                h.SampleCount.ToString("N0"),
                h.AvgInternetMbps.HasValue ? $"{h.AvgInternetMbps.Value:0.1} Mbps" : "—",
                h.MinInternetMbps.HasValue ? $"{h.MinInternetMbps.Value:0.1} Mbps" : "—",
                $"{h.AvgLocalDownMbps:0.1} Mbps",
                $"{h.AvgLocalUpMbps:0.1} Mbps",
                h.AvgGatewayMs.HasValue ? $"{h.AvgGatewayMs.Value:0.1} ms" : "—",
                h.AvgInternetMs.HasValue ? $"{h.AvgInternetMs.Value:0.1} ms" : "—",
                $"{h.AvgPacketLossPercent:0.#}%",
                h.DominantProcess,
                h.IsSlowWindow ? "SLOW WINDOW" : "Normal");

            if (h.IsSlowWindow && !SystemInformation.HighContrast)
            {
                grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
                grid.Rows[idx].Cells[10].Style.ForeColor = Color.FromArgb(185, 28, 28);
                grid.Rows[idx].Cells[10].Style.Font = BoldCellFont;
            }
        }

        return grid;
    }

    private static DataGridView CreateBaseGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(_report.FullTextReport);
            MessageBox.Show(this, "Diagnostic report copied to clipboard.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not copy to clipboard: {ex.Message}", "Copy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveReport()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save Network Diagnostic Report",
            Filter = "Text Report (*.txt)|*.txt|All Files (*.*)|*.*",
            FileName = $"network-diagnosis-{_report.GeneratedAt:yyyy-MM-dd-HHmm}.txt"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _report.FullTextReport);
            MessageBox.Show(this, $"Report saved to:\n{dialog.FileName}", "Report Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save report: {ex.Message}", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
