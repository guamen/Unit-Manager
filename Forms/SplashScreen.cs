using System.Diagnostics;
using System.Drawing.Drawing2D;
using FileViewer.Controls;

namespace FileViewer.Forms;

internal sealed class SplashScreen : Form
{
    private const double DurationMilliseconds = 2500;
    private readonly Stopwatch _elapsed = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 16 };
    private readonly Font _title = new("Segoe UI", 26f, FontStyle.Bold);
    private readonly Bitmap _logo;

    public event EventHandler? Completed;

    public SplashScreen()
    {
        using Stream stream = typeof(SplashScreen).Assembly.GetManifestResourceStream("FileViewer.Assets.GuaroaSoftware.jpg")!;
        using var original = Image.FromStream(stream);
        _logo = new Bitmap(original);

        Text = "Starting Unit Manager";
        Icon = WorkspaceTheme.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(500, 260);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface;
        DoubleBuffered = true;

        _animation.Tick += (_, _) =>
        {
            if (_elapsed.Elapsed.TotalMilliseconds < DurationMilliseconds)
            {
                Invalidate();
                return;
            }

            _animation.Stop();
            Completed?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _elapsed.Start();
        _animation.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int Scale(int value) => (int)Math.Round(value * ClientSize.Height / 260d);
        bool highContrast = SystemInformation.HighContrast;
        Color ink = highContrast ? SystemColors.WindowText : WorkspaceTheme.Ink;
        Color accent = highContrast ? SystemColors.Highlight : WorkspaceTheme.Accent;
        Color line = highContrast ? SystemColors.WindowText : WorkspaceTheme.Line;

        // The original artwork has generous white space around the logo.
        var logoRegion = Rectangle.FromLTRB(225, 288, 1183, 482);
        int logoWidth = Scale(272);
        int logoHeight = logoWidth * logoRegion.Height / logoRegion.Width;
        graphics.DrawImage(_logo, new Rectangle((ClientSize.Width - logoWidth) / 2, Scale(48), logoWidth, logoHeight),
            logoRegion, GraphicsUnit.Pixel);

        TextRenderer.DrawText(graphics, "Unit Manager", _title,
            new Rectangle(0, Scale(119), ClientSize.Width, Scale(53)), ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int trackWidth = Scale(132);
        int trackLeft = (ClientSize.Width - trackWidth) / 2;
        int trackTop = Scale(205);
        int progress = (int)(trackWidth * Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds));
        using var trackBrush = new SolidBrush(line);
        using var accentBrush = new SolidBrush(accent);
        using var border = new Pen(line);
        graphics.FillRectangle(trackBrush, trackLeft, trackTop, trackWidth, Scale(3));
        graphics.FillRectangle(accentBrush, trackLeft, trackTop, progress, Scale(3));
        graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
            _title.Dispose();
            _logo.Dispose();
        }
        base.Dispose(disposing);
    }
}
