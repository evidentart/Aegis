namespace Aegis.Core;

public enum InvestigationStepStatus
{
    Pending,
    Completed,
    Failed,
    Skipped,
    Invalidated,
    Cancelled
}

public sealed record InvestigationStep(
    string StepId,
    string ToolId,
    InvestigationStepStatus Status = InvestigationStepStatus.Pending);

public sealed record InvestigationPlan(
    string Objective,
    IReadOnlyList<InvestigationStep> Steps);

public sealed record InvestigationEvidence(
    string StepId,
    ObservationResult Result);

public sealed record InvestigationPlanHistoryEntry(
    int PlanSequence,
    DateTimeOffset AcceptedAtUtc,
    InvestigationPlan Plan);

public sealed record InvestigationStepExecution(
    int PlanSequence,
    string StepId,
    string ToolId,
    Guid RequestId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ObservedAtUtc,
    string ToolContractVersion,
    InvestigationStepStatus Status,
    ObservationResult? Result)
{
    public string DisplayText =>
        $"Plan {PlanSequence}, {StepId}, {ToolId}, {Status}";
}

public sealed record InvestigationBudget(
    int MaximumObservationExecutions,
    int ObservationsUsed);

public enum InvestigationLifecycleStatus
{
    Created,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum InvestigationExecutionPhase
{
    Planning,
    Executing,
    Replanning,
    Finalizing
}

public sealed record InvestigationOutcome(
    string? FinalAnswer = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    InvestigationReport? Report = null);

public sealed record Investigation(
    Guid InvestigationId,
    string Question,
    string Objective,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    InvestigationLifecycleStatus LifecycleStatus,
    InvestigationOutcome? Outcome,
    IReadOnlyList<InvestigationPlanHistoryEntry> Plans,
    IReadOnlyList<InvestigationStepExecution> StepExecutions);

public sealed record InvestigationState(
    Guid InvestigationId,
    string Question,
    string Objective,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    InvestigationLifecycleStatus LifecycleStatus,
    InvestigationPlan? CurrentPlan,
    IReadOnlyList<InvestigationPlanHistoryEntry> PlanHistory,
    IReadOnlyList<InvestigationStep> Steps,
    IReadOnlyList<InvestigationStepExecution> StepExecutions,
    IReadOnlyList<InvestigationEvidence> Evidence,
    IReadOnlyList<ObservationToolDescriptor> AvailableTools,
    InvestigationBudget Budget,
    int ReplanCount,
    InvestigationExecutionPhase ExecutionPhase,
    InvestigationOutcome? Outcome);
