using Aegis;
using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class ObservationTests
{
    [Fact]
    public async Task InvokesRegisteredToolByExactId()
    {
        var tool = new FakeObservationTool("test.tool");
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));
        var request = CreateRequest("test.tool");

        var result = await runtime.ObserveAsync(request);

        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.ToolId, result.ToolId);
        Assert.Equal(ObservationStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task RejectsUnknownToolIdWithoutInvokingRegisteredTool()
    {
        var tool = new FakeObservationTool("test.tool");
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));

        await Assert.ThrowsAsync<UnknownObservationToolException>(() =>
            runtime.ObserveAsync(CreateRequest("other.tool")));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RejectsInvalidRequestWithoutInvokingTool()
    {
        var tool = new FakeObservationTool("test.tool");
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.ObserveAsync(new ObservationRequest(Guid.Empty, "test.tool")));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public void RejectsDuplicateToolIds()
    {
        var first = new FakeObservationTool("test.tool");
        var second = new FakeObservationTool("test.tool");

        Assert.Throws<ArgumentException>(() => new ObservationRegistry([first, second]));
    }

    [Fact]
    public void RegistryIsFixedAfterConstruction()
    {
        var tools = new List<IObservationTool> { new FakeObservationTool("first.tool") };
        var registry = new ObservationRegistry(tools);
        tools.Add(new FakeObservationTool("second.tool"));

        Assert.True(registry.TryGet("first.tool", out _));
        Assert.False(registry.TryGet("second.tool", out _));
    }

    [Fact]
    public async Task PreservesCancellationAndForwardsTokenToTool()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new FakeObservationTool("test.tool", cancellationToken =>
        {
            Assert.Equal(cancellation.Token, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        });
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.ObserveAsync(CreateRequest("test.tool"), cancellation.Token));
    }

    [Fact]
    public async Task ConvertsToolExceptionToSafeFailureResult()
    {
        var tool = new FakeObservationTool(
            "test.tool",
            exception: new InvalidOperationException("secret internal detail"));
        var diagnostics = new List<Exception>();
        var runtime = new ObservationRuntime(
            new ObservationRegistry([tool]),
            diagnostics.Add);

        var result = await runtime.ObserveAsync(CreateRequest("test.tool"));

        Assert.Equal(ObservationStatus.Failed, result.Status);
        Assert.Equal("observation_failed", result.Failure?.Code);
        Assert.Equal("The observation could not be completed.", result.Failure?.Message);
        Assert.DoesNotContain("secret internal detail", result.Failure?.Message);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task DiagnosticCallbackFailureDoesNotEscapeOrReplaceFailureResult()
    {
        var tool = new FakeObservationTool(
            "test.tool",
            exception: new InvalidOperationException("tool failure"));
        var runtime = new ObservationRuntime(
            new ObservationRegistry([tool]),
            _ => throw new InvalidOperationException("diagnostic failure"));

        var result = await runtime.ObserveAsync(CreateRequest("test.tool"));

        Assert.Equal(ObservationStatus.Failed, result.Status);
        Assert.Equal("observation_failed", result.Failure?.Code);
        Assert.Equal("The observation could not be completed.", result.Failure?.Message);
    }

    [Fact]
    public async Task RejectsToolResultWithMismatchedIdentity()
    {
        var tool = new FakeObservationTool(
            "test.tool",
            result: new ObservationResult(
                Guid.NewGuid(),
                "different.tool",
                DateTimeOffset.UtcNow,
                ObservationStatus.Succeeded));
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));

        var result = await runtime.ObserveAsync(CreateRequest("test.tool"));

        Assert.Equal(ObservationStatus.Failed, result.Status);
        Assert.Equal("observation_failed", result.Failure?.Code);
    }

    [Fact]
    public async Task SystemInfoContainsOnlyApprovedFields()
    {
        var tool = new WindowsSystemInfoObservationTool();
        var result = await tool.ObserveAsync(CreateRequest(WindowsSystemInfoObservationTool.ToolId));
        var data = Assert.IsType<WindowsSystemInfo>(result.Data);

        Assert.Equal("Windows", data.Platform);
        Assert.False(string.IsNullOrWhiteSpace(data.OsVersion));
        Assert.False(string.IsNullOrWhiteSpace(data.Architecture));
        Assert.Equal(
            ["Platform", "OsVersion", "Build", "Architecture"],
            typeof(WindowsSystemInfo).GetProperties().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void BaselineEligibilityIsExplicitAtCapabilityLevel()
    {
        var systemInfo = new WindowsSystemInfoObservationTool();
        var defaultDescriptor = new FakeObservationTool("other.tool").Descriptor;

        Assert.True(systemInfo.Descriptor.BaselineEligible);
        Assert.False(defaultDescriptor.BaselineEligible);
        Assert.Equal(WindowsSystemInfoObservationTool.ToolId, systemInfo.Descriptor.Id);
    }

    [Fact]
    public void RequestHasNoArbitraryArgumentMechanism()
    {
        var propertyNames = typeof(ObservationRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["RequestId", "ToolId", "RequestedAtUtc"], propertyNames);
        Assert.DoesNotContain("Arguments", propertyNames);
    }

    [Fact]
    public async Task PerformanceToolsHaveExactArgumentFreeNonBaselineContracts()
    {
        var systemTool = new WindowsPerformanceSystemObservationTool(
            new FixedSystemSampler(new WindowsPerformanceSystem(
                12.5,
                16_000,
                8_000,
                50,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(500))));
        var topProcessesTool = new WindowsPerformanceTopProcessesObservationTool(
            new FixedTopProcessesSampler(new WindowsTopProcessesCapture(
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(500),
                new SystemTimeSample(0, 0, 0),
                new SystemTimeSample(0, 100, 0),
                [new NativeProcessSample(1, "one.exe", 7, 0, 0, 100)],
                [new NativeProcessSample(1, "one.exe", 7, 20, 0, 200)])));

        Assert.Equal("windows.performance.system", systemTool.Descriptor.Id);
        Assert.Equal("windows.performance.top_processes", topProcessesTool.Descriptor.Id);
        Assert.False(systemTool.Descriptor.BaselineEligible);
        Assert.False(topProcessesTool.Descriptor.BaselineEligible);
        Assert.Equal(
            ["RequestId", "ToolId", "RequestedAtUtc"],
            typeof(ObservationRequest).GetProperties().Select(property => property.Name).ToArray());

        var systemResult = await systemTool.ObserveAsync(CreateRequest(systemTool.Descriptor.Id));
        var topProcessesResult = await topProcessesTool.ObserveAsync(CreateRequest(topProcessesTool.Descriptor.Id));
        Assert.IsType<WindowsPerformanceSystem>(systemResult.Data);
        Assert.IsType<WindowsPerformanceTopProcesses>(topProcessesResult.Data);
    }

    [Fact]
    public async Task RecentErrorEventsToolHasExactArgumentFreeNonBaselineContract()
    {
        var end = DateTimeOffset.UtcNow;
        var data = WindowsRecentErrorEventsValidation.Build(
            end - WindowsRecentErrorEventsValidation.ObservationWindow,
            end,
            [CreateEvent(end, WindowsEventChannelKind.System, WindowsEventSeverity.Error)]);
        var tool = new WindowsRecentErrorEventsObservationTool(new FixedRecentErrorEventsSampler(data));

        Assert.Equal("windows.events.recent_errors", tool.Descriptor.Id);
        Assert.False(tool.Descriptor.BaselineEligible);
        Assert.Equal(
            ["RequestId", "ToolId", "RequestedAtUtc"],
            typeof(ObservationRequest).GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["OccurredAtUtc", "Channel", "ProviderName", "EventId", "Severity"],
            typeof(WindowsDiagnosticEvent).GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["WindowStartUtc", "WindowEndUtc", "IsTruncated", "Events"],
            typeof(WindowsRecentErrorEvents).GetProperties().Select(property => property.Name).ToArray());

        var result = await tool.ObserveAsync(CreateRequest(tool.Descriptor.Id));
        Assert.IsType<WindowsRecentErrorEvents>(result.Data);
    }

    [Fact]
    public void RecentErrorEventsAreBoundedNewestFirstAndTruncated()
    {
        var end = DateTimeOffset.UtcNow;
        var start = end - WindowsRecentErrorEventsValidation.ObservationWindow;
        var events = Enumerable.Range(0, 11)
            .Select(index => CreateEvent(end.AddMinutes(-index), WindowsEventChannelKind.System, WindowsEventSeverity.Error))
            .Concat(Enumerable.Range(0, 11)
                .Select(index => CreateEvent(end.AddMinutes(-index).AddSeconds(-30), WindowsEventChannelKind.Application, WindowsEventSeverity.Critical)))
            .ToArray();

        var data = WindowsRecentErrorEventsValidation.Build(start, end, events);

        Assert.True(data.IsTruncated);
        Assert.Equal(20, data.Events.Count);
        Assert.Equal(10, data.Events.Count(diagnosticEvent => diagnosticEvent.Channel == WindowsEventChannelKind.System));
        Assert.Equal(10, data.Events.Count(diagnosticEvent => diagnosticEvent.Channel == WindowsEventChannelKind.Application));
        Assert.Equal(end, data.Events[0].OccurredAtUtc);
        Assert.True(data.Events.Zip(data.Events.Skip(1)).All(pair => pair.First.OccurredAtUtc >= pair.Second.OccurredAtUtc));
    }

    [Fact]
    public void RecentErrorEventsAreTruncatedWhenRejectedNativeEventsExhaustTheBudget()
    {
        var end = DateTimeOffset.UtcNow;
        var start = end - WindowsRecentErrorEventsValidation.ObservationWindow;
        var data = WindowsRecentErrorEventsValidation.Build(
            start,
            end,
            [CreateEvent(end, WindowsEventChannelKind.System, WindowsEventSeverity.Error)],
            retrievalBudgetExhausted: true);

        Assert.True(data.IsTruncated);
        Assert.Single(data.Events);
    }

    [Fact]
    public void RecentErrorEventsRejectInvalidWindowAndMetadata()
    {
        var end = DateTimeOffset.UtcNow;
        var start = end - WindowsRecentErrorEventsValidation.ObservationWindow;

        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start.AddSeconds(1),
                end,
                []));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start,
                end,
                [CreateEvent(start.AddMinutes(1), (WindowsEventChannelKind)99, WindowsEventSeverity.Error)]));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start,
                end,
                [CreateEvent(start.AddMinutes(1), WindowsEventChannelKind.System, (WindowsEventSeverity)99)]));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start,
                end,
                [new WindowsDiagnosticEvent(start.AddMinutes(1), WindowsEventChannelKind.System, "Provider", -1, WindowsEventSeverity.Error)]));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start,
                end,
                [new WindowsDiagnosticEvent(start.AddMinutes(1), WindowsEventChannelKind.System, " ", 1, WindowsEventSeverity.Error)]));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsRecentErrorEventsValidation.Build(
                start,
                end,
                [CreateEvent(start.AddSeconds(-1), WindowsEventChannelKind.System, WindowsEventSeverity.Error)]));
    }

    [Fact]
    public async Task RecentErrorEventsCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        var sampler = new FixedRecentErrorEventsSampler(
            cancellationAction: token => throw new OperationCanceledException(token));
        var tool = new WindowsRecentErrorEventsObservationTool(sampler);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.ObserveAsync(CreateRequest(tool.Descriptor.Id), cancellation.Token));
    }

    [Fact]
    public async Task RecentErrorEventsFailureIsNormalizedByObservationRuntime()
    {
        var tool = new WindowsRecentErrorEventsObservationTool(
            new FixedRecentErrorEventsSampler(exception: new InvalidOperationException("secret event detail")));
        var runtime = new ObservationRuntime(new ObservationRegistry([tool]));

        var result = await runtime.ObserveAsync(CreateRequest(tool.Descriptor.Id));

        Assert.Equal(ObservationStatus.Failed, result.Status);
        Assert.Equal("observation_failed", result.Failure?.Code);
        Assert.DoesNotContain("secret event detail", result.Failure?.Message);
    }

    [Fact]
    public void RecentErrorEventsDoNotExposeMessageOrEventDataFields()
    {
        var properties = typeof(WindowsRecentErrorEvents).GetProperties()
            .Concat(typeof(WindowsDiagnosticEvent).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Message", properties);
        Assert.DoesNotContain("EventData", properties);
        Assert.DoesNotContain("UserData", properties);
        Assert.DoesNotContain("Xml", properties);
        Assert.DoesNotContain("Path", properties);
    }

    [Fact]
    public void NormalizesProcessCpuAgainstSystemCapacity()
    {
        var cpu = WindowsPerformanceCalculations.CalculateProcessCpuUtilizationPercent(
            firstKernelTicks: 100,
            secondKernelTicks: 120,
            firstUserTicks: 50,
            secondUserTicks: 70,
            systemCpuCapacityDelta: 200);

        Assert.Equal(20, cpu);
    }

    [Fact]
    public void RejectsPidReuseAndBoundsBothProcessRankings()
    {
        var first = Enumerable.Range(1, 13)
            .Select(processId => new NativeProcessSample(
                processId,
                $"process-{processId}.exe",
                CreationTimeTicks: 7,
                KernelTicks: 0,
                UserTicks: 0,
                WorkingSetBytes: (ulong)processId * 1_000))
            .ToArray();
        var second = Enumerable.Range(1, 13)
            .Select(processId => new NativeProcessSample(
                processId,
                $"process-{processId}.exe",
                CreationTimeTicks: processId == 13 ? 8UL : 7UL,
                KernelTicks: (ulong)processId * 5,
                UserTicks: 0,
                WorkingSetBytes: (ulong)(14 - processId) * 1_000))
            .ToArray();

        var result = WindowsTopProcessesBuilder.Build(new WindowsTopProcessesCapture(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            new SystemTimeSample(0, 0, 0),
            new SystemTimeSample(0, 100, 0),
            first,
            second));

        Assert.Equal(10, result.TopCpuProcesses.Count);
        Assert.Equal(10, result.TopMemoryProcesses.Count);
        Assert.DoesNotContain(result.TopCpuProcesses, process => process.ProcessId == 13);
        Assert.DoesNotContain(result.TopMemoryProcesses, process => process.ProcessId == 13);
        Assert.Equal(12, result.TopCpuProcesses[0].ProcessId);
        Assert.Equal(1, result.TopMemoryProcesses[0].ProcessId);
        Assert.Equal(60, result.TopCpuProcesses[0].CpuUtilizationPercent);
    }

    [Fact]
    public void RejectsInvalidPerformanceCalculationsInsteadOfHidingThem()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsPerformanceCalculations.CalculateProcessCpuUtilizationPercent(
                0,
                101,
                0,
                0,
                systemCpuCapacityDelta: 100));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsPerformanceCalculations.CalculateSystemCpuUtilizationPercent(
                10,
                0,
                100,
                100,
                100,
                100));
    }

    private static ObservationRequest CreateRequest(string toolId) =>
        new(Guid.NewGuid(), toolId);

    private static WindowsDiagnosticEvent CreateEvent(
        DateTimeOffset occurredAtUtc,
        WindowsEventChannelKind channel,
        WindowsEventSeverity severity) =>
        new(occurredAtUtc, channel, channel == WindowsEventChannelKind.System ? "System.Provider" : "Application.Provider", 100, severity);

    private sealed class FixedSystemSampler : IWindowsPerformanceSystemSampler
    {
        private readonly WindowsPerformanceSystem _result;

        public FixedSystemSampler(WindowsPerformanceSystem result) => _result = result;

        public Task<WindowsPerformanceSystem> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class FixedTopProcessesSampler : IWindowsTopProcessesSampler
    {
        private readonly WindowsTopProcessesCapture _result;

        public FixedTopProcessesSampler(WindowsTopProcessesCapture result) => _result = result;

        public Task<WindowsTopProcessesCapture> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class FixedRecentErrorEventsSampler : IWindowsRecentErrorEventsSampler
    {
        private readonly WindowsRecentErrorEvents? _result;
        private readonly Action<CancellationToken>? _cancellationAction;
        private readonly Exception? _exception;

        public FixedRecentErrorEventsSampler(
            WindowsRecentErrorEvents? result = null,
            Action<CancellationToken>? cancellationAction = null,
            Exception? exception = null)
        {
            _result = result;
            _cancellationAction = cancellationAction;
            _exception = exception;
        }

        public Task<WindowsRecentErrorEvents> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cancellationAction is not null)
            {
                _cancellationAction(cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeObservationTool : IObservationTool
    {
        private readonly Func<ObservationRequest, CancellationToken, Task<ObservationResult>> _observe;

        public FakeObservationTool(
            string id,
            Action<CancellationToken>? cancellationAction = null,
            Exception? exception = null,
            ObservationResult? result = null)
        {
            Descriptor = new ObservationToolDescriptor(id, id, "Test observation tool.");
            _observe = cancellationAction is not null
                ? (_, cancellationToken) =>
                {
                    cancellationAction(cancellationToken);
                    return Task.FromResult(result ?? CreateSuccessResult(id, Guid.Empty));
                }
                : exception is not null
                    ? (_, _) => Task.FromException<ObservationResult>(exception)
                    : (_, _) => Task.FromResult(result ?? CreateSuccessResult(id, Guid.Empty));
        }

        public ObservationToolDescriptor Descriptor { get; }

        public int InvocationCount { get; private set; }

        public async Task<ObservationResult> ObserveAsync(
            ObservationRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            var result = await _observe(request, cancellationToken);
            return result.RequestId == Guid.Empty
                ? result with { RequestId = request.RequestId }
                : result;
        }

        private static ObservationResult CreateSuccessResult(string toolId, Guid requestId) =>
            new(requestId, toolId, DateTimeOffset.UtcNow, ObservationStatus.Succeeded);
    }
}
