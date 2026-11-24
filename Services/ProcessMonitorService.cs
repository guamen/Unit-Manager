using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FileViewer.Models;

namespace FileViewer.Services;

public sealed record ProcessSnapshotResult(
    SystemPerformanceSnapshot SystemStats,
    List<ProcessInfoItem> Processes,
    List<NetworkConnectionItem> NetworkConnections)
{
    internal IReadOnlyDictionary<int, List<ProcessServiceItem>> ServicesByPid { get; init; } =
        new Dictionary<int, List<ProcessServiceItem>>();
}

/// <summary>
/// High-performance native Win32 process, thread, module, network, handle, and service monitor.
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private readonly record struct ProcessCacheKey(int Pid, long CreateTimeRaw);

    private sealed class ProcessStaticMetadata
    {
        public string ExecutablePath { get; init; } = string.Empty;
        public string CommandLine { get; init; } = string.Empty;
        public string UserName { get; init; } = "-";
        public string IntegrityLevel { get; init; } = "-";
        public bool IsElevated { get; init; }
        public string Architecture { get; init; } = "-";
        public string CompanyName { get; init; } = string.Empty;
        public string FileDescription { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string FileVersion { get; init; } = string.Empty;
        public Image? IconImage { get; init; }
    }

    private sealed class ProcessPrevCounters
    {
        public long TotalCpuTicks { get; init; }
        public long ReadBytes { get; init; }
        public long WriteBytes { get; init; }
    }

    private readonly object _syncRoot = new();
    private readonly Dictionary<ProcessCacheKey, ProcessStaticMetadata> _metadataCache = new();
    private readonly Dictionary<ProcessCacheKey, ProcessPrevCounters> _prevCounters = new();
    private readonly Dictionary<string, (string Company, string Description, string Product, string Version)> _versionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Image _defaultProcessIcon;
    private readonly Image _systemProcessIcon;

    private long _prevSampleTimestampTicks;

    public ProcessMonitorService()
    {
        _defaultProcessIcon = CreateBadgeIcon(Color.FromArgb(66, 133, 244), "⚙");
        _systemProcessIcon = CreateBadgeIcon(Color.FromArgb(95, 99, 104), "🛡");
    }

    public static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public Task<ProcessSnapshotResult> GetSnapshotAsync()
    {
        return Task.Run(GetSnapshot);
    }

    public ProcessSnapshotResult GetSnapshot() => GetSnapshot(includeDetails: true);

    internal ProcessSnapshotResult GetSnapshot(bool includeDetails)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = 0;

        lock (_syncRoot)
        {
            if (_prevSampleTimestampTicks > 0)
            {
                elapsedSeconds = (nowTicks - _prevSampleTimestampTicks) / (double)Stopwatch.Frequency;
            }
            _prevSampleTimestampTicks = nowTicks;
        }

        int processorCount = Math.Max(1, Environment.ProcessorCount);

        // 1. Collect Win32 SystemProcessInformation (single syscall for all processes & threads)
        List<(NativeMethods.SYSTEM_PROCESS_INFORMATION Info, string Name, List<NativeMethods.SYSTEM_THREAD_INFORMATION> Threads)> rawProcesses =
            QuerySystemProcesses();

        // 2. Collect managed Process window info & priority (quick lookup dictionary)
        Dictionary<int, (IntPtr MainHwnd, string WindowTitle, bool Responding, ProcessPriorityClass? Priority, long Affinity)> managedInfo =
            CollectManagedProcessInfo();

        // 3. Collect network connections & hosted services counts
        List<NetworkConnectionItem> networkConnections = includeDetails ? GetAllNetworkConnections() : [];
        Dictionary<int, int> connectionsByPid = new();
        int tcpCount = 0;
        int udpCount = 0;
        foreach (NetworkConnectionItem conn in networkConnections)
        {
            connectionsByPid[conn.Pid] = connectionsByPid.GetValueOrDefault(conn.Pid) + 1;
            if (conn.Protocol.StartsWith("TCP", StringComparison.Ordinal))
            {
                tcpCount++;
            }
            else
            {
                udpCount++;
            }
        }

        Dictionary<int, List<ProcessServiceItem>> servicesByPid = GetAllServicesByPid();

        // Map PID -> Name for network connections
        Dictionary<int, string> pidNames = new(rawProcesses.Count);
        foreach (var entry in rawProcesses)
        {
            int pid = entry.Info.UniqueProcessId.ToInt32();
            pidNames[pid] = entry.Name;
        }
        foreach (NetworkConnectionItem conn in networkConnections)
        {
            if (pidNames.TryGetValue(conn.Pid, out string? procName))
            {
                conn.ProcessName = procName;
            }
            else if (conn.Pid == 0)
            {
                conn.ProcessName = "System Idle Process";
            }
        }

        var resultItems = new List<ProcessInfoItem>(rawProcesses.Count);
        var activeKeys = new HashSet<ProcessCacheKey>();
        double totalCpuPercent = 0;
        double totalDiskReadPerSec = 0;
        double totalDiskWritePerSec = 0;
        int totalThreads = 0;
        int totalHandles = 0;

        foreach (var entry in rawProcesses)
        {
            NativeMethods.SYSTEM_PROCESS_INFORMATION info = entry.Info;
            int pid = info.UniqueProcessId.ToInt32();
            int ppid = info.InheritedFromUniqueProcessId.ToInt32();
            var key = new ProcessCacheKey(pid, info.CreateTime);
            activeKeys.Add(key);

            ProcessStaticMetadata? metadata = includeDetails ? GetOrCreateMetadata(key, pid, entry.Name) : null;

            long kernelTicks = info.KernelTime;
            long userTicks = info.UserTime;
            long totalCpuTicks = kernelTicks + userTicks;

            double cpuPercent = 0;
            double ioReadPerSec = 0;
            double ioWritePerSec = 0;

            lock (_syncRoot)
            {
                if (elapsedSeconds > 0.05 && _prevCounters.TryGetValue(key, out ProcessPrevCounters? prev))
                {
                    long deltaCpuTicks = Math.Max(0, totalCpuTicks - prev.TotalCpuTicks);
                    double elapsed100Ns = elapsedSeconds * 10_000_000.0;
                    cpuPercent = Math.Clamp((deltaCpuTicks / (elapsed100Ns * processorCount)) * 100.0, 0, 100);

                    long deltaRead = Math.Max(0, info.ReadTransferCount - prev.ReadBytes);
                    long deltaWrite = Math.Max(0, info.WriteTransferCount - prev.WriteBytes);
                    ioReadPerSec = deltaRead / elapsedSeconds;
                    ioWritePerSec = deltaWrite / elapsedSeconds;
                }

                _prevCounters[key] = new ProcessPrevCounters
                {
                    TotalCpuTicks = totalCpuTicks,
                    ReadBytes = info.ReadTransferCount,
                    WriteBytes = info.WriteTransferCount
                };
            }

            if (pid != 0)
            {
                totalCpuPercent += cpuPercent;
                totalDiskReadPerSec += ioReadPerSec;
                totalDiskWritePerSec += ioWritePerSec;
            }

            totalThreads += (int)info.NumberOfThreads;
            totalHandles += (int)info.HandleCount;

            bool isSuspended = entry.Threads.Count > 0 &&
                entry.Threads.All(t => t.ThreadState == 5 && (t.WaitReason == 5 || t.WaitReason == 12));

            IntPtr mainHwnd = IntPtr.Zero;
            string mainWindowTitle = string.Empty;
            bool isResponding = true;
            ProcessPriorityClass? priorityClass = null;
            long affinityMask = 0;

            if (managedInfo.TryGetValue(pid, out var mInfo))
            {
                mainHwnd = mInfo.MainHwnd;
                mainWindowTitle = mInfo.WindowTitle;
                isResponding = mInfo.Responding;
                priorityClass = mInfo.Priority;
                affinityMask = mInfo.Affinity;
            }

            // Query GDI / USER objects quickly
            int gdiObjects = 0;
            int userObjects = 0;
            if (includeDetails && pid > 4)
            {
                IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProc != IntPtr.Zero)
                {
                    try
                    {
                        gdiObjects = (int)NativeMethods.GetGuiResources(hProc, NativeMethods.GR_GDIOBJECTS);
                        userObjects = (int)NativeMethods.GetGuiResources(hProc, NativeMethods.GR_USEROBJECTS);
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(hProc);
                    }
                }
            }

            ProcessStatusKind statusKind = pid <= 4
                ? ProcessStatusKind.SystemKernel
                : isSuspended
                    ? ProcessStatusKind.Suspended
                    : !isResponding
                        ? ProcessStatusKind.NotResponding
                        : ProcessStatusKind.Running;

            DateTime? startTime = null;
            if (info.CreateTime > 0)
            {
                try
                {
                    startTime = DateTime.FromFileTime(info.CreateTime);
                }
                catch (ArgumentOutOfRangeException)
                {
                    startTime = null;
                }
            }

            var item = new ProcessInfoItem
            {
                Pid = pid,
                ParentPid = ppid,
                Name = entry.Name,
                TreeDisplayName = entry.Name,
                IconImage = includeDetails ? metadata?.IconImage ?? (pid <= 4 ? _systemProcessIcon : _defaultProcessIcon) : null,
                StatusKind = statusKind,
                IsResponding = isResponding,
                IsSuspended = isSuspended,
                HasMainWindow = mainHwnd != IntPtr.Zero,
                CpuPercent = cpuPercent,
                WorkingSetBytes = (long)info.WorkingSetSize.ToUInt64(),
                PrivateBytes = (long)info.PrivatePageCount.ToUInt64(),
                VirtualBytes = (long)info.VirtualSize.ToUInt64(),
                PeakWorkingSetBytes = (long)info.PeakWorkingSetSize.ToUInt64(),
                PagedPoolBytes = (long)info.QuotaPagedPoolUsage.ToUInt64(),
                NonPagedPoolBytes = (long)info.QuotaNonPagedPoolUsage.ToUInt64(),
                PagefileBytes = (long)info.PagefileUsage.ToUInt64(),
                PageFaultCount = info.PageFaultCount,
                IoReadBytesPerSec = ioReadPerSec,
                IoWriteBytesPerSec = ioWritePerSec,
                IoTotalReadBytes = info.ReadTransferCount,
                IoTotalWriteBytes = info.WriteTransferCount,
                IoTotalOtherBytes = info.OtherTransferCount,
                IoReadOps = info.ReadOperationCount,
                IoWriteOps = info.WriteOperationCount,
                IoOtherOps = info.OtherOperationCount,
                PriorityClass = priorityClass,
                BasePriority = info.BasePriority,
                ThreadCount = (int)info.NumberOfThreads,
                HandleCount = (int)info.HandleCount,
                GdiObjects = gdiObjects,
                UserObjects = userObjects,
                AffinityMask = affinityMask,
                SessionId = info.SessionId,
                TotalProcessorTime = TimeSpan.FromTicks(totalCpuTicks),
                UserProcessorTime = TimeSpan.FromTicks(userTicks),
                KernelProcessorTime = TimeSpan.FromTicks(kernelTicks),
                StartTime = startTime,
                CreateTimeRaw = info.CreateTime,
                UserName = metadata?.UserName ?? "-",
                IntegrityLevel = metadata?.IntegrityLevel ?? "-",
                IsElevated = metadata?.IsElevated ?? false,
                Architecture = metadata?.Architecture ?? "-",
                CompanyName = metadata?.CompanyName ?? string.Empty,
                FileDescription = metadata?.FileDescription ?? string.Empty,
                ProductName = metadata?.ProductName ?? string.Empty,
                FileVersion = metadata?.FileVersion ?? string.Empty,
                ExecutablePath = metadata?.ExecutablePath ?? string.Empty,
                CommandLine = metadata?.CommandLine ?? string.Empty,
                MainWindowTitle = mainWindowTitle,
                ActiveConnectionsCount = connectionsByPid.GetValueOrDefault(pid),
                HostedServicesCount = servicesByPid.TryGetValue(pid, out var svcList) ? svcList.Count : 0
            };

            resultItems.Add(item);
        }

        // Prune exited processes from caches
        lock (_syncRoot)
        {
            foreach (ProcessCacheKey oldKey in _prevCounters.Keys.Where(k => !activeKeys.Contains(k)).ToList())
            {
                _prevCounters.Remove(oldKey);
                _metadataCache.Remove(oldKey);
            }
        }

        // System memory & uptime
        var memStatus = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref memStatus);

        long totalPhys = (long)memStatus.ullTotalPhys;
        long availPhys = (long)memStatus.ullAvailPhys;
        long commitLimit = (long)memStatus.ullTotalPageFile;
        long commitTotal = Math.Max(0, commitLimit - (long)memStatus.ullAvailPageFile);

        var sysStats = new SystemPerformanceSnapshot
        {
            TotalCpuPercent = Math.Clamp(totalCpuPercent, 0, 100),
            TotalPhysicalMemoryBytes = totalPhys,
            AvailablePhysicalMemoryBytes = availPhys,
            CommitTotalBytes = commitTotal,
            CommitLimitBytes = commitLimit,
            TotalProcesses = resultItems.Count,
            TotalThreads = totalThreads,
            TotalHandles = totalHandles,
            TotalTcpConnections = tcpCount,
            TotalUdpEndpoints = udpCount,
            TotalDiskReadBytesPerSec = totalDiskReadPerSec,
            TotalDiskWriteBytesPerSec = totalDiskWritePerSec,
            SystemUptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64()),
            ProcessorCount = processorCount
        };

        return new ProcessSnapshotResult(sysStats, resultItems, networkConnections)
        {
            ServicesByPid = servicesByPid
        };
    }

    public static List<ProcessInfoItem> BuildTreeOrder(List<ProcessInfoItem> items)
    {
        var byPid = new Dictionary<int, ProcessInfoItem>(items.Count);
        foreach (ProcessInfoItem item in items)
        {
            byPid[item.Pid] = item;
        }

        var childrenMap = new Dictionary<int, List<ProcessInfoItem>>();
        var roots = new List<ProcessInfoItem>();

        foreach (ProcessInfoItem item in items)
        {
            bool hasValidParent = item.ParentPid != item.Pid
                && byPid.TryGetValue(item.ParentPid, out ProcessInfoItem? parent)
                && (parent.CreateTimeRaw == 0 || item.CreateTimeRaw == 0 || parent.CreateTimeRaw <= item.CreateTimeRaw);

            if (hasValidParent)
            {
                if (!childrenMap.TryGetValue(item.ParentPid, out var children))
                {
                    children = [];
                    childrenMap[item.ParentPid] = children;
                }
                children.Add(item);
            }
            else
            {
                roots.Add(item);
            }
        }

        roots.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        foreach (var list in childrenMap.Values)
        {
            list.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        }

        var ordered = new List<ProcessInfoItem>(items.Count);
        var visited = new HashSet<int>();

        void Traverse(ProcessInfoItem node, int depth, string prefix, bool isLast, bool isRoot)
        {
            if (!visited.Add(node.Pid))
            {
                return;
            }

            node.TreeDepth = depth;
            if (isRoot)
            {
                node.TreeDisplayName = node.Name;
            }
            else
            {
                string branch = isLast ? "└─ " : "├─ ";
                node.TreeDisplayName = prefix + branch + node.Name;
            }

            ordered.Add(node);

            if (!childrenMap.TryGetValue(node.Pid, out var children))
            {
                return;
            }

            string childPrefix = isRoot ? string.Empty : prefix + (isLast ? "   " : "│  ");
            for (int i = 0; i < children.Count; i++)
            {
                Traverse(children[i], depth + 1, childPrefix, i == children.Count - 1, false);
            }
        }

        for (int i = 0; i < roots.Count; i++)
        {
            Traverse(roots[i], 0, string.Empty, i == roots.Count - 1, true);
        }

        // Safety net for any cycles
        foreach (ProcessInfoItem item in items)
        {
            if (!visited.Contains(item.Pid))
            {
                item.TreeDepth = 0;
                item.TreeDisplayName = item.Name;
                ordered.Add(item);
            }
        }

        return ordered;
    }

    public Task<ProcessDeepDetails> GetProcessDetailsAsync(int pid)
    {
        return Task.Run(() =>
        {
            List<ProcessThreadInfoItem> threads = GetThreadsForProcess(pid);
            (List<ProcessModuleInfoItem> modules, string modulesNote) = GetModulesForProcess(pid);
            List<ProcessServiceItem> services = GetAllServicesByPid().TryGetValue(pid, out var svcList) ? svcList : [];
            List<ProcessWindowItem> windows = GetWindowsForProcess(pid);

            return new ProcessDeepDetails
            {
                Threads = threads,
                Modules = modules,
                Services = services,
                Windows = windows,
                ModulesNote = modulesNote
            };
        });
    }

    public Task<(List<ProcessEnvironmentVariableItem> Items, string Note)> GetEnvironmentVariablesAsync(int pid)
    {
        return Task.Run(() => ReadProcessEnvironmentVariables(pid));
    }

    public Task<(List<ProcessHandleInfoItem> Items, string Note)> GetProcessHandlesAsync(int pid)
    {
        return Task.Run(() => QueryProcessHandles(pid));
    }

    public static void KillProcess(int pid, bool entireTree)
    {
        using Process proc = Process.GetProcessById(pid);
        proc.Kill(entireTree);
    }

    public static void SuspendProcess(int pid)
    {
        IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            int status = NativeMethods.NtSuspendProcess(hProcess);
            if (status != NativeMethods.STATUS_SUCCESS)
            {
                throw new InvalidOperationException($"NtSuspendProcess failed with NTSTATUS 0x{status:X8}.");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    public static void ResumeProcess(int pid)
    {
        IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            int status = NativeMethods.NtResumeProcess(hProcess);
            if (status != NativeMethods.STATUS_SUCCESS)
            {
                throw new InvalidOperationException($"NtResumeProcess failed with NTSTATUS 0x{status:X8}.");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    public static void SetProcessPriority(int pid, ProcessPriorityClass priority)
    {
        using Process proc = Process.GetProcessById(pid);
        proc.PriorityClass = priority;
    }

    public static void SetProcessAffinity(int pid, long affinityMask)
    {
        using Process proc = Process.GetProcessById(pid);
        proc.ProcessorAffinity = new IntPtr(affinityMask);
    }

    public static void SuspendThread(uint threadId)
    {
        IntPtr hThread = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, threadId);
        if (hThread == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (NativeMethods.SuspendThread(hThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hThread);
        }
    }

    public static void ResumeThread(uint threadId)
    {
        IntPtr hThread = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, threadId);
        if (hThread == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (NativeMethods.ResumeThread(hThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hThread);
        }
    }

    public static Task CreateMiniDumpAsync(int pid, string outputPath, bool fullMemory)
    {
        return Task.Run(() =>
        {
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
                false,
                pid);

            if (hProcess == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the process to create a memory dump.");
            }

            try
            {
                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                NativeMethods.MINIDUMP_TYPE dumpType = fullMemory
                    ? NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory
                        | NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData
                        | NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo
                    : NativeMethods.MINIDUMP_TYPE.MiniDumpNormal
                        | NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData
                        | NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo;

                bool ok = NativeMethods.MiniDumpWriteDump(
                    hProcess,
                    (uint)pid,
                    fs.SafeFileHandle,
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "MiniDumpWriteDump failed.");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        });
    }

    public static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private ProcessStaticMetadata GetOrCreateMetadata(ProcessCacheKey key, int pid, string processName)
    {
        lock (_syncRoot)
        {
            if (_metadataCache.TryGetValue(key, out ProcessStaticMetadata? cached))
            {
                return cached;
            }
        }

        ProcessStaticMetadata built = BuildStaticMetadata(pid, processName);

        lock (_syncRoot)
        {
            _metadataCache[key] = built;
        }

        return built;
    }

    private ProcessStaticMetadata BuildStaticMetadata(int pid, string processName)
    {
        if (pid == 0)
        {
            return new ProcessStaticMetadata
            {
                UserName = "NT AUTHORITY\\SYSTEM",
                IntegrityLevel = "System",
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                FileDescription = "Percentage of CPU idle time",
                IconImage = _systemProcessIcon
            };
        }

        if (pid == 4)
        {
            return new ProcessStaticMetadata
            {
                UserName = "NT AUTHORITY\\SYSTEM",
                IntegrityLevel = "System",
                IsElevated = true,
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                FileDescription = "NT Kernel & System",
                CompanyName = "Microsoft Corporation",
                IconImage = _systemProcessIcon
            };
        }

        string exePath = string.Empty;
        string cmdLine = string.Empty;
        string userName = "-";
        string integrityLevel = "-";
        bool isElevated = false;
        string architecture = "-";

        IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                exePath = QueryExePath(hProc);
                cmdLine = QueryCommandLine(hProc);
                architecture = QueryArchitecture(hProc);
                (userName, integrityLevel, isElevated) = QueryTokenDetails(hProc);
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        string company = string.Empty;
        string description = string.Empty;
        string product = string.Empty;
        string version = string.Empty;
        Image? iconImage = null;

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            (company, description, product, version) = GetCachedVersionInfo(exePath);
            iconImage = GetCachedIcon(exePath);
        }

        return new ProcessStaticMetadata
        {
            ExecutablePath = exePath,
            CommandLine = cmdLine,
            UserName = userName,
            IntegrityLevel = integrityLevel,
            IsElevated = isElevated,
            Architecture = architecture,
            CompanyName = company,
            FileDescription = description,
            ProductName = product,
            FileVersion = version,
            IconImage = iconImage
        };
    }

    private (string Company, string Description, string Product, string Version) GetCachedVersionInfo(string exePath)
    {
        lock (_syncRoot)
        {
            if (_versionCache.TryGetValue(exePath, out var cached))
            {
                return cached;
            }
        }

        (string Company, string Description, string Product, string Version) info = (string.Empty, string.Empty, string.Empty, string.Empty);
        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exePath);
            info = (
                fvi.CompanyName?.Trim() ?? string.Empty,
                fvi.FileDescription?.Trim() ?? string.Empty,
                fvi.ProductName?.Trim() ?? string.Empty,
                fvi.FileVersion?.Trim() ?? string.Empty);
        }
        catch
        {
            // Ignore unreadable version info
        }

        lock (_syncRoot)
        {
            _versionCache[exePath] = info;
        }

        return info;
    }

    private Image? GetCachedIcon(string exePath)
    {
        lock (_syncRoot)
        {
            if (_iconCache.TryGetValue(exePath, out Image? cached))
            {
                return cached;
            }
        }

        Image? result = null;
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                using Bitmap rawBmp = icon.ToBitmap();
                result = new Bitmap(rawBmp, new Size(16, 16));
            }
        }
        catch
        {
            result = null;
        }

        if (result is not null)
        {
            lock (_syncRoot)
            {
                if (!_iconCache.TryAdd(exePath, result))
                {
                    result.Dispose();
                    result = _iconCache[exePath];
                }
            }
        }

        return result;
    }

    private static string QueryExePath(IntPtr hProc)
    {
        char[] buffer = new char[1024];
        int size = buffer.Length;
        if (NativeMethods.QueryFullProcessImageNameW(hProc, 0, buffer, ref size) && size > 0)
        {
            return new string(buffer, 0, size);
        }

        return string.Empty;
    }

    private static string QueryCommandLine(IntPtr hProc)
    {
        int bufferSize = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int status = NativeMethods.NtQueryInformationProcess(
                hProc,
                NativeMethods.ProcessCommandLineInformation,
                buffer,
                bufferSize,
                out int needed);

            if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH || status == NativeMethods.STATUS_BUFFER_TOO_SMALL)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = Math.Max(needed, 2048);
                buffer = Marshal.AllocHGlobal(bufferSize);
                status = NativeMethods.NtQueryInformationProcess(
                    hProc,
                    NativeMethods.ProcessCommandLineInformation,
                    buffer,
                    bufferSize,
                    out _);
            }

            if (status == NativeMethods.STATUS_SUCCESS)
            {
                var ustr = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buffer);
                return NativeMethods.ReadUnicodeString(ustr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return string.Empty;
    }

    private static string QueryArchitecture(IntPtr hProc)
    {
        try
        {
            if (NativeMethods.IsWow64Process2(hProc, out ushort processMachine, out ushort nativeMachine))
            {
                if (processMachine == 0)
                {
                    return nativeMachine switch
                    {
                        0x8664 => "x64",
                        0xAA64 => "ARM64",
                        0x014C => "x86",
                        _ => $"0x{nativeMachine:X4}"
                    };
                }

                return processMachine switch
                {
                    0x014C => "x86 (WOW64)",
                    0x01C4 => "ARM32 (WOW64)",
                    _ => $"WOW64 (0x{processMachine:X4})"
                };
            }
        }
        catch
        {
            // Fallback
        }

        return Environment.Is64BitOperatingSystem ? "x64" : "x86";
    }

    private static (string UserName, string IntegrityLevel, bool IsElevated) QueryTokenDetails(IntPtr hProc)
    {
        string userName = "-";
        string integrity = "-";
        bool elevated = false;

        if (!NativeMethods.OpenProcessToken(hProc, NativeMethods.TOKEN_QUERY, out IntPtr hToken))
        {
            return (userName, integrity, elevated);
        }

        try
        {
            userName = QueryTokenUser(hToken);
            integrity = QueryTokenIntegrity(hToken);
            elevated = QueryTokenElevation(hToken);
        }
        finally
        {
            NativeMethods.CloseHandle(hToken);
        }

        return (userName, integrity, elevated);
    }

    private static string QueryTokenUser(IntPtr hToken)
    {
        NativeMethods.GetTokenInformation(hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out uint needed);
        if (needed == 0)
        {
            return "-";
        }

        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.GetTokenInformation(hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser, buf, needed, out _))
            {
                return "-";
            }

            var tokenUser = Marshal.PtrToStructure<NativeMethods.TOKEN_USER>(buf);
            uint nameLen = 256;
            uint domainLen = 256;
            char[] name = new char[nameLen];
            char[] domain = new char[domainLen];

            if (NativeMethods.LookupAccountSidW(null, tokenUser.User.Sid, name, ref nameLen, domain, ref domainLen, out _))
            {
                string domStr = new(domain, 0, (int)domainLen);
                string usrStr = new(name, 0, (int)nameLen);
                return string.IsNullOrEmpty(domStr) ? usrStr : $"{domStr}\\{usrStr}";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return "-";
    }

    private static string QueryTokenIntegrity(IntPtr hToken)
    {
        NativeMethods.GetTokenInformation(hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
        if (needed == 0)
        {
            return "-";
        }

        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.GetTokenInformation(hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buf, needed, out _))
            {
                return "-";
            }

            var label = Marshal.PtrToStructure<NativeMethods.TOKEN_MANDATORY_LABEL>(buf);
            IntPtr sid = label.Label.Sid;
            IntPtr countPtr = NativeMethods.GetSidSubAuthorityCount(sid);
            if (countPtr == IntPtr.Zero)
            {
                return "-";
            }

            byte subCount = Marshal.ReadByte(countPtr);
            if (subCount == 0)
            {
                return "-";
            }

            IntPtr ridPtr = NativeMethods.GetSidSubAuthority(sid, (uint)(subCount - 1));
            uint rid = (uint)Marshal.ReadInt32(ridPtr);

            return rid switch
            {
                < 0x1000 => "Untrusted",
                < 0x2000 => "Low",
                < 0x3000 => "Medium",
                < 0x3100 => "Medium+",
                < 0x4000 => "High",
                >= 0x4000 => "System"
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool QueryTokenElevation(IntPtr hToken)
    {
        int size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetTokenInformation(hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenElevation, buf, (uint)size, out _))
            {
                var elev = Marshal.PtrToStructure<NativeMethods.TOKEN_ELEVATION>(buf);
                return elev.TokenIsElevated != 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return false;
    }

    private static Dictionary<int, (IntPtr MainHwnd, string WindowTitle, bool Responding, ProcessPriorityClass? Priority, long Affinity)> CollectManagedProcessInfo()
    {
        var map = new Dictionary<int, (IntPtr, string, bool, ProcessPriorityClass?, long)>();
        Process[] procs = Process.GetProcesses();

        foreach (Process proc in procs)
        {
            try
            {
                IntPtr hwnd = proc.MainWindowHandle;
                string title = hwnd != IntPtr.Zero ? proc.MainWindowTitle : string.Empty;
                bool responding = true;

                if (hwnd != IntPtr.Zero)
                {
                    IntPtr sent = NativeMethods.SendMessageTimeoutW(
                        hwnd,
                        0,
                        UIntPtr.Zero,
                        IntPtr.Zero,
                        NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
                        120,
                        out _);
                    responding = sent != IntPtr.Zero;
                }

                ProcessPriorityClass? priority = null;
                long affinity = 0;
                try
                {
                    priority = proc.PriorityClass;
                }
                catch
                {
                    // Access denied or exited
                }

                try
                {
                    affinity = proc.ProcessorAffinity.ToInt64();
                }
                catch
                {
                    // Access denied or exited
                }

                map[proc.Id] = (hwnd, title, responding, priority, affinity);
            }
            catch
            {
                // Process exited mid-enumeration
            }
            finally
            {
                proc.Dispose();
            }
        }

        return map;
    }

    private static List<(NativeMethods.SYSTEM_PROCESS_INFORMATION Info, string Name, List<NativeMethods.SYSTEM_THREAD_INFORMATION> Threads)> QuerySystemProcesses()
    {
        int bufferSize = 512 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            int status;
            while (true)
            {
                status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessInformation,
                    buffer,
                    bufferSize,
                    out int needed);

                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    bufferSize = Math.Max(needed + 65536, bufferSize * 2);
                    buffer = Marshal.AllocHGlobal(bufferSize);
                    continue;
                }

                break;
            }

            if (status != NativeMethods.STATUS_SUCCESS)
            {
                return [];
            }

            var results = new List<(NativeMethods.SYSTEM_PROCESS_INFORMATION, string, List<NativeMethods.SYSTEM_THREAD_INFORMATION>)>(256);
            int procStructSize = Marshal.SizeOf<NativeMethods.SYSTEM_PROCESS_INFORMATION>();
            int threadStructSize = Marshal.SizeOf<NativeMethods.SYSTEM_THREAD_INFORMATION>();

            IntPtr current = buffer;
            while (true)
            {
                var procInfo = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESS_INFORMATION>(current);
                int pid = procInfo.UniqueProcessId.ToInt32();
                string name = pid == 0
                    ? "System Idle Process"
                    : NativeMethods.ReadUnicodeString(procInfo.ImageName);

                if (string.IsNullOrEmpty(name))
                {
                    name = pid == 4 ? "System" : $"PID {pid}";
                }

                int threadCount = (int)procInfo.NumberOfThreads;
                var threads = new List<NativeMethods.SYSTEM_THREAD_INFORMATION>(threadCount);

                // For PID 0 (Idle), Windows stores SYSTEM_THREAD_INFORMATION right after SYSTEM_PROCESS_INFORMATION too.
                IntPtr threadPtr = IntPtr.Add(current, procStructSize);
                for (int i = 0; i < threadCount; i++)
                {
                    threads.Add(Marshal.PtrToStructure<NativeMethods.SYSTEM_THREAD_INFORMATION>(threadPtr));
                    threadPtr = IntPtr.Add(threadPtr, threadStructSize);
                }

                results.Add((procInfo, name, threads));

                if (procInfo.NextEntryOffset == 0)
                {
                    break;
                }

                current = IntPtr.Add(current, (int)procInfo.NextEntryOffset);
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<ProcessThreadInfoItem> GetThreadsForProcess(int targetPid)
    {
        var allProcs = QuerySystemProcesses();
        var match = allProcs.FirstOrDefault(p => p.Info.UniqueProcessId.ToInt32() == targetPid);
        if (match.Threads is null)
        {
            return [];
        }

        var list = new List<ProcessThreadInfoItem>(match.Threads.Count);
        foreach (NativeMethods.SYSTEM_THREAD_INFORMATION t in match.Threads)
        {
            DateTime? startTime = null;
            if (t.CreateTime > 0)
            {
                try
                {
                    startTime = DateTime.FromFileTime(t.CreateTime);
                }
                catch
                {
                    startTime = null;
                }
            }

            list.Add(new ProcessThreadInfoItem
            {
                ThreadId = t.ClientId.UniqueThread.ToInt32(),
                Priority = t.Priority,
                BasePriority = t.BasePriority,
                State = NativeMethods.DecodeThreadState(t.ThreadState),
                WaitReason = t.ThreadState == 5 ? NativeMethods.DecodeWaitReason(t.WaitReason) : "-",
                ContextSwitches = t.ContextSwitches,
                StartAddressHex = $"0x{t.StartAddress.ToInt64():X}",
                KernelTime = TimeSpan.FromTicks(t.KernelTime),
                UserTime = TimeSpan.FromTicks(t.UserTime),
                TotalCpuTime = TimeSpan.FromTicks(t.KernelTime + t.UserTime),
                StartTime = startTime
            });
        }

        return list;
    }

    private (List<ProcessModuleInfoItem> Modules, string Note) GetModulesForProcess(int pid)
    {
        if (pid <= 4)
        {
            return ([], "System kernel modules are unavailable from user mode.");
        }

        IntPtr hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
            false,
            pid);

        if (hProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            string note = err == 5
                ? "Access denied. Run as administrator to inspect this process's modules."
                : $"Could not open the process (Win32 error: {err}).";
            return ([], note);
        }

        try
        {
            IntPtr[] moduleHandles = new IntPtr[1024];
            if (!NativeMethods.K32EnumProcessModulesEx(
                hProc,
                moduleHandles,
                (uint)(moduleHandles.Length * IntPtr.Size),
                out uint needed,
                NativeMethods.LIST_MODULES_ALL))
            {
                int err = Marshal.GetLastWin32Error();
                return ([], $"Could not enumerate modules (Win32 error: {err}).");
            }

            int count = (int)(needed / (uint)IntPtr.Size);
            if (count > moduleHandles.Length)
            {
                moduleHandles = new IntPtr[count];
                if (!NativeMethods.K32EnumProcessModulesEx(
                    hProc,
                    moduleHandles,
                    (uint)(moduleHandles.Length * IntPtr.Size),
                    out needed,
                    NativeMethods.LIST_MODULES_ALL))
                {
                    return ([], "Could not enumerate the full list of modules.");
                }
                count = (int)(needed / (uint)IntPtr.Size);
            }

            var modules = new List<ProcessModuleInfoItem>(count);
            char[] pathBuffer = new char[1024];

            for (int i = 0; i < count; i++)
            {
                IntPtr hMod = moduleHandles[i];
                uint len = NativeMethods.K32GetModuleFileNameExW(hProc, hMod, pathBuffer, (uint)pathBuffer.Length);
                string fullPath = len > 0 ? new string(pathBuffer, 0, (int)len) : string.Empty;
                string modName = string.IsNullOrEmpty(fullPath) ? $"Module_{i}" : Path.GetFileName(fullPath);

                long modSize = 0;
                IntPtr baseAddr = hMod;
                if (NativeMethods.K32GetModuleInformation(
                    hProc,
                    hMod,
                    out NativeMethods.MODULEINFO modInfo,
                    (uint)Marshal.SizeOf<NativeMethods.MODULEINFO>()))
                {
                    modSize = modInfo.SizeOfImage;
                    baseAddr = modInfo.lpBaseOfDll;
                }

                (string company, string description, _, string version) = !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath)
                    ? GetCachedVersionInfo(fullPath)
                    : (string.Empty, string.Empty, string.Empty, string.Empty);

                modules.Add(new ProcessModuleInfoItem
                {
                    ModuleName = modName,
                    BaseAddressHex = $"0x{baseAddr.ToInt64():X}",
                    ModuleMemorySize = modSize,
                    FileVersion = version,
                    CompanyName = company,
                    Description = description,
                    FileName = fullPath
                });
            }

            return (modules, string.Empty);
        }
        finally
        {
            NativeMethods.CloseHandle(hProc);
        }
    }

    private static (List<ProcessEnvironmentVariableItem> Items, string Note) ReadProcessEnvironmentVariables(int pid)
    {
        if (pid <= 4)
        {
            return ([], "The system process has no user environment block.");
        }

        IntPtr hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ,
            false,
            pid);

        if (hProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            string note = err == 5
                ? "Access denied. Run as administrator to read this process's environment variables."
                : $"Could not open the process (Win32 error: {err}).";
            return ([], note);
        }

        try
        {
            IntPtr wow64Peb = IntPtr.Zero;
            IntPtr wowBuf = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (NativeMethods.NtQueryInformationProcess(
                    hProc,
                    NativeMethods.ProcessWow64Information,
                    wowBuf,
                    IntPtr.Size,
                    out _) == NativeMethods.STATUS_SUCCESS)
                {
                    wow64Peb = Marshal.ReadIntPtr(wowBuf);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(wowBuf);
            }

            IntPtr envAddress;
            if (wow64Peb != IntPtr.Zero)
            {
                // 32-bit WOW64 process
                byte[] ptrBuf32 = new byte[4];
                if (!NativeMethods.ReadProcessMemory(hProc, IntPtr.Add(wow64Peb, 0x10), ptrBuf32, 4, out _))
                {
                    return ([], "Could not read ProcessParameters (WOW64).");
                }

                uint procParams32 = BitConverter.ToUInt32(ptrBuf32, 0);
                if (procParams32 == 0 ||
                    !NativeMethods.ReadProcessMemory(hProc, new IntPtr((long)procParams32 + 0x48), ptrBuf32, 4, out _))
                {
                    return ([], "Could not read the environment block (WOW64).");
                }

                envAddress = new IntPtr(BitConverter.ToUInt32(ptrBuf32, 0));
            }
            else
            {
                int pbiSize = Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>();
                IntPtr pbiBuf = Marshal.AllocHGlobal(pbiSize);
                IntPtr pebAddress;
                try
                {
                    int status = NativeMethods.NtQueryInformationProcess(
                        hProc,
                        NativeMethods.ProcessBasicInformation,
                        pbiBuf,
                        pbiSize,
                        out _);

                    if (status != NativeMethods.STATUS_SUCCESS)
                    {
                        return ([], $"NtQueryInformationProcess failed (0x{status:X8}).");
                    }

                    var pbi = Marshal.PtrToStructure<NativeMethods.PROCESS_BASIC_INFORMATION>(pbiBuf);
                    pebAddress = pbi.PebBaseAddress;
                }
                finally
                {
                    Marshal.FreeHGlobal(pbiBuf);
                }

                if (pebAddress == IntPtr.Zero)
                {
                    return ([], "The PEB address is null.");
                }

                byte[] ptrBuf64 = new byte[8];
                if (!NativeMethods.ReadProcessMemory(hProc, IntPtr.Add(pebAddress, 0x20), ptrBuf64, 8, out _))
                {
                    return ([], "Could not read RTL_USER_PROCESS_PARAMETERS from the PEB.");
                }

                long procParams64 = BitConverter.ToInt64(ptrBuf64, 0);
                if (procParams64 == 0 ||
                    !NativeMethods.ReadProcessMemory(hProc, new IntPtr(procParams64 + 0x80), ptrBuf64, 8, out _))
                {
                    return ([], "Could not read the environment pointer.");
                }

                envAddress = new IntPtr(BitConverter.ToInt64(ptrBuf64, 0));
            }

            if (envAddress == IntPtr.Zero)
            {
                return ([], "The environment block is empty.");
            }

            // Read UTF-16 block in 4 KB chunks up to 256 KB until double-null
            const int chunkSize = 4096;
            const int maxBytes = 256 * 1024;
            var rawBytes = new List<byte>(8192);
            byte[] chunk = new byte[chunkSize];

            for (int offset = 0; offset < maxBytes; offset += chunkSize)
            {
                if (!NativeMethods.ReadProcessMemory(hProc, IntPtr.Add(envAddress, offset), chunk, chunkSize, out IntPtr bytesReadPtr))
                {
                    break;
                }

                int read = bytesReadPtr.ToInt32();
                if (read <= 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    rawBytes.Add(chunk[i]);
                }

                // Check for UTF-16 double-null terminator
                bool foundEnd = false;
                for (int i = Math.Max(0, rawBytes.Count - read - 4); i <= rawBytes.Count - 4; i += 2)
                {
                    if (rawBytes[i] == 0 && rawBytes[i + 1] == 0 && rawBytes[i + 2] == 0 && rawBytes[i + 3] == 0)
                    {
                        rawBytes.RemoveRange(i + 2, rawBytes.Count - (i + 2));
                        foundEnd = true;
                        break;
                    }
                }

                if (foundEnd)
                {
                    break;
                }
            }

            if (rawBytes.Count < 2)
            {
                return ([], "Could not read environment variables.");
            }

            string text = Encoding.Unicode.GetString(rawBytes.ToArray());
            string[] entries = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var vars = new List<ProcessEnvironmentVariableItem>(entries.Length);

            foreach (string entry in entries)
            {
                int eqIdx = entry.IndexOf('=', 1);
                if (eqIdx <= 0)
                {
                    continue;
                }

                vars.Add(new ProcessEnvironmentVariableItem
                {
                    Name = entry[..eqIdx],
                    Value = entry[(eqIdx + 1)..]
                });
            }

            vars.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return (vars, string.Empty);
        }
        finally
        {
            NativeMethods.CloseHandle(hProc);
        }
    }

    private static (List<ProcessHandleInfoItem> Items, string Note) QueryProcessHandles(int targetPid)
    {
        if (targetPid <= 0)
        {
            return ([], "The idle process has no user handles.");
        }

        int bufferSize = 2 * 1024 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            int status;
            while (true)
            {
                status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemExtendedHandleInformation,
                    buffer,
                    bufferSize,
                    out int needed);

                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    bufferSize = Math.Max(needed + 262144, bufferSize * 2);
                    buffer = Marshal.AllocHGlobal(bufferSize);
                    continue;
                }

                break;
            }

            if (status != NativeMethods.STATUS_SUCCESS)
            {
                return ([], $"Could not query the system handle table (0x{status:X8}).");
            }

            long totalCount = Marshal.ReadIntPtr(buffer).ToInt64();
            int headerSize = IntPtr.Size * 2;
            int entrySize = Marshal.SizeOf<NativeMethods.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

            var matching = new List<NativeMethods.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            IntPtr entryPtr = IntPtr.Add(buffer, headerSize);

            for (long i = 0; i < totalCount; i++)
            {
                var entry = Marshal.PtrToStructure<NativeMethods.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);
                if ((long)entry.UniqueProcessId.ToUInt64() == targetPid)
                {
                    matching.Add(entry);
                }
                entryPtr = IntPtr.Add(entryPtr, entrySize);
            }

            IntPtr hTargetProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_DUP_HANDLE, false, targetPid);
            bool canDup = hTargetProc != IntPtr.Zero;
            string note = canDup
                ? string.Empty
                : "PROCESS_DUP_HANDLE access denied. Showing handle IDs and permissions. Run as administrator to see object types and names.";

            const int maxDisplay = 600;
            if (matching.Count > maxDisplay)
            {
                note = $"Showing the first {maxDisplay} of {matching.Count:N0} open handles. " + note;
            }

            var results = new List<ProcessHandleInfoItem>(Math.Min(matching.Count, maxDisplay));
            IntPtr currentProc = NativeMethods.GetCurrentProcess();
            var typeNameCache = new Dictionary<ushort, string>();

            try
            {
                int limit = Math.Min(matching.Count, maxDisplay);
                for (int i = 0; i < limit; i++)
                {
                    var h = matching[i];
                    string objType = $"Type #{h.ObjectTypeIndex}";
                    string objName = string.Empty;

                    if (canDup && NativeMethods.DuplicateHandle(
                        hTargetProc,
                        new IntPtr(unchecked((long)h.HandleValue.ToUInt64())),
                        currentProc,
                        out IntPtr dupHandle,
                        0,
                        false,
                        0))
                    {
                        try
                        {
                            if (!typeNameCache.TryGetValue(h.ObjectTypeIndex, out string? cachedType))
                            {
                                cachedType = QueryObjectTypeName(dupHandle);
                                if (!string.IsNullOrEmpty(cachedType))
                                {
                                    typeNameCache[h.ObjectTypeIndex] = cachedType;
                                }
                            }

                            if (!string.IsNullOrEmpty(cachedType))
                            {
                                objType = cachedType;
                            }

                            // Guard against synchronous pipe deadlock when querying ObjectNameInformation on File handles
                            if (string.Equals(objType, "File", StringComparison.OrdinalIgnoreCase))
                            {
                                if (h.GrantedAccess != 0x0012019f && h.GrantedAccess != 0x001a019f && h.GrantedAccess != 0x00120189)
                                {
                                    objName = QueryObjectNameWithTimeout(dupHandle, 15);
                                }
                            }
                            else
                            {
                                objName = QueryObjectName(dupHandle);
                            }
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(dupHandle);
                        }
                    }

                    results.Add(new ProcessHandleInfoItem
                    {
                        HandleValueHex = $"0x{h.HandleValue.ToUInt64():X}",
                        ObjectType = objType,
                        ObjectName = objName,
                        GrantedAccessHex = $"0x{h.GrantedAccess:X8}"
                    });
                }
            }
            finally
            {
                if (canDup)
                {
                    NativeMethods.CloseHandle(hTargetProc);
                }
            }

            return (results, note.Trim());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string QueryObjectTypeName(IntPtr handle)
    {
        const int size = 512;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQueryObject(handle, NativeMethods.ObjectTypeInformation, buf, size, out _) == NativeMethods.STATUS_SUCCESS)
            {
                var ustr = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buf);
                return NativeMethods.ReadUnicodeString(ustr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return string.Empty;
    }

    private static string QueryObjectName(IntPtr handle)
    {
        const int size = 2048;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQueryObject(handle, NativeMethods.ObjectNameInformation, buf, size, out _) == NativeMethods.STATUS_SUCCESS)
            {
                var ustr = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buf);
                return NativeMethods.ReadUnicodeString(ustr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return string.Empty;
    }

    private static string QueryObjectNameWithTimeout(IntPtr handle, int timeoutMs)
    {
        string result = string.Empty;
        var thread = new Thread(() =>
        {
            try
            {
                result = QueryObjectName(handle);
            }
            catch
            {
                result = string.Empty;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        if (!thread.Join(timeoutMs))
        {
            return "(Pipe or locked resource)";
        }

        return result;
    }

    private static Dictionary<int, List<ProcessServiceItem>> GetAllServicesByPid()
    {
        var map = new Dictionary<int, List<ProcessServiceItem>>();
        IntPtr scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            return map;
        }

        try
        {
            uint resumeHandle = 0;
            NativeMethods.EnumServicesStatusExW(
                scm,
                NativeMethods.SC_ENUM_PROCESS_INFO,
                NativeMethods.SERVICE_WIN32,
                NativeMethods.SERVICE_STATE_ALL,
                IntPtr.Zero,
                0,
                out uint bytesNeeded,
                out _,
                ref resumeHandle,
                null);

            if (bytesNeeded == 0)
            {
                return map;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (!NativeMethods.EnumServicesStatusExW(
                    scm,
                    NativeMethods.SC_ENUM_PROCESS_INFO,
                    NativeMethods.SERVICE_WIN32,
                    NativeMethods.SERVICE_STATE_ALL,
                    buffer,
                    bytesNeeded,
                    out _,
                    out uint returned,
                    ref resumeHandle,
                    null))
                {
                    return map;
                }

                int structSize = Marshal.SizeOf<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>();
                IntPtr current = buffer;

                for (uint i = 0; i < returned; i++)
                {
                    var svc = Marshal.PtrToStructure<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>(current);
                    int pid = (int)svc.ServiceStatusProcess.dwProcessId;
                    if (pid > 0)
                    {
                        if (!map.TryGetValue(pid, out var list))
                        {
                            list = [];
                            map[pid] = list;
                        }

                        list.Add(new ProcessServiceItem
                        {
                            ServiceName = NativeMethods.ReadNullTerminatedUnicode(svc.lpServiceName),
                            DisplayName = NativeMethods.ReadNullTerminatedUnicode(svc.lpDisplayName),
                            Status = DecodeServiceState(svc.ServiceStatusProcess.dwCurrentState),
                            ServiceType = DecodeServiceType(svc.ServiceStatusProcess.dwServiceType)
                        });
                    }

                    current = IntPtr.Add(current, structSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }

        return map;
    }

    private static string DecodeServiceState(uint state) => state switch
    {
        1 => "Stopped",
        2 => "Starting",
        3 => "Stopping",
        4 => "Running",
        5 => "Continuing",
        6 => "Pausing",
        7 => "Paused",
        _ => $"Status {state}"
    };

    private static string DecodeServiceType(uint type)
    {
        if ((type & 0x10) != 0)
        {
            return "Own Process (Win32)";
        }
        if ((type & 0x20) != 0)
        {
            return "Shared Process (svchost)";
        }
        return $"0x{type:X}";
    }

    private static List<ProcessWindowItem> GetWindowsForProcess(int targetPid)
    {
        var list = new List<ProcessWindowItem>();
        char[] textBuf = new char[512];
        char[] classBuf = new char[256];

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint winPid);
            if ((int)winPid != targetPid)
            {
                return true;
            }

            int titleLen = NativeMethods.GetWindowTextW(hWnd, textBuf, textBuf.Length);
            string title = titleLen > 0 ? new string(textBuf, 0, titleLen) : string.Empty;

            int classLen = NativeMethods.GetClassNameW(hWnd, classBuf, classBuf.Length);
            string className = classLen > 0 ? new string(classBuf, 0, classLen) : string.Empty;

            bool visible = NativeMethods.IsWindowVisible(hWnd);

            if (visible || !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new ProcessWindowItem
                {
                    HandleHex = $"0x{hWnd.ToInt64():X}",
                    Title = string.IsNullOrEmpty(title) ? "(Untitled)" : title,
                    ClassName = className,
                    IsVisible = visible
                });
            }

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static List<NetworkConnectionItem> GetAllNetworkConnections()
    {
        var list = new List<NetworkConnectionItem>(128);
        CollectTcpV4(list);
        CollectTcpV6(list);
        CollectUdpV4(list);
        CollectUdpV6(list);
        return list;
    }

    private static void CollectTcpV4(List<NetworkConnectionItem> list)
    {
        int size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buf, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buf, 4);

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                list.Add(new NetworkConnectionItem
                {
                    Pid = (int)row.dwOwningPid,
                    Protocol = "TCP",
                    LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                    LocalPort = NativeMethods.ConvertNetworkPort(row.dwLocalPort),
                    RemoteAddress = new IPAddress(row.dwRemoteAddr).ToString(),
                    RemotePort = row.dwState == 2 ? 0 : NativeMethods.ConvertNetworkPort(row.dwRemotePort),
                    State = NativeMethods.DecodeTcpState(row.dwState)
                });
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void CollectTcpV6(List<NetworkConnectionItem> list)
    {
        int size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET6, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buf, ref size, true, NativeMethods.AF_INET6, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buf, 4);

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr);
                list.Add(new NetworkConnectionItem
                {
                    Pid = (int)row.dwOwningPid,
                    Protocol = "TCPv6",
                    LocalAddress = $"[{new IPAddress(row.ucLocalAddr)}]",
                    LocalPort = NativeMethods.ConvertNetworkPort(row.dwLocalPort),
                    RemoteAddress = $"[{new IPAddress(row.ucRemoteAddr)}]",
                    RemotePort = row.dwState == 2 ? 0 : NativeMethods.ConvertNetworkPort(row.dwRemotePort),
                    State = NativeMethods.DecodeTcpState(row.dwState)
                });
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void CollectUdpV4(List<NetworkConnectionItem> list)
    {
        int size = 0;
        NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buf, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0) != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_UDPROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buf, 4);

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDPROW_OWNER_PID>(rowPtr);
                list.Add(new NetworkConnectionItem
                {
                    Pid = (int)row.dwOwningPid,
                    Protocol = "UDP",
                    LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                    LocalPort = NativeMethods.ConvertNetworkPort(row.dwLocalPort),
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = "UDP"
                });
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void CollectUdpV6(List<NetworkConnectionItem> list)
    {
        int size = 0;
        NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET6, NativeMethods.UDP_TABLE_OWNER_PID, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buf, ref size, true, NativeMethods.AF_INET6, NativeMethods.UDP_TABLE_OWNER_PID, 0) != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_UDP6ROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buf, 4);

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_UDP6ROW_OWNER_PID>(rowPtr);
                list.Add(new NetworkConnectionItem
                {
                    Pid = (int)row.dwOwningPid,
                    Protocol = "UDPv6",
                    LocalAddress = $"[{new IPAddress(row.ucLocalAddr)}]",
                    LocalPort = NativeMethods.ConvertNetworkPort(row.dwLocalPort),
                    RemoteAddress = "*",
                    RemotePort = 0,
                    State = "UDP"
                });
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static Image CreateBadgeIcon(Color bgColor, string symbol)
    {
        var bmp = new Bitmap(16, 16);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(bgColor);
        g.FillEllipse(brush, 1, 1, 14, 14);
        using var font = new Font("Segoe UI Emoji", 7.5f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(symbol, font, textBrush, new RectangleF(0, 0, 16, 16), sf);
        return bmp;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            foreach (Image img in _iconCache.Values)
            {
                img.Dispose();
            }
            _iconCache.Clear();
            _metadataCache.Clear();
            _prevCounters.Clear();
        }

        _defaultProcessIcon.Dispose();
        _systemProcessIcon.Dispose();
    }
}
