using System.ComponentModel;
using System.Drawing.Drawing2D;
using FileViewer.Models;

namespace FileViewer.Controls;

/// <summary>
/// Double-buffered real-time chart displaying measured Internet speed (Mbps) every 5 seconds,
/// overlaid with optional local PC network usage, anomaly markers, and interactive hover tooltips.
/// </summary>
internal sealed class NetworkSpeedChartPanel : Panel
{
    private static readonly Color LocalUsageColor = Color.FromArgb(16, 148, 128);
    private static readonly Color WarningMarkerColor = Color.FromArgb(217, 119, 6);
    private static readonly Color CriticalMarkerColor = Color.FromArgb(220, 38, 38);

    private IReadOnlyList<NetworkSample> _samples = [];
    private List<NetworkSample> _renderedSamples = [];
    private int _hoveredIndex = -1;
    private RectangleF _plotRect;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLocalUsageOverlay { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string WindowLabel { get; set; } = "Last 5 min";

    public NetworkSpeedChartPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : WorkspaceTheme.Surface;
        MinimumSize = new Size(320, 170);
    }

    public void SetSamples(IReadOnlyList<NetworkSample> samples, string? windowLabel = null)
    {
        _samples = samples;
        if (windowLabel is not null)
        {
            WindowLabel = windowLabel;
        }

        _hoveredIndex = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_renderedSamples.Count == 0 || !_plotRect.Contains(e.Location))
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Invalidate();
            }
            return;
        }

        int newIndex = _renderedSamples.Count == 1
            ? 0
            : (int)Math.Round((e.X - _plotRect.Left) / Math.Max(1f, _plotRect.Width) * (_renderedSamples.Count - 1));
        newIndex = Math.Clamp(newIndex, 0, _renderedSamples.Count - 1);

        if (newIndex != _hoveredIndex)
        {
            _hoveredIndex = newIndex;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (w < 60 || h < 60)
        {
            return;
        }

        bool hc = SystemInformation.HighContrast;
        Color bgColor = hc ? SystemColors.Window : WorkspaceTheme.Surface;
        Color inkColor = hc ? SystemColors.WindowText : WorkspaceTheme.Ink;
        Color mutedColor = hc ? SystemColors.GrayText : WorkspaceTheme.Muted;
        Color gridColor = hc ? SystemColors.GrayText : WorkspaceTheme.Line;
        Color accentColor = hc ? SystemColors.Highlight : WorkspaceTheme.Accent;

        using var bgBrush = new SolidBrush(bgColor);
        g.FillRectangle(bgBrush, ClientRectangle);

        // Header bar & legend
        const float topHeaderHeight = 32f;
        const float leftMargin = 62f;
        const float rightMargin = 18f;
        const float bottomMargin = 26f;

        _plotRect = new RectangleF(
            leftMargin,
            topHeaderHeight + 6f,
            Math.Max(20f, w - leftMargin - rightMargin),
            Math.Max(20f, h - topHeaderHeight - bottomMargin - 10f));

        DrawHeaderAndLegend(g, w, inkColor, mutedColor, accentColor);

        // Prepare downsampled points if there are more points than pixels
        _renderedSamples = DownsampleForWidth(_samples, Math.Max(60, (int)(_plotRect.Width / 2)));

        if (_renderedSamples.Count == 0)
        {
            using var emptyFont = new Font("Segoe UI", 9.5f);
            using var emptyBrush = new SolidBrush(mutedColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Waiting for 5-second Internet speed measurements…", emptyFont, emptyBrush, _plotRect, sf);
            DrawOuterBorder(g, w, h, gridColor);
            return;
        }

        // Determine Y-axis scale (Mbps)
        double maxSpeed = 0;
        foreach (NetworkSample s in _renderedSamples)
        {
            if (s.InternetDownloadMbps.HasValue && s.InternetDownloadMbps.Value > maxSpeed)
                maxSpeed = s.InternetDownloadMbps.Value;
            if (ShowLocalUsageOverlay)
            {
                double localTotal = s.LocalDownloadMbps + s.LocalUploadMbps;
                if (localTotal > maxSpeed) maxSpeed = localTotal;
            }
        }

        double yMax = ComputeNiceCeiling(Math.Max(5.0, maxSpeed * 1.15));

        // Draw horizontal gridlines & Y-axis labels
        using (var axisFont = new Font("Segoe UI", 8f))
        using (var labelBrush = new SolidBrush(mutedColor))
        using (var gridPen = new Pen(gridColor, 1f) { DashStyle = DashStyle.Dot })
        using (var axisPen = new Pen(gridColor, 1f))
        using (var rightAlign = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
        {
            const int gridSteps = 4;
            for (int i = 0; i <= gridSteps; i++)
            {
                float ratio = i / (float)gridSteps;
                float y = _plotRect.Bottom - ratio * _plotRect.Height;
                double val = yMax * ratio;
                g.DrawLine(i == 0 ? axisPen : gridPen, _plotRect.Left, y, _plotRect.Right, y);
                string label = val >= 100 ? $"{val:0} Mb" : $"{val:0.#} Mb";
                g.DrawString(label, axisFont, labelBrush, new RectangleF(4f, y - 9f, leftMargin - 10f, 18f), rightAlign);
            }

            // Draw X-axis time labels
            DrawXAxisLabels(g, axisFont, labelBrush);
        }

        // Map samples to coordinates
        var speedPoints = new List<PointF>(_renderedSamples.Count);
        var localPoints = new List<PointF>(_renderedSamples.Count);

        for (int i = 0; i < _renderedSamples.Count; i++)
        {
            float x = IndexToX(i, _renderedSamples.Count);
            NetworkSample s = _renderedSamples[i];

            double speedVal = s.InternetDownloadMbps ?? 0;
            float ySpeed = ValueToY(speedVal, yMax);
            speedPoints.Add(new PointF(x, ySpeed));

            if (ShowLocalUsageOverlay)
            {
                double localVal = s.LocalDownloadMbps + s.LocalUploadMbps;
                localPoints.Add(new PointF(x, ValueToY(localVal, yMax)));
            }
        }

        // Draw shaded area & main Internet Speed curve
        if (speedPoints.Count >= 2)
        {
            var areaPoly = new PointF[speedPoints.Count + 2];
            areaPoly[0] = new PointF(speedPoints[0].X, _plotRect.Bottom);
            speedPoints.CopyTo(areaPoly, 1);
            areaPoly[^1] = new PointF(speedPoints[^1].X, _plotRect.Bottom);

            using var areaBrush = new LinearGradientBrush(
                new PointF(0, _plotRect.Top),
                new PointF(0, _plotRect.Bottom),
                Color.FromArgb(62, accentColor),
                Color.FromArgb(6, accentColor));
            g.FillPolygon(areaBrush, areaPoly);

            using var speedPen = new Pen(accentColor, 2.2f) { LineJoin = LineJoin.Round };
            g.DrawLines(speedPen, speedPoints.ToArray());
        }
        else if (speedPoints.Count == 1)
        {
            using var dotBrush = new SolidBrush(accentColor);
            g.FillEllipse(dotBrush, speedPoints[0].X - 4, speedPoints[0].Y - 4, 8, 8);
        }

        // Draw optional Local PC Usage overlay curve
        if (ShowLocalUsageOverlay && localPoints.Count >= 2)
        {
            using var localPen = new Pen(LocalUsageColor, 1.5f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
            g.DrawLines(localPen, localPoints.ToArray());
        }

        // Draw anomaly markers (Packet Loss, Offline, Degraded, High Latency)
        for (int i = 0; i < _renderedSamples.Count; i++)
        {
            NetworkSample s = _renderedSamples[i];
            if (s.Status == NetworkHealthStatus.Healthy)
            {
                continue;
            }

            Color markerColor = s.Status is NetworkHealthStatus.Offline or NetworkHealthStatus.PacketLoss
                ? CriticalMarkerColor
                : WarningMarkerColor;
            PointF pt = speedPoints[i];
            using var markerBrush = new SolidBrush(markerColor);
            g.FillEllipse(markerBrush, pt.X - 3.2f, pt.Y - 3.2f, 6.4f, 6.4f);
        }

        // Draw hover crosshair and rich tooltip card
        if (_hoveredIndex >= 0 && _hoveredIndex < _renderedSamples.Count)
        {
            DrawHoverOverlay(g, speedPoints[_hoveredIndex], _renderedSamples[_hoveredIndex], accentColor, bgColor, inkColor, gridColor);
        }

        DrawOuterBorder(g, w, h, gridColor);
    }

    private void DrawHeaderAndLegend(Graphics g, int width, Color ink, Color muted, Color accent)
    {
        using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var legendFont = new Font("Segoe UI", 8.2f);
        using var inkBrush = new SolidBrush(ink);
        using var mutedBrush = new SolidBrush(muted);

        string title = $"Internet Speed (Mbps) — Every 5s ({WindowLabel})";
        g.DrawString(title, titleFont, inkBrush, 10, 8);

        // Compute quick summary stats from displayed samples
        var valid = _samples.Where(s => s.InternetDownloadMbps.HasValue).Select(s => s.InternetDownloadMbps!.Value).ToList();
        string statsText = valid.Count > 0
            ? $"Latest: {valid[^1]:0.1} · Avg: {valid.Average():0.1} · Min: {valid.Min():0.1} · Max: {valid.Max():0.1} Mbps"
            : "No readings yet";

        // Draw right-aligned legend + stats
        SizeF statsSize = g.MeasureString(statsText, legendFont);
        float rightCursor = width - 14f - statsSize.Width;
        if (rightCursor > 260)
        {
            g.DrawString(statsText, legendFont, mutedBrush, rightCursor, 10);
        }

        float legendRight = rightCursor - 14f;
        if (ShowLocalUsageOverlay && legendRight > 340)
        {
            string localLbl = "Local PC Usage";
            SizeF sz = g.MeasureString(localLbl, legendFont);
            legendRight -= sz.Width;
            g.DrawString(localLbl, legendFont, mutedBrush, legendRight, 10);
            legendRight -= 18f;
            using var lp = new Pen(LocalUsageColor, 2f) { DashStyle = DashStyle.Dash };
            g.DrawLine(lp, legendRight, 17, legendRight + 13, 17);
            legendRight -= 14f;
        }

        if (legendRight > 310)
        {
            string inetLbl = "Internet Speed";
            SizeF sz = g.MeasureString(inetLbl, legendFont);
            legendRight -= sz.Width;
            g.DrawString(inetLbl, legendFont, mutedBrush, legendRight, 10);
            legendRight -= 18f;
            using var ap = new Pen(accent, 2.2f);
            g.DrawLine(ap, legendRight, 17, legendRight + 13, 17);
        }
    }

    private void DrawXAxisLabels(Graphics g, Font font, Brush brush)
    {
        if (_renderedSamples.Count == 0)
        {
            return;
        }

        bool spansLongTime = _renderedSamples.Count >= 2
            && (_renderedSamples[^1].Timestamp - _renderedSamples[0].Timestamp).TotalMinutes >= 60;
        string fmt = spansLongTime ? "HH:mm" : "HH:mm:ss";

        int maxLabels = Math.Clamp((int)(_plotRect.Width / 85f), 2, 8);
        using var center = new StringFormat { Alignment = StringAlignment.Center };

        if (_renderedSamples.Count == 1)
        {
            g.DrawString(_renderedSamples[0].Timestamp.ToString(fmt), font, brush, _plotRect.Left + 24, _plotRect.Bottom + 5, center);
            return;
        }

        for (int i = 0; i < maxLabels; i++)
        {
            int idx = (int)Math.Round(i * (_renderedSamples.Count - 1) / (double)(maxLabels - 1));
            float x = IndexToX(idx, _renderedSamples.Count);
            float clampedX = Math.Clamp(x, _plotRect.Left + 24, _plotRect.Right - 24);
            g.DrawString(_renderedSamples[idx].Timestamp.ToString(fmt), font, brush, clampedX, _plotRect.Bottom + 5, center);
        }
    }

    private void DrawHoverOverlay(
        Graphics g,
        PointF pt,
        NetworkSample sample,
        Color accent,
        Color bg,
        Color ink,
        Color border)
    {
        using var guidePen = new Pen(Color.FromArgb(140, accent), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(guidePen, pt.X, _plotRect.Top, pt.X, _plotRect.Bottom);

        using var ringBrush = new SolidBrush(bg);
        using var ringPen = new Pen(accent, 2f);
        g.FillEllipse(ringBrush, pt.X - 4.5f, pt.Y - 4.5f, 9f, 9f);
        g.DrawEllipse(ringPen, pt.X - 4.5f, pt.Y - 4.5f, 9f, 9f);

        string[] lines =
        [
            $"{sample.FullTimestampDisplay}  [{sample.StatusDisplay}]",
            $"Internet Speed: {sample.InternetSpeedDisplay}",
            $"Local PC: Down {sample.LocalDownDisplay} · Up {sample.LocalUpDisplay}",
            $"Ping: Router {sample.GatewayPingDisplay} · Internet {sample.InternetPingDisplay} · Loss {sample.PacketLossDisplay}",
            $"Top Process: {sample.TopProcess}"
        ];

        using var tipFont = new Font("Segoe UI", 8.2f);
        using var boldFont = new Font("Segoe UI", 8.2f, FontStyle.Bold);
        float maxW = 0;
        float lineH = 16f;
        for (int i = 0; i < lines.Length; i++)
        {
            SizeF sz = g.MeasureString(lines[i], i == 0 ? boldFont : tipFont);
            if (sz.Width > maxW) maxW = sz.Width;
        }

        float boxW = maxW + 18f;
        float boxH = lines.Length * lineH + 12f;
        float boxX = pt.X + 12f + boxW <= _plotRect.Right ? pt.X + 12f : Math.Max(_plotRect.Left + 4f, pt.X - boxW - 12f);
        float boxY = Math.Clamp(pt.Y - boxH / 2f, _plotRect.Top + 4f, Math.Max(_plotRect.Top + 4f, _plotRect.Bottom - boxH - 4f));

        var box = new RectangleF(boxX, boxY, boxW, boxH);
        using var cardBrush = new SolidBrush(Color.FromArgb(245, bg));
        using var cardPen = new Pen(border, 1f);
        g.FillRectangle(cardBrush, box);
        g.DrawRectangle(cardPen, box.X, box.Y, box.Width, box.Height);

        using var textBrush = new SolidBrush(ink);
        for (int i = 0; i < lines.Length; i++)
        {
            g.DrawString(lines[i], i == 0 ? boldFont : tipFont, textBrush, box.X + 9f, box.Y + 6f + i * lineH);
        }
    }

    private float IndexToX(int index, int count)
    {
        if (count <= 1) return _plotRect.Left;
        return _plotRect.Left + (index / (float)(count - 1)) * _plotRect.Width;
    }

    private float ValueToY(double valueMbps, double yMax)
    {
        double ratio = Math.Clamp(valueMbps / Math.Max(1.0, yMax), 0, 1);
        return _plotRect.Bottom - (float)(ratio * _plotRect.Height);
    }

    private static double ComputeNiceCeiling(double rawMax)
    {
        double[] steps = [5, 10, 20, 25, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 2000, 5000];
        foreach (double s in steps)
        {
            if (rawMax <= s) return s;
        }

        return Math.Ceiling(rawMax / 1000.0) * 1000.0;
    }

    private static List<NetworkSample> DownsampleForWidth(IReadOnlyList<NetworkSample> source, int maxBuckets)
    {
        if (source.Count <= maxBuckets)
        {
            return source is List<NetworkSample> list ? list : source.ToList();
        }

        // Preserve anomaly dips in each bucket by selecting the worst-status / lowest-speed representative per bucket.
        var result = new List<NetworkSample>(maxBuckets);
        double bucketSize = source.Count / (double)maxBuckets;
        for (int b = 0; b < maxBuckets; b++)
        {
            int start = (int)(b * bucketSize);
            int end = Math.Min(source.Count, (int)((b + 1) * bucketSize));
            if (start >= end) continue;

            NetworkSample chosen = source[start];
            for (int i = start + 1; i < end; i++)
            {
                NetworkSample candidate = source[i];
                if (candidate.Status > chosen.Status ||
                    (candidate.Status == chosen.Status &&
                     (candidate.InternetDownloadMbps ?? 0) < (chosen.InternetDownloadMbps ?? 0)))
                {
                    chosen = candidate;
                }
            }

            result.Add(chosen);
        }

        return result;
    }

    private static void DrawOuterBorder(Graphics g, int w, int h, Color borderColor)
    {
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(borderColor);
        g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
    }
}
