using System.Diagnostics;
using FileViewer.Controls;
using FileViewer.Forms;
using FileViewer.Models;
using FileViewer.Services;

namespace FileViewer;

public partial class MainForm : Form
{
    private const int PageSize = 25;

    private readonly FileSearchService _searchService = new();
    private readonly PcDiagnosticControl _diagnostic = new();
    private readonly TabPage _diagnosticTab = new("PC Diagnostics") { UseVisualStyleBackColor = true };
    private CancellationTokenSource? _searchCts;
    private List<FileSystemItem> _allResults = [];
    private int _currentPage = 1;
    private string _sortColumnName = string.Empty;
    private bool _sortAscending = true;
    private bool _isSearching;

    private sealed record DriveOption(string RootPath, string DisplayText);
    private sealed record TargetTypeOption(SearchTargetType Value, string DisplayText);
    private sealed record SizeUnitOption(SizeUnit Value, string DisplayText);
    private sealed record JunkCategoryOption(JunkFileCategory Value, string DisplayText);

    private static readonly IReadOnlyDictionary<string, Comparison<FileSystemItem>> SortComparisons =
        new Dictionary<string, Comparison<FileSystemItem>>(StringComparer.Ordinal)
        {
            ["colType"] = (a, b) =>
            {
                int cmp = b.IsDirectory.CompareTo(a.IsDirectory);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            },
            ["colName"] = (a, b) =>
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
            },
            ["colExtension"] = (a, b) =>
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Extension, b.Extension);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            },
            ["colJunkCategory"] = (a, b) =>
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.JunkCategory, b.JunkCategory);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            },
            ["colSize"] = (a, b) =>
            {
                int cmp = a.SizeBytes.CompareTo(b.SizeBytes);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            },
            ["colCreated"] = (a, b) =>
            {
                int cmp = a.CreatedAt.CompareTo(b.CreatedAt);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            },
            ["colFullPath"] = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath)
        };

    public MainForm()
    {
        InitializeComponent();
        _diagnosticTab.Controls.Add(_diagnostic);
        tabMain.TabPages.Add(_diagnosticTab);
        ApplyWorkspaceAppearance();
        InitializeFilterOptions();
        WireTaskManagerEvents();
    }

    internal void ShowDiagnosticTab() => tabMain.SelectedTab = _diagnosticTab;

    private void ApplyWorkspaceAppearance()
    {
        SuspendLayout();
        Padding = new Padding(14, 0, 14, 10);
        tabPageSearch.Padding = new Padding(8);
        var brand = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 47,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "Unit Manager",
            Font = WorkspaceTheme.Heading
        };
        Controls.Add(brand);
        WorkspaceTheme.Apply(this);
        WorkspaceTheme.StyleNavigation(tabMain);
        WorkspaceTheme.StyleButton(btnSearch, primary: true);
        _diagnostic.StylePrimaryAction();
        ResumeLayout(performLayout: true);
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        LoadDrives();
        ResetDatesToDefault();
        UpdateFilterControlStates();
        RenderCurrentPage();
        taskManagerControl.SetMonitoringActive(tabMain.SelectedTab == tabPageTaskManager);
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_diagnostic.PendingAnalysis is { IsCompleted: false } analysis)
        {
            e.Cancel = true;
            _diagnostic.CancelAnalysis();
            await analysis;
            if (!IsDisposed)
                Close();
            return;
        }

        _searchCts?.Cancel();
        taskManagerControl.SetMonitoringActive(false);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F9)
        {
            tabMain.SelectedTab = tabMain.SelectedTab == tabPageSearch ? tabPageTaskManager : tabPageSearch;
            return true;
        }

        if (keyData == (Keys.Control | Keys.F) && tabMain.SelectedTab == tabPageTaskManager)
        {
            taskManagerControl.FocusSearch();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void WireTaskManagerEvents()
    {
        taskManagerControl.PopOutRequested += () =>
        {
            var win = new TaskManagerWindow();
            win.ExplorePathInFileViewerRequested += NavigateToFileInViewer;
            win.Show(this);
        };

        taskManagerControl.ExplorePathInFileViewerRequested += NavigateToFileInViewer;
    }

    private void NavigateToFileInViewer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        tabMain.SelectedTab = tabPageSearch;
        string? root = Path.GetPathRoot(filePath);
        if (!string.IsNullOrEmpty(root))
        {
            cmbDrive.SelectedValue = root;
        }

        txtPattern.Text = Path.GetFileName(filePath);
        txtPattern.Focus();
        Activate();
    }

    private void tabMain_SelectedIndexChanged(object? sender, EventArgs e)
    {
        bool isTaskManager = tabMain.SelectedTab == tabPageTaskManager;
        AcceptButton = tabMain.SelectedTab == tabPageSearch ? btnSearch : null;
        taskManagerControl.SetMonitoringActive(isTaskManager);
    }

    private void InitializeFilterOptions()
    {
        cmbTargetType.DisplayMember = nameof(TargetTypeOption.DisplayText);
        cmbTargetType.ValueMember = nameof(TargetTypeOption.Value);
        cmbTargetType.DataSource = new List<TargetTypeOption>
        {
            new(SearchTargetType.Both, "Files and Folders"),
            new(SearchTargetType.FilesOnly, "Files Only"),
            new(SearchTargetType.FoldersOnly, "Folders Only")
        };

        cmbSizeUnit.DisplayMember = nameof(SizeUnitOption.DisplayText);
        cmbSizeUnit.ValueMember = nameof(SizeUnitOption.Value);
        cmbSizeUnit.DataSource = new List<SizeUnitOption>
        {
            new(SizeUnit.Bytes, "Bytes"),
            new(SizeUnit.KB, "KB"),
            new(SizeUnit.MB, "MB"),
            new(SizeUnit.GB, "GB")
        };
        cmbSizeUnit.SelectedValue = SizeUnit.KB;

        cmbJunkCategory.DisplayMember = nameof(JunkCategoryOption.DisplayText);
        cmbJunkCategory.ValueMember = nameof(JunkCategoryOption.Value);
        cmbJunkCategory.DataSource = Enum.GetValues<JunkFileCategory>()
            .Select(category => new JunkCategoryOption(category, JunkFileClassifier.GetCategoryDisplayName(category)))
            .ToList();
        cmbJunkCategory.SelectedValue = JunkFileCategory.All;
        UpdateJunkHint();
    }

    private void LoadDrives()
    {
        string? previousSelection = cmbDrive.SelectedValue as string;
        var drives = new List<DriveOption>();

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            drives.Add(new DriveOption(drive.Name, FormatDriveDisplay(drive)));
        }

        cmbDrive.DisplayMember = nameof(DriveOption.DisplayText);
        cmbDrive.ValueMember = nameof(DriveOption.RootPath);
        cmbDrive.DataSource = drives;

        if (drives.Count == 0)
        {
            return;
        }

        DriveOption? targetDrive = drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, previousSelection, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, @"C:\", StringComparison.OrdinalIgnoreCase))
            ?? drives[0];

        cmbDrive.SelectedValue = targetDrive.RootPath;
    }

    private static string FormatDriveDisplay(DriveInfo drive)
    {
        try
        {
            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
            double freeGb = drive.AvailableFreeSpace / (1024d * 1024d * 1024d);
            return $"{drive.Name} - {label} ({drive.DriveFormat}, {freeGb:0.#} GB free)";
        }
        catch (IOException)
        {
            return drive.Name;
        }
        catch (UnauthorizedAccessException)
        {
            return drive.Name;
        }
    }

    private void ResetDatesToDefault()
    {
        dtpDateFrom.Value = DateTime.Today.AddMonths(-1);
        dtpDateTo.Value = DateTime.Today;
    }

    private void UpdateFilterControlStates()
    {
        SearchTargetType targetType = GetSelectedTargetType();
        bool junkOnly = chkJunkFiles.Checked;
        bool allowsFileFilters = !_isSearching && (junkOnly || targetType != SearchTargetType.FoldersOnly);

        cmbDrive.Enabled = !_isSearching;
        btnRefreshDrives.Enabled = !_isSearching;
        txtPattern.Enabled = !_isSearching;
        cmbTargetType.Enabled = !_isSearching && !junkOnly;

        txtExtension.Enabled = allowsFileFilters;
        chkMinSize.Enabled = allowsFileFilters;
        numMinSize.Enabled = allowsFileFilters && chkMinSize.Checked;
        chkMaxSize.Enabled = allowsFileFilters;
        numMaxSize.Enabled = allowsFileFilters && chkMaxSize.Checked;
        cmbSizeUnit.Enabled = allowsFileFilters;

        chkJunkFiles.Enabled = !_isSearching;
        cmbJunkCategory.Enabled = !_isSearching && junkOnly;

        chkDateFrom.Enabled = !_isSearching;
        dtpDateFrom.Enabled = !_isSearching && chkDateFrom.Checked;
        chkDateTo.Enabled = !_isSearching;
        dtpDateTo.Enabled = !_isSearching && chkDateTo.Checked;

        btnSearch.Enabled = !_isSearching;
        btnStop.Enabled = _isSearching;
        btnClear.Enabled = !_isSearching;
    }

    private JunkFileCategory GetSelectedJunkCategory()
    {
        return cmbJunkCategory.SelectedValue is JunkFileCategory category
            ? category
            : JunkFileCategory.All;
    }

    private void UpdateJunkHint()
    {
        lblJunkHint.Text = chkJunkFiles.Checked
            ? JunkFileClassifier.GetCategoryHint(GetSelectedJunkCategory())
            : "Enable to find dumps (.dmp), temporary files (.tmp, ~$*), backups, logs, and cache files.";
    }

    private SearchTargetType GetSelectedTargetType()
    {
        return cmbTargetType.SelectedValue is SearchTargetType type
            ? type
            : SearchTargetType.Both;
    }

    private SizeUnit GetSelectedSizeUnit()
    {
        return cmbSizeUnit.SelectedValue is SizeUnit unit
            ? unit
            : SizeUnit.KB;
    }

    private bool TryBuildFilter(out SearchFilter? filter)
    {
        filter = null;

        if (cmbDrive.SelectedValue is not string rootPath || string.IsNullOrWhiteSpace(rootPath))
        {
            MessageBox.Show(
                this,
                "Select a valid drive.",
                "Drive Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (!Directory.Exists(rootPath))
        {
            MessageBox.Show(
                this,
                $"The selected drive is no longer available: {rootPath}",
                "Drive Unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            LoadDrives();
            return false;
        }

        bool junkFilesOnly = chkJunkFiles.Checked;
        SearchTargetType targetType = junkFilesOnly ? SearchTargetType.FilesOnly : GetSelectedTargetType();
        SizeUnit sizeUnit = GetSelectedSizeUnit();

        bool useSizeFilters = targetType != SearchTargetType.FoldersOnly;
        bool useMinSize = useSizeFilters && chkMinSize.Checked;
        bool useMaxSize = useSizeFilters && chkMaxSize.Checked;
        long minSizeBytes = useMinSize ? SearchFilter.ToBytes(numMinSize.Value, sizeUnit) : 0L;
        long maxSizeBytes = useMaxSize ? SearchFilter.ToBytes(numMaxSize.Value, sizeUnit) : 0L;

        if (useMinSize && useMaxSize && minSizeBytes > maxSizeBytes)
        {
            MessageBox.Show(
                this,
                "Minimum size must be less than or equal to maximum size.",
                "Invalid Size Filter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (chkDateFrom.Checked && chkDateTo.Checked && dtpDateFrom.Value.Date > dtpDateTo.Value.Date)
        {
            MessageBox.Show(
                this,
                "The Created From date must be on or before the Created To date.",
                "Invalid Date Filter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        filter = new SearchFilter
        {
            RootPath = rootPath,
            SearchPattern = txtPattern.Text,
            TargetType = targetType,
            ExtensionFilter = useSizeFilters ? txtExtension.Text : string.Empty,
            UseMinSize = useMinSize,
            MinSizeBytes = minSizeBytes,
            UseMaxSize = useMaxSize,
            MaxSizeBytes = maxSizeBytes,
            UseCreatedFrom = chkDateFrom.Checked,
            CreatedFrom = dtpDateFrom.Value.Date,
            UseCreatedTo = chkDateTo.Checked,
            CreatedTo = dtpDateTo.Value.Date,
            JunkFilesOnly = junkFilesOnly,
            JunkCategory = GetSelectedJunkCategory()
        };

        return true;
    }

    private async void btnSearch_Click(object? sender, EventArgs e)
    {
        if (_isSearching || !TryBuildFilter(out SearchFilter? filter) || filter is null)
        {
            return;
        }

        await RunSearchAsync(filter);
    }

    private async Task RunSearchAsync(SearchFilter filter)
    {
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        _isSearching = true;
        _allResults = [];
        _currentPage = 1;
        colJunkCategory.Visible = filter.JunkFilesOnly;
        if (!filter.JunkFilesOnly && string.Equals(_sortColumnName, colJunkCategory.Name, StringComparison.Ordinal))
        {
            _sortColumnName = string.Empty;
        }

        RenderCurrentPage();
        UpdateFilterControlStates();
        progressBar.Visible = true;
        lblStatus.Text = filter.JunkFilesOnly
            ? $"Searching for junk files ({JunkFileClassifier.GetCategoryDisplayName(filter.JunkCategory)}) in {filter.RootPath}..."
            : $"Searching {filter.RootPath}...";

        var progress = new Progress<SearchProgressInfo>(info =>
        {
            if (!_isSearching)
            {
                return;
            }

            lblStatus.Text = $"Searching {filter.RootPath}... Scanned: {info.ScannedCount:N0} | Matches: {info.MatchedCount:N0}";
        });

        try
        {
            SearchExecutionResult result = await _searchService.SearchAsync(filter, progress, _searchCts.Token);

            _allResults = result.Items;
            if (!string.IsNullOrEmpty(_sortColumnName))
            {
                SortResults();
            }

            _currentPage = 1;
            RenderCurrentPage();

            string prefix = result.WasCancelled ? "Search stopped." : "Search complete.";
            lblStatus.Text = $"{prefix} Scanned: {result.ScannedCount:N0} | Found: {_allResults.Count:N0}{GetFileSizeSummary()}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lblStatus.Text = "The search failed.";
            MessageBox.Show(
                this,
                $"Could not complete the search in {filter.RootPath}:\n{ex.Message}",
                "Search Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _isSearching = false;
            progressBar.Visible = false;
            UpdateFilterControlStates();
        }
    }

    private void btnStop_Click(object? sender, EventArgs e)
    {
        if (_isSearching)
        {
            lblStatus.Text = "Stopping search...";
            _searchCts?.Cancel();
        }
    }

    private void btnClear_Click(object? sender, EventArgs e)
    {
        txtPattern.Clear();
        txtExtension.Clear();
        cmbTargetType.SelectedValue = SearchTargetType.Both;
        chkMinSize.Checked = false;
        numMinSize.Value = 0;
        chkMaxSize.Checked = false;
        numMaxSize.Value = 100;
        cmbSizeUnit.SelectedValue = SizeUnit.KB;
        chkDateFrom.Checked = false;
        chkDateTo.Checked = false;
        chkJunkFiles.Checked = false;
        cmbJunkCategory.SelectedValue = JunkFileCategory.All;
        ResetDatesToDefault();

        _allResults = [];
        _currentPage = 1;
        _sortColumnName = string.Empty;
        _sortAscending = true;
        colJunkCategory.Visible = false;
        RenderCurrentPage();
        UpdateFilterControlStates();
        UpdateJunkHint();
        lblStatus.Text = "Filters cleared. Ready to search.";
    }

    private void btnRefreshDrives_Click(object? sender, EventArgs e)
    {
        LoadDrives();
        lblStatus.Text = "Drive list refreshed.";
    }

    private void cmbTargetType_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateFilterControlStates();
    }

    private void FilterToggle_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateFilterControlStates();
    }

    private void chkJunkFiles_CheckedChanged(object? sender, EventArgs e)
    {
        if (chkJunkFiles.Checked && GetSelectedTargetType() != SearchTargetType.FilesOnly)
        {
            cmbTargetType.SelectedValue = SearchTargetType.FilesOnly;
        }

        UpdateFilterControlStates();
        UpdateJunkHint();
    }

    private void cmbJunkCategory_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateJunkHint();
    }

    private void dgvResults_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || _allResults.Count == 0)
        {
            return;
        }

        string columnName = dgvResults.Columns[e.ColumnIndex].Name;
        if (!SortComparisons.ContainsKey(columnName))
        {
            return;
        }

        if (string.Equals(_sortColumnName, columnName, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumnName = columnName;
            _sortAscending = true;
        }

        SortResults();
        _currentPage = 1;
        RenderCurrentPage();
    }

    private void SortResults()
    {
        if (!SortComparisons.TryGetValue(_sortColumnName, out Comparison<FileSystemItem>? comparison))
        {
            return;
        }

        _allResults.Sort((a, b) => _sortAscending ? comparison(a, b) : comparison(b, a));
    }

    private void RenderCurrentPage()
    {
        int totalCount = _allResults.Count;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)PageSize);

        if (totalPages == 0)
        {
            _currentPage = 0;
            dgvResults.DataSource = null;
            lblPageInfo.Text = "Page 0 of 0 (0 results)";
            UpdatePaginationButtons(0, 0);
            UpdateSortGlyphs();
            UpdateSelectionActionButtons();
            return;
        }

        _currentPage = Math.Clamp(_currentPage, 1, totalPages);
        int startIndex = (_currentPage - 1) * PageSize;
        List<FileSystemItem> pageItems = _allResults.Skip(startIndex).Take(PageSize).ToList();

        dgvResults.DataSource = null;
        dgvResults.DataSource = pageItems;

        int fromItem = startIndex + 1;
        int toItem = startIndex + pageItems.Count;
        lblPageInfo.Text = $"Page {_currentPage} of {totalPages} (Showing {fromItem}-{toItem} of {totalCount:N0} results)";

        UpdatePaginationButtons(_currentPage, totalPages);
        UpdateSortGlyphs();
        UpdateSelectionActionButtons();
    }

    private void UpdatePaginationButtons(int currentPage, int totalPages)
    {
        btnFirstPage.Enabled = totalPages > 0 && currentPage > 1;
        btnPrevPage.Enabled = totalPages > 0 && currentPage > 1;
        btnNextPage.Enabled = totalPages > 0 && currentPage < totalPages;
        btnLastPage.Enabled = totalPages > 0 && currentPage < totalPages;
    }

    private void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn column in dgvResults.Columns)
        {
            if (column.SortMode != DataGridViewColumnSortMode.Programmatic)
            {
                continue;
            }

            column.HeaderCell.SortGlyphDirection =
                string.Equals(column.Name, _sortColumnName, StringComparison.Ordinal)
                    ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                    : SortOrder.None;
        }
    }

    private void UpdateSelectionActionButtons()
    {
        bool hasSelection = GetSelectedItem() is not null;
        btnExploreSelected.Enabled = hasSelection;
        btnDeleteSelected.Enabled = hasSelection;
    }

    private FileSystemItem? GetSelectedItem()
    {
        if (dgvResults.SelectedRows.Count == 0)
        {
            return null;
        }

        return dgvResults.SelectedRows[0].DataBoundItem as FileSystemItem;
    }

    private FileSystemItem? GetItemAtRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= dgvResults.Rows.Count)
        {
            return null;
        }

        return dgvResults.Rows[rowIndex].DataBoundItem as FileSystemItem;
    }

    private void dgvResults_SelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionActionButtons();
    }

    private void dgvResults_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0)
        {
            return;
        }

        dgvResults.ClearSelection();
        dgvResults.Rows[e.RowIndex].Selected = true;
        if (e.ColumnIndex >= 0)
        {
            dgvResults.CurrentCell = dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex];
        }
    }

    private void dgvResults_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        FileSystemItem? item = GetItemAtRow(e.RowIndex);
        if (item is null)
        {
            return;
        }

        string columnName = dgvResults.Columns[e.ColumnIndex].Name;
        if (string.Equals(columnName, colExplore.Name, StringComparison.Ordinal))
        {
            ExploreItem(item);
        }
        else if (string.Equals(columnName, colDelete.Name, StringComparison.Ordinal))
        {
            DeleteItem(item);
        }
    }

    private void cmsGrid_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (GetSelectedItem() is null)
        {
            e.Cancel = true;
        }
    }

    private void mnuExplore_Click(object? sender, EventArgs e)
    {
        if (GetSelectedItem() is FileSystemItem item)
        {
            ExploreItem(item);
        }
    }

    private void mnuDelete_Click(object? sender, EventArgs e)
    {
        if (GetSelectedItem() is FileSystemItem item)
        {
            DeleteItem(item);
        }
    }

    private void btnExploreSelected_Click(object? sender, EventArgs e)
    {
        if (GetSelectedItem() is FileSystemItem item)
        {
            ExploreItem(item);
        }
    }

    private void btnDeleteSelected_Click(object? sender, EventArgs e)
    {
        if (GetSelectedItem() is FileSystemItem item)
        {
            DeleteItem(item);
        }
    }

    private void btnFirstPage_Click(object? sender, EventArgs e)
    {
        _currentPage = 1;
        RenderCurrentPage();
    }

    private void btnPrevPage_Click(object? sender, EventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            RenderCurrentPage();
        }
    }

    private void btnNextPage_Click(object? sender, EventArgs e)
    {
        _currentPage++;
        RenderCurrentPage();
    }

    private void btnLastPage_Click(object? sender, EventArgs e)
    {
        int totalPages = _allResults.Count == 0 ? 0 : (int)Math.Ceiling(_allResults.Count / (double)PageSize);
        if (totalPages > 0)
        {
            _currentPage = totalPages;
            RenderCurrentPage();
        }
    }

    private void ExploreItem(FileSystemItem item)
    {
        bool exists = item.IsDirectory ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath);
        if (exists)
        {
            LaunchExplorer($"/select,\"{item.FullPath}\"");
            return;
        }

        string? parentDir = Path.GetDirectoryName(item.FullPath);
        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
        {
            DialogResult openParent = MessageBox.Show(
                this,
                $"The item no longer exists at this path:\n{item.FullPath}\n\nOpen its parent folder?\n{parentDir}",
                "Item Not Found",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (openParent == DialogResult.Yes)
            {
                LaunchExplorer($"\"{parentDir}\"");
            }

            return;
        }

        MessageBox.Show(
            this,
            $"Could not find the item or its parent folder:\n{item.FullPath}",
            "Location Not Found",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void LaunchExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open Windows File Explorer:\n{ex.Message}",
                "File Explorer Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void DeleteItem(FileSystemItem item)
    {
        string prompt = item.IsDirectory
            ? $"Permanently delete this folder and all its contents?\n\n{item.FullPath}"
            : $"Permanently delete this file?\n\n{item.FullPath}";

        DialogResult confirmation = MessageBox.Show(
            this,
            prompt,
            "Confirm Deletion",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.FullPath))
                {
                    Directory.Delete(item.FullPath, recursive: true);
                }
            }
            else if (File.Exists(item.FullPath))
            {
                FileAttributes attributes = File.GetAttributes(item.FullPath);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(item.FullPath, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(item.FullPath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(
                this,
                $"Could not delete:\n{item.FullPath}\n\nDetails: {ex.Message}",
                "Deletion Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        RemoveDeletedFromResults(item);
        if (_sortColumnName == "colSize")
            SortResults();
        RenderCurrentPage();
        lblStatus.Text = $"Deleted: {item.FullPath} | Remaining: {_allResults.Count:N0}{GetFileSizeSummary()}.";
    }

    private string GetFileSizeSummary()
    {
        var files = _allResults.Where(item => !item.IsDirectory);
        return files.Any() ? $" ({FileSystemItem.FormatBytes(files.Sum(item => item.SizeBytes))} in files)" : "";
    }

    private void RemoveDeletedFromResults(FileSystemItem deletedItem)
    {
        _allResults.Remove(deletedItem);

        foreach (FileSystemItem folder in _allResults.Where(item => item.IsDirectory && item.SizeBytes >= 0))
        {
            string prefix = Path.TrimEndingDirectorySeparator(folder.FullPath) + Path.DirectorySeparatorChar;
            if (deletedItem.FullPath.StartsWith(prefix, StringComparison.Ordinal))
                folder.SizeBytes = deletedItem.SizeBytes >= 0 && deletedItem.SizeBytes <= folder.SizeBytes
                    ? folder.SizeBytes - deletedItem.SizeBytes : -1;
        }

        if (!deletedItem.IsDirectory)
        {
            return;
        }

        string childPrefix = deletedItem.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        _allResults.RemoveAll(entry =>
            entry.FullPath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
