using System.ComponentModel;
using System.Runtime.InteropServices;
using Aegis.Core;
using Microsoft.Win32.SafeHandles;

namespace Aegis;

public sealed record WindowsPerformanceProcess(
    int ProcessId,
    string ProcessName,
    double CpuUtilizationPercent,
    ulong WorkingSetBytes);

public sealed record WindowsPerformanceTopProcesses(
    DateTimeOffset SampleStartedAtUtc,
    TimeSpan SampleDuration,
    IReadOnlyList<WindowsPerformanceProcess> TopCpuProcesses,
    IReadOnlyList<WindowsPerformanceProcess> TopMemoryProcesses) : IObservationData;

public sealed class WindowsPerformanceTopProcessesObservationTool : IObservationTool
{
    public const string ToolId = "windows.performance.top_processes";

    private readonly IWindowsTopProcessesSampler _sampler;

    public WindowsPerformanceTopProcessesObservationTool()
        : this(new NativeWindowsTopProcessesSampler())
    {
    }

    internal WindowsPerformanceTopProcessesObservationTool(IWindowsTopProcessesSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public ObservationToolDescriptor Descriptor { get; } = new(
        ToolId,
        "Top process performance snapshot",
        "Reads bounded CPU and working-set rankings for accessible processes.",
        BaselineEligible: false);

    public async Task<ObservationResult> ObserveAsync(
        ObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.ToolId, ToolId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request does not target this observation tool.", nameof(request));
        }

        var capture = await _sampler.CaptureAsync(cancellationToken);
        var data = WindowsTopProcessesBuilder.Build(capture);
        return new ObservationResult(
            request.RequestId,
            ToolId,
            DateTimeOffset.UtcNow,
            ObservationStatus.Succeeded,
            data);
    }
}

internal interface IWindowsTopProcessesSampler
{
    Task<WindowsTopProcessesCapture> CaptureAsync(CancellationToken cancellationToken);
}

internal sealed class NativeWindowsTopProcessesSampler : IWindowsTopProcessesSampler
{
    private const uint SnapshotProcesses = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SampleMilliseconds = 500;
    private const int ErrorNoMoreFiles = 18;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public async Task<WindowsTopProcessesCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampleStartedAtUtc = DateTimeOffset.UtcNow;
        var firstSystemTimes = ReadSystemTimes();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var firstProcesses = ReadProcessSamples(EnumerateProcesses(cancellationToken), cancellationToken);

        await Task.Delay(SampleMilliseconds, cancellationToken).ConfigureAwait(false);

        var secondProcessEntries = EnumerateProcesses(cancellationToken)
            .ToDictionary(process => process.ProcessId);
        var secondProcesses = ReadProcessSamples(
            firstProcesses
                .Where(process => secondProcessEntries.ContainsKey(process.ProcessId))
                .Select(process => secondProcessEntries[process.ProcessId]),
            cancellationToken);
        var secondSystemTimes = ReadSystemTimes();
        stopwatch.Stop();

