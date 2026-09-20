using System.ComponentModel;
using System.Runtime.InteropServices;
using Aegis.Core;
using Microsoft.Win32.SafeHandles;

namespace Aegis;

public enum WindowsEventChannelKind
{
    System,
    Application
}

public enum WindowsEventSeverity
{
    Critical,
    Error
}

public sealed record WindowsDiagnosticEvent(
    DateTimeOffset OccurredAtUtc,
    WindowsEventChannelKind Channel,
    string ProviderName,
    int EventId,
    WindowsEventSeverity Severity);

public sealed record WindowsRecentErrorEvents(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    bool IsTruncated,
    IReadOnlyList<WindowsDiagnosticEvent> Events) : IObservationData;

public sealed class WindowsRecentErrorEventsObservationTool : IObservationTool
{
    public const string ToolId = "windows.events.recent_errors";

    private readonly IWindowsRecentErrorEventsSampler _sampler;

    public WindowsRecentErrorEventsObservationTool()
        : this(new NativeWindowsRecentErrorEventsSampler())
    {
    }

    internal WindowsRecentErrorEventsObservationTool(IWindowsRecentErrorEventsSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public ObservationToolDescriptor Descriptor { get; } = new(
        ToolId,
        "Recent Windows error events",
        "Reads bounded Critical and Error metadata from the local System and Application logs for the last 48 hours.",
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

        var data = await _sampler.CaptureAsync(cancellationToken).ConfigureAwait(false);
        WindowsRecentErrorEventsValidation.Validate(data);
        return new ObservationResult(
            request.RequestId,
            ToolId,
            DateTimeOffset.UtcNow,
            ObservationStatus.Succeeded,
            data);
    }
}

internal interface IWindowsRecentErrorEventsSampler
{
    Task<WindowsRecentErrorEvents> CaptureAsync(CancellationToken cancellationToken);
}

internal static class WindowsRecentErrorEventsValidation
{
    internal static readonly TimeSpan ObservationWindow = TimeSpan.FromHours(48);
    internal const int MaximumEventsPerChannel = 10;
    internal const int MaximumEvents = 20;
    internal const int MaximumProviderNameLength = 256;
    internal const int MaximumEventId = ushort.MaxValue;

    public static WindowsRecentErrorEvents Build(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        IReadOnlyList<WindowsDiagnosticEvent> events,
        bool retrievalBudgetExhausted = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        ValidateWindow(windowStartUtc, windowEndUtc);

        foreach (var diagnosticEvent in events)
        {
            ValidateEvent(diagnosticEvent, windowStartUtc, windowEndUtc);
        }

        var retainedCounts = new Dictionary<WindowsEventChannelKind, int>();
        var retained = events
            .OrderByDescending(diagnosticEvent => diagnosticEvent.OccurredAtUtc)
            .ThenBy(diagnosticEvent => diagnosticEvent.Channel)
            .ThenBy(diagnosticEvent => diagnosticEvent.ProviderName, StringComparer.Ordinal)
            .ThenBy(diagnosticEvent => diagnosticEvent.EventId)
            .Where(diagnosticEvent =>
            {
                retainedCounts.TryGetValue(diagnosticEvent.Channel, out var count);
                if (count >= MaximumEventsPerChannel)
                {
                    return false;
                }

                retainedCounts[diagnosticEvent.Channel] = count + 1;
                return true;
            })
            .Take(MaximumEvents)
            .ToArray();

        var data = new WindowsRecentErrorEvents(
            windowStartUtc.ToUniversalTime(),
            windowEndUtc.ToUniversalTime(),
            retrievalBudgetExhausted || events.Count > retained.Length,
            retained);
        Validate(data);
        return data;
    }

    public static void Validate(WindowsRecentErrorEvents data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateWindow(data.WindowStartUtc, data.WindowEndUtc);
        ArgumentNullException.ThrowIfNull(data.Events);
        if (data.Events.Count > MaximumEvents)
        {
            throw new InvalidOperationException("The recent event observation exceeds its total event bound.");
        }

        var channelCounts = new Dictionary<WindowsEventChannelKind, int>();
        for (var index = 0; index < data.Events.Count; index++)
        {
            var diagnosticEvent = data.Events[index];
            ValidateEvent(diagnosticEvent, data.WindowStartUtc, data.WindowEndUtc);
            if (index > 0 && data.Events[index - 1].OccurredAtUtc < diagnosticEvent.OccurredAtUtc)
            {
                throw new InvalidOperationException("Recent Windows events must be ordered newest first.");
            }

            channelCounts.TryGetValue(diagnosticEvent.Channel, out var count);
            if (count >= MaximumEventsPerChannel)
            {
                throw new InvalidOperationException("The recent event observation exceeds its per-channel bound.");
            }

            channelCounts[diagnosticEvent.Channel] = count + 1;
        }
    }

