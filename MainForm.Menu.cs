using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FileViewer.Services;

namespace FileViewer;

public partial class MainForm
{
    private void AddApplicationMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Close", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Alt | Keys.F4
        });

        var about = new ToolStripMenuItem("&About");
        var check = new ToolStripMenuItem("Check for &updates");
        check.Click += async (_, _) => await CheckForUpdatesAsync(check);
        about.DropDownItems.Add(check);
        about.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) =>
            MessageBox.Show(this, $"Unit Manager\nVersion {UpdateService.CurrentVersion}\n\n© 2026 Guaroa Software SRL",
                "About Unit Manager", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        menu.Items.AddRange([file, about]);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private async Task CheckForUpdatesAsync(ToolStripMenuItem check)
    {
        check.Enabled = false;
        check.Text = "Checking for updates…";
        string current = $"Current version: {UpdateService.CurrentVersion}";
        try
        {
            var release = await UpdateService.GetLatestAsync();
            if (IsDisposed || Disposing)
                return;

            if (release is null)
            {
                MessageBox.Show(this, $"{current}\n\nThe repository has not published version information yet.",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool available = release.Version > UpdateService.CurrentVersion;
            var page = new TaskDialogPage
            {
                Caption = "Check for updates",
                Heading = available ? "A new version is available" : "You’re up to date",
                Text = $"{current}\nLatest published version: {release.Version}",
                Icon = TaskDialogIcon.Information,
                AllowCancel = true
            };

            var zip = new TaskDialogButton("Download ZIP");
            var installer = new TaskDialogButton(release.HasSetup ? "Download Setup" : "Download installer (MSI)");
            if (available)
            {
                if (release.HasInstaller)
                    page.Buttons.Add(installer);
                page.Buttons.Add(zip);
            }
            page.Buttons.Add(TaskDialogButton.Close);
            TaskDialogButton selected = TaskDialog.ShowDialog(this, page);
            Uri? download = selected == zip ? release.ZipUrl : selected == installer ? release.InstallerUrl : null;
            if (download is not null)
            {
                using var browser = Process.Start(new ProcessStartInfo(download.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or InvalidDataException or Win32Exception)
        {
            if (!IsDisposed && !Disposing)
                MessageBox.Show(this, $"{current}\n\nCould not complete the update check or open the download.\n{ex.Message}",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                check.Text = "Check for &updates";
                check.Enabled = true;
            }
        }
    }
}
