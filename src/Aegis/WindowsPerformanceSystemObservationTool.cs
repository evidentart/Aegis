using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aegis.Core;

namespace Aegis;

public sealed record WindowsPerformanceSystem(
    double CpuUtilizationPercent,
    ulong PhysicalMemoryTotalBytes,
    ulong PhysicalMemoryAvailableBytes,
    int MemoryLoadPercent,
    DateTimeOffset SampleStartedAtUtc,
    TimeSpan SampleDuration) : IObservationData;

public sealed class WindowsPerformanceSystemObservationTool : IObservationTool
{
    public const string ToolId = "windows.performance.system";

    private readonly IWindowsPerformanceSystemSampler _sampler;

    public WindowsPerformanceSystemObservationTool()
        : this(new NativeWindowsPerformanceSystemSampler())
    {
    }

    internal WindowsPerformanceSystemObservationTool(IWindowsPerformanceSystemSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public ObservationToolDescriptor Descriptor { get; } = new(
        ToolId,
        "System performance snapshot",
        "Reads a bounded CPU utilization and physical memory snapshot.",
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

        var data = await _sampler.CaptureAsync(cancellationToken);
        return new ObservationResult(
            request.RequestId,
            ToolId,
            DateTimeOffset.UtcNow,
            ObservationStatus.Succeeded,
            data);
    }
}

internal interface IWindowsPerformanceSystemSampler
{
    Task<WindowsPerformanceSystem> CaptureAsync(CancellationToken cancellationToken);
}

internal sealed class NativeWindowsPerformanceSystemSampler : IWindowsPerformanceSystemSampler
{
    private const int SampleMilliseconds = 500;

    public async Task<WindowsPerformanceSystem> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampleStartedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var first = ReadSystemTimes();

        await Task.Delay(SampleMilliseconds, cancellationToken).ConfigureAwait(false);

        var second = ReadSystemTimes();
        var memory = ReadMemoryStatus();
        stopwatch.Stop();

        var cpuUtilizationPercent = WindowsPerformanceCalculations.CalculateSystemCpuUtilizationPercent(
            first.IdleTicks,
            second.IdleTicks,
            first.KernelTicks,
            second.KernelTicks,
            first.UserTicks,
            second.UserTicks);

        return new WindowsPerformanceSystem(
            cpuUtilizationPercent,
            memory.TotalPhysicalBytes,
            memory.AvailablePhysicalBytes,
            memory.MemoryLoadPercent,
            sampleStartedAtUtc,
            ValidateSampleDuration(stopwatch.Elapsed));
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

    private static MemoryStatusSample ReadMemoryStatus()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalMemoryStatusEx failed.");
        }

        if (status.MemoryLoad > 100)
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx returned an invalid memory load.");
        }

        if (status.AvailablePhysicalBytes > status.TotalPhysicalBytes)
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx returned invalid physical memory data.");
        }

        return new MemoryStatusSample(
            status.TotalPhysicalBytes,
            status.AvailablePhysicalBytes,
            checked((int)status.MemoryLoad));
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }
}

internal readonly record struct SystemTimeSample(
    ulong IdleTicks,
    ulong KernelTicks,
    ulong UserTicks);

internal readonly record struct MemoryStatusSample(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    int MemoryLoadPercent);

internal static class WindowsPerformanceCalculations
{
    private const double BoundaryEpsilon = 0.000001;

    public static double CalculateSystemCpuUtilizationPercent(
        ulong firstIdleTicks,
        ulong secondIdleTicks,
        ulong firstKernelTicks,
        ulong secondKernelTicks,
        ulong firstUserTicks,
        ulong secondUserTicks)
    {
        var idleDelta = CalculateDelta(firstIdleTicks, secondIdleTicks, nameof(firstIdleTicks));
        var kernelDelta = CalculateDelta(firstKernelTicks, secondKernelTicks, nameof(firstKernelTicks));
        var userDelta = CalculateDelta(firstUserTicks, secondUserTicks, nameof(firstUserTicks));
        var systemCpuCapacityDelta = checked(kernelDelta + userDelta);
        if (systemCpuCapacityDelta == 0 || idleDelta > systemCpuCapacityDelta)
        {
            throw new InvalidOperationException("The system CPU timing sample was invalid.");
        }

        return ClampPercentageBoundary(
            (double)(systemCpuCapacityDelta - idleDelta) / systemCpuCapacityDelta * 100,
            "The system CPU utilization calculation was invalid.");
    }

    public static double CalculateProcessCpuUtilizationPercent(
        ulong firstKernelTicks,
        ulong secondKernelTicks,
        ulong firstUserTicks,
        ulong secondUserTicks,
        ulong systemCpuCapacityDelta)
    {
        var kernelDelta = CalculateDelta(firstKernelTicks, secondKernelTicks, nameof(firstKernelTicks));
        var userDelta = CalculateDelta(firstUserTicks, secondUserTicks, nameof(firstUserTicks));
        var processCpuDelta = checked(kernelDelta + userDelta);
        if (systemCpuCapacityDelta == 0 || processCpuDelta > systemCpuCapacityDelta)
        {
            throw new InvalidOperationException("The process CPU timing sample was invalid.");
        }

        return ClampPercentageBoundary(
            (double)processCpuDelta / systemCpuCapacityDelta * 100,
            "The process CPU utilization calculation was invalid.");
    }

    private static ulong CalculateDelta(ulong first, ulong second, string parameterName)
    {
        if (second < first)
        {
            throw new InvalidOperationException($"The performance timing value '{parameterName}' moved backwards.");
        }

        return second - first;
    }

    private static double ClampPercentageBoundary(double value, string errorMessage)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException(errorMessage);
        }

        if (value < -BoundaryEpsilon || value > 100 + BoundaryEpsilon)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return Math.Clamp(value, 0, 100);
    }
}
