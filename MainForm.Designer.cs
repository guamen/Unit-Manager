#nullable enable
using FileViewer.Controls;
using FileViewer.Models;

namespace FileViewer;

partial class MainForm
{
    private System.ComponentModel.IContainer? components;

    private TabControl tabMain = null!;
    private TabPage tabPageSearch = null!;
    private TabPage tabPageTaskManager = null!;
    private TaskManagerControl taskManagerControl = null!;

    private GroupBox grpFilters = null!;
    private TableLayoutPanel tlpFilters = null!;
    private Label lblDrive = null!;
    private Panel pnlDrive = null!;
    private ComboBox cmbDrive = null!;
    private Button btnRefreshDrives = null!;
    private Label lblPattern = null!;
    private TextBox txtPattern = null!;
    private Label lblTargetType = null!;
    private ComboBox cmbTargetType = null!;
    private Label lblExtension = null!;
    private TextBox txtExtension = null!;
    private CheckBox chkMinSize = null!;
    private NumericUpDown numMinSize = null!;
    private CheckBox chkMaxSize = null!;
    private Panel pnlMaxSize = null!;
    private NumericUpDown numMaxSize = null!;
    private ComboBox cmbSizeUnit = null!;
    private CheckBox chkJunkFiles = null!;
    private ComboBox cmbJunkCategory = null!;
    private Label lblJunkHint = null!;
    private Panel pnlRowDatesAndActions = null!;
    private FlowLayoutPanel flpDates = null!;
    private CheckBox chkDateFrom = null!;
    private DateTimePicker dtpDateFrom = null!;
    private CheckBox chkDateTo = null!;
    private DateTimePicker dtpDateTo = null!;
    private FlowLayoutPanel flpSearchActions = null!;
    private Button btnSearch = null!;
    private Button btnStop = null!;
    private Button btnClear = null!;

    private Panel pnlGrid = null!;
    private DataGridView dgvResults = null!;
    private DataGridViewTextBoxColumn colType = null!;
    private DataGridViewTextBoxColumn colName = null!;
    private DataGridViewTextBoxColumn colExtension = null!;
    private DataGridViewTextBoxColumn colJunkCategory = null!;
    private DataGridViewTextBoxColumn colSize = null!;
    private DataGridViewTextBoxColumn colCreated = null!;
    private DataGridViewTextBoxColumn colFullPath = null!;
    private DataGridViewButtonColumn colExplore = null!;
    private DataGridViewButtonColumn colDelete = null!;

    private ContextMenuStrip cmsGrid = null!;
    private ToolStripMenuItem mnuExplore = null!;
    private ToolStripMenuItem mnuDelete = null!;

    private Panel pnlBottom = null!;
    private Panel pnlActions = null!;
    private Panel pnlPagination = null!;
    private FlowLayoutPanel flpPagination = null!;
    private Button btnFirstPage = null!;
    private Button btnPrevPage = null!;
    private Label lblPageInfo = null!;
    private Button btnNextPage = null!;
    private Button btnLastPage = null!;
    private FlowLayoutPanel flpSelectionActions = null!;
    private Button btnExploreSelected = null!;
    private Button btnDeleteSelected = null!;

    private Panel pnlStatus = null!;
    private Label lblStatus = null!;
    private ProgressBar progressBar = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _searchCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        tabMain = new TabControl();
        tabPageSearch = new TabPage();
        tabPageTaskManager = new TabPage();
        taskManagerControl = new TaskManagerControl();

        grpFilters = new GroupBox();
        tlpFilters = new TableLayoutPanel();
        lblDrive = new Label();
        pnlDrive = new Panel();
        cmbDrive = new ComboBox();
        btnRefreshDrives = new Button();
        lblPattern = new Label();
        txtPattern = new TextBox();
        lblTargetType = new Label();
        cmbTargetType = new ComboBox();
        lblExtension = new Label();
        txtExtension = new TextBox();
        chkMinSize = new CheckBox();
        numMinSize = new NumericUpDown();
        chkMaxSize = new CheckBox();
        pnlMaxSize = new Panel();
        numMaxSize = new NumericUpDown();
        cmbSizeUnit = new ComboBox();
        chkJunkFiles = new CheckBox();
        cmbJunkCategory = new ComboBox();
        lblJunkHint = new Label();
        pnlRowDatesAndActions = new Panel();
        flpDates = new FlowLayoutPanel();
        chkDateFrom = new CheckBox();
        dtpDateFrom = new DateTimePicker();
        chkDateTo = new CheckBox();
        dtpDateTo = new DateTimePicker();
        flpSearchActions = new FlowLayoutPanel();
        btnSearch = new Button();
        btnStop = new Button();
        btnClear = new Button();

