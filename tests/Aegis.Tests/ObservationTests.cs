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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
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
    public void RequestHasNoArbitraryArgumentMechanism()
    {
        var propertyNames = typeof(ObservationRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["RequestId", "ToolId", "RequestedAtUtc"], propertyNames);
        Assert.DoesNotContain("Arguments", propertyNames);
    }

    private static ObservationRequest CreateRequest(string toolId) =>
        new(Guid.NewGuid(), toolId);

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
