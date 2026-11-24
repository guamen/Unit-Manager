using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace FileViewer.Controls;

/// <summary>
/// Double-buffered real-time sparkline chart for CPU and memory telemetry.
/// </summary>
public sealed class SparklinePanel : Panel
{
    private readonly Queue<double> _values = new();
    private const int MaxPoints = 60;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "CPU";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(0, 120, 215);

    public SparklinePanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface;
        MinimumSize = new Size(160, 56);
    }

    public void AddValue(double percent, string? subtitle = null)
    {
        double clamped = Math.Clamp(percent, 0, 100);
        if (_values.Count >= MaxPoints)
        {
            _values.Dequeue();
        }
        _values.Enqueue(clamped);

        if (subtitle is not null)
        {
            Subtitle = subtitle;
        }

        Invalidate();
    }

    public void ClearValues()
    {
        _values.Clear();
        Subtitle = string.Empty;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (w <= 4 || h <= 4)
        {
            return;
        }

        // Draw subtle horizontal grid lines
        using (var gridPen = new Pen(SystemInformation.HighContrast ? SystemColors.GrayText : WorkspaceTheme.Line, 1f))
        {
            for (int i = 1; i <= 3; i++)
            {
                int y = (h * i) / 4;
                g.DrawLine(gridPen, 0, y, w, y);
            }
        }

        // Draw sparkline polygon and stroke
        double[] pts = _values.ToArray();
        if (pts.Length >= 2)
        {
            var linePoints = new PointF[pts.Length];
            float stepX = (w - 2f) / (MaxPoints - 1);
            int offset = MaxPoints - pts.Length;

            for (int i = 0; i < pts.Length; i++)
            {
                float x = 1f + (offset + i) * stepX;
                float y = (h - 2f) - (float)(pts[i] / 100.0 * (h - 6f));
                linePoints[i] = new PointF(x, y);
            }

            var polyPoints = new PointF[pts.Length + 2];
            polyPoints[0] = new PointF(linePoints[0].X, h - 1f);
            Array.Copy(linePoints, 0, polyPoints, 1, linePoints.Length);
            polyPoints[^1] = new PointF(linePoints[^1].X, h - 1f);

            Color accent = SystemInformation.HighContrast ? SystemColors.Highlight : AccentColor;
            using var fillBrush = new SolidBrush(Color.FromArgb(35, accent));
            g.FillPolygon(fillBrush, polyPoints);

            using var linePen = new Pen(accent, 1.8f);
            g.DrawLines(linePen, linePoints);
        }

        // Header text overlay
        double latest = pts.Length > 0 ? pts[^1] : 0;
        using var titleFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 7.8f, FontStyle.Regular);
        using var textBrush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : WorkspaceTheme.Ink);
        using var subBrush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : WorkspaceTheme.Muted);

        string headerText = $"{Title}: {latest:0.0}%";
        g.DrawString(headerText, titleFont, textBrush, 6, 4);

        if (!string.IsNullOrEmpty(Subtitle))
        {
            g.DrawString(Subtitle, subFont, subBrush, 6, 20);
        }

        using var borderPen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : WorkspaceTheme.Line);
        g.SmoothingMode = SmoothingMode.None;
        g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);
    }
}
