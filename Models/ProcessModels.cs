using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace FileViewer.Models;

public enum ProcessStatusKind
{
    Running,
    NotResponding,
    Suspended,
    SystemKernel
}

public enum ProcessQuickFilter
{
    All,
    WithWindow,
    HighCpu,
    HighMemory,
    WithNetwork,
    Elevated,
    SuspendedOrNotResponding
}

/// <summary>
/// Snapshot of a running Windows process with CPU, memory, I/O, security, and metadata details.
/// </summary>
public sealed class ProcessInfoItem
{
    public int Pid { get; init; }

    public int ParentPid { get; init; }

    public required string Name { get; init; }

    public string TreeDisplayName { get; set; } = string.Empty;

    public int TreeDepth { get; set; }

    public Image? IconImage { get; set; }

    public ProcessStatusKind StatusKind { get; set; }

    public string StatusDisplay => StatusKind switch
    {
        ProcessStatusKind.SystemKernel => "Kernel / System",
        ProcessStatusKind.Suspended => "Suspended",
        ProcessStatusKind.NotResponding => "Not Responding",
        _ => "Running"
    };

    public bool IsResponding { get; set; } = true;

    public bool IsSuspended { get; set; }

    public bool HasMainWindow { get; set; }

    public double CpuPercent { get; set; }

    public string CpuDisplay => $"{CpuPercent:0.0} %";

    public long WorkingSetBytes { get; set; }

    public string WorkingSetDisplay => FileSystemItem.FormatBytes(WorkingSetBytes);

    public long PrivateBytes { get; set; }

    public string PrivateBytesDisplay => FileSystemItem.FormatBytes(PrivateBytes);

    public long VirtualBytes { get; set; }

    public string VirtualBytesDisplay => FileSystemItem.FormatBytes(VirtualBytes);

    public long PeakWorkingSetBytes { get; set; }

    public long PagedPoolBytes { get; set; }

    public long NonPagedPoolBytes { get; set; }

    public long PagefileBytes { get; set; }

    public uint PageFaultCount { get; set; }

    public double IoReadBytesPerSec { get; set; }

    public double IoWriteBytesPerSec { get; set; }

    public string IoReadSpeedDisplay => FormatSpeed(IoReadBytesPerSec);

    public string IoWriteSpeedDisplay => FormatSpeed(IoWriteBytesPerSec);

    public long IoTotalReadBytes { get; set; }

    public long IoTotalWriteBytes { get; set; }

    public long IoTotalOtherBytes { get; set; }

    public long IoReadOps { get; set; }

    public long IoWriteOps { get; set; }

    public long IoOtherOps { get; set; }

    public ProcessPriorityClass? PriorityClass { get; set; }

    public string PriorityDisplay => PriorityClass switch
    {
        ProcessPriorityClass.RealTime => "Real Time",
        ProcessPriorityClass.High => "High",
        ProcessPriorityClass.AboveNormal => "Above Normal",
        ProcessPriorityClass.Normal => "Normal",
        ProcessPriorityClass.BelowNormal => "Below Normal",
        ProcessPriorityClass.Idle => "Low",
        _ => "-"
    };

    public int BasePriority { get; set; }

    public int ThreadCount { get; set; }

    public int HandleCount { get; set; }

    public int GdiObjects { get; set; }

    public int UserObjects { get; set; }

    public long AffinityMask { get; set; }

    public uint SessionId { get; set; }

    public TimeSpan TotalProcessorTime { get; set; }

    public TimeSpan UserProcessorTime { get; set; }

    public TimeSpan KernelProcessorTime { get; set; }

    public string TotalCpuTimeDisplay => FormatDuration(TotalProcessorTime);

    public DateTime? StartTime { get; set; }

    public long CreateTimeRaw { get; init; }

    public string StartTimeDisplay => StartTime.HasValue
        ? StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public string UptimeDisplay => StartTime.HasValue
        ? FormatDuration(DateTime.Now - StartTime.Value)
        : "-";

    public string UserName { get; set; } = "-";

    public string IntegrityLevel { get; set; } = "-";

    public bool IsElevated { get; set; }

    public string ElevatedDisplay => IsElevated ? "Yes" : "No";

    public string Architecture { get; set; } = "-";

    public string CompanyName { get; set; } = string.Empty;

