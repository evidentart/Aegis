namespace Aegis.Core;

public enum ObservationStatus
{
    Succeeded,
    Failed
}

public sealed record ObservationRequest(
    Guid RequestId,
    string ToolId,
    DateTimeOffset? RequestedAtUtc = null);

public sealed record ObservationToolDescriptor(
    string Id,
    string Name,
    string Description,
    string ContractVersion = "1.0");

public interface IObservationData
{
}

public sealed record ObservationFailure(
    string Code,
    string Message);

public sealed record ObservationResult(
    Guid RequestId,
    string ToolId,
    DateTimeOffset ObservedAtUtc,
    ObservationStatus Status,
    IObservationData? Data = null,
    ObservationFailure? Failure = null);

public interface IObservationTool
{
    ObservationToolDescriptor Descriptor { get; }

    Task<ObservationResult> ObserveAsync(
        ObservationRequest request,
        CancellationToken cancellationToken = default);
}
