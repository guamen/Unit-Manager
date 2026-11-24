using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FileViewer.Models;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FileViewer.Services;

internal sealed record DiagnosticProcess(
    ProcessInfoItem Process, string Services, double? Cpu, long? DiskRead, long? DiskWrite,
    long? Received, long? Sent, long? Internet, double Seconds)
{
    public double? DiskRate => (DiskRead + DiskWrite) / Seconds;
    public double? NetworkRate => (Received + Sent) / Seconds;
    public double? InternetRate => Internet / Seconds;
}

internal sealed record PcDiagnosticResult(
    SystemPerformanceSnapshot System, DiagnosticProcess[] Processes, string TraceStatus, bool HasTrace);

internal static class PcDiagnosticService
{
    private static readonly TimeSpan SampleDuration = TimeSpan.FromSeconds(5);

    private sealed class Traffic
    {
        public long Read, Write, Received, Sent, Internet;
    }

    public static Task<PcDiagnosticResult> AnalyzeAsync(CancellationToken token) =>
        Task.Run(async () =>
        {
            using var monitor = new ProcessMonitorService();
            var before = monitor.GetSnapshot(includeDetails: false);
            var (traffic, seconds, status) = await MeasureTrafficAsync(token);
            token.ThrowIfCancellationRequested();
            var after = monitor.GetSnapshot(includeDetails: false);
            var identities = before.Processes.ToDictionary(p => p.Pid, p => p.CreateTimeRaw);
            var processes = after.Processes.Where(p => p.Pid > 0).Select(p =>
            {
                bool sameProcess = identities.TryGetValue(p.Pid, out long start) && start == p.CreateTimeRaw;
                bool measured = traffic is not null && sameProcess;
                Traffic? counters = measured ? traffic!.GetValueOrDefault(p.Pid) : null;
                string services = after.ServicesByPid.TryGetValue(p.Pid, out var hosted)
                    ? string.Join(", ", hosted.Select(s => s.DisplayName).Order())
                    : string.Empty;
                return new DiagnosticProcess(p, services, sameProcess ? p.CpuPercent : null,
                    measured ? counters?.Read ?? 0 : null, measured ? counters?.Write ?? 0 : null,
                    measured ? counters?.Received ?? 0 : null, measured ? counters?.Sent ?? 0 : null,
                    measured ? counters?.Internet ?? 0 : null, seconds);
            }).ToArray();
            return new PcDiagnosticResult(after.SystemStats, processes, status, traffic is not null);
        }, token);

    private static async Task<(Dictionary<int, Traffic>? Traffic, double Seconds, string Status)> MeasureTrafficAsync(
        CancellationToken token)
    {
        string fallback = "Open Unit Manager as administrator to measure disk, network, and internet usage by process.";
        if (ProcessMonitorService.IsCurrentProcessElevated())
        {
            try
            {
                return await CaptureEtwAsync(token);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or COMException
                or InvalidOperationException or NetworkInformationException or NotSupportedException)
            {
                fallback = $"Windows could not measure disk and network usage through ETW. {ex.Message}";
            }
        }

        await Task.Delay(SampleDuration, token);
        return (null, SampleDuration.TotalSeconds, fallback);
    }

    private static async Task<(Dictionary<int, Traffic>, double, string)> CaptureEtwAsync(CancellationToken token)
    {
        var counters = new Dictionary<int, Traffic>();
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => NormalizeAddress(entry.Address)).ToHashSet();
        using var session = new TraceEventSession($"FileViewer-Diagnostic-{Guid.NewGuid():N}");
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO |
            KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Thread |
            KernelTraceEventParser.Keywords.NetworkTCPIP);
        var source = session.Source;
        var kernel = source.Kernel;

        Traffic ForProcess(int pid)
        {
            if (!counters.TryGetValue(pid, out Traffic? traffic))
                counters.Add(pid, traffic = new Traffic());
            return traffic;
        }

        void Disk(int pid, int size, bool write)
        {
            if (pid <= 0 || size <= 0)
                return;

            Traffic traffic = ForProcess(pid);
            if (write)
                traffic.Write += size;
            else
                traffic.Read += size;
        }

        void Network(int pid, int size, IPAddress destination, IPAddress origin, bool send)
        {
            if (pid <= 0 || size <= 0)
                return;

            destination = NormalizeAddress(destination);
            origin = NormalizeAddress(origin);
            // For receive events, the local adapter address disambiguates the endpoint order.
            IPAddress remote = !send && localAddresses.Contains(destination) ? origin : destination;
            Traffic traffic = ForProcess(pid);
            if (send)
                traffic.Sent += size;
            else
                traffic.Received += size;
            if (!localAddresses.Contains(remote) && IsPublicAddress(remote))
                traffic.Internet += size;
        }

        kernel.DiskIORead += e => Disk(e.ProcessID, e.TransferSize, write: false);
        kernel.DiskIOWrite += e => Disk(e.ProcessID, e.TransferSize, write: true);
        kernel.TcpIpSend += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: true);
        kernel.TcpIpRecv += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: false);
        kernel.TcpIpSendIPV6 += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: true);
        kernel.TcpIpRecvIPV6 += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: false);
        kernel.UdpIpSend += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: true);
        kernel.UdpIpRecv += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: false);
        kernel.UdpIpSendIPV6 += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: true);
        kernel.UdpIpRecvIPV6 += e => Network(e.ProcessID, e.size, e.daddr, e.saddr, send: false);

        var clock = Stopwatch.StartNew();
        Task consumer = Task.Factory.StartNew(() => source.Process(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        int lost;
        try
        {
            Task delay = Task.Delay(SampleDuration, token);
            if (await Task.WhenAny(consumer, delay) == consumer)
            {
                await consumer;
                throw new InvalidOperationException("The trace ended before the sample was complete.");
            }
            await delay;
            lost = session.EventsLost;
        }
        finally
        {
            clock.Stop();
            if (!session.Stop(noThrow: true))
                source.StopProcessing();
            await consumer;
        }

        string status = $"Sample duration: {clock.Elapsed.TotalSeconds:0.0} s. Internet usage is estimated from traffic with public IP addresses.";
        if (lost > 0)
            status += $" Windows reported {lost:N0} lost events. Rates may be underestimated.";
        return (counters, clock.Elapsed.TotalSeconds, status);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int length);
        if (length == 16)
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 1 && bytes[2] == 0x0d && bytes[3] == 0xb8);

        byte first = bytes[0], second = bytes[1], third = bytes[2];
        return first is > 0 and < 224 and not (10 or 127)
            && !(first == 100 && second is >= 64 and <= 127)
            && !(first == 169 && second == 254)
            && !(first == 172 && second is >= 16 and <= 31)
            && !(first == 192 && (second == 168 || second == 0 && third is 0 or 2))
            && !(first == 198 && (second is 18 or 19 || second == 51 && third == 100))
            && !(first == 203 && second == 0 && third == 113);
    }
}