    public string FileDescription { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string FileVersion { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string CommandLine { get; set; } = string.Empty;

    public string MainWindowTitle { get; set; } = string.Empty;

    public int ActiveConnectionsCount { get; set; }

    public int HostedServicesCount { get; set; }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1)
        {
            return "0 B/s";
        }

        return $"{FileSystemItem.FormatBytes((long)bytesPerSec)}/s";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "00:00:00";
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

public sealed class ProcessThreadInfoItem
{
    public int ThreadId { get; init; }

    public int Priority { get; init; }

    public int BasePriority { get; init; }

    public string State { get; init; } = string.Empty;

    public string WaitReason { get; init; } = string.Empty;

    public uint ContextSwitches { get; init; }

    public string StartAddressHex { get; init; } = string.Empty;

    public TimeSpan TotalCpuTime { get; init; }

    public string TotalCpuTimeDisplay => ProcessInfoItem.FormatDuration(TotalCpuTime);

    public TimeSpan KernelTime { get; init; }

    public TimeSpan UserTime { get; init; }

    public DateTime? StartTime { get; init; }

    public string StartTimeDisplay => StartTime.HasValue
        ? StartTime.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
        : "-";
}

public sealed class ProcessModuleInfoItem
{
    public required string ModuleName { get; init; }

    public string BaseAddressHex { get; init; } = string.Empty;

    public long ModuleMemorySize { get; init; }

    public string SizeDisplay => FileSystemItem.FormatBytes(ModuleMemorySize);

    public string FileVersion { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
}

public sealed class NetworkConnectionItem
{
    public int Pid { get; init; }

    public string ProcessName { get; set; } = string.Empty;

    public required string Protocol { get; init; }

    public required string LocalAddress { get; init; }

    public int LocalPort { get; init; }

    public required string RemoteAddress { get; init; }

    public int RemotePort { get; init; }

    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";

    public string RemoteEndpoint => RemotePort > 0 ? $"{RemoteAddress}:{RemotePort}" : RemoteAddress;

    public required string State { get; init; }
}

public sealed class ProcessEnvironmentVariableItem
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

public sealed class ProcessServiceItem
{
    public required string ServiceName { get; init; }

    public required string DisplayName { get; init; }

    public required string Status { get; init; }

    public required string ServiceType { get; init; }
}

public sealed class ProcessWindowItem
{
    public required string HandleHex { get; init; }

    public required string Title { get; init; }

    public required string ClassName { get; init; }

    public bool IsVisible { get; init; }

    public string VisibleDisplay => IsVisible ? "Yes" : "Hidden";
}

public sealed class ProcessHandleInfoItem
{
    public required string HandleValueHex { get; init; }

    public required string ObjectType { get; init; }

    public required string ObjectName { get; init; }

    public required string GrantedAccessHex { get; init; }
}

public sealed class SystemPerformanceSnapshot
{
    public double TotalCpuPercent { get; init; }

    public long TotalPhysicalMemoryBytes { get; init; }

    public long AvailablePhysicalMemoryBytes { get; init; }

    public long UsedPhysicalMemoryBytes => Math.Max(0, TotalPhysicalMemoryBytes - AvailablePhysicalMemoryBytes);

    public double MemoryUsagePercent => TotalPhysicalMemoryBytes > 0
        ? UsedPhysicalMemoryBytes * 100.0 / TotalPhysicalMemoryBytes
        : 0;

    public long CommitTotalBytes { get; init; }

    public long CommitLimitBytes { get; init; }

    public int TotalProcesses { get; init; }

    public int TotalThreads { get; init; }

    public int TotalHandles { get; init; }

    public int TotalTcpConnections { get; init; }

    public int TotalUdpEndpoints { get; init; }

    public double TotalDiskReadBytesPerSec { get; init; }

    public double TotalDiskWriteBytesPerSec { get; init; }

    public TimeSpan SystemUptime { get; init; }

    public int ProcessorCount { get; init; }
}

public sealed class ProcessDeepDetails
{
    public List<ProcessThreadInfoItem> Threads { get; init; } = [];

    public List<ProcessModuleInfoItem> Modules { get; init; } = [];

    public List<ProcessServiceItem> Services { get; init; } = [];

    public List<ProcessWindowItem> Windows { get; init; } = [];

    public string ModulesNote { get; init; } = string.Empty;
}
