using System.Drawing;
using FileViewer.Controls;

namespace FileViewer.Forms;

/// <summary>
/// Standalone window hosting the Super Task Manager control.
/// </summary>
public sealed class TaskManagerWindow : Form
{
    private readonly TaskManagerControl _taskManager;

    public event Action<string>? ExplorePathInFileViewerRequested;

    public TaskManagerWindow()
    {
        Text = "Task Manager · Unit Manager";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1340, 820);
        MinimumSize = new Size(1020, 640);

        _taskManager = new TaskManagerControl
        {
            Dock = DockStyle.Fill,
            ShowPopOutButton = false
        };
        _taskManager.ExplorePathInFileViewerRequested += path => ExplorePathInFileViewerRequested?.Invoke(path);

        Controls.Add(_taskManager);
        WorkspaceTheme.Apply(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _taskManager.SetMonitoringActive(false);
        base.OnFormClosing(e);
    }
}
