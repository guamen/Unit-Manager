using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using FileViewer.Models;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FileViewer.Services;

internal sealed class NetworkMonitorService : IDisposable
{
    private static readonly string[] InternetPingTargets = ["1.1.1.1", "8.8.8.8"];
    private static readonly Uri CloudflareDownBase = new("https://speed.cloudflare.com/__down");
    private static readonly Uri CloudflareUpUri = new("https://speed.cloudflare.com/__up");
    private static readonly Uri FallbackDownloadUri = new("https://ajax.googleapis.com/ajax/libs/jquery/3.7.1/jquery.min.js");

    private readonly HttpClient _http;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly object _counterLock = new();

    private string? _lastAdapterId;
    private long _prevBytesReceived;
    private long _prevBytesSent;
    private long _prevInterfaceErrors;
    private long _prevTimestampTicks;
    private double? _lastMeasuredMbps;
    private double? _lastInternetPingMs;
    private double? _runningPeakInternetMbps;

    // Optional live ETW per-process network tracker (when elevated)
    private readonly ConcurrentDictionary<int, long> _etwProcessBytes = new();
    private readonly object _etwLock = new();
    private TraceEventSession? _etwSession;
    private Task? _etwConsumerTask;
    private bool _etwActive;
    private bool _disposed;

    public bool IsElevated => ProcessMonitorService.IsCurrentProcessElevated();
    public bool IsEtwTrackingActive => _etwActive;