        pnlGrid = new Panel();
        dgvResults = new DataGridView();
        colType = new DataGridViewTextBoxColumn();
        colName = new DataGridViewTextBoxColumn();
        colExtension = new DataGridViewTextBoxColumn();
        colJunkCategory = new DataGridViewTextBoxColumn();
        colSize = new DataGridViewTextBoxColumn();
        colCreated = new DataGridViewTextBoxColumn();
        colFullPath = new DataGridViewTextBoxColumn();
        colExplore = new DataGridViewButtonColumn();
        colDelete = new DataGridViewButtonColumn();

        cmsGrid = new ContextMenuStrip(components);
        mnuExplore = new ToolStripMenuItem();
        mnuDelete = new ToolStripMenuItem();

        pnlBottom = new Panel();
        pnlActions = new Panel();
        pnlPagination = new Panel();
        flpPagination = new FlowLayoutPanel();
        btnFirstPage = new Button();
        btnPrevPage = new Button();
        lblPageInfo = new Label();
        btnNextPage = new Button();
        btnLastPage = new Button();
        flpSelectionActions = new FlowLayoutPanel();
        btnExploreSelected = new Button();
        btnDeleteSelected = new Button();

        pnlStatus = new Panel();
        lblStatus = new Label();
        progressBar = new ProgressBar();

