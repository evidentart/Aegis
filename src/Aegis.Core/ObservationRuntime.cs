namespace Aegis.Core;

public sealed class ObservationRegistry
{
    private readonly IReadOnlyDictionary<string, IObservationTool> _tools;

    public ObservationRegistry(IEnumerable<IObservationTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var registeredTools = new Dictionary<string, IObservationTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var descriptor = tool.Descriptor ?? throw new ArgumentException("An observation tool descriptor is required.", nameof(tools));
            if (string.IsNullOrWhiteSpace(descriptor.Id))
            {
                throw new ArgumentException("Observation tool IDs must be non-empty.", nameof(tools));
            }

            if (!string.Equals(descriptor.Id, descriptor.Id.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Observation tool IDs must not contain leading or trailing whitespace.", nameof(tools));
            }

            if (!registeredTools.TryAdd(descriptor.Id, tool))
            {
                throw new ArgumentException($"Observation tool ID '{descriptor.Id}' is already registered.", nameof(tools));
            }
        }

        _tools = registeredTools;
        Descriptors = Array.AsReadOnly(
            registeredTools.Values
                .Select(tool => tool.Descriptor)
                .ToArray());
    }

    public IReadOnlyList<ObservationToolDescriptor> Descriptors { get; }

    public bool TryGet(string toolId, out IObservationTool? tool)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            tool = null;
            return false;
        }

        return _tools.TryGetValue(toolId, out tool);
    }
}

public sealed class UnknownObservationToolException : Exception
{
    public UnknownObservationToolException(string toolId)
        : base($"Unknown observation tool: {toolId}")
    {
    }
}

public sealed class ObservationRuntime
{
    private readonly ObservationRegistry _registry;
    private readonly Action<Exception> _logDiagnostic;

    public ObservationRuntime(
        ObservationRegistry registry,
        Action<Exception>? logDiagnostic = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logDiagnostic = logDiagnostic ?? (_ => { });
    }

    public IReadOnlyList<ObservationToolDescriptor> Descriptors => _registry.Descriptors;

    public async Task<ObservationResult> ObserveAsync(
        ObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        if (!_registry.TryGet(request.ToolId, out var tool) || tool is null)
        {
            throw new UnknownObservationToolException(request.ToolId);
        }

        try
        {
            var result = await tool.ObserveAsync(request, cancellationToken);
            if (result is null)
            {
                throw new InvalidOperationException("The observation tool returned no result.");
            }

            if (result.RequestId != request.RequestId ||
                !string.Equals(result.ToolId, request.ToolId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The observation result did not match the request.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                _logDiagnostic(exception);
            }
            catch
            {
                // Diagnostics must never replace the normalized observation failure.
            }

            return new ObservationResult(
                request.RequestId,
                request.ToolId,
                DateTimeOffset.UtcNow,
                ObservationStatus.Failed,
                Failure: new ObservationFailure(
                    "observation_failed",
                    "The observation could not be completed."));
        }
    }

    private static void ValidateRequest(ObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("An observation request ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ToolId) ||
            !string.Equals(request.ToolId, request.ToolId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid observation tool ID is required.", nameof(request));
        }
    }
}