    public NetworkMonitorService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UnitManager-NetworkMonitor/1.0");
    }

    public void StartProcessTrafficCapture()
    {
        if (!IsElevated || _disposed)
        {
            return;
        }

        lock (_etwLock)
        {
            if (_etwActive)
            {
                return;
            }

            try
            {
                var session = new TraceEventSession($"UnitManager-NetMon-{Guid.NewGuid():N}");
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;

                void Record(int pid, int size)
                {
                    if (pid <= 0 || size <= 0 || pid == _ownPid)
                    {
                        return;
                    }

                    _etwProcessBytes.AddOrUpdate(pid, static (_, s) => s, static (_, current, s) => current + s, (long)size);
                }

                kernel.TcpIpSend += e => Record(e.ProcessID, e.size);
                kernel.TcpIpRecv += e => Record(e.ProcessID, e.size);
                kernel.TcpIpSendIPV6 += e => Record(e.ProcessID, e.size);
                kernel.TcpIpRecvIPV6 += e => Record(e.ProcessID, e.size);
                kernel.UdpIpSend += e => Record(e.ProcessID, e.size);
                kernel.UdpIpRecv += e => Record(e.ProcessID, e.size);
                kernel.UdpIpSendIPV6 += e => Record(e.ProcessID, e.size);
                kernel.UdpIpRecvIPV6 += e => Record(e.ProcessID, e.size);

                _etwSession = session;
                _etwActive = true;
                _etwConsumerTask = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        session.Source.Process();
                    }
                    catch
                    {
                        // Session stopped or unavailable.
                    }
                    finally
                    {
                        _etwActive = false;
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                StopProcessTrafficCapture();
            }
        }
    }

    public void StopProcessTrafficCapture()
    {
        TraceEventSession? sessionToStop;
        lock (_etwLock)
        {
            sessionToStop = _etwSession;
            _etwSession = null;
            _etwActive = false;
        }

        if (sessionToStop is not null)
        {
            try
            {
                sessionToStop.Stop(noThrow: true);
            }
            catch
            {
                // Ignore shutdown exceptions.
            }

            try
            {
                sessionToStop.Dispose();
            }
            catch
            {
                // Ignore disposal exceptions.
            }
        }

        _etwProcessBytes.Clear();
    }

    public void ResetCounters()
    {
        lock (_counterLock)
        {
            _lastAdapterId = null;
            _prevBytesReceived = 0;
            _prevBytesSent = 0;
            _prevInterfaceErrors = 0;
            _prevTimestampTicks = 0;
            _lastMeasuredMbps = null;
            _lastInternetPingMs = null;
            _runningPeakInternetMbps = null;
        }

        _etwProcessBytes.Clear();
    }

    /// <summary>
    /// Collects one 5-second telemetry sample: local adapter RX/TX, gateway ping, internet ping/jitter/loss,
    /// DNS latency, Wi-Fi signal, top network process, and a lightweight internet speed probe.
    /// </summary>
    public async Task<NetworkSample> CollectSampleAsync(CancellationToken token)
    {
        DateTime now = DateTime.Now;
        AdapterSnapshot adapter = GetPrimaryAdapter();
        int? wifiSignal = adapter.IsWifi ? TryGetWifiSignalPercent() : null;

        // Run gateway ping, internet ping, DNS latency, and lightweight speed test concurrently.
        Task<(double? AvgMs, double LossPct)> gatewayTask = string.IsNullOrEmpty(adapter.GatewayIp)
            ? Task.FromResult<(double?, double)>((null, 0))
            : PingBurstAsync(adapter.GatewayIp, count: 2, timeoutMs: 1200, token);

        Task<(double? AvgMs, double? JitterMs, double LossPct)> internetPingTask =
            PingInternetAsync(count: 2, timeoutMs: 1500, token);

        Task<double?> dnsTask = MeasureDnsLatencyAsync(adapter.PrimaryDnsIp, token);

        Task<(double? SpeedMbps, long ProbeBytes)> speedTask =
            MeasureQuickDownloadSpeedAsync(token);

        await Task.WhenAll(gatewayTask, internetPingTask, dnsTask, speedTask);

        (double? gatewayMs, double gatewayLoss) = gatewayTask.Result;
        (double? internetMs, double? burstJitter, double internetLoss) = internetPingTask.Result;
        double? dnsMs = dnsTask.Result;
        (double? speedMbps, long probeBytes) = speedTask.Result;

        // Compute jitter using burst jitter or delta from previous sample.
        double? jitterMs = burstJitter;
        if (!jitterMs.HasValue && internetMs.HasValue && _lastInternetPingMs.HasValue)
        {
            jitterMs = Math.Abs(internetMs.Value - _lastInternetPingMs.Value);
        }

        if (internetMs.HasValue)
        {
            _lastInternetPingMs = internetMs.Value;
        }

        if (speedMbps.HasValue)
        {
            _lastMeasuredMbps = speedMbps.Value;
            if (!_runningPeakInternetMbps.HasValue || speedMbps.Value > _runningPeakInternetMbps.Value)
            {
                _runningPeakInternetMbps = speedMbps.Value;
            }
        }

        // Read adapter statistics after the probe so we can subtract our own probe download bytes accurately.
        (double localDownMbps, double localUpMbps, long errorDelta, double elapsedSeconds) =
            ReadAdapterRateDelta(adapter.Id, probeBytes);

        // Capture top active network process for this window.
        (string topProcess, double? topProcBytesPerSec) = GetTopNetworkProcess(elapsedSeconds);

        // Classify health status for this 5-second sample.
        (NetworkHealthStatus status, string notes) = ClassifySampleHealth(
            speedMbps, localDownMbps, localUpMbps, gatewayMs, gatewayLoss, internetMs, internetLoss, dnsMs, wifiSignal, errorDelta);

        return new NetworkSample(
            Timestamp: now,
            InternetDownloadMbps: speedMbps,
            LocalDownloadMbps: localDownMbps,
            LocalUploadMbps: localUpMbps,
            GatewayIp: adapter.GatewayIp,
            GatewayLatencyMs: gatewayMs,
            GatewayLossPercent: gatewayLoss,
            InternetLatencyMs: internetMs,
            JitterMs: jitterMs,
            InternetLossPercent: internetLoss,
            DnsLatencyMs: dnsMs,
            WifiSignalPercent: wifiSignal,
            LinkSpeedMbps: adapter.LinkSpeedMbps,
            AdapterName: adapter.Name,
            ConnectionType: adapter.ConnectionType,
            InterfaceErrorsDelta: errorDelta,
            TopProcess: topProcess,
            TopProcessBytesPerSec: topProcBytesPerSec,
            Status: status,
            Notes: notes);
    }

    /// <summary>
    /// Runs a comprehensive active network & internet diagnosis and combines it with all collected
    /// historical samples (session + logs) to detect time-of-day slow windows (e.g., 11:00–16:00) and root causes.
    /// </summary>
    public async Task<NetworkDiagnosisReport> RunFullDiagnosisAsync(
        IReadOnlyList<NetworkSample> historicalSamples,
        IProgress<string>? progress,
        CancellationToken token)
    {
        progress?.Report("Inspecting network adapter and Wi-Fi link quality...");
        AdapterSnapshot adapter = GetPrimaryAdapter();
        int? wifiSignal = adapter.IsWifi ? TryGetWifiSignalPercent() : null;

        long rxStart = adapter.BytesReceived;
        long txStart = adapter.BytesSent;
        var diagWatch = Stopwatch.StartNew();

        // Step 1: Multi-hop latency test (Gateway -> ISP Hop 2 -> Internet)
        progress?.Report("Testing latency and packet loss to Router, ISP Hop 2, and Internet...");
        Task<(double? AvgMs, double? JitterMs, double LossPct)> gwTask = string.IsNullOrEmpty(adapter.GatewayIp)
            ? Task.FromResult<(double?, double?, double)>((null, null, 0))
            : PingDetailedBurstAsync(adapter.GatewayIp, count: 6, timeoutMs: 1200, token);

        Task<(double? AvgMs, double? JitterMs, double LossPct)> inetTask =
            PingInternetAsync(count: 8, timeoutMs: 1500, token);

        Task<(string HopIp, double? AvgMs, double LossPct)> ispHopTask =
            DiscoverAndPingIspHopAsync(adapter.GatewayIp, token);

        await Task.WhenAll(gwTask, inetTask, ispHopTask);
        (double? gwAvg, double? gwJitter, double gwLoss) = gwTask.Result;
        (double? inetAvg, double? inetJitter, double inetLoss) = inetTask.Result;
        (string ispHopIp, double? ispHopAvg, double ispHopLoss) = ispHopTask.Result;

        // Step 2: DNS Benchmark (System DNS vs 1.1.1.1 vs 8.8.8.8)
        progress?.Report("Benchmarking System DNS vs Cloudflare (1.1.1.1) and Google (8.8.8.8)...");
        Task<double?> sysDnsTask = MeasureDnsLatencyAsync(adapter.PrimaryDnsIp, token);
        Task<double?> cfDnsTask = MeasureDnsLatencyAsync("1.1.1.1", token);
        Task<double?> gDnsTask = MeasureDnsLatencyAsync("8.8.8.8", token);
        await Task.WhenAll(sysDnsTask, cfDnsTask, gDnsTask);

        // Step 3: Active Download speed + Bufferbloat loaded latency test
        progress?.Report("Measuring Internet download speed and latency under load (bufferbloat)...");
        (double? downloadMbps, long downProbeBytes, double? loadedDownLatency) =
            await MeasureDeepDownloadWithLoadedLatencyAsync(token);

        // Step 4: Active Upload speed + Upload Bufferbloat test
        progress?.Report("Measuring Internet upload speed and upload bufferbloat...");
        (double? uploadMbps, long upProbeBytes, double? loadedUpLatency) =
            await MeasureDeepUploadWithLoadedLatencyAsync(token);

        double? loadedLatency = (loadedDownLatency, loadedUpLatency) switch
        {
            (double d, double u) => Math.Max(d, u),
            (double d, null) => d,
            (null, double u) => u,
            _ => null
        };
        double? bufferbloatDelta = loadedLatency.HasValue && inetAvg.HasValue
            ? Math.Max(0, loadedLatency.Value - inetAvg.Value)
            : null;

        // Step 5: Process bandwidth attribution
        progress?.Report("Identifying active network processes and analyzing historical patterns...");
        diagWatch.Stop();
        AdapterSnapshot adapterEnd = GetPrimaryAdapter();
        double diagSecs = Math.Max(0.5, diagWatch.Elapsed.TotalSeconds);
        long localRxDelta = Math.Max(0, (adapterEnd.BytesReceived - rxStart) - downProbeBytes);
        long localTxDelta = Math.Max(0, (adapterEnd.BytesSent - txStart) - upProbeBytes);
        double localDownMbps = localRxDelta * 8.0 / (diagSecs * 1_000_000.0);
        double localUpMbps = localTxDelta * 8.0 / (diagSecs * 1_000_000.0);

        (List<string> topProcesses, bool hasElevatedTraffic) = await CaptureTopProcessesForDiagnosisAsync(token);

        var activeSnapshot = new ActiveDiagnosticSnapshot(
            ExecutedAt: DateTime.Now,
            AdapterName: adapter.Name,
            ConnectionType: adapter.ConnectionType,
            LinkSpeedMbps: adapter.LinkSpeedMbps,
            WifiSignalPercent: wifiSignal,
            GatewayIp: adapter.GatewayIp,
            GatewayAvgMs: gwAvg,
            GatewayJitterMs: gwJitter,
            GatewayLossPercent: gwLoss,
            IspHopIp: ispHopIp,
            IspHopAvgMs: ispHopAvg,
            IspHopLossPercent: ispHopLoss,
            InternetAvgMs: inetAvg,
            InternetJitterMs: inetJitter,
            InternetLossPercent: inetLoss,
            LoadedLatencyMs: loadedLatency,
            BufferbloatDeltaMs: bufferbloatDelta,
            MeasuredDownloadMbps: downloadMbps,
            MeasuredUploadMbps: uploadMbps,
            SystemDnsMs: sysDnsTask.Result,
            CloudflareDnsMs: cfDnsTask.Result,
            GoogleDnsMs: gDnsTask.Result,
            LocalDownMbps: localDownMbps,
            LocalUpMbps: localUpMbps,
            TopProcesses: topProcesses,
            HasElevatedTraffic: hasElevatedTraffic);

        return BuildDiagnosisReport(activeSnapshot, historicalSamples);
    }

    private NetworkDiagnosisReport BuildDiagnosisReport(
        ActiveDiagnosticSnapshot active,
        IReadOnlyList<NetworkSample> samples)
    {
        var ordered = samples.OrderBy(s => s.Timestamp).ToList();
        string timeRange = ordered.Count >= 2
            ? $"{ordered[0].Timestamp:yyyy-MM-dd HH:mm:ss} to {ordered[^1].Timestamp:yyyy-MM-dd HH:mm:ss} ({ordered.Count:N0} samples)"
            : ordered.Count == 1
                ? $"{ordered[0].Timestamp:yyyy-MM-dd HH:mm:ss} (1 sample)"
                : "Live probe only (no 5-second log samples loaded)";

        // Compute hourly summaries
        var hourlyRaw = ordered
            .GroupBy(s => s.Timestamp.Hour)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var speeds = g.Where(x => x.InternetDownloadMbps.HasValue).Select(x => x.InternetDownloadMbps!.Value).ToList();
                var gwPings = g.Where(x => x.GatewayLatencyMs.HasValue).Select(x => x.GatewayLatencyMs!.Value).ToList();
                var inetPings = g.Where(x => x.InternetLatencyMs.HasValue).Select(x => x.InternetLatencyMs!.Value).ToList();
                var wifis = g.Where(x => x.WifiSignalPercent.HasValue).Select(x => (double)x.WifiSignalPercent!.Value).ToList();
                int degradedCount = g.Count(x => x.Status != NetworkHealthStatus.Healthy);
                string dominantProc = g
                    .Select(x => ExtractProcessName(x.TopProcess))
                    .Where(name => !string.IsNullOrEmpty(name))
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(pg => pg.Count())
                    .Select(pg => pg.Key)
                    .FirstOrDefault() ?? "—";

                return new HourlyNetworkSummary(
                    Hour: g.Key,
                    SampleCount: g.Count(),
                    AvgInternetMbps: speeds.Count > 0 ? speeds.Average() : null,
                    MinInternetMbps: speeds.Count > 0 ? speeds.Min() : null,
                    AvgLocalDownMbps: g.Average(x => x.LocalDownloadMbps),
                    AvgLocalUpMbps: g.Average(x => x.LocalUploadMbps),
                    AvgGatewayMs: gwPings.Count > 0 ? gwPings.Average() : null,
                    AvgInternetMs: inetPings.Count > 0 ? inetPings.Average() : null,
                    AvgPacketLossPercent: g.Average(x => x.InternetLossPercent),
                    AvgWifiSignalPercent: wifis.Count > 0 ? wifis.Average() : null,
                    DegradedPercent: degradedCount * 100.0 / g.Count(),
                    DominantProcess: dominantProc,
                    IsSlowWindow: false);
            })
            .ToList();

        // Determine baseline speed (80th percentile of all valid speed samples, or best hourly average)
        var allValidSpeeds = ordered
            .Where(s => s.InternetDownloadMbps.HasValue && s.InternetDownloadMbps.Value > 0.1)
            .Select(s => s.InternetDownloadMbps!.Value)
            .OrderBy(v => v)
            .ToList();

        double? baselineSpeed = null;
        if (allValidSpeeds.Count >= 5)
        {
            int p80Index = Math.Min(allValidSpeeds.Count - 1, (int)(allValidSpeeds.Count * 0.80));
            baselineSpeed = allValidSpeeds[p80Index];
        }
        else if (active.MeasuredDownloadMbps.HasValue)
        {
            baselineSpeed = active.MeasuredDownloadMbps.Value;
        }

        var validPings = ordered
            .Where(s => s.InternetLatencyMs.HasValue)
            .Select(s => s.InternetLatencyMs!.Value)
            .OrderBy(v => v)
            .ToList();
        double? baselinePing = validPings.Count > 0
            ? validPings[validPings.Count / 4]
            : active.InternetAvgMs;

        // Flag slow hours: speed dropped >= 30% below baseline, or packet loss >= 3%, or latency >= 2x baseline (+30ms)
        var hourly = hourlyRaw.Select(h =>
        {
            bool speedDrop = baselineSpeed.HasValue && baselineSpeed.Value >= 5 && h.AvgInternetMbps.HasValue
                && h.AvgInternetMbps.Value <= baselineSpeed.Value * 0.70;
            bool highLoss = h.AvgPacketLossPercent >= 3.0;
            bool highLatency = baselinePing.HasValue && h.AvgInternetMs.HasValue
                && h.AvgInternetMs.Value >= Math.Max(baselinePing.Value * 2.0, baselinePing.Value + 35);
            return h with { IsSlowWindow = speedDrop || highLoss || highLatency };
        }).ToList();

        // Merge contiguous slow hours into human-readable time windows (e.g., "11:00 - 16:00")
        List<string> detectedWindows = BuildSlowWindowDescriptions(hourly, baselineSpeed);

        // Evaluate diagnostic findings (both active and historical)
        var findings = new List<DiagnosticFinding>();

        // 1. Offline / complete outage check
        if (!active.InternetAvgMs.HasValue && !active.MeasuredDownloadMbps.HasValue)
        {
            bool routerReachable = active.GatewayAvgMs.HasValue;
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Critical,
                routerReachable ? "ISP Outage" : "Local Network Disconnected",
                routerReachable
                    ? "Router is reachable, but Internet is unreachable"
                    : "Cannot reach local router or Internet",
                routerReachable
                    ? $"Gateway ({active.GatewayIp}) responded in {active.GatewayAvgMs:0.#} ms, while 1.1.1.1 / 8.8.8.8 and HTTP speed probes timed out."
                    : $"No reply from local gateway ({(string.IsNullOrEmpty(active.GatewayIp) ? "none" : active.GatewayIp)}) or external hosts.",
                routerReachable
                    ? "Your ISP connection or modem is down. Restart the ISP modem/ONT and check with your provider for an area outage."
                    : "Check your Wi-Fi connection, Ethernet cable, or restart your home router."));
        }

        // 2. Historical time-window root-cause attribution (the 11am-4pm solver!)
        var slowHours = hourly.Where(h => h.IsSlowWindow).ToList();
        var normalHours = hourly.Where(h => !h.IsSlowWindow).ToList();
        if (slowHours.Count > 0)
        {
            var slowHourSet = slowHours.Select(h => h.Hour).ToHashSet();
            var slowSamples = ordered.Where(s => slowHourSet.Contains(s.Timestamp.Hour)).ToList();
            double slowAvgSpeed = slowSamples.Where(s => s.InternetDownloadMbps.HasValue).Select(s => s.InternetDownloadMbps!.Value).DefaultIfEmpty(0).Average();
            double slowAvgLocalDown = slowSamples.Average(s => s.LocalDownloadMbps);
            double slowAvgLocalUp = slowSamples.Average(s => s.LocalUploadMbps);
            double slowAvgGw = slowSamples.Where(s => s.GatewayLatencyMs.HasValue).Select(s => s.GatewayLatencyMs!.Value).DefaultIfEmpty(0).Average();
            double slowGwLoss = slowSamples.Average(s => s.GatewayLossPercent);
            double slowInetLoss = slowSamples.Average(s => s.InternetLossPercent);
            double? slowWifi = slowSamples.Where(s => s.WifiSignalPercent.HasValue).Select(s => (double)s.WifiSignalPercent!.Value) is var w && w.Any()
                ? w.Average() : null;

            double normalAvgGw = normalHours.Where(h => h.AvgGatewayMs.HasValue).Select(h => h.AvgGatewayMs!.Value).DefaultIfEmpty(slowAvgGw).Average();
            string windowList = string.Join(", ", detectedWindows);

            bool routerOrWifiBadInWindow = slowAvgGw >= 20 || slowGwLoss >= 2.0 || (slowAvgGw - normalAvgGw >= 12) || (slowWifi.HasValue && slowWifi.Value < 55);
            bool localPcSaturatedInWindow = (baselineSpeed.HasValue && baselineSpeed.Value > 5 && slowAvgLocalDown >= baselineSpeed.Value * 0.45)
                || slowAvgLocalUp >= 4.0
                || slowAvgLocalDown >= 15.0;

            if (localPcSaturatedInWindow)
            {
                string topCulprit = slowHours
                    .Select(h => h.DominantProcess)
                    .Where(p => p != "—")
                    .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault() ?? "background process";

                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Critical,
                    "Local PC Saturation",
                    "This PC is saturating the connection during slow hours",
                    $"Slow window(s): {windowList}. During these hours, this PC averaged {slowAvgLocalDown:0.1} Mbps down / {slowAvgLocalUp:0.1} Mbps up (dominant process: {topCulprit}).",
                    $"Throttle or reschedule '{topCulprit}' (e.g., cloud backups, Windows Update active hours, OneDrive/Google Drive upload limit, or game downloads) outside those hours."));
            }

            if (routerOrWifiBadInWindow)
            {
                string wifiNote = slowWifi.HasValue ? $" Avg Wi-Fi signal: {slowWifi.Value:0}%." : string.Empty;
                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Critical,
                    "Router / Wi-Fi / LAN Congestion",
                    "Home router or Wi-Fi degrades during slow hours",
                    $"Slow window(s): {windowList}. Local router ping averaged {slowAvgGw:0.1} ms (loss {slowGwLoss:0.1}%).{wifiNote}",
                    "The bottleneck is inside your home network during those hours: either another device on your LAN is flooding the router, Wi-Fi interference spikes on your channel, or the router is overheating/overloaded. Test with Ethernet, switch to 5 GHz, inspect connected devices in your router admin page during those hours, or enable SQM/QoS."));
            }

            if (!localPcSaturatedInWindow && !routerOrWifiBadInWindow)
            {
                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Critical,
                    "ISP Peak-Hour Congestion / Throttling",
                    "ISP slows down during specific hours while your home network is healthy",
                    $"Slow window(s): {windowList}. Avg Internet speed dropped to {slowAvgSpeed:0.1} Mbps (baseline {baselineSpeed:0.1} Mbps, loss {slowInetLoss:0.1}%), while Router ping stayed clean ({slowAvgGw:0.1} ms, {slowGwLoss:0.1}% loss) and this PC's usage was low ({slowAvgLocalDown:0.1} Mbps down / {slowAvgLocalUp:0.1} Mbps up).",
                    "This signature strongly points to ISP neighborhood node congestion or time-based traffic shaping/throttling (or another wired device saturating the modem WAN if your router doesn't show LAN ping spikes). Export this report & the CSV log as timestamped evidence for your ISP, and confirm by checking router WAN traffic stats during those hours."));
            }
        }
        else if (ordered.Count < 60 || hourly.Count < 2)
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Info,
                "Time-Window Coverage",
                "Not enough multi-hour data yet to confirm a recurring schedule (e.g., 11:00–16:00)",
                $"Only {ordered.Count:N0} sample(s) spanning {hourly.Count} clock hour(s) were analyzed.",
                "Enable 'Enable diagnostic log (every 5s)' and leave Unit Manager running across both normal and slow hours (for example, 10:00 AM to 5:00 PM). You can also click 'Load Log...' to analyze previously saved daily CSV logs."));
        }
        else
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Info,
                "Time-Window Pattern",
                "No major hourly speed drop detected across the analyzed logs",
                $"Analyzed {ordered.Count:N0} samples across {hourly.Count} hour(s). Baseline speed: {(baselineSpeed.HasValue ? $"{baselineSpeed.Value:0.1} Mbps" : "N/A")}.",
                "Keep the 5-second diagnostic log enabled during the hours when you experience slowdowns to capture the exact window."));
        }

        // 3. Active Router vs ISP Hop 2 vs Internet path comparison
        if (active.GatewayAvgMs.HasValue && active.InternetAvgMs.HasValue)
        {
            if (active.GatewayAvgMs.Value >= 20 || active.GatewayLossPercent > 0)
            {
                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Critical,
                    "Local Router / Wi-Fi",
                    "High latency or packet loss to your local router right now",
                    $"Gateway ({active.GatewayIp}): avg {active.GatewayAvgMs:0.1} ms, jitter {active.GatewayJitterMs:0.1} ms, loss {active.GatewayLossPercent:0.#}%.",
                    "Move closer to the router, switch from 2.4 GHz to 5 GHz Wi-Fi, change to a less crowded Wi-Fi channel, or test with an Ethernet cable."));
            }
            else if (active.InternetLossPercent >= 5 || active.InternetAvgMs.Value >= 100 || (active.InternetJitterMs ?? 0) >= 30)
            {
                string hopDetail = !string.IsNullOrEmpty(active.IspHopIp) && active.IspHopAvgMs.HasValue
                    ? $" First ISP hop ({active.IspHopIp}) responded in {active.IspHopAvgMs:0.1} ms ({active.IspHopLossPercent:0.#}% loss)."
                    : string.Empty;

                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Warning,
                    "ISP Upstream Path",
                    "Local router is fast, but Internet latency/loss is elevated right now",
                    $"Gateway ({active.GatewayIp}) is healthy ({active.GatewayAvgMs:0.1} ms, 0% loss), while Internet ping is {active.InternetAvgMs:0.1} ms (jitter {active.InternetJitterMs:0.1} ms, loss {active.InternetLossPercent:0.#}%).{hopDetail}",
                    "The delay is happening beyond your home router on your ISP's network or modem uplink. Reboot the modem and report persistent spikes/loss to your ISP."));
            }
        }

        // 4. Bufferbloat check (latency under load)
        if (active.BufferbloatDeltaMs.HasValue && active.BufferbloatDeltaMs.Value >= 50)
        {
            var sev = active.BufferbloatDeltaMs.Value >= 120 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
            findings.Add(new DiagnosticFinding(
                sev,
                "Bufferbloat",
                $"Latency spikes by +{active.BufferbloatDeltaMs.Value:0} ms when the connection is under load",
                $"Idle Internet ping: {active.InternetAvgMs:0.1} ms -> Loaded ping during transfer: {active.LoadedLatencyMs:0.1} ms.",
                "Your router/modem queues packets excessively when someone downloads or uploads (making the whole house feel laggy). Enable SQM / Smart Queue Management or QoS in your router settings and cap max upload/download to ~85–90% of your plan speed."));
        }

        // 5. Wi-Fi signal & link speed check
        if (active.WifiSignalPercent.HasValue && active.WifiSignalPercent.Value < 65)
        {
            findings.Add(new DiagnosticFinding(
                active.WifiSignalPercent.Value < 45 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
                "Wi-Fi Signal",
                $"Weak Wi-Fi signal ({active.WifiSignalPercent.Value}%)",
                $"Adapter '{active.AdapterName}' is connected via Wi-Fi at {active.WifiSignalPercent.Value}% signal ({active.LinkSpeedMbps:0} Mbps link).",
                "Weak Wi-Fi causes retransmissions and sudden speed drops when neighbors are active. Reposition the router, use a mesh node/repeater, or connect via Ethernet."));
        }

        // 6. DNS health check
        if (active.SystemDnsMs.HasValue)
        {
            double? fastestPublic = (active.CloudflareDnsMs, active.GoogleDnsMs) switch
            {
                (double c, double g) => Math.Min(c, g),
                (double c, null) => c,
                (null, double g) => g,
                _ => null
            };

            if (active.SystemDnsMs.Value >= 80 || (fastestPublic.HasValue && active.SystemDnsMs.Value > fastestPublic.Value * 2.5 + 25))
            {
                findings.Add(new DiagnosticFinding(
                    DiagnosticSeverity.Warning,
                    "DNS Performance",
                    $"System DNS is slow ({active.SystemDnsMs.Value:0} ms)",
                    $"System DNS: {active.SystemDnsMs.Value:0.1} ms vs Cloudflare 1.1.1.1 ({active.CloudflareDnsMs?.ToString("0.1") ?? "N/A"} ms) and Google 8.8.8.8 ({active.GoogleDnsMs?.ToString("0.1") ?? "N/A"} ms).",
                    "Slow ISP DNS makes websites feel sluggish to start loading even if download speed is fine. Switch your router or Windows DNS to 1.1.1.1 and 8.8.8.8."));
            }
        }

        // 7. Live local PC background traffic
        if (active.LocalDownMbps >= 8.0 || active.LocalUpMbps >= 3.0)
        {
            string procs = active.TopProcesses.Count > 0 ? string.Join(", ", active.TopProcesses.Take(3)) : "run as administrator for exact process rates";
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Warning,
                "Active Local Usage",
                "This PC is actively using noticeable bandwidth right now",
                $"Local background usage: {active.LocalDownMbps:0.1} Mbps down / {active.LocalUpMbps:0.1} Mbps up. Top processes: {procs}.",
                "Pause active downloads, cloud sync, or streaming on this PC before blaming your ISP, or open PC Diagnostics to throttle heavy apps."));
        }

        // 8. Interface errors check from historical samples
        long totalInterfaceErrors = ordered.Sum(s => s.InterfaceErrorsDelta);
        if (totalInterfaceErrors >= 20)
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Warning,
                "Adapter Errors",
                $"Network adapter logged {totalInterfaceErrors:N0} packet errors/discards",
                $"Detected {totalInterfaceErrors:N0} RX/TX errors or discards on '{active.AdapterName}' during the monitoring period.",
                "Update your network adapter driver, replace damaged Ethernet cables, or inspect your router port."));
        }

        // Ensure at least one healthy summary finding if no warnings/criticals exist
        if (!findings.Any(f => f.Severity != DiagnosticSeverity.Info))
        {
            findings.Insert(0, new DiagnosticFinding(
                DiagnosticSeverity.Info,
                "Overall Health",
                "Live network and Internet checks look healthy right now",
                $"Download: {active.MeasuredDownloadMbps?.ToString("0.1") ?? "N/A"} Mbps, Upload: {active.MeasuredUploadMbps?.ToString("0.1") ?? "N/A"} Mbps, Router: {active.GatewayAvgMs?.ToString("0.1") ?? "N/A"} ms, Internet: {active.InternetAvgMs?.ToString("0.1") ?? "N/A"} ms (0% loss).",
                "If slowdowns only happen at specific hours (such as 11:00–16:00), keep the 5-second diagnostic log enabled through that time window and re-run this diagnosis afterward."));
        }

        // Order findings by severity (Critical -> Warning -> Info)
        findings = findings.OrderByDescending(f => f.Severity).ToList();

        string executiveSummary = BuildExecutiveSummary(active, findings, detectedWindows, ordered.Count);
        string fullText = BuildFullTextReport(active, timeRange, baselineSpeed, detectedWindows, hourly, findings, executiveSummary);

        return new NetworkDiagnosisReport(
            GeneratedAt: DateTime.Now,
            ActiveProbe: active,
            TotalSamplesAnalyzed: ordered.Count,
            TimeRangeDisplay: timeRange,
            BaselineInternetMbps: baselineSpeed,
            DetectedSlowWindows: detectedWindows,
            HourlyBreakdown: hourly,
            Findings: findings,
            ExecutiveSummary: executiveSummary,
            FullTextReport: fullText);
    }

    private static List<string> BuildSlowWindowDescriptions(List<HourlyNetworkSummary> hourly, double? baselineSpeed)
    {
        var result = new List<string>();
        int i = 0;
        while (i < hourly.Count)
        {
            if (!hourly[i].IsSlowWindow)
            {
                i++;
                continue;
            }

            int startIdx = i;
            while (i + 1 < hourly.Count && hourly[i + 1].IsSlowWindow && hourly[i + 1].Hour == hourly[i].Hour + 1)
            {
                i++;
            }

            var span = hourly.GetRange(startIdx, i - startIdx + 1);
            int startHour = span[0].Hour;
            int endHour = (span[^1].Hour + 1) % 24;
            double avgSpeed = span.Where(h => h.AvgInternetMbps.HasValue).Select(h => h.AvgInternetMbps!.Value).DefaultIfEmpty(0).Average();
            string dropText = baselineSpeed.HasValue && baselineSpeed.Value > 0.1
                ? $", {((avgSpeed - baselineSpeed.Value) / baselineSpeed.Value * 100.0):0}% vs {baselineSpeed.Value:0.1} Mbps baseline"
                : string.Empty;

            result.Add($"{startHour:00}:00 - {endHour:00}:00 (avg {avgSpeed:0.1} Mbps{dropText})");
            i++;
        }

        return result;
    }

    private static string BuildExecutiveSummary(
        ActiveDiagnosticSnapshot active,
        List<DiagnosticFinding> findings,
        List<string> detectedWindows,
        int sampleCount)
    {
        var topFinding = findings.FirstOrDefault();
        var sb = new StringBuilder();
        if (topFinding is not null)
        {
            sb.Append($"Primary conclusion: [{topFinding.Category}] {topFinding.Title}. ");
        }

        if (detectedWindows.Count > 0)
        {
            sb.Append($"Detected slow window(s): {string.Join("; ", detectedWindows)}. ");
        }

        sb.Append($"Live test: Down {active.MeasuredDownloadMbps?.ToString("0.1") ?? "N/A"} Mbps · Up {active.MeasuredUploadMbps?.ToString("0.1") ?? "N/A"} Mbps · Router {active.GatewayAvgMs?.ToString("0.#") ?? "N/A"} ms · Internet {active.InternetAvgMs?.ToString("0.#") ?? "N/A"} ms");
        if (active.BufferbloatDeltaMs.HasValue)
        {
            sb.Append($" · Load Latency +{active.BufferbloatDeltaMs.Value:0} ms");
        }

        sb.Append($" · Samples analyzed: {sampleCount:N0}.");
        return sb.ToString();
    }

    private static string BuildFullTextReport(
        ActiveDiagnosticSnapshot active,
        string timeRange,
        double? baselineSpeed,
        List<string> detectedWindows,
        List<HourlyNetworkSummary> hourly,
        List<DiagnosticFinding> findings,
        string executiveSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================================================");
        sb.AppendLine("UNIT MANAGER - NETWORK & INTERNET DIAGNOSTIC REPORT");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("========================================================================");
        sb.AppendLine();
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine(executiveSummary);
        sb.AppendLine();
        sb.AppendLine("LIVE DIAGNOSTIC PROBE");
        sb.AppendLine($"  Adapter:          {active.AdapterName} ({active.ConnectionType}, {active.LinkSpeedMbps:0} Mbps link)");
        if (active.WifiSignalPercent.HasValue)
            sb.AppendLine($"  Wi-Fi Signal:     {active.WifiSignalPercent.Value}%");
        sb.AppendLine($"  Download Speed:   {(active.MeasuredDownloadMbps.HasValue ? $"{active.MeasuredDownloadMbps.Value:0.00} Mbps" : "Failed / Offline")}");
        sb.AppendLine($"  Upload Speed:     {(active.MeasuredUploadMbps.HasValue ? $"{active.MeasuredUploadMbps.Value:0.00} Mbps" : "Failed / Offline")}");
        sb.AppendLine($"  Local PC Usage:   Down {active.LocalDownMbps:0.00} Mbps | Up {active.LocalUpMbps:0.00} Mbps");
        sb.AppendLine($"  Router (Hop 1):   {active.GatewayIp} -> Avg {active.GatewayAvgMs?.ToString("0.1") ?? "Timeout"} ms | Jitter {active.GatewayJitterMs?.ToString("0.1") ?? "—"} ms | Loss {active.GatewayLossPercent:0.#}%");
        if (!string.IsNullOrEmpty(active.IspHopIp))
            sb.AppendLine($"  ISP Node (Hop 2): {active.IspHopIp} -> Avg {active.IspHopAvgMs?.ToString("0.1") ?? "No ICMP reply"} ms | Loss {active.IspHopLossPercent:0.#}%");
        sb.AppendLine($"  Internet Ping:    Avg {active.InternetAvgMs?.ToString("0.1") ?? "Timeout"} ms | Jitter {active.InternetJitterMs?.ToString("0.1") ?? "—"} ms | Loss {active.InternetLossPercent:0.#}%");
        sb.AppendLine($"  Loaded Latency:   {active.LoadedLatencyMs?.ToString("0.1") ?? "N/A"} ms (Bufferbloat delta: +{(active.BufferbloatDeltaMs?.ToString("0.1") ?? "N/A")} ms)");
        sb.AppendLine($"  DNS Latency:      System {active.SystemDnsMs?.ToString("0.1") ?? "N/A"} ms | 1.1.1.1 {active.CloudflareDnsMs?.ToString("0.1") ?? "N/A"} ms | 8.8.8.8 {active.GoogleDnsMs?.ToString("0.1") ?? "N/A"} ms");
        sb.AppendLine($"  Top Processes:    {(active.TopProcesses.Count > 0 ? string.Join(", ", active.TopProcesses) : "None detected")}");
        if (!active.HasElevatedTraffic)
            sb.AppendLine("  Note:             Run Unit Manager as administrator for exact per-process Mbps measurement via ETW.");
        sb.AppendLine();

        sb.AppendLine("DIAGNOSTIC FINDINGS & RECOMMENDATIONS");
        foreach (DiagnosticFinding f in findings)
        {
            sb.AppendLine($"  [{f.SeverityDisplay.ToUpperInvariant()}] {f.Category}: {f.Title}");
            sb.AppendLine($"    Evidence:       {f.Evidence}");
            sb.AppendLine($"    Recommendation: {f.Recommendation}");
            sb.AppendLine();
        }

        sb.AppendLine("HISTORICAL TIME-WINDOW ANALYSIS");
        sb.AppendLine($"  Dataset:          {timeRange}");
        sb.AppendLine($"  Baseline Speed:   {(baselineSpeed.HasValue ? $"{baselineSpeed.Value:0.1} Mbps (80th percentile)" : "N/A")}");
        sb.AppendLine($"  Slow Windows:     {(detectedWindows.Count > 0 ? string.Join("; ", detectedWindows) : "None detected")}");
        if (hourly.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Hour Window    | Samples | Avg Mbps | Min Mbps | Local Down/Up | Router ms | Inet ms | Loss % | Flag");
            sb.AppendLine("  ---------------+---------+----------+----------+---------------+-----------+---------+--------+--------");
            foreach (HourlyNetworkSummary h in hourly)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0,-14} | {1,7} | {2,8} | {3,8} | {4,6:0.1} / {5,-4:0.1} | {6,9} | {7,7} | {8,5:0.#}% | {9}",
                    h.HourRangeDisplay,
                    h.SampleCount,
                    h.AvgInternetMbps.HasValue ? $"{h.AvgInternetMbps.Value:0.1}" : "—",
                    h.MinInternetMbps.HasValue ? $"{h.MinInternetMbps.Value:0.1}" : "—",
                    h.AvgLocalDownMbps,
                    h.AvgLocalUpMbps,
                    h.AvgGatewayMs.HasValue ? $"{h.AvgGatewayMs.Value:0.1}" : "—",
                    h.AvgInternetMs.HasValue ? $"{h.AvgInternetMs.Value:0.1}" : "—",
                    h.AvgPacketLossPercent,
                    h.IsSlowWindow ? "SLOW" : "OK"));
            }
        }

        return sb.ToString();
    }

    private (NetworkHealthStatus Status, string Notes) ClassifySampleHealth(
        double? speedMbps,
        double localDownMbps,
        double localUpMbps,
        double? gatewayMs,
        double gatewayLoss,
        double? internetMs,
        double internetLoss,
        double? dnsMs,
        int? wifiSignal,
        long errorDelta)
    {
        if (!speedMbps.HasValue && !internetMs.HasValue)
        {
            return (NetworkHealthStatus.Offline, gatewayMs.HasValue ? "Router OK, Internet unreachable" : "Network disconnected");
        }

        var notes = new List<string>();
        if (gatewayLoss > 0) notes.Add($"Router loss {gatewayLoss:0}%");
        if (internetLoss > 0) notes.Add($"Inet loss {internetLoss:0}%");
        if (gatewayMs is >= 25) notes.Add($"High router ping ({gatewayMs:0} ms)");
        if (internetMs is >= 120) notes.Add($"High Inet ping ({internetMs:0} ms)");
        if (dnsMs is >= 120) notes.Add($"Slow DNS ({dnsMs:0} ms)");
        if (wifiSignal is < 50) notes.Add($"Weak Wi-Fi ({wifiSignal}%)");
        if (localDownMbps >= 15 || localUpMbps >= 4) notes.Add($"High local traffic ({localDownMbps:0.1}/{localUpMbps:0.1} Mbps)");
        if (errorDelta > 0) notes.Add($"{errorDelta} NIC errors");

        if (internetLoss >= 25 || gatewayLoss >= 25)
        {
            return (NetworkHealthStatus.PacketLoss, string.Join("; ", notes));
        }

        if ((gatewayMs is >= 25) || (internetMs is >= 130))
        {
            return (NetworkHealthStatus.HighLatency, string.Join("; ", notes));
        }

        bool sharpSpeedDrop = speedMbps.HasValue && _runningPeakInternetMbps.HasValue
            && _runningPeakInternetMbps.Value >= 8.0
            && speedMbps.Value < _runningPeakInternetMbps.Value * 0.55;
        if (sharpSpeedDrop || (speedMbps.HasValue && speedMbps.Value < 3.0))
        {
            notes.Insert(0, $"Speed drop ({speedMbps:0.1} Mbps)");
            return (NetworkHealthStatus.Degraded, string.Join("; ", notes));
        }

        return (NetworkHealthStatus.Healthy, notes.Count > 0 ? string.Join("; ", notes) : "OK");
    }

    private (double LocalDownMbps, double LocalUpMbps, long ErrorDelta, double ElapsedSeconds) ReadAdapterRateDelta(
        string adapterId,
        long ownProbeDownloadBytes)
    {
        AdapterSnapshot current = GetPrimaryAdapter();
        long nowTicks = Stopwatch.GetTimestamp();

        lock (_counterLock)
        {
            if (_lastAdapterId is null || !string.Equals(_lastAdapterId, current.Id, StringComparison.Ordinal) || _prevTimestampTicks == 0)
            {
                _lastAdapterId = current.Id;
                _prevBytesReceived = current.BytesReceived;
                _prevBytesSent = current.BytesSent;
                _prevInterfaceErrors = current.TotalErrors;
                _prevTimestampTicks = nowTicks;
                return (0, 0, 0, 5.0);
            }

            double elapsed = Math.Max(0.25, (nowTicks - _prevTimestampTicks) / (double)Stopwatch.Frequency);
            long rxDelta = Math.Max(0, current.BytesReceived - _prevBytesReceived);
            long txDelta = Math.Max(0, current.BytesSent - _prevBytesSent);
            long errDelta = Math.Max(0, current.TotalErrors - _prevInterfaceErrors);

            _prevBytesReceived = current.BytesReceived;
            _prevBytesSent = current.BytesSent;
            _prevInterfaceErrors = current.TotalErrors;
            _prevTimestampTicks = nowTicks;

            long adjustedRx = Math.Max(0, rxDelta - ownProbeDownloadBytes);
            double downMbps = adjustedRx * 8.0 / (elapsed * 1_000_000.0);
            double upMbps = txDelta * 8.0 / (elapsed * 1_000_000.0);
            return (downMbps, upMbps, errDelta, elapsed);
        }
    }

    private async Task<(double? SpeedMbps, long ProbeBytes)> MeasureQuickDownloadSpeedAsync(CancellationToken token)
    {
        // Adaptive probe size so slow connections don't stall and fast connections get a clean reading within ~1-2.5s.
        int targetBytes = _lastMeasuredMbps switch
        {
            null => 393_216,       // 384 KB initial probe
            < 5.0 => 131_072,      // 128 KB when very slow
            < 25.0 => 393_216,     // 384 KB normal
            < 100.0 => 786_432,    // 768 KB fast
            _ => 1_310_720         // 1.25 MB very fast
        };

        var uri = new Uri($"{CloudflareDownBase}?bytes={targetBytes}&t={Stopwatch.GetTimestamp()}");
        var primary = await TimedDownloadAsync(uri, TimeSpan.FromMilliseconds(3200), token);
        if (primary.SpeedMbps.HasValue)
        {
            return primary;
        }

        // Fallback if Cloudflare speed endpoint is blocked or unreachable.
        return await TimedDownloadAsync(FallbackDownloadUri, TimeSpan.FromMilliseconds(3200), token);
    }

    private async Task<(double? SpeedMbps, long ProbeBytes)> TimedDownloadAsync(
        Uri uri,
        TimeSpan maxDuration,
        CancellationToken outerToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(maxDuration);

        long totalBytes = 0;
        var sw = new Stopwatch();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (null, 0);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
            byte[] buffer = new byte[32768];
            sw.Start();
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                {
                    break;
                }

                totalBytes += read;
            }

            sw.Stop();
        }
        catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
        {
            sw.Stop();
            // Partial read during slow link still yields an accurate speed reading!
        }
        catch
        {
            return (null, totalBytes);
        }

        if (totalBytes < 8192 || sw.Elapsed.TotalSeconds < 0.005)
        {
            return (null, totalBytes);
        }

        double mbps = totalBytes * 8.0 / (sw.Elapsed.TotalSeconds * 1_000_000.0);
        return (Math.Round(mbps, 2), totalBytes);
    }

    private async Task<(double? SpeedMbps, long ProbeBytes, double? LoadedLatencyMs)> MeasureDeepDownloadWithLoadedLatencyAsync(
        CancellationToken token)
    {
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pingSamples = new List<double>();
        Task loadedPingTask = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!loadCts.IsCancellationRequested)
            {
                try
                {
                    PingReply reply = await ping.SendPingAsync(InternetPingTargets[0], 1200);
                    if (reply.Status == IPStatus.Success)
                    {
                        lock (pingSamples) pingSamples.Add(reply.RoundtripTime);
                    }
                }
                catch
                {
                    // Ignore ping errors during load test.
                }

                try
                {
                    await Task.Delay(250, loadCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        var uri = new Uri($"{CloudflareDownBase}?bytes=6291456&t={Stopwatch.GetTimestamp()}"); // 6 MB or up to 4.5s
        (double? speedMbps, long probeBytes) = await TimedDownloadAsync(uri, TimeSpan.FromMilliseconds(4500), token);
        if (!speedMbps.HasValue)
        {
            (speedMbps, probeBytes) = await TimedDownloadAsync(FallbackDownloadUri, TimeSpan.FromMilliseconds(3500), token);
        }

        loadCts.Cancel();
        await loadedPingTask;

        double? loadedAvg;
        lock (pingSamples)
        {
            loadedAvg = pingSamples.Count > 0 ? pingSamples.Average() : null;
        }

        return (speedMbps, probeBytes, loadedAvg);
    }

    private async Task<(double? SpeedMbps, long ProbeBytes, double? LoadedLatencyMs)> MeasureDeepUploadWithLoadedLatencyAsync(
        CancellationToken token)
    {
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pingSamples = new List<double>();
        Task loadedPingTask = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!loadCts.IsCancellationRequested)
            {
                try
                {
                    PingReply reply = await ping.SendPingAsync(InternetPingTargets[0], 1200);
                    if (reply.Status == IPStatus.Success)
                    {
                        lock (pingSamples) pingSamples.Add(reply.RoundtripTime);
                    }
                }
                catch
                {
                    // Ignore ping errors during upload load.
                }

                try
                {
                    await Task.Delay(250, loadCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        int payloadBytes = 1_572_864; // 1.5 MB upload probe
        byte[] payload = new byte[payloadBytes];
        Random.Shared.NextBytes(payload);

        double? uploadMbps = null;
        long bytesSent = 0;
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        uploadCts.CancelAfter(TimeSpan.FromMilliseconds(5000));

        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var sw = Stopwatch.StartNew();
            using HttpResponseMessage response = await _http.PostAsync(CloudflareUpUri, content, uploadCts.Token);
            sw.Stop();
            if (response.IsSuccessStatusCode && sw.Elapsed.TotalSeconds >= 0.01)
            {
                bytesSent = payloadBytes;
                uploadMbps = Math.Round(payloadBytes * 8.0 / (sw.Elapsed.TotalSeconds * 1_000_000.0), 2);
            }
        }
        catch
        {
            uploadMbps = null;
        }

        loadCts.Cancel();
        await loadedPingTask;

        double? loadedAvg;
        lock (pingSamples)
        {
            loadedAvg = pingSamples.Count > 0 ? pingSamples.Average() : null;
        }

        return (uploadMbps, bytesSent, loadedAvg);
    }

    private static async Task<(double? AvgMs, double LossPct)> PingBurstAsync(
        string host,
        int count,
        int timeoutMs,
        CancellationToken token)
    {
        (double? avg, _, double loss) = await PingDetailedBurstAsync(host, count, timeoutMs, token);
        return (avg, loss);
    }

    private static async Task<(double? AvgMs, double? JitterMs, double LossPct)> PingInternetAsync(
        int count,
        int timeoutMs,
        CancellationToken token)
    {
        var primary = await PingDetailedBurstAsync(InternetPingTargets[0], count, timeoutMs, token);
        if (primary.AvgMs.HasValue)
        {
            return primary;
        }

        return await PingDetailedBurstAsync(InternetPingTargets[1], count, timeoutMs, token);
    }

    private static async Task<(double? AvgMs, double? JitterMs, double LossPct)> PingDetailedBurstAsync(
        string host,
        int count,
        int timeoutMs,
        CancellationToken token)
    {
        var rtts = new List<double>(count);
        int failures = 0;
        byte[] buffer = new byte[32];

        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                PingReply reply = await ping.SendPingAsync(host, timeoutMs, buffer);
                if (reply.Status == IPStatus.Success)
                {
                    rtts.Add(reply.RoundtripTime);
                }
                else
                {
                    failures++;
                }
            }
            catch
            {
                failures++;
            }

            if (i + 1 < count)
            {
                await Task.Delay(80, token);
            }
        }

        double lossPct = count > 0 ? failures * 100.0 / count : 0;
        if (rtts.Count == 0)
        {
            return (null, null, lossPct);
        }

        double avg = rtts.Average();
        double? jitter = null;
        if (rtts.Count >= 2)
        {
            double diffSum = 0;
            for (int i = 1; i < rtts.Count; i++)
            {
                diffSum += Math.Abs(rtts[i] - rtts[i - 1]);
            }

            jitter = diffSum / (rtts.Count - 1);
        }

        return (avg, jitter, lossPct);
    }

    private static async Task<(string HopIp, double? AvgMs, double LossPct)> DiscoverAndPingIspHopAsync(
        string gatewayIp,
        CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            byte[] buffer = new byte[32];
            PingReply hopReply = await ping.SendPingAsync(InternetPingTargets[0], 1500, buffer, new PingOptions(ttl: 2, dontFragment: true));
            if ((hopReply.Status == IPStatus.TtlExpired || hopReply.Status == IPStatus.Success) && hopReply.Address is not null)
            {
                string hopIp = hopReply.Address.ToString();
                if (!string.Equals(hopIp, gatewayIp, StringComparison.Ordinal))
                {
                    (double? avg, double loss) = await PingBurstAsync(hopIp, count: 4, timeoutMs: 1200, token);
                    return (hopIp, avg, loss);
                }
            }
        }
        catch
        {
            // Ignore traceroute failure.
        }

        return (string.Empty, null, 0);
    }

    /// <summary>
    /// Measures true DNS response latency by sending a minimal UDP A-record query directly to the target DNS server,
    /// bypassing the local Windows DNS client cache.
    /// </summary>
    private static async Task<double?> MeasureDnsLatencyAsync(string? dnsServerIp, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(dnsServerIp) || !IPAddress.TryParse(dnsServerIp, out IPAddress? dnsAddress))
        {
            return null;
        }

        try
        {
            ushort txId = (ushort)Random.Shared.Next(1, 65535);
            byte[] query = BuildDnsAQuery("cloudflare.com", txId);

            using var udp = new UdpClient(dnsAddress.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(1500);

            var endpoint = new IPEndPoint(dnsAddress, 53);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, endpoint, cts.Token);
            UdpReceiveResult result = await udp.ReceiveAsync(cts.Token);
            sw.Stop();

            if (result.Buffer.Length >= 2 &&
                result.Buffer[0] == (byte)(txId >> 8) &&
                result.Buffer[1] == (byte)(txId & 0xFF))
            {
                return Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            }
        }
        catch
        {
            // Fall through to null on timeout/unreachable DNS.
        }

        return null;
    }

    private static byte[] BuildDnsAQuery(string domain, ushort transactionId)
    {
        var packet = new List<byte>(64)
        {
            (byte)(transactionId >> 8), (byte)(transactionId & 0xFF), // Transaction ID
            0x01, 0x00, // Standard query, recursion desired
            0x00, 0x01, // QDCOUNT = 1
            0x00, 0x00, // ANCOUNT = 0
            0x00, 0x00, // NSCOUNT = 0
            0x00, 0x00  // ARCOUNT = 0
        };

        foreach (string label in domain.Split('.'))
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)labelBytes.Length);
            packet.AddRange(labelBytes);
        }

        packet.Add(0x00); // Root label
        packet.Add(0x00); packet.Add(0x01); // QTYPE = A
        packet.Add(0x00); packet.Add(0x01); // QCLASS = IN
        return packet.ToArray();
    }

    private (string TopProcess, double? BytesPerSec) GetTopNetworkProcess(double elapsedSeconds)
    {
        if (_etwActive)
        {
            int bestPid = 0;
            long bestBytes = 0;
            foreach (int pid in _etwProcessBytes.Keys.ToArray())
            {
                if (_etwProcessBytes.TryRemove(pid, out long bytes) && bytes > bestBytes)
                {
                    bestBytes = bytes;
                    bestPid = pid;
                }
            }

            if (bestPid > 0 && bestBytes > 0)
            {
                double rate = bestBytes / Math.Max(0.5, elapsedSeconds);
                string name = TryGetProcessName(bestPid);
                return ($"{name} ({ProcessInfoItem.FormatSpeed(rate)})", rate);
            }

            return ("Idle", 0);
        }

        // Non-elevated fallback: top process by active external TCP connections.
        var topByConns = GetTopProcessesByExternalConnections(maxCount: 1);
        return topByConns.Count > 0 ? (topByConns[0], null) : ("—", null);
    }

    private async Task<(List<string> TopProcesses, bool HasElevatedTraffic)> CaptureTopProcessesForDiagnosisAsync(
        CancellationToken token)
    {
        if (_etwActive)
        {
            // Snapshot accumulated bytes over a short window or current live capture.
            var snapshot = _etwProcessBytes
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Where(kv => kv.Value > 0)
                .Select(kv => $"{TryGetProcessName(kv.Key)} ({FileSystemItem.FormatBytes(kv.Value)})")
                .ToList();
            if (snapshot.Count > 0)
            {
                return (snapshot, true);
            }
        }

        if (IsElevated && !_etwActive)
        {
            // Quick 2-second ETW burst if monitoring wasn't already capturing ETW.
            var counts = new Dictionary<int, long>();
            try
            {
                using var session = new TraceEventSession($"UnitManager-NetDiag-{Guid.NewGuid():N}");
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;
                void Add(int pid, int size)
                {
                    if (pid <= 0 || size <= 0 || pid == _ownPid) return;
                    counts[pid] = counts.GetValueOrDefault(pid) + size;
                }
                kernel.TcpIpSend += e => Add(e.ProcessID, e.size);
                kernel.TcpIpRecv += e => Add(e.ProcessID, e.size);
                kernel.UdpIpSend += e => Add(e.ProcessID, e.size);
                kernel.UdpIpRecv += e => Add(e.ProcessID, e.size);

                Task consumer = Task.Factory.StartNew(() => session.Source.Process(), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                try
                {
                    await Task.Delay(2000, token);
                }
                finally
                {
                    session.Stop(noThrow: true);
                    await consumer;
                }

                var results = counts
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .Select(kv => $"{TryGetProcessName(kv.Key)} ({ProcessInfoItem.FormatSpeed(kv.Value / 2.0)})")
                    .ToList();
                return (results, true);
            }
            catch
            {
                // Fall back to connection count below.
            }
        }

        return (GetTopProcessesByExternalConnections(maxCount: 5), false);
    }

    private List<string> GetTopProcessesByExternalConnections(int maxCount)
    {
        try
        {
            var countsByPid = new Dictionary<int, int>();
            int bufferSize = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            if (bufferSize <= 0)
            {
                return [];
            }

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buffer, ref bufferSize, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                {
                    return [];
                }

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    int pid = (int)row.dwOwningPid;
                    if (pid <= 4 || pid == _ownPid || row.dwState != 5) // 5 = ESTABLISHED
                    {
                        continue;
                    }

                    var remote = new IPAddress(row.dwRemoteAddr);
                    if (IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    countsByPid[pid] = countsByPid.GetValueOrDefault(pid) + 1;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return countsByPid
                .OrderByDescending(kv => kv.Value)
                .Take(maxCount)
                .Select(kv => $"{TryGetProcessName(kv.Key)} ({kv.Value} conns)")
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string TryGetProcessName(int pid)
    {
        if (pid == 4) return "System";
        try
        {
            using Process proc = Process.GetProcessById(pid);
            return proc.ProcessName + ".exe";
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private static string ExtractProcessName(string topProcessDisplay)
    {
        if (string.IsNullOrWhiteSpace(topProcessDisplay) || topProcessDisplay is "—" or "Idle")
        {
            return string.Empty;
        }

        int parenIdx = topProcessDisplay.IndexOf('(');
        return parenIdx > 0 ? topProcessDisplay[..parenIdx].Trim() : topProcessDisplay.Trim();
    }

    private sealed record AdapterSnapshot(
        string Id,
        string Name,
        string ConnectionType,
        bool IsWifi,
        double LinkSpeedMbps,
        string GatewayIp,
        string? PrimaryDnsIp,
        long BytesReceived,
        long BytesSent,
        long TotalErrors);

    private static AdapterSnapshot GetPrimaryAdapter()
    {
        NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
        NetworkInterface? best = null;
        int bestScore = int.MinValue;
        string bestGateway = string.Empty;
        string? bestDns = null;

        foreach (NetworkInterface nic in adapters)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            string? ipv4Gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                ?.ToString();

            int score = 0;
            if (!string.IsNullOrEmpty(ipv4Gateway)) score += 100;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 20;
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 15;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase))
            {
                score -= 40;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = nic;
                bestGateway = ipv4Gateway ?? props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? string.Empty;
                bestDns = props.DnsAddresses
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                    ?.ToString()
                    ?? props.DnsAddresses.FirstOrDefault()?.ToString();
            }
        }

        if (best is null)
        {
            return new AdapterSnapshot(string.Empty, "No Active Adapter", "Disconnected", false, 0, string.Empty, null, 0, 0, 0);
        }

        long rx = 0, tx = 0, errs = 0;
        try
        {
            IPInterfaceStatistics stats = best.GetIPStatistics();
            rx = stats.BytesReceived;
            tx = stats.BytesSent;
            errs = stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors + stats.IncomingPacketsDiscarded;
        }
        catch
        {
            // Ignore unreadable interface counters.
        }

        bool isWifi = best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        string connType = isWifi
            ? "Wi-Fi"
            : best.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                ? "Ethernet"
                : best.NetworkInterfaceType.ToString();

        double linkMbps = best.Speed > 0 ? best.Speed / 1_000_000.0 : 0;
        return new AdapterSnapshot(best.Id, best.Name, connType, isWifi, linkMbps, bestGateway, bestDns, rx, tx, errs);
    }

    private static int? TryGetWifiSignalPercent()
    {
        IntPtr clientHandle = IntPtr.Zero;
        IntPtr listPtr = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out clientHandle) != 0)
            {
                return null;
            }

            if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero)
            {
                return null;
            }

            int count = Marshal.ReadInt32(listPtr);
            IntPtr itemPtr = IntPtr.Add(listPtr, 8); // dwNumberOfItems (4) + dwIndex (4)
            int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPtr);
                itemPtr = IntPtr.Add(itemPtr, itemSize);

                if (info.isState != 1) // 1 = wlan_interface_state_connected
                {
                    continue;
                }

                IntPtr dataPtr = IntPtr.Zero;
                try
                {
                    int result = WlanQueryInterface(
                        clientHandle,
                        ref info.InterfaceGuid,
                        7, // wlan_intf_opcode_current_connection
                        IntPtr.Zero,
                        out _,
                        out dataPtr,
                        IntPtr.Zero);

                    if (result == 0 && dataPtr != IntPtr.Zero)
                    {
                        var attrs = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                        return (int)Math.Clamp(attrs.wlanAssociationAttributes.wlanSignalQuality, 0u, 100u);
                    }
                }
                finally
                {
                    if (dataPtr != IntPtr.Zero)
                    {
                        WlanFreeMemory(dataPtr);
                    }
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (listPtr != IntPtr.Zero) WlanFreeMemory(listPtr);
            if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcessTrafficCapture();
        _http.Dispose();
    }

    #region Native WLAN API

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        int OpCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    #endregion
}