        tabMain.SuspendLayout();
        tabPageSearch.SuspendLayout();
        tabPageTaskManager.SuspendLayout();
        grpFilters.SuspendLayout();
        tlpFilters.SuspendLayout();
        pnlDrive.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)numMinSize).BeginInit();
        pnlMaxSize.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)numMaxSize).BeginInit();
        pnlRowDatesAndActions.SuspendLayout();
        flpDates.SuspendLayout();
        flpSearchActions.SuspendLayout();
        pnlGrid.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)dgvResults).BeginInit();
        cmsGrid.SuspendLayout();
        pnlBottom.SuspendLayout();
        pnlActions.SuspendLayout();
        pnlPagination.SuspendLayout();
        flpPagination.SuspendLayout();
        flpSelectionActions.SuspendLayout();
        pnlStatus.SuspendLayout();
        SuspendLayout();

        //
        // grpFilters
        //
        grpFilters.Controls.Add(tlpFilters);
        grpFilters.Dock = DockStyle.Top;
        grpFilters.Height = 184;
        grpFilters.Name = "grpFilters";
        grpFilters.Padding = new Padding(10, 6, 10, 8);
        grpFilters.TabIndex = 0;
        grpFilters.TabStop = false;
        grpFilters.Text = "Search Filters";

        //
        // tlpFilters
        //
        tlpFilters.ColumnCount = 6;
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37F));
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tlpFilters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
        tlpFilters.Dock = DockStyle.Fill;
        tlpFilters.Name = "tlpFilters";
        tlpFilters.RowCount = 4;
        tlpFilters.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        tlpFilters.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        tlpFilters.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        tlpFilters.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        tlpFilters.TabIndex = 0;

        tlpFilters.Controls.Add(lblDrive, 0, 0);
        tlpFilters.Controls.Add(pnlDrive, 1, 0);
        tlpFilters.Controls.Add(lblPattern, 2, 0);
        tlpFilters.Controls.Add(txtPattern, 3, 0);
        tlpFilters.Controls.Add(lblTargetType, 4, 0);
        tlpFilters.Controls.Add(cmbTargetType, 5, 0);

        tlpFilters.Controls.Add(lblExtension, 0, 1);
        tlpFilters.Controls.Add(txtExtension, 1, 1);
        tlpFilters.Controls.Add(chkMinSize, 2, 1);
        tlpFilters.Controls.Add(numMinSize, 3, 1);
        tlpFilters.Controls.Add(chkMaxSize, 4, 1);
        tlpFilters.Controls.Add(pnlMaxSize, 5, 1);

        tlpFilters.Controls.Add(chkJunkFiles, 0, 2);
        tlpFilters.Controls.Add(cmbJunkCategory, 1, 2);
        tlpFilters.Controls.Add(lblJunkHint, 2, 2);
        tlpFilters.SetColumnSpan(lblJunkHint, 4);

        tlpFilters.Controls.Add(pnlRowDatesAndActions, 0, 3);
        tlpFilters.SetColumnSpan(pnlRowDatesAndActions, 6);

        //
        // Row 0
        //
        lblDrive.AutoSize = true;
        lblDrive.Anchor = AnchorStyles.Left;
        lblDrive.Margin = new Padding(3, 0, 6, 0);
        lblDrive.Name = "lblDrive";
        lblDrive.Text = "Drive:";

        pnlDrive.Dock = DockStyle.Fill;
        pnlDrive.Margin = new Padding(0, 4, 12, 4);
        pnlDrive.Name = "pnlDrive";
        pnlDrive.Controls.Add(cmbDrive);
        pnlDrive.Controls.Add(btnRefreshDrives);

        cmbDrive.Dock = DockStyle.Fill;
        cmbDrive.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDrive.FormattingEnabled = true;
        cmbDrive.Name = "cmbDrive";
        cmbDrive.TabIndex = 0;

        btnRefreshDrives.Dock = DockStyle.Right;
        btnRefreshDrives.Margin = new Padding(4, 0, 0, 0);
        btnRefreshDrives.Name = "btnRefreshDrives";
        btnRefreshDrives.TabIndex = 1;
        btnRefreshDrives.Text = "↻";
        btnRefreshDrives.UseVisualStyleBackColor = true;
        btnRefreshDrives.Width = 34;
        btnRefreshDrives.Click += btnRefreshDrives_Click;

        lblPattern.AutoSize = true;
        lblPattern.Anchor = AnchorStyles.Left;
        lblPattern.Margin = new Padding(3, 0, 6, 0);
        lblPattern.Name = "lblPattern";
        lblPattern.Text = "Pattern:";

        txtPattern.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        txtPattern.Margin = new Padding(0, 4, 12, 4);
        txtPattern.Name = "txtPattern";
        txtPattern.PlaceholderText = "e.g. report, *.pdf, invoice*";
        txtPattern.TabIndex = 2;

        lblTargetType.AutoSize = true;
        lblTargetType.Anchor = AnchorStyles.Left;
        lblTargetType.Margin = new Padding(3, 0, 6, 0);
        lblTargetType.Name = "lblTargetType";
        lblTargetType.Text = "Find:";

        cmbTargetType.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        cmbTargetType.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbTargetType.FormattingEnabled = true;
        cmbTargetType.Margin = new Padding(0, 4, 3, 4);
        cmbTargetType.Name = "cmbTargetType";
        cmbTargetType.TabIndex = 3;
        cmbTargetType.SelectedIndexChanged += cmbTargetType_SelectedIndexChanged;

        //
        // Row 1
        //
        lblExtension.AutoSize = true;
        lblExtension.Anchor = AnchorStyles.Left;
        lblExtension.Margin = new Padding(3, 0, 6, 0);
        lblExtension.Name = "lblExtension";
        lblExtension.Text = "Extension:";

        txtExtension.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        txtExtension.Margin = new Padding(0, 4, 12, 4);
        txtExtension.Name = "txtExtension";
        txtExtension.PlaceholderText = "e.g. .txt, .pdf, .docx";
        txtExtension.TabIndex = 4;

        chkMinSize.AutoSize = true;
        chkMinSize.Anchor = AnchorStyles.Left;
        chkMinSize.Margin = new Padding(3, 0, 6, 0);
        chkMinSize.Name = "chkMinSize";
        chkMinSize.TabIndex = 5;
        chkMinSize.Text = "Minimum Size:";
        chkMinSize.UseVisualStyleBackColor = true;
        chkMinSize.CheckedChanged += FilterToggle_CheckedChanged;

        numMinSize.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        numMinSize.Enabled = false;
        numMinSize.Margin = new Padding(0, 4, 12, 4);
        numMinSize.Maximum = 1000000m;
        numMinSize.Minimum = 0m;
        numMinSize.Name = "numMinSize";
        numMinSize.TabIndex = 6;
        numMinSize.ThousandsSeparator = true;

        chkMaxSize.AutoSize = true;
        chkMaxSize.Anchor = AnchorStyles.Left;
        chkMaxSize.Margin = new Padding(3, 0, 6, 0);
        chkMaxSize.Name = "chkMaxSize";
        chkMaxSize.TabIndex = 7;
        chkMaxSize.Text = "Maximum Size:";
        chkMaxSize.UseVisualStyleBackColor = true;
        chkMaxSize.CheckedChanged += FilterToggle_CheckedChanged;

        pnlMaxSize.Dock = DockStyle.Fill;
        pnlMaxSize.Margin = new Padding(0, 4, 3, 4);
        pnlMaxSize.Name = "pnlMaxSize";
        pnlMaxSize.Controls.Add(numMaxSize);
        pnlMaxSize.Controls.Add(cmbSizeUnit);

        numMaxSize.Dock = DockStyle.Fill;
        numMaxSize.Enabled = false;
        numMaxSize.Maximum = 1000000m;
        numMaxSize.Minimum = 0m;
        numMaxSize.Name = "numMaxSize";
        numMaxSize.TabIndex = 8;
        numMaxSize.ThousandsSeparator = true;
        numMaxSize.Value = 100m;

        cmbSizeUnit.Dock = DockStyle.Right;
        cmbSizeUnit.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSizeUnit.FormattingEnabled = true;
        cmbSizeUnit.Name = "cmbSizeUnit";
        cmbSizeUnit.TabIndex = 9;
        cmbSizeUnit.Width = 76;

        //
        // Row 2: Junk files
        //
        chkJunkFiles.AutoSize = true;
        chkJunkFiles.Anchor = AnchorStyles.Left;
        chkJunkFiles.Margin = new Padding(3, 0, 6, 0);
        chkJunkFiles.Name = "chkJunkFiles";
        chkJunkFiles.TabIndex = 10;
        chkJunkFiles.Text = "Junk files:";
        chkJunkFiles.UseVisualStyleBackColor = true;
        chkJunkFiles.CheckedChanged += chkJunkFiles_CheckedChanged;

        cmbJunkCategory.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        cmbJunkCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbJunkCategory.Enabled = false;
        cmbJunkCategory.FormattingEnabled = true;
        cmbJunkCategory.Margin = new Padding(0, 4, 12, 4);
        cmbJunkCategory.Name = "cmbJunkCategory";
        cmbJunkCategory.TabIndex = 11;
        cmbJunkCategory.SelectedIndexChanged += cmbJunkCategory_SelectedIndexChanged;

        lblJunkHint.AutoEllipsis = true;
        lblJunkHint.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        lblJunkHint.ForeColor = SystemColors.GrayText;
        lblJunkHint.Margin = new Padding(3, 0, 3, 0);
        lblJunkHint.Name = "lblJunkHint";
        lblJunkHint.Text = string.Empty;

        //
        // Row 3: Dates & Actions
        //
        pnlRowDatesAndActions.Dock = DockStyle.Fill;
        pnlRowDatesAndActions.Margin = new Padding(0, 4, 0, 0);
        pnlRowDatesAndActions.Name = "pnlRowDatesAndActions";
        pnlRowDatesAndActions.Controls.Add(flpDates);
        pnlRowDatesAndActions.Controls.Add(flpSearchActions);

        flpDates.AutoSize = true;
        flpDates.Dock = DockStyle.Left;
        flpDates.FlowDirection = FlowDirection.LeftToRight;
        flpDates.Name = "flpDates";
        flpDates.WrapContents = false;
        flpDates.Controls.Add(chkDateFrom);
        flpDates.Controls.Add(dtpDateFrom);
        flpDates.Controls.Add(chkDateTo);
        flpDates.Controls.Add(dtpDateTo);

        chkDateFrom.AutoSize = true;
        chkDateFrom.Margin = new Padding(3, 7, 6, 3);
        chkDateFrom.Name = "chkDateFrom";
        chkDateFrom.TabIndex = 12;
        chkDateFrom.Text = "Created From:";
        chkDateFrom.UseVisualStyleBackColor = true;
        chkDateFrom.CheckedChanged += FilterToggle_CheckedChanged;

        dtpDateFrom.Enabled = false;
        dtpDateFrom.Format = DateTimePickerFormat.Short;
        dtpDateFrom.Margin = new Padding(0, 4, 16, 4);
        dtpDateFrom.Name = "dtpDateFrom";
        dtpDateFrom.TabIndex = 13;
        dtpDateFrom.Width = 115;

        chkDateTo.AutoSize = true;
        chkDateTo.Margin = new Padding(3, 7, 6, 3);
        chkDateTo.Name = "chkDateTo";
        chkDateTo.TabIndex = 14;
        chkDateTo.Text = "Created To:";
        chkDateTo.UseVisualStyleBackColor = true;
        chkDateTo.CheckedChanged += FilterToggle_CheckedChanged;

        dtpDateTo.Enabled = false;
        dtpDateTo.Format = DateTimePickerFormat.Short;
        dtpDateTo.Margin = new Padding(0, 4, 8, 4);
        dtpDateTo.Name = "dtpDateTo";
        dtpDateTo.TabIndex = 15;
        dtpDateTo.Width = 115;

        flpSearchActions.AutoSize = true;
        flpSearchActions.Dock = DockStyle.Right;
        flpSearchActions.FlowDirection = FlowDirection.LeftToRight;
        flpSearchActions.Name = "flpSearchActions";
        flpSearchActions.WrapContents = false;
        flpSearchActions.Controls.Add(btnSearch);
        flpSearchActions.Controls.Add(btnStop);
        flpSearchActions.Controls.Add(btnClear);

        btnSearch.Height = 30;
        btnSearch.Margin = new Padding(4, 2, 4, 2);
        btnSearch.Name = "btnSearch";
        btnSearch.TabIndex = 16;
        btnSearch.Text = "Search";
        btnSearch.UseVisualStyleBackColor = true;
        btnSearch.Width = 96;
        btnSearch.Click += btnSearch_Click;

        btnStop.Enabled = false;
        btnStop.Height = 30;
        btnStop.Margin = new Padding(4, 2, 4, 2);
        btnStop.Name = "btnStop";
        btnStop.TabIndex = 17;
        btnStop.Text = "Stop";
        btnStop.UseVisualStyleBackColor = true;
        btnStop.Width = 96;
        btnStop.Click += btnStop_Click;

        btnClear.Height = 30;
        btnClear.Margin = new Padding(4, 2, 3, 2);
        btnClear.Name = "btnClear";
        btnClear.TabIndex = 18;
        btnClear.Text = "Clear Filters";
        btnClear.UseVisualStyleBackColor = true;
        btnClear.Width = 128;
        btnClear.Click += btnClear_Click;

        //
        // pnlGrid
        //
        pnlGrid.Controls.Add(dgvResults);
        pnlGrid.Dock = DockStyle.Fill;
        pnlGrid.Name = "pnlGrid";
        pnlGrid.Padding = new Padding(10, 6, 10, 4);
        pnlGrid.TabIndex = 1;

        //
        // dgvResults
        //
        dgvResults.AllowUserToAddRows = false;
        dgvResults.AllowUserToDeleteRows = false;
        dgvResults.AllowUserToResizeRows = false;
        dgvResults.AutoGenerateColumns = false;
        dgvResults.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        dgvResults.BackgroundColor = SystemColors.Window;
        dgvResults.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        dgvResults.Columns.AddRange(
            colType,
            colName,
            colExtension,
            colJunkCategory,
            colSize,
            colCreated,
            colFullPath,
            colExplore,
            colDelete);
        dgvResults.ContextMenuStrip = cmsGrid;
        dgvResults.Dock = DockStyle.Fill;
        dgvResults.MultiSelect = false;
        dgvResults.Name = "dgvResults";
        dgvResults.ReadOnly = true;
        dgvResults.RowHeadersVisible = false;
        dgvResults.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvResults.TabIndex = 0;
        dgvResults.CellContentClick += dgvResults_CellContentClick;
        dgvResults.CellMouseDown += dgvResults_CellMouseDown;
        dgvResults.ColumnHeaderMouseClick += dgvResults_ColumnHeaderMouseClick;
        dgvResults.SelectionChanged += dgvResults_SelectionChanged;

        colType.DataPropertyName = nameof(FileSystemItem.ItemTypeDisplay);
        colType.HeaderText = "Type";
        colType.Name = "colType";
        colType.ReadOnly = true;
        colType.SortMode = DataGridViewColumnSortMode.Programmatic;
        colType.Width = 100;

        colName.DataPropertyName = nameof(FileSystemItem.Name);
        colName.HeaderText = "Name";
        colName.Name = "colName";
        colName.ReadOnly = true;
        colName.SortMode = DataGridViewColumnSortMode.Programmatic;
        colName.Width = 220;

        colExtension.DataPropertyName = nameof(FileSystemItem.Extension);
        colExtension.HeaderText = "Extension";
        colExtension.Name = "colExtension";
        colExtension.ReadOnly = true;
        colExtension.SortMode = DataGridViewColumnSortMode.Programmatic;
        colExtension.Width = 85;

        colJunkCategory.DataPropertyName = nameof(FileSystemItem.JunkCategory);
        colJunkCategory.HeaderText = "Category";
        colJunkCategory.Name = "colJunkCategory";
        colJunkCategory.ReadOnly = true;
        colJunkCategory.SortMode = DataGridViewColumnSortMode.Programmatic;
        colJunkCategory.Visible = false;
        colJunkCategory.Width = 135;

        colSize.DataPropertyName = nameof(FileSystemItem.SizeDisplay);
        colSize.DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        colSize.HeaderText = "Size";
        colSize.Name = "colSize";
        colSize.ReadOnly = true;
        colSize.SortMode = DataGridViewColumnSortMode.Programmatic;
        colSize.Width = 95;

        colCreated.DataPropertyName = nameof(FileSystemItem.CreatedAtDisplay);
        colCreated.HeaderText = "Creation Date";
        colCreated.Name = "colCreated";
        colCreated.ReadOnly = true;
        colCreated.SortMode = DataGridViewColumnSortMode.Programmatic;
        colCreated.Width = 140;

        colFullPath.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colFullPath.DataPropertyName = nameof(FileSystemItem.FullPath);
        colFullPath.HeaderText = "Full Path";
        colFullPath.MinimumWidth = 180;
        colFullPath.Name = "colFullPath";
        colFullPath.ReadOnly = true;
        colFullPath.SortMode = DataGridViewColumnSortMode.Programmatic;

        colExplore.HeaderText = "Location";
        colExplore.Name = "colExplore";
        colExplore.ReadOnly = true;
        colExplore.Resizable = DataGridViewTriState.False;
        colExplore.Text = "Open";
        colExplore.UseColumnTextForButtonValue = true;
        colExplore.Width = 95;

        colDelete.HeaderText = "Action";
        colDelete.Name = "colDelete";
        colDelete.ReadOnly = true;
        colDelete.Resizable = DataGridViewTriState.False;
        colDelete.Text = "Delete";
        colDelete.UseColumnTextForButtonValue = true;
        colDelete.Width = 95;

        //
        // cmsGrid
        //
        cmsGrid.Items.AddRange([mnuExplore, mnuDelete]);
        cmsGrid.Name = "cmsGrid";
        cmsGrid.Opening += cmsGrid_Opening;

        mnuExplore.Name = "mnuExplore";
        mnuExplore.Text = "Open Location";
        mnuExplore.Click += mnuExplore_Click;

        mnuDelete.Name = "mnuDelete";
        mnuDelete.Text = "Delete";
        mnuDelete.Click += mnuDelete_Click;

        //
        // pnlBottom
        //
        pnlBottom.Controls.Add(pnlActions);
        pnlBottom.Controls.Add(pnlPagination);
        pnlBottom.Controls.Add(pnlStatus);
        pnlBottom.Dock = DockStyle.Bottom;
        pnlBottom.Height = 108;
        pnlBottom.Name = "pnlBottom";
        pnlBottom.TabIndex = 2;

        //
        // pnlPagination
        //
        pnlPagination.Controls.Add(flpPagination);
        pnlPagination.Dock = DockStyle.Top;
        pnlPagination.Height = 40;
        pnlPagination.Name = "pnlPagination";
        pnlPagination.Padding = new Padding(10, 2, 10, 2);

        //
        // pnlActions
        //
        pnlActions.Controls.Add(flpSelectionActions);
        pnlActions.Dock = DockStyle.Top;
        pnlActions.Height = 40;
        pnlActions.Name = "pnlActions";
        pnlActions.Padding = new Padding(10, 2, 10, 2);

        flpPagination.AutoSize = true;
        flpPagination.Dock = DockStyle.Left;
        flpPagination.FlowDirection = FlowDirection.LeftToRight;
        flpPagination.Name = "flpPagination";
        flpPagination.WrapContents = false;
        flpPagination.Controls.Add(btnFirstPage);
        flpPagination.Controls.Add(btnPrevPage);
        flpPagination.Controls.Add(lblPageInfo);
        flpPagination.Controls.Add(btnNextPage);
        flpPagination.Controls.Add(btnLastPage);

        btnFirstPage.Enabled = false;
        btnFirstPage.Height = 28;
        btnFirstPage.Margin = new Padding(0, 3, 4, 3);
        btnFirstPage.Name = "btnFirstPage";
        btnFirstPage.TabIndex = 0;
        btnFirstPage.Text = "First";
        btnFirstPage.UseVisualStyleBackColor = true;
        btnFirstPage.Width = 88;
        btnFirstPage.Click += btnFirstPage_Click;

        btnPrevPage.Enabled = false;
        btnPrevPage.Height = 28;
        btnPrevPage.Margin = new Padding(0, 3, 8, 3);
        btnPrevPage.Name = "btnPrevPage";
        btnPrevPage.TabIndex = 1;
        btnPrevPage.Text = "Previous";
        btnPrevPage.UseVisualStyleBackColor = true;
        btnPrevPage.Width = 84;
        btnPrevPage.Click += btnPrevPage_Click;

        lblPageInfo.AutoSize = true;
        lblPageInfo.Margin = new Padding(4, 9, 10, 3);
        lblPageInfo.Name = "lblPageInfo";
        lblPageInfo.Text = "Page 0 of 0 (0 results)";

        btnNextPage.Enabled = false;
        btnNextPage.Height = 28;
        btnNextPage.Margin = new Padding(0, 3, 4, 3);
        btnNextPage.Name = "btnNextPage";
        btnNextPage.TabIndex = 2;
        btnNextPage.Text = "Next";
        btnNextPage.UseVisualStyleBackColor = true;
        btnNextPage.Width = 88;
        btnNextPage.Click += btnNextPage_Click;

        btnLastPage.Enabled = false;
        btnLastPage.Height = 28;
        btnLastPage.Margin = new Padding(0, 3, 4, 3);
        btnLastPage.Name = "btnLastPage";
        btnLastPage.TabIndex = 3;
        btnLastPage.Text = "Last";
        btnLastPage.UseVisualStyleBackColor = true;
        btnLastPage.Width = 88;
        btnLastPage.Click += btnLastPage_Click;

        flpSelectionActions.AutoSize = true;
        flpSelectionActions.Dock = DockStyle.Right;
        flpSelectionActions.FlowDirection = FlowDirection.LeftToRight;
        flpSelectionActions.Name = "flpSelectionActions";
        flpSelectionActions.WrapContents = false;
        flpSelectionActions.Controls.Add(btnExploreSelected);
        flpSelectionActions.Controls.Add(btnDeleteSelected);

        btnExploreSelected.Enabled = false;
        btnExploreSelected.Height = 28;
        btnExploreSelected.Margin = new Padding(4, 3, 4, 3);
        btnExploreSelected.Name = "btnExploreSelected";
        btnExploreSelected.TabIndex = 4;
        btnExploreSelected.Text = "Open Selected Location";
        btnExploreSelected.UseVisualStyleBackColor = true;
        btnExploreSelected.Width = 215;
        btnExploreSelected.Click += btnExploreSelected_Click;

        btnDeleteSelected.Enabled = false;
        btnDeleteSelected.Height = 28;
        btnDeleteSelected.Margin = new Padding(4, 3, 0, 3);
        btnDeleteSelected.Name = "btnDeleteSelected";
        btnDeleteSelected.TabIndex = 5;
        btnDeleteSelected.Text = "Delete Selected";
        btnDeleteSelected.UseVisualStyleBackColor = true;
        btnDeleteSelected.Width = 165;
        btnDeleteSelected.Click += btnDeleteSelected_Click;

        //
        // pnlStatus
        //
        pnlStatus.BorderStyle = BorderStyle.FixedSingle;
        pnlStatus.Controls.Add(lblStatus);
        pnlStatus.Controls.Add(progressBar);
        pnlStatus.Dock = DockStyle.Bottom;
        pnlStatus.Height = 28;
        pnlStatus.Name = "pnlStatus";
        pnlStatus.Padding = new Padding(10, 3, 10, 3);

        lblStatus.Dock = DockStyle.Fill;
        lblStatus.Name = "lblStatus";
        lblStatus.Text = "Select a drive and click Search.";
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;

        progressBar.Dock = DockStyle.Right;
        progressBar.MarqueeAnimationSpeed = 25;
        progressBar.Name = "progressBar";
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.Visible = false;
        progressBar.Width = 180;

        //
        // tabMain
        //
        tabMain.Controls.Add(tabPageSearch);
        tabMain.Controls.Add(tabPageTaskManager);
        tabMain.Dock = DockStyle.Fill;
        tabMain.Name = "tabMain";
        tabMain.Padding = new Point(14, 5);
        tabMain.SelectedIndex = 0;
        tabMain.TabIndex = 0;
        tabMain.SelectedIndexChanged += tabMain_SelectedIndexChanged;

        //
        // tabPageSearch
        //
        tabPageSearch.Controls.Add(pnlGrid);
        tabPageSearch.Controls.Add(grpFilters);
        tabPageSearch.Controls.Add(pnlBottom);
        tabPageSearch.Name = "tabPageSearch";
        tabPageSearch.Padding = new Padding(2);
        tabPageSearch.Text = "File Search";
        tabPageSearch.UseVisualStyleBackColor = true;

        //
        // tabPageTaskManager
        //
        tabPageTaskManager.Controls.Add(taskManagerControl);
        tabPageTaskManager.Name = "tabPageTaskManager";
        tabPageTaskManager.Padding = new Padding(2);
        tabPageTaskManager.Text = "Task Manager";
        tabPageTaskManager.UseVisualStyleBackColor = true;

        //
        // taskManagerControl
        //
        taskManagerControl.Dock = DockStyle.Fill;
        taskManagerControl.Name = "taskManagerControl";

        //
        // MainForm
        //
        AcceptButton = btnSearch;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1240, 760);
        Controls.Add(tabMain);
        MinimumSize = new Size(980, 620);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Unit Manager";
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;

        tabMain.ResumeLayout(false);
        tabPageSearch.ResumeLayout(false);
        tabPageTaskManager.ResumeLayout(false);
        grpFilters.ResumeLayout(false);
        tlpFilters.ResumeLayout(false);
        tlpFilters.PerformLayout();
        pnlDrive.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)numMinSize).EndInit();
        pnlMaxSize.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)numMaxSize).EndInit();
        pnlRowDatesAndActions.ResumeLayout(false);
        pnlRowDatesAndActions.PerformLayout();
        flpDates.ResumeLayout(false);
        flpDates.PerformLayout();
        flpSearchActions.ResumeLayout(false);
        flpSearchActions.PerformLayout();
        pnlGrid.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)dgvResults).EndInit();
        cmsGrid.ResumeLayout(false);
        pnlBottom.ResumeLayout(false);
        pnlActions.ResumeLayout(false);
        pnlActions.PerformLayout();
        pnlPagination.ResumeLayout(false);
        pnlPagination.PerformLayout();
        flpPagination.ResumeLayout(false);
        flpPagination.PerformLayout();
        flpSelectionActions.ResumeLayout(false);
        flpSelectionActions.PerformLayout();
        pnlStatus.ResumeLayout(false);
        ResumeLayout(false);
    }
}