        return new WindowsTopProcessesCapture(
            sampleStartedAtUtc,
            ValidateSampleDuration(stopwatch.Elapsed),
            firstSystemTimes,
            secondSystemTimes,
            firstProcesses,
            secondProcesses);
    }

    private static IReadOnlyList<NativeProcessSample> ReadProcessSamples(
        IEnumerable<ProcessIdentity> processes,
        CancellationToken cancellationToken)
    {
        var samples = new List<NativeProcessSample>();
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = TryReadProcessSample(process, cancellationToken);
            if (sample is not null)
            {
                samples.Add(sample.Value);
            }
        }

        return samples;
    }

    private static NativeProcessSample? TryReadProcessSample(
        ProcessIdentity process,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)process.ProcessId));
        if (handle.IsInvalid)
        {
            return null;
        }

        try
        {
            if (!GetProcessTimes(
                    handle,
                    out var creationTime,
                    out _,
                    out var kernelTime,
                    out var userTime))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessTimes failed.");
            }

            var counters = new ProcessMemoryCounters
            {
                Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>()
            };
            if (!GetProcessMemoryInfo(handle, ref counters, counters.Size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessMemoryInfo failed.");
            }

            return new NativeProcessSample(
                process.ProcessId,
                process.ProcessName,
                FileTimeToUInt64(creationTime),
                FileTimeToUInt64(kernelTime),
                FileTimeToUInt64(userTime),
                counters.WorkingSetSize.ToUInt64());
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ProcessIdentity> EnumerateProcesses(CancellationToken cancellationToken)
    {
        var rawSnapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (rawSnapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed.");
        }

        using var snapshot = new NativeSnapshotHandle(rawSnapshot);
        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>()
        };
        if (!Process32FirstW(snapshot.DangerousGetHandle(), ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNoMoreFiles)
            {
                return Array.Empty<ProcessIdentity>();
            }

            throw new Win32Exception(error, "Process32First failed.");
        }

        var processes = new List<ProcessIdentity>();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ProcessId <= int.MaxValue && entry.ProcessId > 0)
            {
                processes.Add(new ProcessIdentity(
                    (int)entry.ProcessId,
                    entry.ExecutableName ?? string.Empty));
            }

            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32NextW(snapshot.DangerousGetHandle(), ref entry));

        var finalError = Marshal.GetLastWin32Error();
        if (finalError != ErrorNoMoreFiles)
        {
            throw new Win32Exception(finalError, "Process32Next failed.");
        }

        return processes;
    }

    private static SystemTimeSample ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        }

        return new SystemTimeSample(
            FileTimeToUInt64(idleTime),
            FileTimeToUInt64(kernelTime),
            FileTimeToUInt64(userTime));
    }

    private static TimeSpan ValidateSampleDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("The performance sample duration was outside the supported range.");
        }

        return duration;
    }

    private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern NativeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        SafeHandle process,
        ref ProcessMemoryCounters counters,
        uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    private sealed class NativeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeSnapshotHandle(IntPtr handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class NativeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeProcessHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal readonly record struct ProcessIdentity(int ProcessId, string ProcessName);

internal readonly record struct NativeProcessSample(
    int ProcessId,
    string ProcessName,
    ulong CreationTimeTicks,
    ulong KernelTicks,
    ulong UserTicks,
    ulong WorkingSetBytes);

internal sealed record WindowsTopProcessesCapture(
    DateTimeOffset SampleStartedAtUtc,
    TimeSpan SampleDuration,
    SystemTimeSample FirstSystemTimes,
    SystemTimeSample SecondSystemTimes,
    IReadOnlyList<NativeProcessSample> FirstProcesses,
    IReadOnlyList<NativeProcessSample> SecondProcesses);

internal static class WindowsTopProcessesBuilder
{
    private const int MaximumEntriesPerRanking = 10;

    public static WindowsPerformanceTopProcesses Build(WindowsTopProcessesCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.SampleDuration <= TimeSpan.Zero || capture.SampleDuration > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("The performance sample duration was outside the supported range.");
        }

        var systemCpuCapacityDelta = CalculateSystemCpuCapacityDelta(
            capture.FirstSystemTimes,
            capture.SecondSystemTimes);
        var secondByProcessId = capture.SecondProcesses.ToDictionary(process => process.ProcessId);
        var processes = new List<WindowsPerformanceProcess>();
        foreach (var first in capture.FirstProcesses)
        {
            if (!secondByProcessId.TryGetValue(first.ProcessId, out var second) ||
                first.CreationTimeTicks != second.CreationTimeTicks)
            {
                continue;
            }

            var cpuPercent = WindowsPerformanceCalculations.CalculateProcessCpuUtilizationPercent(
                first.KernelTicks,
                second.KernelTicks,
                first.UserTicks,
                second.UserTicks,
                systemCpuCapacityDelta);
            if (first.ProcessId <= 0 || string.IsNullOrWhiteSpace(first.ProcessName))
            {
                throw new InvalidOperationException("A process performance sample had invalid identity data.");
            }

            processes.Add(new WindowsPerformanceProcess(
                first.ProcessId,
                first.ProcessName,
                cpuPercent,
                second.WorkingSetBytes));
        }

        return new WindowsPerformanceTopProcesses(
            capture.SampleStartedAtUtc,
            capture.SampleDuration,
            processes
                .OrderByDescending(process => process.CpuUtilizationPercent)
                .ThenByDescending(process => process.WorkingSetBytes)
                .ThenBy(process => process.ProcessId)
                .Take(MaximumEntriesPerRanking)
                .ToArray(),
            processes
                .OrderByDescending(process => process.WorkingSetBytes)
                .ThenByDescending(process => process.CpuUtilizationPercent)
                .ThenBy(process => process.ProcessId)
                .Take(MaximumEntriesPerRanking)
                .ToArray());
    }

    private static ulong CalculateSystemCpuCapacityDelta(
        SystemTimeSample first,
        SystemTimeSample second)
    {
        var kernelDelta = CalculateDelta(first.KernelTicks, second.KernelTicks);
        var userDelta = CalculateDelta(first.UserTicks, second.UserTicks);
        var idleDelta = CalculateDelta(first.IdleTicks, second.IdleTicks);
        var capacityDelta = checked(kernelDelta + userDelta);
        if (capacityDelta == 0 || idleDelta > capacityDelta)
        {
            throw new InvalidOperationException("The system CPU timing sample was invalid.");
        }

        return capacityDelta;
    }

    private static ulong CalculateDelta(ulong first, ulong second)
    {
        if (second < first)
        {
            throw new InvalidOperationException("The performance timing value moved backwards.");
        }

        return second - first;
    }
}
