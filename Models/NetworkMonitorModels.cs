using System.Globalization;

namespace FileViewer.Models;

public enum NetworkHealthStatus
{
    Healthy,
    Degraded,
    HighLatency,
    PacketLoss,
    Offline
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record NetworkSample(
    DateTime Timestamp,
    double? InternetDownloadMbps,
    double LocalDownloadMbps,
    double LocalUploadMbps,
    string GatewayIp,
    double? GatewayLatencyMs,
    double GatewayLossPercent,
    double? InternetLatencyMs,
    double? JitterMs,
    double InternetLossPercent,
    double? DnsLatencyMs,
    int? WifiSignalPercent,
    double LinkSpeedMbps,
    string AdapterName,
    string ConnectionType,
    long InterfaceErrorsDelta,
    string TopProcess,
    double? TopProcessBytesPerSec,
    NetworkHealthStatus Status,
    string Notes)
{
    public string TimestampDisplay => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string FullTimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string InternetSpeedDisplay => InternetDownloadMbps.HasValue
        ? $"{InternetDownloadMbps.Value:0.00} Mbps"
        : "—";

    public string LocalDownDisplay => $"{LocalDownloadMbps:0.00} Mbps";

    public string LocalUpDisplay => $"{LocalUploadMbps:0.00} Mbps";

    public string GatewayPingDisplay => GatewayLatencyMs.HasValue
        ? $"{GatewayLatencyMs.Value:0.#} ms"
        : (string.IsNullOrEmpty(GatewayIp) ? "—" : "Timeout");

    public string InternetPingDisplay => InternetLatencyMs.HasValue
        ? $"{InternetLatencyMs.Value:0.#} ms"
        : "Timeout";

    public string JitterDisplay => JitterMs.HasValue ? $"{JitterMs.Value:0.#} ms" : "—";

    public string PacketLossDisplay => $"{InternetLossPercent:0.#}%";

    public string DnsDisplay => DnsLatencyMs.HasValue ? $"{DnsLatencyMs.Value:0.#} ms" : "—";

    public string LinkDisplay => WifiSignalPercent.HasValue
        ? $"Wi-Fi {WifiSignalPercent.Value}% ({LinkSpeedMbps:0} Mbps)"
        : LinkSpeedMbps > 0
            ? $"{ConnectionType} ({LinkSpeedMbps:0} Mbps)"
            : ConnectionType;

    public string StatusDisplay => Status switch
    {
        NetworkHealthStatus.Healthy => "Healthy",
        NetworkHealthStatus.Degraded => "Slow Speed",
        NetworkHealthStatus.HighLatency => "High Latency",
        NetworkHealthStatus.PacketLoss => "Packet Loss",
        NetworkHealthStatus.Offline => "Offline",
        _ => "Unknown"
    };
}

public sealed record HourlyNetworkSummary(
    int Hour,
    int SampleCount,
    double? AvgInternetMbps,
    double? MinInternetMbps,
    double AvgLocalDownMbps,
    double AvgLocalUpMbps,
    double? AvgGatewayMs,
    double? AvgInternetMs,
    double AvgPacketLossPercent,
    double? AvgWifiSignalPercent,
    double DegradedPercent,
    string DominantProcess,
    bool IsSlowWindow)
{
    public string HourRangeDisplay => $"{Hour:00}:00 - {(Hour + 1) % 24:00}:00";
}

public sealed record DiagnosticFinding(
    DiagnosticSeverity Severity,
    string Category,
    string Title,
    string Evidence,
    string Recommendation)
{
    public string SeverityDisplay => Severity switch
    {
        DiagnosticSeverity.Critical => "Critical",
        DiagnosticSeverity.Warning => "Warning",
        _ => "Info"
    };
}

public sealed record ActiveDiagnosticSnapshot(
    DateTime ExecutedAt,
    string AdapterName,
    string ConnectionType,
    double LinkSpeedMbps,
    int? WifiSignalPercent,
    string GatewayIp,
    double? GatewayAvgMs,
    double? GatewayJitterMs,
    double GatewayLossPercent,
    string IspHopIp,
    double? IspHopAvgMs,
    double IspHopLossPercent,
    double? InternetAvgMs,
    double? InternetJitterMs,
    double InternetLossPercent,
    double? LoadedLatencyMs,
    double? BufferbloatDeltaMs,
    double? MeasuredDownloadMbps,
    double? MeasuredUploadMbps,
    double? SystemDnsMs,
    double? CloudflareDnsMs,
    double? GoogleDnsMs,
    double LocalDownMbps,
    double LocalUpMbps,
    List<string> TopProcesses,
    bool HasElevatedTraffic);

public sealed record NetworkDiagnosisReport(
    DateTime GeneratedAt,
    ActiveDiagnosticSnapshot ActiveProbe,
    int TotalSamplesAnalyzed,
    string TimeRangeDisplay,
    double? BaselineInternetMbps,
    List<string> DetectedSlowWindows,
    List<HourlyNetworkSummary> HourlyBreakdown,
    List<DiagnosticFinding> Findings,
    string ExecutiveSummary,
    string FullTextReport);
