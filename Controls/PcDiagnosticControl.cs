using System.ComponentModel;
using System.Diagnostics;
using FileViewer.Models;
using FileViewer.Services;

namespace FileViewer.Controls;

internal sealed class PcDiagnosticControl : UserControl
{
    private sealed record Recommendation(ProcessInfoItem Process, string Reason, bool LowerPriority);

    private readonly Button _analyze = new() { Text = "Analyze now", AutoSize = true };
    private readonly Button _apply = new() { Text = "Apply recommendation", AutoSize = true, Enabled = false };
    private int _sortColumn = 2;
    private bool _sortDescending = true;
    private readonly CheckBox _servicesOnly = new() { Text = "Only processes with services", AutoSize = true };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
    private readonly DataGridView _processes = CreateGrid();
    private readonly DataGridView _recommendations = CreateGrid();
    private readonly int _sessionId;
    private CancellationTokenSource? _cancellation;
    private PcDiagnosticResult? _result;

    public Task? PendingAnalysis { get; private set; }

    public PcDiagnosticControl()
    {
        using var currentProcess = Process.GetCurrentProcess();
        _sessionId = currentProcess.SessionId;
        Dock = DockStyle.Fill;
        AddColumn(_processes, "Process", 130, fill: true);
        AddColumn(_processes, "PID", 60);
        AddColumn(_processes, "CPU", 74);
        AddColumn(_processes, "RAM", 88);
        AddColumn(_processes, "Disk Read", 98);
        AddColumn(_processes, "Disk Write", 104);
        AddColumn(_processes, "Network In", 98);
        AddColumn(_processes, "Network Out", 98);
        AddColumn(_processes, "Est. Internet", 96);
        AddColumn(_processes, "Hosted Services", 170, fill: true);
        AddColumn(_recommendations, "Application", 140);
        AddColumn(_recommendations, "Observed Usage", 220, fill: true);
        AddColumn(_recommendations, "Action", 210);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
        toolbar.Controls.AddRange([_analyze, _servicesOnly]);
        if (!ProcessMonitorService.IsCurrentProcessElevated())
        {
            var elevate = new Button { Text = "Run as administrator", AutoSize = true };
            elevate.Click += (_, _) => OpenElevatedDiagnostic();
            toolbar.Controls.Add(elevate);
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
        actions.Controls.Add(_apply);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_summary, 0, 1);
        layout.Controls.Add(_processes, 0, 2);
        layout.Controls.Add(new Label { Text = "Recommendations", AutoSize = true,
            Font = WorkspaceTheme.Heading, Margin = new Padding(4, 10, 4, 4) }, 0, 3);
        layout.Controls.Add(_recommendations, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(_status, 0, 6);
        Controls.Add(layout);
        _summary.Text = "Take a five-second sample of resource usage.";

        _analyze.Click += async (_, _) =>
        {
            if (PendingAnalysis is { IsCompleted: false })
            {
                CancelAnalysis();
                return;
            }
            PendingAnalysis = AnalyzeAsync();
            await PendingAnalysis;
        };
        _processes.ColumnHeaderMouseClick += (_, e) =>
        {
            if (_sortColumn == e.ColumnIndex)
                _sortDescending = !_sortDescending;
            else
            {
                _sortColumn = e.ColumnIndex;
                _sortDescending = _sortColumn is not (0 or 9);
            }
            ShowProcesses();
        };
        _servicesOnly.CheckedChanged += (_, _) => ShowProcesses();
        _recommendations.SelectionChanged += (_, _) => UpdateApplyButton();
        _recommendations.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _apply.Enabled)
                ApplyRecommendation();
        };
        _apply.Click += (_, _) => ApplyRecommendation();
    }

    public void CancelAnalysis() => _cancellation?.Cancel();

    public void StylePrimaryAction() => WorkspaceTheme.StyleButton(_analyze, primary: true);

    private void OpenElevatedDiagnostic()
    {
        try
        {
            Program.RestartAsAdministrator(Program.DiagnosticArgument);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _status.Text = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "The administrator permission request was canceled."
                : $"Could not open Unit Manager as administrator. {ex.Message}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CancelAnalysis();
        base.Dispose(disposing);
    }

    private async Task AnalyzeAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _analyze.Text = "Cancel";
        _status.Text = "Measuring resource usage…";
        UpdateApplyButton();
        try
        {
            _result = await PcDiagnosticService.AnalyzeAsync(cancellation.Token);
            if (IsDisposed)
                return;
            var system = _result.System;
            string disk = _result.HasTrace ? "" : " · Disk and network data unavailable";
            _summary.Text = $"CPU {system.TotalCpuPercent:0.0} %  ·  RAM {system.MemoryUsagePercent:0.0} % "
                + $"({FileSystemItem.FormatBytes(system.UsedPhysicalMemoryBytes)} of {FileSystemItem.FormatBytes(system.TotalPhysicalMemoryBytes)})"
                + $"  · {_result.Processes.Length} processes{disk}";
            ShowProcesses();
            ShowRecommendations();
            _status.Text = _result.TraceStatus + " Each row shows usage for the PID, including any services it hosts.";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                _status.Text = "Measurement canceled." + (_result is null ? "" : " The previous sample is still displayed.");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _status.Text = $"Could not complete the diagnosis. {ex.Message}";
        }
        finally
        {
            _cancellation = null;
            if (!IsDisposed)
            {
                _analyze.Text = "Analyze now";
                UpdateApplyButton();
            }
        }
    }

    private void ShowProcesses()
    {
        foreach (DataGridViewColumn column in _processes.Columns)
            column.HeaderCell.SortGlyphDirection = column.Index != _sortColumn ? SortOrder.None
                : _sortDescending ? SortOrder.Descending : SortOrder.Ascending;

        if (_result is null)
            return;
        double? Rank(DiagnosticProcess row) => _sortColumn switch
        {
            1 => row.Process.Pid,
            2 => row.Cpu,
            3 => row.Process.WorkingSetBytes,
            4 => row.DiskRead / row.Seconds,
            5 => row.DiskWrite / row.Seconds,
            6 => row.Received / row.Seconds,
            7 => row.Sent / row.Seconds,
            _ => row.InternetRate
        };
        string TextKey(DiagnosticProcess row) => _sortColumn == 0 ? row.Process.Name : row.Services;
        var visible = _result.Processes.Where(row => !_servicesOnly.Checked || row.Services.Length > 0);
        var ordered = _sortColumn is 0 or 9
            ? (_sortDescending ? visible.OrderByDescending(TextKey, StringComparer.CurrentCultureIgnoreCase)
                : visible.OrderBy(TextKey, StringComparer.CurrentCultureIgnoreCase))
            : (_sortDescending ? visible.OrderByDescending(row => Rank(row) ?? double.NegativeInfinity)
                : visible.OrderBy(row => Rank(row) ?? double.PositiveInfinity));

        _processes.Rows.Clear();
        foreach (var row in ordered.ThenBy(row => row.Process.Name).ThenBy(row => row.Process.Pid))
        {
            int index = _processes.Rows.Add(row.Process.Name, row.Process.Pid,
                row.Cpu is double cpu ? $"{cpu:0.0} %" : "—", row.Process.WorkingSetDisplay,
                Speed(row.DiskRead / row.Seconds), Speed(row.DiskWrite / row.Seconds),
                Speed(row.Received / row.Seconds), Speed(row.Sent / row.Seconds), Speed(row.InternetRate), row.Services);
            _processes.Rows[index].Cells[9].ToolTipText = row.Services;
        }
    }

    private void ShowRecommendations()
    {
        _recommendations.Rows.Clear();
        foreach (var row in _result!.Processes.Where(row => CanRecommend(row.Process)))
        {
            var reasons = new List<string>();
            if (row.Cpu >= 10)
                reasons.Add($"CPU {row.Cpu:0.0} %");
            if (row.Process.WorkingSetBytes >= 512L * 1024 * 1024)
                reasons.Add($"RAM {row.Process.WorkingSetDisplay}");
            if (row.DiskRate >= 1024 * 1024)
                reasons.Add($"Disk {Speed(row.DiskRate)}");
            if (row.NetworkRate >= 128 * 1024)
                reasons.Add($"Network {Speed(row.NetworkRate)}");
            if (row.InternetRate >= 128 * 1024)
                reasons.Add($"Est. Internet {Speed(row.InternetRate)}");
            if (reasons.Count == 0)
                continue;

            AddRecommendation(new(row.Process, string.Join(" · ", reasons), false));
            if (row.Cpu >= 10 && row.Process.PriorityClass == ProcessPriorityClass.Normal)
                AddRecommendation(new(row.Process, $"CPU {row.Cpu:0.0} %", true));
        }
        if (_recommendations.Rows.Count == 0)
            _recommendations.Rows.Add("", "No recommendations for the open applications in this sample.", "");
        UpdateApplyButton();
    }

    private bool CanRecommend(ProcessInfoItem process) => process.Pid > 4
        && process.Pid != Environment.ProcessId && process.SessionId == _sessionId && process.CreateTimeRaw > 0
        && process.HasMainWindow && process.HostedServicesCount == 0
        && !process.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
        && !process.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    private void AddRecommendation(Recommendation item)
    {
        int index = _recommendations.Rows.Add($"{item.Process.Name} ({item.Process.Pid})", item.Reason,
            item.LowerPriority ? "Lower CPU priority" : "Request application close");
        _recommendations.Rows[index].Tag = item;
        _recommendations.Rows[index].Cells[1].ToolTipText = item.Reason;
    }

    private void UpdateApplyButton() => _apply.Enabled =
        _cancellation is null && _recommendations.CurrentRow?.Tag is Recommendation;

    private void ApplyRecommendation()
    {
        if (_recommendations.CurrentRow?.Tag is not Recommendation item)
            return;
        string description = item.LowerPriority
            ? "Lowering CPU priority can make the application less responsive when the CPU is busy. You can restore its priority in Task Manager."
            : "Unit Manager will ask the application to close. Save your work before continuing.";
        if (MessageBox.Show(this, $"{item.Process.Name} (PID {item.Process.Pid})\n{item.Reason}\n\n{description}",
            "Confirm Recommendation", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        try
        {
            using var process = Process.GetProcessById(item.Process.Pid);
            // Keep the process handle open while checking identity and applying the action.
            _ = process.SafeHandle;
            if (process.StartTime.ToFileTimeUtc() != item.Process.CreateTimeRaw || process.HasExited
                || process.SessionId != _sessionId || process.MainWindowHandle == IntPtr.Zero)
                throw new InvalidOperationException("The process changed or closed its window. Run the analysis again.");

            if (item.LowerPriority)
            {
                if (process.PriorityClass != ProcessPriorityClass.Normal)
                    throw new InvalidOperationException("The priority already changed. Run the analysis again.");
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                _status.Text = $"Reduced CPU priority for {item.Process.Name}.";
            }
            else
            {
                if (!process.CloseMainWindow())
                    throw new InvalidOperationException("The application did not accept the close request.");
                _status.Text = $"Sent a close request to {item.Process.Name}. The application may ask for confirmation.";
            }

            var completed = _recommendations.CurrentRow!;
            if (item.LowerPriority)
                _recommendations.Rows.Remove(completed);
            else
            {
                foreach (var row in _recommendations.Rows.Cast<DataGridViewRow>()
                    .Where(r => r.Tag is Recommendation action && action.Process.Pid == item.Process.Pid).ToArray())
                    _recommendations.Rows.Remove(row);
            }
            UpdateApplyButton();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _status.Text = $"Could not apply the recommendation. {ex.Message}";
        }
    }

    private static string Speed(double? rate) => rate.HasValue ? ProcessInfoItem.FormatSpeed(rate.Value) : "—";

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window,
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 246, 250) }
    };

    private void AddColumn(DataGridView grid, string title, int width, bool fill = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = title,
            Width = width,
            MinimumWidth = fill ? width : 40,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = grid == _processes ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.Automatic
        });
}