    private static void ValidateWindow(DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
    {
        if (windowStartUtc.Offset != TimeSpan.Zero || windowEndUtc.Offset != TimeSpan.Zero ||
            windowStartUtc >= windowEndUtc || windowEndUtc - windowStartUtc != ObservationWindow)
        {
            throw new InvalidOperationException("The recent event observation window is invalid.");
        }
    }

    private static void ValidateEvent(
        WindowsDiagnosticEvent diagnosticEvent,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (diagnosticEvent.Channel is not (WindowsEventChannelKind.System or WindowsEventChannelKind.Application) ||
            diagnosticEvent.Severity is not (WindowsEventSeverity.Critical or WindowsEventSeverity.Error) ||
            diagnosticEvent.OccurredAtUtc.Offset != TimeSpan.Zero ||
            diagnosticEvent.OccurredAtUtc < windowStartUtc ||
            diagnosticEvent.OccurredAtUtc > windowEndUtc ||
            string.IsNullOrWhiteSpace(diagnosticEvent.ProviderName) ||
            !string.Equals(diagnosticEvent.ProviderName, diagnosticEvent.ProviderName.Trim(), StringComparison.Ordinal) ||
            diagnosticEvent.ProviderName.Length > MaximumProviderNameLength ||
            diagnosticEvent.EventId < 0 ||
            diagnosticEvent.EventId > MaximumEventId)
        {
            throw new InvalidOperationException("The recent Windows event metadata is invalid.");
        }
    }
}

internal sealed class NativeWindowsRecentErrorEventsSampler : IWindowsRecentErrorEventsSampler
{
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    internal const int MaximumQueriedEventsPerChannel = WindowsRecentErrorEventsValidation.MaximumEventsPerChannel + 1;
    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryReverseDirection = 0x200;
    internal const int EvtRenderContextValues = 0;
    internal const int EvtRenderEventValues = 0;
    internal const int EvtVariantTypeMask = 0x7f;
    internal const int EvtVariantTypeArray = 0x80;
    internal const int EvtVariantTypeString = 1;
    internal const int EvtVariantTypeUInt8 = 4;
    internal const int EvtVariantTypeUInt16 = 6;
    internal const int EvtVariantTypeFileTime = 17;
    internal const int EvtVariantTypeHexInt32 = 20;

    private const string FixedQuery =
        "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 172800000]]]";

    private static readonly string[] RenderPaths =
    [
        "Event/System/Provider/@Name",
        "Event/System/EventID",
        "Event/System/Level",
        "Event/System/TimeCreated/@SystemTime"
    ];

    public Task<WindowsRecentErrorEvents> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windowEndUtc = DateTimeOffset.UtcNow;
        var windowStartUtc = windowEndUtc - WindowsRecentErrorEventsValidation.ObservationWindow;
        var systemRead = ReadChannel(
            WindowsEventChannelKind.System,
            "System",
            windowStartUtc,
            windowEndUtc,
            cancellationToken);
        var applicationRead = ReadChannel(
            WindowsEventChannelKind.Application,
            "Application",
            windowStartUtc,
            windowEndUtc,
            cancellationToken);
        var events = systemRead.Events.Concat(applicationRead.Events).ToArray();
        return Task.FromResult(
            WindowsRecentErrorEventsValidation.Build(
                windowStartUtc,
                windowEndUtc,
                events,
                systemRead.RetrievalBudgetExhausted || applicationRead.RetrievalBudgetExhausted));
    }

    private static ChannelReadResult ReadChannel(
        WindowsEventChannelKind channel,
        string channelPath,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        using var query = CreateQuery(channelPath);
        using var renderContext = CreateRenderContext();
        var events = new List<WindowsDiagnosticEvent>();
        var eventHandles = new IntPtr[1];
        var retrievedEventCount = 0;
        while (HasRetrievalBudget(retrievedEventCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EvtNext(query, 1, eventHandles, 0, 0, out var returned))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    break;
                }

                throw new Win32Exception(error, "EvtNext failed.");
            }

            retrievedEventCount++;
            if (returned != 1 || eventHandles[0] == IntPtr.Zero)
            {
                throw new InvalidOperationException("EvtNext returned an invalid event handle.");
            }

            using var eventHandle = new NativeEventLogHandle(eventHandles[0]);
            cancellationToken.ThrowIfCancellationRequested();
            var diagnosticEvent = RenderEvent(renderContext, eventHandle, channel);
            if (diagnosticEvent.OccurredAtUtc >= windowStartUtc && diagnosticEvent.OccurredAtUtc <= windowEndUtc)
            {
                events.Add(diagnosticEvent);
            }
        }

