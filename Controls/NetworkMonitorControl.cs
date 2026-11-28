using System.ComponentModel;
using System.Diagnostics;
using FileViewer.Forms;
using FileViewer.Models;
using FileViewer.Services;

namespace FileViewer.Controls;

internal sealed class NetworkMonitorControl : UserControl
{
    private const int SampleIntervalSeconds = 5;
    private const int MaxGridRows = 500;
    private const int MaxInMemorySamples = 20_000; // > 27 hours of 5-second samples
    private static readonly Font BoldStatusFont = new("Segoe UI", 8.8f, FontStyle.Bold);

    private sealed record TimeWindowOption(string Label, TimeSpan? Duration)
    {
        public override string ToString() => Label;
    }

    private readonly NetworkMonitorService _service = new();
    private readonly NetworkDiagnosticLogger _logger = new();
    private readonly List<NetworkSample> _samples = [];

    private readonly Button _btnToggleMonitor = new() { Text = "Start Monitoring", AutoSize = true };
    private readonly ComboBox _cmbWindow = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 128 };
    private readonly CheckBox _chkOverlayLocal = new() { Text = "Show local PC usage", AutoSize = true, Checked = true };
    private readonly Button _btnLoadLog = new() { Text = "Load Log...", AutoSize = true };
    private readonly Button _btnOpenLogFolder = new() { Text = "Open Logs", AutoSize = true };
    private readonly Button _btnClear = new() { Text = "Clear", AutoSize = true };

    private readonly Label _lblMetrics = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        Padding = new Padding(6, 4, 6, 4)
    };

    private readonly NetworkSpeedChartPanel _chart = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _logGrid = CreateLogGrid();

    private readonly Button _btnDiagnose = new() { Text = "Run Network & Internet Diagnosis", AutoSize = true };
    private readonly Button _btnViewReport = new() { Text = "View Last Report", AutoSize = true, Enabled = false };
    private readonly Label _lblStatus = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        Padding = new Padding(4, 6, 4, 4)
    };

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorLoopTask;
    private CancellationTokenSource? _diagnosisCts;
    private NetworkDiagnosisReport? _lastReport;
    private bool _hasAutoStarted;
    private int _loggedSamplesCount;

    public Task? PendingDiagnosis { get; private set; }

    public bool IsMonitoring => _monitorCts is not null;

    public NetworkMonitorControl()
    {
        Dock = DockStyle.Fill;

        _cmbWindow.Items.AddRange(
        [
            new TimeWindowOption("Last 5 min", TimeSpan.FromMinutes(5)),
            new TimeWindowOption("Last 30 min", TimeSpan.FromMinutes(30)),
            new TimeWindowOption("Last 2 hours", TimeSpan.FromHours(2)),
            new TimeWindowOption("All samples", null)
        ]);
        _cmbWindow.SelectedIndex = 0;

        // Top toolbar
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(2, 4, 2, 2)
        };
        _chkOverlayLocal.Margin = new Padding(10, 6, 8, 3);
        _cmbWindow.Margin = new Padding(4, 3, 6, 3);

        var lblWindow = new Label { Text = "Chart:", AutoSize = true, Margin = new Padding(8, 7, 2, 3) };
        toolbar.Controls.AddRange(
        [
            _btnToggleMonitor,
            lblWindow,
            _cmbWindow,
            _chkOverlayLocal,
            _btnLoadLog,
            _btnOpenLogFolder,
            _btnClear
        ]);

        if (!_service.IsElevated)
        {
            var btnElevate = new Button { Text = "Run as administrator", AutoSize = true };
            btnElevate.Click += (_, _) => OpenElevatedNetworkMonitor();
            toolbar.Controls.Add(btnElevate);
        }

        // Summary KPI strip
        var metricsCard = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3, 2, 3, 6)
        };
        metricsCard.Controls.Add(_lblMetrics);
        _lblMetrics.Text = "Start monitoring to record a sample every five seconds.";

        // Chart container
        _chart.Margin = new Padding(3, 2, 3, 6);

        // Log grid section header
        var logHeader = new Label
        {
            Text = "5-Second Network & Internet Diagnostic Log",
            AutoSize = true,
            Font = WorkspaceTheme.Heading,
            Margin = new Padding(4, 4, 4, 4)
        };

        // Bottom action bar (final diagnosis button)
        var bottomBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(3, 6, 3, 2)
        };
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0)
        };
        rightActions.Controls.AddRange([_btnDiagnose, _btnViewReport]);

        bottomBar.Controls.Add(_lblStatus, 0, 0);
        bottomBar.Controls.Add(rightActions, 1, 0);

        // Main layout
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(metricsCard, 0, 1);
        layout.Controls.Add(_chart, 0, 2);
        layout.Controls.Add(logHeader, 0, 3);
        layout.Controls.Add(_logGrid, 0, 4);
        layout.Controls.Add(bottomBar, 0, 5);
        Controls.Add(layout);

        _lblStatus.Text = _service.IsElevated
            ? $"Administrator mode active (per-process network attribution enabled). Log folder: {_logger.LogDirectory}"
            : $"Sampling every {SampleIntervalSeconds}s. Run as administrator for exact per-process Mbps attribution.";

        WireEvents();
    }

    public void StylePrimaryAction()
    {
        WorkspaceTheme.StyleButton(_btnDiagnose, primary: true);
    }

    public void OnTabActivated()
    {
        if (!_hasAutoStarted)
        {
            _hasAutoStarted = true;
            StartMonitoring();
        }
    }

    public void CancelPendingDiagnosis() => _diagnosisCts?.Cancel();

    public void StopMonitoring()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _service.StopProcessTrafficCapture();

        if (!IsDisposed)
        {
            _btnToggleMonitor.Text = "Start Monitoring";
            UpdateMetricsBar();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _diagnosisCts?.Cancel();
            _diagnosisCts?.Dispose();
            StopMonitoring();
            _service.Dispose();
        }

        base.Dispose(disposing);
    }

    private void WireEvents()
    {
        _btnToggleMonitor.Click += async (_, _) =>
        {
            if (IsMonitoring)
            {
                _btnToggleMonitor.Enabled = false;
                StopMonitoring();
                _lblStatus.Text = "Monitoring stopped. The CSV is available in Open Logs.";
                if (_monitorLoopTask is { } loop)
                    await loop;
                if (!IsDisposed)
                    _btnToggleMonitor.Enabled = true;
            }
            else
            {
                StartMonitoring();
            }
        };

        _cmbWindow.SelectedIndexChanged += (_, _) => RefreshChart();

        _chkOverlayLocal.CheckedChanged += (_, _) =>
        {
            _chart.ShowLocalUsageOverlay = _chkOverlayLocal.Checked;
            _chart.Invalidate();
        };

        _btnLoadLog.Click += (_, _) => LoadSavedLogFiles();

        _btnOpenLogFolder.Click += (_, _) => OpenLogFolder();

        _btnClear.Click += (_, _) =>
        {
            _samples.Clear();
            _logGrid.Rows.Clear();
            _service.ResetCounters();
            RefreshChart();
            UpdateMetricsBar();
            _lblStatus.Text = "Cleared in-memory samples (saved CSV log files were not deleted).";
        };

        _btnDiagnose.Click += async (_, _) =>
        {
            if (PendingDiagnosis is { IsCompleted: false })
            {
                CancelPendingDiagnosis();
                return;
            }

            PendingDiagnosis = RunDiagnosisAsync();
            await PendingDiagnosis;
        };

        _btnViewReport.Click += (_, _) =>
        {
            if (_lastReport is not null)
            {
                using var dialog = new NetworkDiagnosisDialog(_lastReport);
                dialog.ShowDialog(FindForm());
            }
        };
    }

    private void StartMonitoring()
    {
        if (IsMonitoring || IsDisposed || _monitorLoopTask is { IsCompleted: false })
        {
            return;
        }

        string logPath;
        try
        {
            logPath = _logger.StartSession(DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lblStatus.Text = $"Could not create the diagnostic log: {ex.Message}";
            return;
        }

        _loggedSamplesCount = 0;
        _monitorCts = new CancellationTokenSource();
        CancellationToken token = _monitorCts.Token;

        _service.StartProcessTrafficCapture();
        _btnToggleMonitor.Text = "Stop Monitoring";
        _lblStatus.Text = $"Recording every {SampleIntervalSeconds}s to {Path.GetFileName(logPath)}.";
        UpdateMetricsBar();

        _monitorLoopTask = RunMonitoringLoopAsync(logPath, token);
    }

    private async Task RunMonitoringLoopAsync(string logPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !IsDisposed)
        {
            var cycleClock = Stopwatch.StartNew();
            try
            {
                NetworkSample sample = await _service.CollectSampleAsync(token);
                if (token.IsCancellationRequested || IsDisposed)
                {
                    break;
                }

                try
                {
                    _logger.AppendSample(logPath, sample);
                    _loggedSamplesCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _lblStatus.Text = $"Log write warning: {ex.Message}";
                }

                AddSampleToView(sample);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && !IsDisposed)
                {
                    _lblStatus.Text = $"Measurement error: {ex.Message}";
                }
            }

            cycleClock.Stop();
            TimeSpan remaining = TimeSpan.FromSeconds(SampleIntervalSeconds) - cycleClock.Elapsed;
            if (remaining > TimeSpan.FromMilliseconds(200))
            {
                try
                {
                    await Task.Delay(remaining, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void AddSampleToView(NetworkSample sample)
    {
        _samples.Add(sample);
        if (_samples.Count > MaxInMemorySamples)
        {
            _samples.RemoveAt(0);
        }

        InsertGridRowAtTop(sample);
        if (_logGrid.Rows.Count > MaxGridRows)
        {
            _logGrid.Rows.RemoveAt(_logGrid.Rows.Count - 1);
        }

        RefreshChart();
        UpdateMetricsBar();
    }

    private void InsertGridRowAtTop(NetworkSample s)
    {
        _logGrid.Rows.Insert(0,
            s.TimestampDisplay,
            s.InternetSpeedDisplay,
            s.LocalDownDisplay,
            s.LocalUpDisplay,
            s.GatewayPingDisplay,
            s.InternetPingDisplay,
            s.JitterDisplay,
            s.PacketLossDisplay,
            s.DnsDisplay,
            s.LinkDisplay,
            s.TopProcess,
            s.StatusDisplay);

        DataGridViewRow row = _logGrid.Rows[0];
        row.Cells[11].ToolTipText = s.Notes;

        if (!SystemInformation.HighContrast && s.Status != NetworkHealthStatus.Healthy)
        {
            bool severe = s.Status is NetworkHealthStatus.Offline or NetworkHealthStatus.PacketLoss;
            row.DefaultCellStyle.BackColor = severe ? Color.FromArgb(254, 242, 242) : Color.FromArgb(255, 251, 235);
            row.Cells[11].Style.ForeColor = severe ? Color.FromArgb(185, 28, 28) : Color.FromArgb(180, 83, 9);
            row.Cells[11].Style.Font = BoldStatusFont;
        }
    }

    private void RefreshChart()
    {
        TimeWindowOption option = _cmbWindow.SelectedItem as TimeWindowOption
            ?? new TimeWindowOption("Last 5 min", TimeSpan.FromMinutes(5));

        IReadOnlyList<NetworkSample> slice;
        if (option.Duration is TimeSpan window && _samples.Count > 0)
        {
            DateTime cutoff = _samples[^1].Timestamp - window;
            slice = _samples.Where(s => s.Timestamp >= cutoff).ToList();
        }
        else
        {
            slice = _samples;
        }

        _chart.SetSamples(slice, option.Label);
    }

    private void UpdateMetricsBar()
    {
        if (_samples.Count == 0)
        {
            _lblMetrics.Text = IsMonitoring ? "Waiting for the first sample…" : "Monitoring stopped.";
            return;
        }

        NetworkSample latest = _samples[^1];
        string logState = $"Log: {_loggedSamplesCount:N0} saved";
        string state = IsMonitoring ? "Live (5s)" : "Stopped";
        string etwNote = _service.IsEtwTrackingActive ? "ETW" : "Conns";

        _lblMetrics.Text =
            $"{state}  ·  Internet: {latest.InternetSpeedDisplay}  ·  " +
            $"Local PC: ↓ {latest.LocalDownDisplay} / ↑ {latest.LocalUpDisplay}  ·  " +
            $"Router: {latest.GatewayPingDisplay}  ·  " +
            $"Internet Ping: {latest.InternetPingDisplay} (Jitter {latest.JitterDisplay}, Loss {latest.PacketLossDisplay})  ·  " +
            $"Link: {latest.LinkDisplay}  ·  " +
            $"Top ({etwNote}): {latest.TopProcess}  ·  {logState}";
    }

    private void LoadSavedLogFiles()
    {
        _logger.EnsureLogDirectory();
        using var dialog = new OpenFileDialog
        {
            Title = "Load Network Diagnostic Log CSV",
            InitialDirectory = _logger.LogDirectory,
            Filter = "Network Diagnostic Logs (*.csv)|*.csv|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.FileNames.Length == 0)
        {
            return;
        }

        try
        {
            var merged = new List<NetworkSample>();
            foreach (string file in dialog.FileNames)
            {
                merged.AddRange(_logger.LoadSamplesFromFile(file));
            }

            merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            if (merged.Count == 0)
            {
                _lblStatus.Text = "Selected file(s) contained no valid network diagnostic samples.";
                return;
            }

            PopulateSamples(merged);
            _cmbWindow.SelectedIndex = _cmbWindow.Items.Count - 1; // Switch chart to "All samples"
            _lblStatus.Text = $"Loaded {merged.Count:N0} samples from {dialog.FileNames.Length} log file(s). Click 'Run Network & Internet Diagnosis' to analyze hourly slowdowns.";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Could not load log file(s): {ex.Message}";
        }
    }

    private void PopulateSamples(List<NetworkSample> loaded)
    {
        _samples.Clear();
        int skip = Math.Max(0, loaded.Count - MaxInMemorySamples);
        for (int i = skip; i < loaded.Count; i++)
        {
            _samples.Add(loaded[i]);
        }

        _logGrid.SuspendLayout();
        _logGrid.Rows.Clear();
        int gridStart = Math.Max(0, _samples.Count - MaxGridRows);
        for (int i = gridStart; i < _samples.Count; i++)
        {
            InsertGridRowAtTop(_samples[i]);
        }

        _logGrid.ResumeLayout();
        RefreshChart();
        UpdateMetricsBar();
    }

    private async Task RunDiagnosisAsync()
    {
        using var cts = new CancellationTokenSource();
        _diagnosisCts = cts;

        _btnDiagnose.Text = "Cancel Diagnosis";
        _btnViewReport.Enabled = false;

        var progress = new Progress<string>(msg =>
        {
            if (!IsDisposed)
            {
                _lblStatus.Text = msg;
            }
        });

        try
        {
            // Snapshot current in-memory history (which includes loaded logs + live 5s session)
            List<NetworkSample> snapshot = _samples.ToList();
            NetworkDiagnosisReport report = await _service.RunFullDiagnosisAsync(snapshot, progress, cts.Token);
            if (IsDisposed)
            {
                return;
            }

            _lastReport = report;
            _btnViewReport.Enabled = true;
            _lblStatus.Text = report.ExecutiveSummary;

            using var dialog = new NetworkDiagnosisDialog(report);
            dialog.ShowDialog(FindForm());
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _lblStatus.Text = "Network & Internet diagnosis canceled.";
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _lblStatus.Text = $"Diagnosis failed: {ex.Message}";
            }
        }
        finally
        {
            _diagnosisCts = null;
            if (!IsDisposed)
            {
                _btnDiagnose.Text = "Run Network & Internet Diagnosis";
                _btnViewReport.Enabled = _lastReport is not null;
            }
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            _logger.EnsureLogDirectory();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logger.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Could not open log folder: {ex.Message}";
        }
    }

    private void OpenElevatedNetworkMonitor()
    {
        try
        {
            Program.RestartAsAdministrator(Program.NetworkArgument);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _lblStatus.Text = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "The administrator permission request was canceled."
                : $"Could not open Unit Manager as administrator. {ex.Message}";
        }
    }

    private static DataGridView CreateLogGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window
        };

        void AddCol(string header, int width, bool fill = false) => grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Width = width,
            MinimumWidth = fill ? width : 52,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        AddCol("Time", 76);
        AddCol("Internet Speed", 104);
        AddCol("Local Down", 88);
        AddCol("Local Up", 82);
        AddCol("Router Ping", 82);
        AddCol("Internet Ping", 88);
        AddCol("Jitter", 64);
        AddCol("Loss %", 60);
        AddCol("DNS", 66);
        AddCol("Link / Wi-Fi", 136);
        AddCol("Top Process", 150, fill: true);
        AddCol("Status", 96);
        return grid;
    }
}
