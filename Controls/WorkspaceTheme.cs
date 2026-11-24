using System.Drawing.Drawing2D;

namespace FileViewer.Controls;

internal static class WorkspaceTheme
{
    public static readonly Color Canvas = Color.FromArgb(247, 247, 249);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(31, 31, 37);
    public static readonly Color Muted = Color.FromArgb(88, 87, 100);
    public static readonly Color Line = Color.FromArgb(226, 225, 232);
    public static readonly Color Accent = Color.FromArgb(90, 68, 175);
    public static readonly Color Selection = Color.FromArgb(237, 232, 250);
    public static readonly Font Heading = new("Segoe UI", 12F, FontStyle.Bold);
    private static readonly Font TableHeader = new("Segoe UI", 9F, FontStyle.Bold);
    public static readonly Icon AppIcon = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        using Stream stream = typeof(WorkspaceTheme).Assembly.GetManifestResourceStream("FileViewer.Assets.UnitManager.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static void Apply(Control control)
    {
        if (control is Form window)
            window.Icon = AppIcon;
        if (SystemInformation.HighContrast)
            return;

        switch (control)
        {
            case Form:
            case UserControl:
                control.BackColor = Canvas;
                control.ForeColor = Ink;
                break;
            case TabPage page:
                page.UseVisualStyleBackColor = false;
                page.BackColor = Canvas;
                break;
            case GroupBox group:
                group.BackColor = Surface;
                group.ForeColor = Ink;
                break;
            case DataGridView grid:
                StyleGrid(grid);
                break;
            case Button button:
                StyleButton(button, primary: false);
                break;
            case TextBox input:
                input.BackColor = Surface;
                input.ForeColor = Ink;
                input.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Surface;
                combo.ForeColor = Ink;
                break;
            case ToolStrip strip:
                strip.BackColor = Surface;
                strip.ForeColor = Ink;
                break;
        }

        foreach (Control child in control.Controls)
            Apply(child);
    }

    public static void StyleButton(Button button, bool primary)
    {
        if (SystemInformation.HighContrast)
            return;
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(76, 56, 154) : Canvas;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(65, 46, 139) : Selection;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Surface : Ink;
        if (button.AutoSize)
            button.Padding = new Padding(7, 1, 7, 1);
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.GridColor = Line;
        grid.RowTemplate.Height = 26;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Selection;
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(251, 250, 253);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Canvas;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Selection;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.Font = TableHeader;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 4, 3, 4);
        foreach (DataGridViewButtonColumn column in grid.Columns.OfType<DataGridViewButtonColumn>())
            column.FlatStyle = FlatStyle.Flat;
    }

    public static void StyleNavigation(TabControl tabs)
    {
        if (SystemInformation.HighContrast)
            return;
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(172, 36);
        tabs.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= tabs.TabCount)
                return;
            bool selected = e.Index == tabs.SelectedIndex;
            using var background = new SolidBrush(Canvas);
            e.Graphics.FillRectangle(background, e.Bounds);
            var item = Rectangle.Inflate(e.Bounds, -3, -3);
            if (selected && item.Width > 12 && item.Height > 12)
            {
                using var path = new GraphicsPath();
                path.AddRoundedRectangle(item, new Size(12, 12));
                using var fill = new SolidBrush(Selection);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(fill, path);
            }
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, selected ? TableHeader : tabs.Font,
                item, selected ? Accent : Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0 && tabs.Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(item, -4, -4));
        };
        tabs.SelectedIndexChanged += (_, _) => tabs.Invalidate();
    }
}
