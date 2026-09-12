namespace Aegis.Core;

public enum InvestigationStepStatus
{
    Pending,
    Completed,
    Failed,
    Skipped,
    Invalidated
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

public sealed record InvestigationBudget(
    int MaximumObservationExecutions,
    int ObservationsUsed);

public enum InvestigationStatus
{
    Planning,
    Executing,
    Replanning,
    Finalizing,
    Completed,
    Failed
}

public sealed record InvestigationState(
    string Question,
    string Objective,
    InvestigationPlan? CurrentPlan,
    IReadOnlyList<InvestigationStep> Steps,
    IReadOnlyList<InvestigationEvidence> Evidence,
    IReadOnlyList<ObservationToolDescriptor> AvailableTools,
    InvestigationBudget Budget,
    int ReplanCount,
    InvestigationStatus Status);
