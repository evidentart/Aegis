namespace Aegis.Core;

public sealed record InvestigationSummary(
    Guid InvestigationId,
    string Question,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    InvestigationLifecycleStatus LifecycleStatus,
    string? FinalAnswer)
{
    public string DisplayText =>
        $"{CreatedAtUtc.LocalDateTime:g}  {LifecycleStatus}  {Question}";
}

public sealed record InvestigationDetails(Investigation Investigation);

public sealed record Baseline(
    Guid BaselineId,
    Guid SourceInvestigationId,
    string SourceStepId,
    string ToolId,
    DateTimeOffset CreatedAtUtc,
    ObservationResult Observation);

public sealed record BaselineSummary(
    Guid BaselineId,
    Guid SourceInvestigationId,
    string ToolId,
    DateTimeOffset CreatedAtUtc,
    string Platform,
    string OsVersion,
    int? Build,
    string Architecture)
{
    public string DisplayText =>
        $"{CreatedAtUtc.LocalDateTime:g}  {Platform} {OsVersion}  {Architecture}";
}

public interface IInvestigationHistoryStore
{
    Task CreateAsync(
        Investigation investigation,
        CancellationToken cancellationToken = default);

    Task MarkRunningAsync(
        Guid investigationId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task AppendPlanAsync(
        Guid investigationId,
        InvestigationPlanHistoryEntry plan,
        CancellationToken cancellationToken = default);

    Task AppendStepExecutionAsync(
        Guid investigationId,
        InvestigationStepExecution execution,
        CancellationToken cancellationToken = default);

    Task MarkStepsSkippedAsync(
        Guid investigationId,
        int planSequence,
        IReadOnlyList<string> stepIds,
        CancellationToken cancellationToken = default);

    Task<bool> CommitTerminalOutcomeAsync(
        Guid investigationId,
        InvestigationLifecycleStatus terminalStatus,
        InvestigationOutcome outcome,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvestigationSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<InvestigationDetails?> GetAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default);
}

public interface IBaselineStore
{
    Task CreateAsync(
        Baseline baseline,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BaselineSummary>> ListBaselinesAsync(
        CancellationToken cancellationToken = default);
}
