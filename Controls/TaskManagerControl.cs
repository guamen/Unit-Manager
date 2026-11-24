using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using FileViewer.Forms;
using FileViewer.Models;
using FileViewer.Services;

namespace FileViewer.Controls;

/// <summary>
/// Full-featured Super Task Manager control with process tree, live telemetry,
/// thread/module/network/environment/handle/service inspection, and process control actions.
/// </summary>
public sealed class TaskManagerControl : UserControl
{
    private readonly ProcessMonitorService _monitor = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Dictionary<int, Queue<double>> _processCpuHistory = new();
    private readonly Dictionary<int, Queue<double>> _processRamPercentHistory = new();

    private List<ProcessInfoItem> _allProcesses = [];
    private List<ProcessInfoItem> _displayedProcesses = [];
    private List<NetworkConnectionItem> _allConnections = [];
    private List<ProcessModuleInfoItem> _currentModules = [];
    private List<ProcessEnvironmentVariableItem> _currentEnvVars = [];
    private List<ProcessHandleInfoItem> _currentHandles = [];
    private SystemPerformanceSnapshot? _lastSystemStats;

    private bool _isRefreshing;
    private bool _isPaused;
    private bool _isTreeMode = true;
    private bool _contextMenuOpen;
    private string _sortColumnName = "colCpu";
    private bool _sortAscending;
    private int? _selectedPid;
    private int? _lastDeepInspectedPid;
    private int? _lastEnvLoadedPid;
    private int? _lastHandlesLoadedPid;
    private int _refreshTickCounter;

    // Top Toolbar
    private Panel _pnlToolbar = null!;
    private TextBox _txtSearch = null!;
    private ComboBox _cmbQuickFilter = null!;
    private Button _btnTreeToggle = null!;
    private ComboBox _cmbInterval = null!;
    private Button _btnPauseResume = null!;
    private Button _btnRefreshNow = null!;
    private Button _btnNewTask = null!;
    private Button _btnElevate = null!;
    private Button _btnPopOut = null!;

    // KPI Bar
    private Panel _pnlKpi = null!;
    private SparklinePanel _sparkCpu = null!;
    private SparklinePanel _sparkRam = null!;
    private Label _lblKpiDiskNet = null!;
    private Label _lblKpiCounts = null!;

    // Main Split
    private SplitContainer _splitMain = null!;
    private Panel _pnlProcessListContainer = null!;
    private DataGridView _dgvProcesses = null!;
    private Panel _pnlQuickProcessBar = null!;
    private Button _btnEndTask = null!;
    private Button _btnEndTree = null!;
    private Button _btnSuspendResume = null!;
    private Button _btnAffinity = null!;
    private Button _btnCreateDump = null!;
    private Button _btnOpenLocation = null!;
    private Label _lblProcessCountInfo = null!;

    // Context Menu
    private ContextMenuStrip _cmsProcess = null!;
    private ToolStripMenuItem _mnuSuspendResume = null!;

    // Inspector Tabs
    private TabControl _tabInspector = null!;
    private Label _lblInspectorBanner = null!;

    // Summary Tab
    private SparklinePanel _sparkProcCpu = null!;
    private SparklinePanel _sparkProcRam = null!;
    private TextBox _txtProcSummary = null!;
    private TextBox _txtProcCmdLine = null!;
    private TextBox _txtProcSha256 = null!;
    private Button _btnComputeHash = null!;

    // Threads Tab
    private DataGridView _dgvThreads = null!;
    private ContextMenuStrip _cmsThreads = null!;

    // Modules Tab
    private TextBox _txtModuleFilter = null!;
    private DataGridView _dgvModules = null!;
    private Label _lblModulesNote = null!;

    // Process Network Tab
    private DataGridView _dgvProcNetwork = null!;

    // Global Network Tab
    private TextBox _txtGlobalNetFilter = null!;
    private DataGridView _dgvGlobalNetwork = null!;

    // Environment Tab
    private TextBox _txtEnvFilter = null!;
    private DataGridView _dgvEnv = null!;
    private Label _lblEnvNote = null!;

    // Handles Tab
    private TextBox _txtHandlesFilter = null!;
    private DataGridView _dgvHandles = null!;
    private Label _lblHandlesNote = null!;

    // Services & Windows Tab
    private DataGridView _dgvServices = null!;
    private DataGridView _dgvWindows = null!;

    public event Action<string>? ExplorePathInFileViewerRequested;
    public event Action? PopOutRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowPopOutButton
    {
        get => _btnPopOut.Visible;
        set => _btnPopOut.Visible = value;
    }

    public TaskManagerControl()
    {
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += async (_, _) => await RefreshDataAsync();

        InitializeLayout();
        ApplyElevationBadge();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!DesignMode)
        {
            _ = RefreshDataAsync();
            _refreshTimer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _monitor.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SetMonitoringActive(bool active)
    {
        if (active && !_isPaused)
        {
            _refreshTimer.Start();
            _ = RefreshDataAsync();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    public void FocusSearch()
    {
        _txtSearch.Focus();
        _txtSearch.SelectAll();
    }

    private void InitializeLayout()
    {
        SuspendLayout();
        Dock = DockStyle.Fill;

        BuildToolbar();
        BuildKpiBar();
        BuildProcessGridAndActions();
        BuildInspectorTabs();

        _splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Panel1MinSize = 200,
            Panel2MinSize = 150
        };
        _splitMain.Panel1.Controls.Add(_pnlProcessListContainer);
        _splitMain.Panel2.Controls.Add(_tabInspector);
        _splitMain.Panel2.Controls.Add(_lblInspectorBanner);

        Controls.Add(_splitMain);
        Controls.Add(_pnlKpi);
        Controls.Add(_pnlToolbar);

        // Set reasonable initial splitter distance after handle creation
        HandleCreated += (_, _) =>
        {
            BeginInvoke(() =>
            {
                if (_splitMain.Height > 420)
                {
                    _splitMain.SplitterDistance = (int)(_splitMain.Height * 0.58);
                }
            });
        };

        ResumeLayout(false);
    }

    private void BuildToolbar()
    {
        _pnlToolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 4)
        };

        var flpLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };

        var lblSearch = new Label
        {
            Text = "Filter:",
            AutoSize = true,
            Margin = new Padding(0, 6, 4, 0)
        };

        _txtSearch = new TextBox
        {
            Width = 210,
            PlaceholderText = "Name, PID, path, user, command...",
            Margin = new Padding(0, 2, 8, 2)
        };
        _txtSearch.TextChanged += (_, _) => ApplyFilterAndRender(preserveScroll: false);