        return new ChannelReadResult(
            events,
            retrievedEventCount >= MaximumQueriedEventsPerChannel);
    }

    private sealed record ChannelReadResult(
        IReadOnlyList<WindowsDiagnosticEvent> Events,
        bool RetrievalBudgetExhausted);

    internal static bool HasRetrievalBudget(int retrievedEventCount) =>
        retrievedEventCount < MaximumQueriedEventsPerChannel;

    private static NativeEventLogHandle CreateQuery(string channelPath)
    {
        var handle = EvtQuery(IntPtr.Zero, channelPath, FixedQuery, EvtQueryChannelPath | EvtQueryReverseDirection);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EvtQuery failed.");
        }

        return new NativeEventLogHandle(handle);
    }

    private static NativeEventLogHandle CreateRenderContext()
    {
        var pathArray = Marshal.AllocHGlobal(checked(RenderPaths.Length * IntPtr.Size));
        var pathPointers = new IntPtr[RenderPaths.Length];
        try
        {
            for (var index = 0; index < RenderPaths.Length; index++)
            {
                pathPointers[index] = Marshal.StringToCoTaskMemUni(RenderPaths[index]);
                Marshal.WriteIntPtr(pathArray, index * IntPtr.Size, pathPointers[index]);
            }

            var handle = EvtCreateRenderContext(
                RenderPaths.Length,
                pathArray,
                EvtRenderContextValues);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EvtCreateRenderContext failed.");
            }

            return new NativeEventLogHandle(handle);
        }
        finally
        {
            foreach (var pathPointer in pathPointers)
            {
                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }

            Marshal.FreeHGlobal(pathArray);
        }
    }

    private static WindowsDiagnosticEvent RenderEvent(
        NativeEventLogHandle renderContext,
        NativeEventLogHandle eventHandle,
        WindowsEventChannelKind channel)
    {
        var bufferSize = 0;
        var propertyCount = 0;
        if (!EvtRender(
                renderContext,
                eventHandle,
                EvtRenderEventValues,
                0,
                IntPtr.Zero,
                out bufferSize,
                out propertyCount))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || bufferSize <= 0)
            {
                throw new Win32Exception(error, "EvtRender failed.");
            }
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!EvtRender(
                    renderContext,
                    eventHandle,
                    EvtRenderEventValues,
                    bufferSize,
                    buffer,
                    out bufferSize,
                    out propertyCount))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EvtRender failed.");
            }

            if (propertyCount != RenderPaths.Length)
            {
                throw new InvalidOperationException("EvtRender returned an unexpected field count.");
            }

            var providerName = ReadStringVariant(buffer, 0);
            var eventId = ReadEventIdVariant(buffer, 1);
            var level = ReadUInt8Variant(buffer, 2);
            var occurredAtUtc = ReadFileTimeVariant(buffer, 3);
            var severity = level switch
            {
                1 => WindowsEventSeverity.Critical,
                2 => WindowsEventSeverity.Error,
                _ => throw new InvalidOperationException("EvtRender returned an unsupported event severity.")
            };

            return new WindowsDiagnosticEvent(
                occurredAtUtc,
                channel,
                providerName,
                eventId,
                severity);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string ReadStringVariant(IntPtr buffer, int index)
    {
        var variant = GetVariantAddress(buffer, index);
        EnsureVariantType(variant, EvtVariantTypeString);
        var value = Marshal.ReadIntPtr(variant);
        var text = value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("EvtRender returned an empty provider name.");
        }

        return text;
    }

    internal static int ReadEventIdVariant(IntPtr buffer, int index)
    {
        var variant = GetVariantAddress(buffer, index);
        EnsureVariantType(variant, EvtVariantTypeUInt16);
        return (ushort)Marshal.ReadInt16(variant);
    }

    internal static byte ReadUInt8Variant(IntPtr buffer, int index)
    {
        var variant = GetVariantAddress(buffer, index);
        EnsureVariantType(variant, EvtVariantTypeUInt8);
        return Marshal.ReadByte(variant);
    }

    internal static DateTimeOffset ReadFileTimeVariant(IntPtr buffer, int index)
    {
        var variant = GetVariantAddress(buffer, index);
        EnsureVariantType(variant, EvtVariantTypeFileTime);
        return new DateTimeOffset(DateTime.FromFileTimeUtc(Marshal.ReadInt64(variant)));
    }

    private static IntPtr GetVariantAddress(IntPtr buffer, int index) =>
        IntPtr.Add(buffer, checked(index * 16));

    private static int GetScalarVariantType(IntPtr variant)
    {
        var rawType = Marshal.ReadInt32(variant, 12);
        if ((rawType & EvtVariantTypeArray) != 0)
        {
            throw new InvalidOperationException("EvtRender returned an array where a scalar value was required.");
        }

        return rawType & EvtVariantTypeMask;
    }

    private static void EnsureVariantType(IntPtr variant, int expectedType)
    {
        if (GetScalarVariantType(variant) != expectedType)
        {
            throw new InvalidOperationException("EvtRender returned an unexpected field type.");
        }
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(
        IntPtr session,
        string path,
        string query,
        int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(
        SafeHandle queryResultSet,
        int eventArraySize,
        [Out] IntPtr[] eventArray,
        int timeout,
        int flags,
        out int returned);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtCreateRenderContext(
        int valuePathsCount,
        IntPtr valuePaths,
        int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(
        SafeHandle context,
        SafeHandle fragment,
        int flags,
        int bufferSize,
        IntPtr buffer,
        out int bufferUsed,
        out int propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr objectHandle);

    private sealed class NativeEventLogHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeEventLogHandle(IntPtr handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => EvtClose(handle);
    }
}