        _cmbQuickFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 165,
            Margin = new Padding(0, 2, 8, 2)
        };
        _cmbQuickFilter.Items.AddRange(
        [
            "All processes",
            "With Window (Apps)",
            "High CPU (> 5%)",
            "High RAM (> 200 MB)",
            "Active Network",
            "Elevated (Admin/System)",
            "Suspended / Not Responding"
        ]);
        _cmbQuickFilter.SelectedIndex = 0;
        _cmbQuickFilter.SelectedIndexChanged += (_, _) => ApplyFilterAndRender(preserveScroll: false);

        _btnTreeToggle = new Button
        {
            Text = "Tree View",
            Width = 122,
            Height = 26,
            Margin = new Padding(0, 1, 8, 1),
            UseVisualStyleBackColor = true
        };
        _btnTreeToggle.Click += (_, _) =>
        {
            _isTreeMode = !_isTreeMode;
            UpdateTreeToggleVisual();
            ApplyFilterAndRender(preserveScroll: false);
        };

        var lblInterval = new Label
        {
            Text = "Refresh:",
            AutoSize = true,
            Margin = new Padding(2, 6, 4, 0)
        };

        _cmbInterval = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 76,
            Margin = new Padding(0, 2, 6, 2)
        };
        _cmbInterval.Items.AddRange(["0.5 s", "1 s", "2 s", "3 s", "5 s", "Manual"]);
        _cmbInterval.SelectedIndex = 3;
        _cmbInterval.SelectedIndexChanged += (_, _) => UpdateTimerInterval();

        _btnPauseResume = new Button
        {
            Text = "Pause",
            Width = 84,
            Height = 26,
            Margin = new Padding(0, 1, 4, 1),
            UseVisualStyleBackColor = true
        };
        _btnPauseResume.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            _btnPauseResume.Text = _isPaused ? "Reanudar" : "Pause";
            UpdateTimerInterval();
        };

        _btnRefreshNow = new Button
        {
            Text = "↻",
            Width = 34,
            Height = 26,
            Margin = new Padding(0, 1, 4, 1),
            UseVisualStyleBackColor = true
        };
        _btnRefreshNow.Click += async (_, _) => await RefreshDataAsync(forceDeep: true);

        flpLeft.Controls.AddRange([lblSearch, _txtSearch, _cmbQuickFilter, _btnTreeToggle, lblInterval, _cmbInterval, _btnPauseResume, _btnRefreshNow]);

        var flpRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft
        };

        _btnPopOut = new Button
        {
            Text = "Window",
            Width = 90,
            Height = 26,
            Margin = new Padding(4, 1, 0, 1),
            UseVisualStyleBackColor = true
        };
        _btnPopOut.Click += (_, _) => PopOutRequested?.Invoke();

        _btnElevate = new Button
        {
            Text = "Admin Mode",
            Width = 115,
            Height = 26,
            Margin = new Padding(4, 1, 0, 1),
            UseVisualStyleBackColor = true
        };
        _btnElevate.Click += BtnElevate_Click;

        _btnNewTask = new Button
        {
            Text = "New Task",
            Width = 110,
            Height = 26,
            Margin = new Padding(4, 1, 0, 1),
            UseVisualStyleBackColor = true
        };
        _btnNewTask.Click += BtnNewTask_Click;

        flpRight.Controls.AddRange([_btnPopOut, _btnElevate, _btnNewTask]);

        _pnlToolbar.Controls.Add(flpLeft);
        _pnlToolbar.Controls.Add(flpRight);
    }

    private void BuildKpiBar()
    {
        _pnlKpi = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            Padding = new Padding(8, 2, 8, 4)
        };

        var tlpKpi = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1
        };
        tlpKpi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
        tlpKpi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
        tlpKpi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26F));
        tlpKpi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26F));

        _sparkCpu = new SparklinePanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0),
            Title = "Total CPU",
            AccentColor = Color.FromArgb(29, 112, 164)
        };

        _sparkRam = new SparklinePanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0),
            Title = "Physical RAM",
            AccentColor = WorkspaceTheme.Accent
        };

        _lblKpiDiskNet = new Label
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface,
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : WorkspaceTheme.Ink,
            Padding = new Padding(8, 6, 8, 6),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Disk I/O: -\nActive Network: -"
        };

        _lblKpiCounts = new Label
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface,
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : WorkspaceTheme.Ink,
            Padding = new Padding(8, 6, 8, 6),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Processes: - | Threads: -\nHandles: - | Uptime: -"
        };

        tlpKpi.Controls.Add(_sparkCpu, 0, 0);
        tlpKpi.Controls.Add(_sparkRam, 1, 0);
        tlpKpi.Controls.Add(_lblKpiDiskNet, 2, 0);
        tlpKpi.Controls.Add(_lblKpiCounts, 3, 0);

        _pnlKpi.Controls.Add(tlpKpi);
    }

    private void BuildProcessGridAndActions()
    {
        _pnlProcessListContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 2, 8, 2)
        };

        _dgvProcesses = CreateStyledGrid();
        _dgvProcesses.MultiSelect = false;
        _dgvProcesses.SelectionChanged += DgvProcesses_SelectionChanged;
        _dgvProcesses.ColumnHeaderMouseClick += DgvProcesses_ColumnHeaderMouseClick;
        _dgvProcesses.CellFormatting += DgvProcesses_CellFormatting;
        _dgvProcesses.CellMouseDown += DgvProcesses_CellMouseDown;
        _dgvProcesses.CellDoubleClick += (_, _) => OpenSelectedExecutableLocation();

        BuildProcessGridColumns();
        BuildProcessContextMenu();
        _dgvProcesses.ContextMenuStrip = _cmsProcess;

        _pnlQuickProcessBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(0, 4, 0, 2)
        };

        var flpActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false
        };

        _btnEndTask = new Button { Text = "End", Width = 96, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnEndTask.Click += (_, _) => KillSelectedProcess(entireTree: false);

        _btnEndTree = new Button { Text = "End Tree", Width = 124, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnEndTree.Click += (_, _) => KillSelectedProcess(entireTree: true);

        _btnSuspendResume = new Button { Text = "Suspend", Width = 104, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnSuspendResume.Click += (_, _) => ToggleSuspendSelectedProcess();

        _btnAffinity = new Button { Text = "Affinity", Width = 92, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnAffinity.Click += (_, _) => ConfigureAffinityForSelected();

        _btnCreateDump = new Button { Text = "Dump (.dmp)", Width = 118, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnCreateDump.Click += async (_, _) => await CreateMiniDumpForSelectedAsync();

        _btnOpenLocation = new Button { Text = "Location", Width = 96, Height = 26, Enabled = false, UseVisualStyleBackColor = true };
        _btnOpenLocation.Click += (_, _) => OpenSelectedExecutableLocation();

        flpActions.Controls.AddRange([_btnEndTask, _btnEndTree, _btnSuspendResume, _btnAffinity, _btnCreateDump, _btnOpenLocation]);

        _lblProcessCountInfo = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 6, 4, 0),
            Text = "0 processes"
        };

        _pnlQuickProcessBar.Controls.Add(flpActions);
        _pnlQuickProcessBar.Controls.Add(_lblProcessCountInfo);

        _pnlProcessListContainer.Controls.Add(_dgvProcesses);
        _pnlProcessListContainer.Controls.Add(_pnlQuickProcessBar);
    }

    private void BuildProcessGridColumns()
    {
        var colIcon = new DataGridViewImageColumn
        {
            Name = "colIcon",
            HeaderText = string.Empty,
            DataPropertyName = nameof(ProcessInfoItem.IconImage),
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Width = 26,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        var colName = new DataGridViewTextBoxColumn
        {
            Name = "colName",
            HeaderText = "Process",
            DataPropertyName = nameof(ProcessInfoItem.TreeDisplayName),
            Width = 230,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colPid = new DataGridViewTextBoxColumn
        {
            Name = "colPid",
            HeaderText = "PID",
            DataPropertyName = nameof(ProcessInfoItem.Pid),
            Width = 62,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colPpid = new DataGridViewTextBoxColumn
        {
            Name = "colPpid",
            HeaderText = "PPID",
            DataPropertyName = nameof(ProcessInfoItem.ParentPid),
            Width = 62,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colStatus = new DataGridViewTextBoxColumn
        {
            Name = "colStatus",
            HeaderText = "Status",
            DataPropertyName = nameof(ProcessInfoItem.StatusDisplay),
            Width = 112,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colCpu = new DataGridViewTextBoxColumn
        {
            Name = "colCpu",
            HeaderText = "CPU %",
            DataPropertyName = nameof(ProcessInfoItem.CpuDisplay),
            Width = 72,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colRam = new DataGridViewTextBoxColumn
        {
            Name = "colRam",
            HeaderText = "RAM (WS)",
            DataPropertyName = nameof(ProcessInfoItem.WorkingSetDisplay),
            Width = 90,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colPrivateBytes = new DataGridViewTextBoxColumn
        {
            Name = "colPrivateBytes",
            HeaderText = "Private RAM",
            DataPropertyName = nameof(ProcessInfoItem.PrivateBytesDisplay),
            Width = 92,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colVirtualBytes = new DataGridViewTextBoxColumn
        {
            Name = "colVirtualBytes",
            HeaderText = "Virtual",
            DataPropertyName = nameof(ProcessInfoItem.VirtualBytesDisplay),
            Width = 88,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colDiskRead = new DataGridViewTextBoxColumn
        {
            Name = "colDiskRead",
            HeaderText = "Disk Read/s",
            DataPropertyName = nameof(ProcessInfoItem.IoReadSpeedDisplay),
            Width = 84,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colDiskWrite = new DataGridViewTextBoxColumn
        {
            Name = "colDiskWrite",
            HeaderText = "Disk Write/s",
            DataPropertyName = nameof(ProcessInfoItem.IoWriteSpeedDisplay),
            Width = 84,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colNetCount = new DataGridViewTextBoxColumn
        {
            Name = "colNetCount",
            HeaderText = "Sockets",
            DataPropertyName = nameof(ProcessInfoItem.ActiveConnectionsCount),
            Width = 64,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colPriority = new DataGridViewTextBoxColumn
        {
            Name = "colPriority",
            HeaderText = "Priority",
            DataPropertyName = nameof(ProcessInfoItem.PriorityDisplay),
            Width = 104,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colThreads = new DataGridViewTextBoxColumn
        {
            Name = "colThreads",
            HeaderText = "Threads",
            DataPropertyName = nameof(ProcessInfoItem.ThreadCount),
            Width = 58,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colHandles = new DataGridViewTextBoxColumn
        {
            Name = "colHandles",
            HeaderText = "Handles",
            DataPropertyName = nameof(ProcessInfoItem.HandleCount),
            Width = 68,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colGdi = new DataGridViewTextBoxColumn
        {
            Name = "colGdi",
            HeaderText = "GDI",
            DataPropertyName = nameof(ProcessInfoItem.GdiObjects),
            Width = 52,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colUserObj = new DataGridViewTextBoxColumn
        {
            Name = "colUserObj",
            HeaderText = "USER",
            DataPropertyName = nameof(ProcessInfoItem.UserObjects),
            Width = 54,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colUser = new DataGridViewTextBoxColumn
        {
            Name = "colUser",
            HeaderText = "User",
            DataPropertyName = nameof(ProcessInfoItem.UserName),
            Width = 145,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colIntegrity = new DataGridViewTextBoxColumn
        {
            Name = "colIntegrity",
            HeaderText = "Integrity",
            DataPropertyName = nameof(ProcessInfoItem.IntegrityLevel),
            Width = 86,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colElevated = new DataGridViewTextBoxColumn
        {
            Name = "colElevated",
            HeaderText = "Elevated",
            DataPropertyName = nameof(ProcessInfoItem.ElevatedDisplay),
            Width = 68,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colArch = new DataGridViewTextBoxColumn
        {
            Name = "colArch",
            HeaderText = "Arch.",
            DataPropertyName = nameof(ProcessInfoItem.Architecture),
            Width = 76,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colSession = new DataGridViewTextBoxColumn
        {
            Name = "colSession",
            HeaderText = "Session",
            DataPropertyName = nameof(ProcessInfoItem.SessionId),
            Width = 56,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colCpuTime = new DataGridViewTextBoxColumn
        {
            Name = "colCpuTime",
            HeaderText = "CPU Time",
            DataPropertyName = nameof(ProcessInfoItem.TotalCpuTimeDisplay),
            Width = 88,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colStartTime = new DataGridViewTextBoxColumn
        {
            Name = "colStartTime",
            HeaderText = "Start Time",
            DataPropertyName = nameof(ProcessInfoItem.StartTimeDisplay),
            Width = 132,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colServicesCount = new DataGridViewTextBoxColumn
        {
            Name = "colServicesCount",
            HeaderText = "Services",
            DataPropertyName = nameof(ProcessInfoItem.HostedServicesCount),
            Width = 66,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = RightAlignStyle()
        };

        var colWindowTitle = new DataGridViewTextBoxColumn
        {
            Name = "colWindowTitle",
            HeaderText = "Window Title",
            DataPropertyName = nameof(ProcessInfoItem.MainWindowTitle),
            Width = 180,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colCompany = new DataGridViewTextBoxColumn
        {
            Name = "colCompany",
            HeaderText = "Company",
            DataPropertyName = nameof(ProcessInfoItem.CompanyName),
            Width = 140,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colDescription = new DataGridViewTextBoxColumn
        {
            Name = "colDescription",
            HeaderText = "Description",
            DataPropertyName = nameof(ProcessInfoItem.FileDescription),
            Width = 200,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colCmdLine = new DataGridViewTextBoxColumn
        {
            Name = "colCmdLine",
            HeaderText = "Command Line",
            DataPropertyName = nameof(ProcessInfoItem.CommandLine),
            Width = 280,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        var colPath = new DataGridViewTextBoxColumn
        {
            Name = "colPath",
            HeaderText = "Executable Path",
            DataPropertyName = nameof(ProcessInfoItem.ExecutablePath),
            Width = 280,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        _dgvProcesses.Columns.AddRange(
            colIcon, colName, colPid, colPpid, colStatus,
            colCpu, colRam, colPrivateBytes, colVirtualBytes,
            colDiskRead, colDiskWrite, colNetCount,
            colPriority, colThreads, colHandles, colGdi, colUserObj,
            colUser, colIntegrity, colElevated, colArch, colSession,
            colCpuTime, colStartTime, colServicesCount,
            colWindowTitle, colCompany, colDescription, colCmdLine, colPath);

        // Freeze icon and name columns for effortless horizontal scrolling across all 30 columns
        colIcon.Frozen = true;
        colName.Frozen = true;
    }

    private void BuildProcessContextMenu()
    {
        _cmsProcess = new ContextMenuStrip();
        _cmsProcess.Opening += (_, e) =>
        {
            ProcessInfoItem? proc = GetSelectedProcess();
            if (proc is null)
            {
                e.Cancel = true;
                return;
            }

            _contextMenuOpen = true;
            _mnuSuspendResume.Text = proc.IsSuspended ? "Resume process" : "Suspend process";
        };
        _cmsProcess.Closed += (_, _) => _contextMenuOpen = false;

        var mnuKill = new ToolStripMenuItem("End process", null, (_, _) => KillSelectedProcess(entireTree: false));
        var mnuKillTree = new ToolStripMenuItem("End process tree", null, (_, _) => KillSelectedProcess(entireTree: true));
        _mnuSuspendResume = new ToolStripMenuItem("Suspend process", null, (_, _) => ToggleSuspendSelectedProcess());

        var mnuPriority = new ToolStripMenuItem("Set priority");
        mnuPriority.DropDownItems.Add("Real Time", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.RealTime));
        mnuPriority.DropDownItems.Add("High", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.High));
        mnuPriority.DropDownItems.Add("Above Normal", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.AboveNormal));
        mnuPriority.DropDownItems.Add("Normal", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.Normal));
        mnuPriority.DropDownItems.Add("Below Normal", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.BelowNormal));
        mnuPriority.DropDownItems.Add("Low (Idle)", null, (_, _) => SetSelectedPriority(ProcessPriorityClass.Idle));

        var mnuAffinity = new ToolStripMenuItem("Set CPU affinity...", null, (_, _) => ConfigureAffinityForSelected());
        var mnuDump = new ToolStripMenuItem("Create memory dump (.dmp)...", null, async (_, _) => await CreateMiniDumpForSelectedAsync());
        var mnuExplore = new ToolStripMenuItem("Open location in File Explorer", null, (_, _) => OpenSelectedExecutableLocation());
        var mnuSearchInViewer = new ToolStripMenuItem("Find folder in Unit Manager", null, (_, _) => SendSelectedFolderToFileViewer());

        var mnuCopy = new ToolStripMenuItem("Copy Details");
        mnuCopy.DropDownItems.Add("Copy Command Line", null, (_, _) => CopySelectedField(p => p.CommandLine));
        mnuCopy.DropDownItems.Add("Copy Executable Path", null, (_, _) => CopySelectedField(p => p.ExecutablePath));
        mnuCopy.DropDownItems.Add("Copy PID", null, (_, _) => CopySelectedField(p => p.Pid.ToString()));
        mnuCopy.DropDownItems.Add("Copy Name", null, (_, _) => CopySelectedField(p => p.Name));

        _cmsProcess.Items.AddRange(
        [
            mnuKill,
            mnuKillTree,
            _mnuSuspendResume,
            new ToolStripSeparator(),
            mnuPriority,
            mnuAffinity,
            mnuDump,
            new ToolStripSeparator(),
            mnuExplore,
            mnuSearchInViewer,
            mnuCopy
        ]);
    }

    private void BuildInspectorTabs()
    {
        _lblInspectorBanner = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : WorkspaceTheme.Canvas,
            Padding = new Padding(10, 5, 10, 2),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = "Process Inspector — Select a process to inspect its threads, modules, sockets, environment, handles, and services"
        };

        _tabInspector = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(10, 4)
        };
        _tabInspector.SelectedIndexChanged += async (_, _) => await OnInspectorTabChangedAsync();

        // 1. Summary Tab
        var tabSummary = new TabPage("Summary and Performance");
        BuildSummaryTab(tabSummary);

        // 2. Threads Tab
        var tabThreads = new TabPage("Threads (0)");
        BuildThreadsTab(tabThreads);

        // 3. Modules Tab
        var tabModules = new TabPage("Modules / DLLs (0)");
        BuildModulesTab(tabModules);

        // 4. Process Network Tab
        var tabProcNet = new TabPage("Process Network (0)");
        BuildProcessNetworkTab(tabProcNet);

        // 5. Global Network Tab
        var tabGlobalNet = new TabPage("System Network (0)");
        BuildGlobalNetworkTab(tabGlobalNet);

        // 6. Environment Tab
        var tabEnv = new TabPage("Environment Variables");
        BuildEnvironmentTab(tabEnv);

        // 7. Handles Tab
        var tabHandles = new TabPage("Handles");
        BuildHandlesTab(tabHandles);

        // 8. Services & Windows Tab
        var tabServicesWindows = new TabPage("Services and Windows (0)");
        BuildServicesWindowsTab(tabServicesWindows);

        _tabInspector.TabPages.AddRange([tabSummary, tabThreads, tabModules, tabProcNet, tabGlobalNet, tabEnv, tabHandles, tabServicesWindows]);
    }

    private void BuildSummaryTab(TabPage page)
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5
        };

        // Left: process sparklines + SHA-256 + command line
        var pnlLeft = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        _sparkProcCpu = new SparklinePanel
        {
            Dock = DockStyle.Top,
            Height = 68,
            Title = "Process CPU",
            AccentColor = Color.FromArgb(52, 211, 153)
        };
        _sparkProcRam = new SparklinePanel
        {
            Dock = DockStyle.Top,
            Height = 68,
            Title = "Process RAM",
            AccentColor = Color.FromArgb(251, 191, 36)
        };

        var lblCmd = new Label { Text = "Command line:", Dock = DockStyle.Top, Height = 20, Padding = new Padding(0, 4, 0, 0) };
        _txtProcCmdLine = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 46,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical
        };

        var pnlHash = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 4, 0, 0) };
        _btnComputeHash = new Button
        {
            Text = "SHA-256",
            Dock = DockStyle.Left,
            Width = 96,
            UseVisualStyleBackColor = true
        };
        _btnComputeHash.Click += async (_, _) => await ComputeSelectedSha256Async();
        _txtProcSha256 = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            PlaceholderText = "Click SHA-256 to compute the executable's hash"
        };
        pnlHash.Controls.Add(_txtProcSha256);
        pnlHash.Controls.Add(_btnComputeHash);

        pnlLeft.Controls.Add(pnlHash);
        pnlLeft.Controls.Add(_txtProcCmdLine);
        pnlLeft.Controls.Add(lblCmd);
        pnlLeft.Controls.Add(_sparkProcRam);
        pnlLeft.Controls.Add(_sparkProcCpu);

        // Right: full text summary
        _txtProcSummary = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Console", 9f),
            BackColor = SystemColors.Window
        };

        split.Panel1.Controls.Add(pnlLeft);
        split.Panel2.Controls.Add(_txtProcSummary);
        page.HandleCreated += (_, _) =>
        {
            page.BeginInvoke(() =>
            {
                if (split.Width > 350)
                {
                    split.SplitterDistance = (int)(split.Width * 0.42);
                }
            });
        };

        page.Controls.Add(split);
    }

    private void BuildThreadsTab(TabPage page)
    {
        _dgvThreads = CreateStyledGrid();
        _dgvThreads.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "colTid", HeaderText = "TID", DataPropertyName = nameof(ProcessThreadInfoItem.ThreadId), Width = 72 },
            new DataGridViewTextBoxColumn { Name = "colTState", HeaderText = "Status", DataPropertyName = nameof(ProcessThreadInfoItem.State), Width = 92 },
            new DataGridViewTextBoxColumn { Name = "colTWait", HeaderText = "Wait Reason", DataPropertyName = nameof(ProcessThreadInfoItem.WaitReason), Width = 125 },
            new DataGridViewTextBoxColumn { Name = "colTPrio", HeaderText = "Priority", DataPropertyName = nameof(ProcessThreadInfoItem.Priority), Width = 72 },
            new DataGridViewTextBoxColumn { Name = "colTBasePrio", HeaderText = "Base Priority", DataPropertyName = nameof(ProcessThreadInfoItem.BasePriority), Width = 78 },
            new DataGridViewTextBoxColumn { Name = "colTCtx", HeaderText = "Context Switches", DataPropertyName = nameof(ProcessThreadInfoItem.ContextSwitches), Width = 115 },
            new DataGridViewTextBoxColumn { Name = "colTStartAddr", HeaderText = "Start Address", DataPropertyName = nameof(ProcessThreadInfoItem.StartAddressHex), Width = 140 },
            new DataGridViewTextBoxColumn { Name = "colTCpu", HeaderText = "Total CPU", DataPropertyName = nameof(ProcessThreadInfoItem.TotalCpuTimeDisplay), Width = 95 },
            new DataGridViewTextBoxColumn { Name = "colTStart", HeaderText = "Start Time", DataPropertyName = nameof(ProcessThreadInfoItem.StartTimeDisplay), Width = 110 }
        );

        _cmsThreads = new ContextMenuStrip();
        _cmsThreads.Items.Add("Suspend thread", null, (_, _) => ControlSelectedThread(suspend: true));
        _cmsThreads.Items.Add("Resume thread", null, (_, _) => ControlSelectedThread(suspend: false));
        _dgvThreads.ContextMenuStrip = _cmsThreads;
        _dgvThreads.CellMouseDown += SelectRowOnRightClick;

        page.Controls.Add(_dgvThreads);
    }

    private void BuildModulesTab(TabPage page)
    {
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 4, 6, 4) };
        _txtModuleFilter = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 260,
            PlaceholderText = "Filter modules by name, path, or company..."
        };
        _txtModuleFilter.TextChanged += (_, _) => RenderModulesGrid();

        _lblModulesNote = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            ForeColor = Color.DarkRed
        };

        pnlTop.Controls.Add(_lblModulesNote);
        pnlTop.Controls.Add(_txtModuleFilter);

        _dgvModules = CreateStyledGrid();
        _dgvModules.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "colMName", HeaderText = "Module", DataPropertyName = nameof(ProcessModuleInfoItem.ModuleName), Width = 180 },
            new DataGridViewTextBoxColumn { Name = "colMBase", HeaderText = "Base Address", DataPropertyName = nameof(ProcessModuleInfoItem.BaseAddressHex), Width = 135 },
            new DataGridViewTextBoxColumn { Name = "colMSize", HeaderText = "Size", DataPropertyName = nameof(ProcessModuleInfoItem.SizeDisplay), Width = 85 },
            new DataGridViewTextBoxColumn { Name = "colMVer", HeaderText = "Version", DataPropertyName = nameof(ProcessModuleInfoItem.FileVersion), Width = 110 },
            new DataGridViewTextBoxColumn { Name = "colMComp", HeaderText = "Company", DataPropertyName = nameof(ProcessModuleInfoItem.CompanyName), Width = 150 },
            new DataGridViewTextBoxColumn { Name = "colMDesc", HeaderText = "Description", DataPropertyName = nameof(ProcessModuleInfoItem.Description), Width = 190 },
            new DataGridViewTextBoxColumn { Name = "colMPath", HeaderText = "Full Path", DataPropertyName = nameof(ProcessModuleInfoItem.FileName), Width = 340 }
        );

        var cmsMod = new ContextMenuStrip();
        cmsMod.Items.Add("Open module location", null, (_, _) =>
        {
            if (_dgvModules.SelectedRows.Count > 0 &&
                _dgvModules.SelectedRows[0].DataBoundItem is ProcessModuleInfoItem mod &&
                File.Exists(mod.FileName))
            {
                LaunchExplorer($"/select,\"{mod.FileName}\"");
            }
        });
        _dgvModules.ContextMenuStrip = cmsMod;
        _dgvModules.CellMouseDown += SelectRowOnRightClick;

        page.Controls.Add(_dgvModules);
        page.Controls.Add(pnlTop);
    }

    private void BuildProcessNetworkTab(TabPage page)
    {
        _dgvProcNetwork = CreateStyledGrid();
        _dgvProcNetwork.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Protocol", DataPropertyName = nameof(NetworkConnectionItem.Protocol), Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Local Address", DataPropertyName = nameof(NetworkConnectionItem.LocalEndpoint), Width = 210 },
            new DataGridViewTextBoxColumn { HeaderText = "Remote Address", DataPropertyName = nameof(NetworkConnectionItem.RemoteEndpoint), Width = 210 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(NetworkConnectionItem.State), Width = 130 }
        );
        page.Controls.Add(_dgvProcNetwork);
    }

    private void BuildGlobalNetworkTab(TabPage page)
    {
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 4, 6, 4) };
        _txtGlobalNetFilter = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 300,
            PlaceholderText = "Filter sockets by PID, process, IP, port, or state..."
        };
        _txtGlobalNetFilter.TextChanged += (_, _) => RenderGlobalNetworkGrid();

        var lblHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "  Double-click a connection to select its owning process",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText
        };
        pnlTop.Controls.Add(lblHint);
        pnlTop.Controls.Add(_txtGlobalNetFilter);

        _dgvGlobalNetwork = CreateStyledGrid();
        _dgvGlobalNetwork.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "PID", DataPropertyName = nameof(NetworkConnectionItem.Pid), Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Process", DataPropertyName = nameof(NetworkConnectionItem.ProcessName), Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "Protocol", DataPropertyName = nameof(NetworkConnectionItem.Protocol), Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Local", DataPropertyName = nameof(NetworkConnectionItem.LocalEndpoint), Width = 210 },
            new DataGridViewTextBoxColumn { HeaderText = "Remote", DataPropertyName = nameof(NetworkConnectionItem.RemoteEndpoint), Width = 210 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(NetworkConnectionItem.State), Width = 130 }
        );
        _dgvGlobalNetwork.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _dgvGlobalNetwork.Rows[e.RowIndex].DataBoundItem is NetworkConnectionItem conn)
            {
                SelectProcessByPid(conn.Pid);
            }
        };

        page.Controls.Add(_dgvGlobalNetwork);
        page.Controls.Add(pnlTop);
    }

    private void BuildEnvironmentTab(TabPage page)
    {
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 4, 6, 4) };
        _txtEnvFilter = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 260,
            PlaceholderText = "Filter by variable or value..."
        };
        _txtEnvFilter.TextChanged += (_, _) => RenderEnvironmentGrid();

        var btnReload = new Button
        {
            Dock = DockStyle.Left,
            Text = "Refresh",
            Width = 96,
            UseVisualStyleBackColor = true
        };
        btnReload.Click += async (_, _) =>
        {
            _lastEnvLoadedPid = null;
            await LoadEnvironmentForSelectedAsync();
        };

        _lblEnvNote = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Color.DarkRed
        };

        pnlTop.Controls.Add(_lblEnvNote);
        pnlTop.Controls.Add(btnReload);
        pnlTop.Controls.Add(_txtEnvFilter);

        _dgvEnv = CreateStyledGrid();
        _dgvEnv.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Variable", DataPropertyName = nameof(ProcessEnvironmentVariableItem.Name), Width = 220 },
            new DataGridViewTextBoxColumn { HeaderText = "Value", DataPropertyName = nameof(ProcessEnvironmentVariableItem.Value), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        );

        page.Controls.Add(_dgvEnv);
        page.Controls.Add(pnlTop);
    }

    private void BuildHandlesTab(TabPage page)
    {
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 4, 6, 4) };
        _txtHandlesFilter = new TextBox
        {
            Dock = DockStyle.Left,
            Width = 260,
            PlaceholderText = "Filter by type (File, Key, Mutant...) or name..."
        };
        _txtHandlesFilter.TextChanged += (_, _) => RenderHandlesGrid();

        var btnLoadHandles = new Button
        {
            Dock = DockStyle.Left,
            Text = "Load / Refresh Handles",
            Width = 195,
            UseVisualStyleBackColor = true
        };
        btnLoadHandles.Click += async (_, _) =>
        {
            _lastHandlesLoadedPid = null;
            await LoadHandlesForSelectedAsync();
        };

        _lblHandlesNote = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = SystemColors.GrayText
        };

        pnlTop.Controls.Add(_lblHandlesNote);
        pnlTop.Controls.Add(btnLoadHandles);
        pnlTop.Controls.Add(_txtHandlesFilter);

        _dgvHandles = CreateStyledGrid();
        _dgvHandles.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Handle", DataPropertyName = nameof(ProcessHandleInfoItem.HandleValueHex), Width = 95 },
            new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = nameof(ProcessHandleInfoItem.ObjectType), Width = 135 },
            new DataGridViewTextBoxColumn { HeaderText = "Access", DataPropertyName = nameof(ProcessHandleInfoItem.GrantedAccessHex), Width = 100 },
            new DataGridViewTextBoxColumn { HeaderText = "Object Name", DataPropertyName = nameof(ProcessHandleInfoItem.ObjectName), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        );

        page.Controls.Add(_dgvHandles);
        page.Controls.Add(pnlTop);
    }

    private void BuildServicesWindowsTab(TabPage page)
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5
        };

        var grpSvc = new GroupBox { Text = "Windows services hosted by this process", Dock = DockStyle.Fill, Padding = new Padding(4) };
        _dgvServices = CreateStyledGrid();
        _dgvServices.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Service", DataPropertyName = nameof(ProcessServiceItem.ServiceName), Width = 130 },
            new DataGridViewTextBoxColumn { HeaderText = "Display Name", DataPropertyName = nameof(ProcessServiceItem.DisplayName), Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(ProcessServiceItem.Status), Width = 95 },
            new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = nameof(ProcessServiceItem.ServiceType), Width = 140 }
        );
        grpSvc.Controls.Add(_dgvServices);

        var grpWin = new GroupBox { Text = "Process windows", Dock = DockStyle.Fill, Padding = new Padding(4) };
        _dgvWindows = CreateStyledGrid();
        _dgvWindows.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "HWND", DataPropertyName = nameof(ProcessWindowItem.HandleHex), Width = 100 },
            new DataGridViewTextBoxColumn { HeaderText = "Visible", DataPropertyName = nameof(ProcessWindowItem.VisibleDisplay), Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "Title", DataPropertyName = nameof(ProcessWindowItem.Title), Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Class", DataPropertyName = nameof(ProcessWindowItem.ClassName), Width = 150 }
        );
        grpWin.Controls.Add(_dgvWindows);

        split.Panel1.Controls.Add(grpSvc);
        split.Panel2.Controls.Add(grpWin);

        page.HandleCreated += (_, _) =>
        {
            page.BeginInvoke(() =>
            {
                if (split.Width > 300)
                {
                    split.SplitterDistance = split.Width / 2;
                }
            });
        };

        page.Controls.Add(split);
    }

    private async Task RefreshDataAsync(bool forceDeep = false)
    {
        if (_isRefreshing || _contextMenuOpen)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            ProcessSnapshotResult snapshot = await _monitor.GetSnapshotAsync();
            if (IsDisposed)
            {
                return;
            }

            _lastSystemStats = snapshot.SystemStats;
            _allProcesses = snapshot.Processes;
            _allConnections = snapshot.NetworkConnections;

            RecordProcessHistory(snapshot.Processes, snapshot.SystemStats.TotalPhysicalMemoryBytes);
            UpdateKpiDisplay(snapshot.SystemStats);
            ApplyFilterAndRender(preserveScroll: true);
            RenderGlobalNetworkGrid();

            _refreshTickCounter++;
            if (forceDeep || _refreshTickCounter % 2 == 0)
            {
                await RefreshInspectorForSelectedAsync(forceDeep);
            }
            else
            {
                UpdateSummaryAndNetworkForSelected();
            }
        }
        catch
        {
            // Prevent background timer crash
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RecordProcessHistory(List<ProcessInfoItem> processes, long totalRam)
    {
        var alivePids = new HashSet<int>(processes.Count);
        foreach (ProcessInfoItem p in processes)
        {
            alivePids.Add(p.Pid);

            if (!_processCpuHistory.TryGetValue(p.Pid, out var cpuQ))
            {
                cpuQ = new Queue<double>();
                _processCpuHistory[p.Pid] = cpuQ;
            }
            if (cpuQ.Count >= 60)
            {
                cpuQ.Dequeue();
            }
            cpuQ.Enqueue(p.CpuPercent);

            double ramPct = totalRam > 0 ? p.WorkingSetBytes * 100.0 / totalRam : 0;
            if (!_processRamPercentHistory.TryGetValue(p.Pid, out var ramQ))
            {
                ramQ = new Queue<double>();
                _processRamPercentHistory[p.Pid] = ramQ;
            }
            if (ramQ.Count >= 60)
            {
                ramQ.Dequeue();
            }
            ramQ.Enqueue(ramPct);
        }

        foreach (int oldPid in _processCpuHistory.Keys.Where(pid => !alivePids.Contains(pid)).ToList())
        {
            _processCpuHistory.Remove(oldPid);
            _processRamPercentHistory.Remove(oldPid);
        }
    }

    private void UpdateKpiDisplay(SystemPerformanceSnapshot s)
    {
        _sparkCpu.AddValue(s.TotalCpuPercent, $"{s.ProcessorCount} logical cores");
        _sparkRam.AddValue(
            s.MemoryUsagePercent,
            $"{FileSystemItem.FormatBytes(s.UsedPhysicalMemoryBytes)} / {FileSystemItem.FormatBytes(s.TotalPhysicalMemoryBytes)}");

        _lblKpiDiskNet.Text =
            $"Disk Read: {ProcessInfoItem.FormatSpeed(s.TotalDiskReadBytesPerSec)}  |  Write: {ProcessInfoItem.FormatSpeed(s.TotalDiskWriteBytesPerSec)}\n" +
            $"Active Network: {s.TotalTcpConnections:N0} TCP  |  {s.TotalUdpEndpoints:N0} UDP  |  Commit: {FileSystemItem.FormatBytes(s.CommitTotalBytes)}";

        _lblKpiCounts.Text =
            $"Processes: {s.TotalProcesses:N0}  |  Threads: {s.TotalThreads:N0}  |  Handles: {s.TotalHandles:N0}\n" +
            $"System Uptime: {ProcessInfoItem.FormatDuration(s.SystemUptime)}";
    }

    private void ApplyFilterAndRender(bool preserveScroll)
    {
        string query = _txtSearch.Text.Trim();
        int quickFilterIdx = _cmbQuickFilter.SelectedIndex;

        IEnumerable<ProcessInfoItem> filtered = _allProcesses;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(query, StringComparison.Ordinal) ||
                p.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.CommandLine.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.UserName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.FileDescription.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.MainWindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = quickFilterIdx switch
        {
            1 => filtered.Where(p => p.HasMainWindow),
            2 => filtered.Where(p => p.CpuPercent > 5.0),
            3 => filtered.Where(p => p.WorkingSetBytes > 200L * 1024 * 1024),
            4 => filtered.Where(p => p.ActiveConnectionsCount > 0),
            5 => filtered.Where(p => p.IsElevated),
            6 => filtered.Where(p => p.IsSuspended || !p.IsResponding),
            _ => filtered
        };

        List<ProcessInfoItem> list = filtered.ToList();

        if (_isTreeMode && string.IsNullOrEmpty(query) && quickFilterIdx == 0)
        {
            _displayedProcesses = ProcessMonitorService.BuildTreeOrder(list);
        }
        else
        {
            foreach (ProcessInfoItem p in list)
            {
                p.TreeDisplayName = p.Name;
                p.TreeDepth = 0;
            }
            SortProcessList(list);
            _displayedProcesses = list;
        }

        int scrollIndex = preserveScroll && _dgvProcesses.FirstDisplayedScrollingRowIndex >= 0
            ? _dgvProcesses.FirstDisplayedScrollingRowIndex
            : 0;
        int? targetPid = _selectedPid;

        _dgvProcesses.SelectionChanged -= DgvProcesses_SelectionChanged;
        try
        {
            _dgvProcesses.SuspendLayout();
            _dgvProcesses.DataSource = null;
            _dgvProcesses.DataSource = _displayedProcesses;

            if (targetPid.HasValue)
            {
                for (int i = 0; i < _displayedProcesses.Count; i++)
                {
                    if (_displayedProcesses[i].Pid == targetPid.Value)
                    {
                        _dgvProcesses.Rows[i].Selected = true;
                        break;
                    }
                }
            }

            if (preserveScroll && _dgvProcesses.Rows.Count > 0)
            {
                _dgvProcesses.FirstDisplayedScrollingRowIndex = Math.Clamp(scrollIndex, 0, _dgvProcesses.Rows.Count - 1);
            }
        }
        finally
        {
            _dgvProcesses.ResumeLayout();
            _dgvProcesses.SelectionChanged += DgvProcesses_SelectionChanged;
        }

        UpdateSortGlyphs();
        UpdateProcessActionButtons();
        _lblProcessCountInfo.Text = $"Showing {_displayedProcesses.Count:N0} of {_allProcesses.Count:N0} processes";
    }

    private void SortProcessList(List<ProcessInfoItem> list)
    {
        Comparison<ProcessInfoItem> cmp = _sortColumnName switch
        {
            "colName" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            "colPid" => (a, b) => a.Pid.CompareTo(b.Pid),
            "colPpid" => (a, b) => a.ParentPid.CompareTo(b.ParentPid),
            "colStatus" => (a, b) => a.StatusKind.CompareTo(b.StatusKind),
            "colCpu" => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent),
            "colRam" => (a, b) => a.WorkingSetBytes.CompareTo(b.WorkingSetBytes),
            "colPrivateBytes" => (a, b) => a.PrivateBytes.CompareTo(b.PrivateBytes),
            "colVirtualBytes" => (a, b) => a.VirtualBytes.CompareTo(b.VirtualBytes),
            "colDiskRead" => (a, b) => a.IoReadBytesPerSec.CompareTo(b.IoReadBytesPerSec),
            "colDiskWrite" => (a, b) => a.IoWriteBytesPerSec.CompareTo(b.IoWriteBytesPerSec),
            "colNetCount" => (a, b) => a.ActiveConnectionsCount.CompareTo(b.ActiveConnectionsCount),
            "colPriority" => (a, b) => Nullable.Compare(a.PriorityClass, b.PriorityClass),
            "colThreads" => (a, b) => a.ThreadCount.CompareTo(b.ThreadCount),
            "colHandles" => (a, b) => a.HandleCount.CompareTo(b.HandleCount),
            "colGdi" => (a, b) => a.GdiObjects.CompareTo(b.GdiObjects),
            "colUserObj" => (a, b) => a.UserObjects.CompareTo(b.UserObjects),
            "colUser" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.UserName, b.UserName),
            "colIntegrity" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.IntegrityLevel, b.IntegrityLevel),
            "colElevated" => (a, b) => a.IsElevated.CompareTo(b.IsElevated),
            "colArch" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Architecture, b.Architecture),
            "colSession" => (a, b) => a.SessionId.CompareTo(b.SessionId),
            "colCpuTime" => (a, b) => a.TotalProcessorTime.CompareTo(b.TotalProcessorTime),
            "colStartTime" => (a, b) => Nullable.Compare(a.StartTime, b.StartTime),
            "colServicesCount" => (a, b) => a.HostedServicesCount.CompareTo(b.HostedServicesCount),
            "colWindowTitle" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.MainWindowTitle, b.MainWindowTitle),
            "colCompany" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.CompanyName, b.CompanyName),
            "colDescription" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FileDescription, b.FileDescription),
            "colCmdLine" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.CommandLine, b.CommandLine),
            "colPath" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ExecutablePath, b.ExecutablePath),
            _ => (a, b) => a.CpuPercent.CompareTo(b.CpuPercent)
        };

        list.Sort((a, b) =>
        {
            int res = _sortAscending ? cmp(a, b) : cmp(b, a);
            return res != 0 ? res : a.Pid.CompareTo(b.Pid);
        });
    }

    private void DgvProcesses_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0)
        {
            return;
        }

        string colName = _dgvProcesses.Columns[e.ColumnIndex].Name;
        if (colName == "colIcon")
        {
            return;
        }

        if (_isTreeMode)
        {
            _isTreeMode = false;
            UpdateTreeToggleVisual();
        }

        if (string.Equals(_sortColumnName, colName, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumnName = colName;
            _sortAscending = colName is "colName" or "colPid" or "colUser" or "colCompany";
        }

        ApplyFilterAndRender(preserveScroll: false);
    }

    private void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn col in _dgvProcesses.Columns)
        {
            if (col.SortMode != DataGridViewColumnSortMode.Programmatic)
            {
                continue;
            }

            col.HeaderCell.SortGlyphDirection = !_isTreeMode && string.Equals(col.Name, _sortColumnName, StringComparison.Ordinal)
                ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
        }
    }

    private void DgvProcesses_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _displayedProcesses.Count)
        {
            return;
        }

        ProcessInfoItem item = _displayedProcesses[e.RowIndex];
        DataGridViewRow row = _dgvProcesses.Rows[e.RowIndex];

        if (item.StatusKind == ProcessStatusKind.NotResponding)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 232, 232);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(145, 20, 20);
        }
        else if (item.StatusKind == ProcessStatusKind.Suspended)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(236, 242, 250);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(75, 95, 125);
        }
        else if (item.Pid <= 4)
        {
            row.DefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
            row.DefaultCellStyle.ForeColor = Color.FromArgb(70, 75, 82);
        }
        else
        {
            row.DefaultCellStyle.BackColor = SystemColors.Window;
            row.DefaultCellStyle.ForeColor = SystemColors.ControlText;
        }

        string colName = _dgvProcesses.Columns[e.ColumnIndex].Name;
        if (colName == "colCpu" && e.CellStyle is not null)
        {
            if (item.CpuPercent >= 25.0)
            {
                e.CellStyle.BackColor = Color.FromArgb(255, 214, 204);
                e.CellStyle.ForeColor = Color.FromArgb(150, 25, 10);
                e.CellStyle.Font = new Font(_dgvProcesses.Font, FontStyle.Bold);
            }
            else if (item.CpuPercent >= 5.0)
            {
                e.CellStyle.BackColor = Color.FromArgb(255, 243, 205);
                e.CellStyle.ForeColor = Color.FromArgb(120, 75, 0);
            }
        }
    }

    private async void DgvProcesses_SelectionChanged(object? sender, EventArgs e)
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        _selectedPid = proc?.Pid;
        UpdateProcessActionButtons();

        if (proc is not null && proc.Pid != _lastDeepInspectedPid)
        {
            _txtProcSha256.Clear();
            await RefreshInspectorForSelectedAsync(forceDeep: true);
        }
        else
        {
            UpdateSummaryAndNetworkForSelected();
        }
    }

    private async Task OnInspectorTabChangedAsync()
    {
        if (_tabInspector.SelectedIndex == 5)
        {
            await LoadEnvironmentForSelectedAsync();
        }
        else if (_tabInspector.SelectedIndex == 6)
        {
            await LoadHandlesForSelectedAsync();
        }
    }

    private async Task RefreshInspectorForSelectedAsync(bool forceDeep)
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null)
        {
            _lblInspectorBanner.Text = "Process Inspector — No process selected";
            _txtProcSummary.Clear();
            _txtProcCmdLine.Clear();
            _sparkProcCpu.ClearValues();
            _sparkProcRam.ClearValues();
            _dgvThreads.DataSource = null;
            _dgvModules.DataSource = null;
            _dgvProcNetwork.DataSource = null;
            _dgvServices.DataSource = null;
            _dgvWindows.DataSource = null;
            return;
        }

        UpdateSummaryAndNetworkForSelected();

        int pid = proc.Pid;
        if (forceDeep || _lastDeepInspectedPid != pid)
        {
            _lastDeepInspectedPid = pid;
            ProcessDeepDetails details = await _monitor.GetProcessDetailsAsync(pid);
            if (IsDisposed || _selectedPid != pid)
            {
                return;
            }

            _dgvThreads.DataSource = details.Threads;
            _tabInspector.TabPages[1].Text = $"Threads ({details.Threads.Count:N0})";

            _currentModules = details.Modules;
            _lblModulesNote.Text = details.ModulesNote;
            RenderModulesGrid();
            _tabInspector.TabPages[2].Text = $"Modules / DLLs ({details.Modules.Count:N0})";

            _dgvServices.DataSource = details.Services;
            _dgvWindows.DataSource = details.Windows;
            _tabInspector.TabPages[7].Text = $"Services ({details.Services.Count}) / Windows ({details.Windows.Count})";

            if (_tabInspector.SelectedIndex == 5 && _lastEnvLoadedPid != pid)
            {
                await LoadEnvironmentForSelectedAsync();
            }
            else if (_tabInspector.SelectedIndex == 6 && _lastHandlesLoadedPid != pid)
            {
                await LoadHandlesForSelectedAsync();
            }
        }
    }

    private void UpdateSummaryAndNetworkForSelected()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null)
        {
            return;
        }

        _lblInspectorBanner.Text = $"Process Inspector — {proc.Name} (PID: {proc.Pid} | PPID: {proc.ParentPid} | {proc.StatusDisplay})";
        _txtProcCmdLine.Text = string.IsNullOrEmpty(proc.CommandLine) ? "(Unavailable)" : proc.CommandLine;

        // Update mini sparklines from recorded per-PID history
        _sparkProcCpu.ClearValues();
        if (_processCpuHistory.TryGetValue(proc.Pid, out var cpuQ))
        {
            foreach (double v in cpuQ)
            {
                _sparkProcCpu.AddValue(v, $"{proc.TotalCpuTimeDisplay} total");
            }
        }

        _sparkProcRam.ClearValues();
        if (_processRamPercentHistory.TryGetValue(proc.Pid, out var ramQ))
        {
            foreach (double v in ramQ)
            {
                _sparkProcRam.AddValue(v, $"WS: {proc.WorkingSetDisplay} | Priv: {proc.PrivateBytesDisplay}");
            }
        }

        _txtProcSummary.Text =
            $"Process:             {proc.Name} (PID {proc.Pid}, Parent PID {proc.ParentPid})\r\n" +
            $"Status:              {proc.StatusDisplay}\r\n" +
            $"Executable:          {(string.IsNullOrEmpty(proc.ExecutablePath) ? "(No accesible)" : proc.ExecutablePath)}\r\n" +
            $"Description:         {proc.FileDescription}\r\n" +
            $"Company / Product:   {proc.CompanyName} {(string.IsNullOrEmpty(proc.ProductName) ? string.Empty : $"({proc.ProductName})")}\r\n" +
            $"File Version:        {proc.FileVersion}\r\n" +
            $"User / Session:      {proc.UserName} (Session {proc.SessionId})\r\n" +
            $"Integrity / Admin:   {proc.IntegrityLevel} | Elevated: {proc.ElevatedDisplay} | Arch.: {proc.Architecture}\r\n" +
            $"Start Time / Uptime: {proc.StartTimeDisplay} ({proc.UptimeDisplay})\r\n" +
            $"Priority / Affinity: {proc.PriorityDisplay} (Base {proc.BasePriority}) | Affinity Mask: 0x{proc.AffinityMask:X}\r\n" +
            $"CPU (Total/User/Kernel): {proc.TotalCpuTimeDisplay} | User: {ProcessInfoItem.FormatDuration(proc.UserProcessorTime)} | Kernel: {ProcessInfoItem.FormatDuration(proc.KernelProcessorTime)}\r\n" +
            $"Working Set / Peak:  {proc.WorkingSetDisplay} / {FileSystemItem.FormatBytes(proc.PeakWorkingSetBytes)}\r\n" +
            $"Private Memory:      {proc.PrivateBytesDisplay} | Virtual: {proc.VirtualBytesDisplay} | Pagefile: {FileSystemItem.FormatBytes(proc.PagefileBytes)}\r\n" +
            $"Pools (Paged/NonP):  {FileSystemItem.FormatBytes(proc.PagedPoolBytes)} / {FileSystemItem.FormatBytes(proc.NonPagedPoolBytes)} | Page Faults: {proc.PageFaultCount:N0}\r\n" +
            $"Resources:           Threads: {proc.ThreadCount:N0} | Handles: {proc.HandleCount:N0} | GDI: {proc.GdiObjects:N0} | USER: {proc.UserObjects:N0}\r\n" +
            $"Total I/O Read:      {FileSystemItem.FormatBytes(proc.IoTotalReadBytes)} ({proc.IoReadOps:N0} ops, {proc.IoReadSpeedDisplay})\r\n" +
            $"Total I/O Write:     {FileSystemItem.FormatBytes(proc.IoTotalWriteBytes)} ({proc.IoWriteOps:N0} ops, {proc.IoWriteSpeedDisplay})\r\n" +
            $"Total Other I/O:     {FileSystemItem.FormatBytes(proc.IoTotalOtherBytes)} ({proc.IoOtherOps:N0} ops)\r\n" +
            $"Main Window:         {(string.IsNullOrEmpty(proc.MainWindowTitle) ? "(Ninguna)" : proc.MainWindowTitle)}";

        List<NetworkConnectionItem> procConns = _allConnections.Where(c => c.Pid == proc.Pid).ToList();
        _dgvProcNetwork.DataSource = procConns;
        _tabInspector.TabPages[3].Text = $"Process Network ({procConns.Count})";
    }

    private void RenderModulesGrid()
    {
        string filter = _txtModuleFilter.Text.Trim();
        List<ProcessModuleInfoItem> items = string.IsNullOrEmpty(filter)
            ? _currentModules
            : _currentModules.Where(m =>
                m.ModuleName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.CompanyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _dgvModules.DataSource = items;
    }

    private void RenderGlobalNetworkGrid()
    {
        string filter = _txtGlobalNetFilter.Text.Trim();
        List<NetworkConnectionItem> items = string.IsNullOrEmpty(filter)
            ? _allConnections
            : _allConnections.Where(c =>
                c.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Pid.ToString().Contains(filter, StringComparison.Ordinal) ||
                c.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.LocalEndpoint.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteEndpoint.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.State.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _dgvGlobalNetwork.DataSource = items;
        _tabInspector.TabPages[4].Text = $"System Network ({_allConnections.Count:N0})";
    }

    private async Task LoadEnvironmentForSelectedAsync()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null)
        {
            _dgvEnv.DataSource = null;
            _lblEnvNote.Text = string.Empty;
            return;
        }

        int pid = proc.Pid;
        _lastEnvLoadedPid = pid;
        _lblEnvNote.Text = "Reading the environment block...";
        var (items, note) = await _monitor.GetEnvironmentVariablesAsync(pid);
        if (IsDisposed || _selectedPid != pid)
        {
            return;
        }

        _currentEnvVars = items;
        _lblEnvNote.Text = note;
        RenderEnvironmentGrid();
    }

    private void RenderEnvironmentGrid()
    {
        string filter = _txtEnvFilter.Text.Trim();
        List<ProcessEnvironmentVariableItem> items = string.IsNullOrEmpty(filter)
            ? _currentEnvVars
            : _currentEnvVars.Where(v =>
                v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                v.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _dgvEnv.DataSource = items;
    }

    private async Task LoadHandlesForSelectedAsync()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null)
        {
            _dgvHandles.DataSource = null;
            _lblHandlesNote.Text = string.Empty;
            return;
        }

        int pid = proc.Pid;
        _lastHandlesLoadedPid = pid;
        _lblHandlesNote.Text = "Enumerating open handles...";
        var (items, note) = await _monitor.GetProcessHandlesAsync(pid);
        if (IsDisposed || _selectedPid != pid)
        {
            return;
        }

        _currentHandles = items;
        _lblHandlesNote.Text = string.IsNullOrEmpty(note)
            ? $"{items.Count:N0} handles loaded."
            : note;
        _tabInspector.TabPages[6].Text = $"Handles ({items.Count:N0})";
        RenderHandlesGrid();
    }

    private void RenderHandlesGrid()
    {
        string filter = _txtHandlesFilter.Text.Trim();
        List<ProcessHandleInfoItem> items = string.IsNullOrEmpty(filter)
            ? _currentHandles
            : _currentHandles.Where(h =>
                h.ObjectType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                h.ObjectName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                h.HandleValueHex.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _dgvHandles.DataSource = items;
    }

    private ProcessInfoItem? GetSelectedProcess()
    {
        if (_dgvProcesses.SelectedRows.Count == 0)
        {
            return null;
        }
        return _dgvProcesses.SelectedRows[0].DataBoundItem as ProcessInfoItem;
    }

    private void SelectProcessByPid(int pid)
    {
        for (int i = 0; i < _displayedProcesses.Count; i++)
        {
            if (_displayedProcesses[i].Pid == pid)
            {
                _dgvProcesses.ClearSelection();
                _dgvProcesses.Rows[i].Selected = true;
                _dgvProcesses.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
                return;
            }
        }
    }

    private void UpdateProcessActionButtons()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        bool canControl = proc is not null && proc.Pid > 4;
        _btnEndTask.Enabled = canControl;
        _btnEndTree.Enabled = canControl;
        _btnSuspendResume.Enabled = canControl;
        _btnSuspendResume.Text = proc?.IsSuspended == true ? "Reanudar" : "Suspend";
        _btnAffinity.Enabled = canControl;
        _btnCreateDump.Enabled = canControl;
        _btnOpenLocation.Enabled = proc is not null && !string.IsNullOrEmpty(proc.ExecutablePath);
        _btnComputeHash.Enabled = proc is not null && !string.IsNullOrEmpty(proc.ExecutablePath) && File.Exists(proc.ExecutablePath);
    }

    private void KillSelectedProcess(bool entireTree)
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || proc.Pid <= 4)
        {
            return;
        }

        string msg = entireTree
            ? $"End '{proc.Name}' (PID {proc.Pid}) and all its child processes?"
            : $"End '{proc.Name}' (PID {proc.Pid})?";

        if (MessageBox.Show(this, msg, "Confirm Termination", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ProcessMonitorService.KillProcess(proc.Pid, entireTree);
            _ = RefreshDataAsync(forceDeep: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not end the process:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleSuspendSelectedProcess()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || proc.Pid <= 4)
        {
            return;
        }

        if (proc.Pid == Environment.ProcessId)
        {
            MessageBox.Show(this, "You cannot suspend Unit Manager from this window.", "Action Blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (proc.IsSuspended)
            {
                ProcessMonitorService.ResumeProcess(proc.Pid);
            }
            else
            {
                if (MessageBox.Show(
                    this,
                    $"Suspend '{proc.Name}' (PID {proc.Pid})?\nThe process will remain paused until you resume it.",
                    "Suspend process",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
                ProcessMonitorService.SuspendProcess(proc.Pid);
            }

            _ = RefreshDataAsync(forceDeep: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not suspend or resume the process:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetSelectedPriority(ProcessPriorityClass priority)
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || proc.Pid <= 4)
        {
            return;
        }

        try
        {
            ProcessMonitorService.SetProcessPriority(proc.Pid, priority);
            _ = RefreshDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change priority:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ConfigureAffinityForSelected()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || proc.Pid <= 4)
        {
            return;
        }

        using var dlg = new AffinityDialog(proc.Name, proc.Pid, proc.AffinityMask);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ProcessMonitorService.SetProcessAffinity(proc.Pid, dlg.SelectedMask);
            _ = RefreshDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change CPU affinity:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CreateMiniDumpForSelectedAsync()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || proc.Pid <= 4)
        {
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = $"Save Memory Dump - {proc.Name}",
            Filter = "Dump files (*.dmp)|*.dmp",
            FileName = $"{Path.GetFileNameWithoutExtension(proc.Name)}_{proc.Pid}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp"
        };

        if (sfd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        DialogResult fullChoice = MessageBox.Show(
            this,
            "Create a full memory dump?\n\n• Yes = Full dump (includes all allocated memory, large file)\n• No = Standard minidump (threads, modules, and handles)",
            "Dump Type (.dmp)",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (fullChoice == DialogResult.Cancel)
        {
            return;
        }

        bool fullMemory = fullChoice == DialogResult.Yes;
        try
        {
            Cursor = Cursors.WaitCursor;
            await ProcessMonitorService.CreateMiniDumpAsync(proc.Pid, sfd.FileName, fullMemory);
            MessageBox.Show(this, $"Memory dump saved to:\n{sfd.FileName}", "Memory Dump Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create the memory dump:\n{ex.Message}", "Memory Dump Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private async Task ComputeSelectedSha256Async()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || string.IsNullOrEmpty(proc.ExecutablePath) || !File.Exists(proc.ExecutablePath))
        {
            return;
        }

        try
        {
            _btnComputeHash.Enabled = false;
            _txtProcSha256.Text = "Computing SHA-256...";
            string hash = await ProcessMonitorService.ComputeFileSha256Async(proc.ExecutablePath);
            if (!IsDisposed && _selectedPid == proc.Pid)
            {
                _txtProcSha256.Text = hash;
            }
        }
        catch (Exception ex)
        {
            _txtProcSha256.Text = $"Error: {ex.Message}";
        }
        finally
        {
            UpdateProcessActionButtons();
        }
    }

    private void ControlSelectedThread(bool suspend)
    {
        if (_dgvThreads.SelectedRows.Count == 0 ||
            _dgvThreads.SelectedRows[0].DataBoundItem is not ProcessThreadInfoItem thread)
        {
            return;
        }

        try
        {
            if (suspend)
            {
                ProcessMonitorService.SuspendThread((uint)thread.ThreadId);
            }
            else
            {
                ProcessMonitorService.ResumeThread((uint)thread.ThreadId);
            }
            _ = RefreshInspectorForSelectedAsync(forceDeep: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not control thread {thread.ThreadId}:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedExecutableLocation()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || string.IsNullOrEmpty(proc.ExecutablePath))
        {
            return;
        }

        if (File.Exists(proc.ExecutablePath))
        {
            LaunchExplorer($"/select,\"{proc.ExecutablePath}\"");
        }
    }

    private void SendSelectedFolderToFileViewer()
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null || string.IsNullOrEmpty(proc.ExecutablePath))
        {
            return;
        }

        ExplorePathInFileViewerRequested?.Invoke(proc.ExecutablePath);
    }

    private void CopySelectedField(Func<ProcessInfoItem, string> selector)
    {
        ProcessInfoItem? proc = GetSelectedProcess();
        if (proc is null)
        {
            return;
        }

        string text = selector(proc);
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private void BtnNewTask_Click(object? sender, EventArgs e)
    {
        using var dlg = new RunTaskDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(dlg.CommandText, dlg.ArgumentsText)
            {
                UseShellExecute = true
            };
            if (dlg.RunAsAdministrator)
            {
                psi.Verb = "runas";
            }
            Process.Start(psi);
            _ = RefreshDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start the task:\n{ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnElevate_Click(object? sender, EventArgs e)
    {
        if (ProcessMonitorService.IsCurrentProcessElevated())
        {
            MessageBox.Show(
                this,
                "Unit Manager is already running as administrator.",
                "Administrator Mode Active",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
            this,
            "Running Unit Manager as administrator lets you inspect command lines, modules, environment variables, and handles for system processes.\n\nRestart with administrator permissions?",
            "Restart as Administrator",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            Program.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not elevate the process:\n{ex.Message}", "Elevation Canceled", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyElevationBadge()
    {
        if (ProcessMonitorService.IsCurrentProcessElevated())
        {
            _btnElevate.Text = "Admin Active";
            _btnElevate.ForeColor = Color.DarkGreen;
        }
        else
        {
            _btnElevate.Text = "Run as Admin";
        }
    }

    private void UpdateTreeToggleVisual()
    {
        _btnTreeToggle.Text = _isTreeMode ? "Tree View" : "List View";
    }

    private void UpdateTimerInterval()
    {
        if (_isPaused || _cmbInterval.SelectedIndex == 5)
        {
            _refreshTimer.Stop();
            return;
        }

        _refreshTimer.Interval = _cmbInterval.SelectedIndex switch
        {
            0 => 500,
            1 => 1000,
            2 => 2000,
            3 => 3000,
            4 => 5000,
            _ => 3000
        };
        _refreshTimer.Start();
    }

    private void LaunchExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open File Explorer:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static DataGridView CreateStyledGrid()
    {
        var dgv = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            Dock = DockStyle.Fill,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            RowTemplate = { Height = 22 },
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        typeof(DataGridView)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(dgv, true);

        return dgv;
    }

    private static DataGridViewCellStyle RightAlignStyle() =>
        new() { Alignment = DataGridViewContentAlignment.MiddleRight };

    private static void DgvProcesses_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        SelectRowOnRightClick(sender, e);
    }

    private static void SelectRowOnRightClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (sender is not DataGridView dgv || e.Button != MouseButtons.Right || e.RowIndex < 0)
        {
            return;
        }

        dgv.ClearSelection();
        dgv.Rows[e.RowIndex].Selected = true;
        if (e.ColumnIndex >= 0 && e.ColumnIndex < dgv.Columns.Count)
        {
            dgv.CurrentCell = dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
        }
    }
}
