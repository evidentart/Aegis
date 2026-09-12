namespace Aegis.Core;

public sealed class AgentRuntime
{
    private const int MaximumObservationExecutions = 3;

    private readonly IInvestigationPlanner _planner;
    private readonly ILanguageModel _languageModel;
    private readonly ObservationRuntime _observationRuntime;

    public AgentRuntime(
        IInvestigationPlanner planner,
        ILanguageModel languageModel,
        ObservationRuntime observationRuntime)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _observationRuntime = observationRuntime ?? throw new ArgumentNullException(nameof(observationRuntime));
    }

    public async Task<AgentRunResult> RunAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A question is required.", nameof(question));
        }

        var normalizedQuestion = question.Trim();
        var state = new InvestigationState(
            normalizedQuestion,
            normalizedQuestion,
            CurrentPlan: null,
            Steps: Array.Empty<InvestigationStep>(),
            Evidence: Array.Empty<InvestigationEvidence>(),
            AvailableTools: _observationRuntime.Descriptors.ToArray(),
            Budget: new InvestigationBudget(MaximumObservationExecutions, 0),
            ReplanCount: 0,
            Status: InvestigationStatus.Planning);

        var initialPlan = await CreateAndValidatePlanAsync(state, cancellationToken);
        state = ApplyPlan(state, initialPlan);
        state = await ExecutePlanAsync(state, initialPlan, cancellationToken);

        if (state.Budget.ObservationsUsed < state.Budget.MaximumObservationExecutions)
        {
            state = state with
            {
                ReplanCount = 1,
                Status = InvestigationStatus.Replanning
            };
            var revisedPlan = await CreateAndValidatePlanAsync(state, cancellationToken);
            state = ApplyPlan(state, revisedPlan);
            state = await ExecutePlanAsync(state, revisedPlan, cancellationToken);
        }

        return await FinalizeAsync(
            state with { Status = InvestigationStatus.Finalizing },
            cancellationToken);
    }

    private async Task<InvestigationPlan> CreateAndValidatePlanAsync(
        InvestigationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = await _planner.CreatePlanAsync(state, cancellationToken);
        if (decision is null)
        {
            throw new AgentRuntimeException("The planner returned no decision.");
        }

        ValidatePlan(decision.Plan, state);
        return decision.Plan;
    }

    private void ValidatePlan(
        InvestigationPlan plan,
        InvestigationState state)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.Objective))
        {
            throw new AgentRuntimeException("The planner returned an invalid investigation objective.");
        }

        if (!string.Equals(plan.Objective, plan.Objective.Trim(), StringComparison.Ordinal))
        {
            throw new AgentRuntimeException("The planner returned an invalid investigation objective.");
        }

        if (!string.Equals(plan.Objective, state.Objective, StringComparison.Ordinal))
        {
            throw new AgentRuntimeException(
                "The investigation plan objective must match the authoritative investigation objective.");
        }

        if (plan.Steps is null || plan.Steps.Count == 0)
        {
            throw new AgentRuntimeException("The planner returned an empty investigation plan.");
        }

        var remainingBudget = state.Budget.MaximumObservationExecutions - state.Budget.ObservationsUsed;
        if (plan.Steps.Count > remainingBudget)
        {
            throw new AgentRuntimeException("The investigation plan exceeds the remaining observation budget.");
        }

        var knownStepIds = state.Steps
            .Select(step => step.StepId)
            .ToHashSet(StringComparer.Ordinal);
        var planStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (step is null ||
                string.IsNullOrWhiteSpace(step.StepId) ||
                !string.Equals(step.StepId, step.StepId.Trim(), StringComparison.Ordinal) ||
                !planStepIds.Add(step.StepId) ||
                !knownStepIds.Add(step.StepId))
            {
                throw new AgentRuntimeException("The investigation plan contains an invalid or duplicate step ID.");
            }

            if (string.IsNullOrWhiteSpace(step.ToolId) ||
                !string.Equals(step.ToolId, step.ToolId.Trim(), StringComparison.Ordinal) ||
                step.Status != InvestigationStepStatus.Pending)
            {
                throw new AgentRuntimeException("The investigation plan contains an invalid step.");
            }

            if (!_observationRuntime.Descriptors.Any(descriptor =>
                    string.Equals(descriptor.Id, step.ToolId, StringComparison.Ordinal)))
            {
                throw new AgentRuntimeException(
                    $"The language model requested an unavailable observation tool '{step.ToolId}'.");
            }
        }
    }

    private static InvestigationState ApplyPlan(
        InvestigationState state,
        InvestigationPlan plan)
    {
        var allSteps = state.Steps
            .Concat(plan.Steps)
            .ToArray();
        return state with
        {
            CurrentPlan = plan,
            Steps = allSteps,
            Status = InvestigationStatus.Executing
        };
    }

    private async Task<InvestigationState> ExecutePlanAsync(
        InvestigationState state,
        InvestigationPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var planStep in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Budget.ObservationsUsed >= state.Budget.MaximumObservationExecutions)
            {
                throw new AgentRuntimeException("The observation budget has been exhausted.");
            }

            var request = new ObservationRequest(
                Guid.NewGuid(),
                planStep.ToolId,
                DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            state = state with
            {
                Budget = state.Budget with
                {
                    ObservationsUsed = state.Budget.ObservationsUsed + 1
                }
            };

            ObservationResult result;
            try
            {
                result = await _observationRuntime.ObserveAsync(request, cancellationToken);
            }
            catch (UnknownObservationToolException exception)
            {
                throw new AgentRuntimeException(
                    "The requested observation tool is not available.",
                    exception);
            }

            var status = result.Status == ObservationStatus.Succeeded
                ? InvestigationStepStatus.Completed
                : InvestigationStepStatus.Failed;
            state = state with
            {
                Steps = UpdateStepStatus(state.Steps, planStep.StepId, status),
                Evidence = state.Evidence
                    .Concat([new InvestigationEvidence(planStep.StepId, result)])
                    .ToArray()
            };
        }

        return state;
    }

    private async Task<AgentRunResult> FinalizeAsync(
        InvestigationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = await _languageModel.CompleteAsync(
            InvestigationModelContext.BuildFinalRequest(state),
            cancellationToken);
        if (decision is null)
        {
            throw new AgentRuntimeException("The language model returned no final decision.");
        }

        if (decision is not FinalAnswerDecision finalAnswer)
        {
            throw new AgentRuntimeException("The language model did not return a final answer.");
        }

        if (string.IsNullOrWhiteSpace(finalAnswer.Answer))
        {
            throw new AgentRuntimeException("The language model returned an empty final answer.");
        }

        return new AgentRunResult(finalAnswer.Answer.Trim());
    }

    private static IReadOnlyList<InvestigationStep> UpdateStepStatus(
        IReadOnlyList<InvestigationStep> steps,
        string stepId,
        InvestigationStepStatus status) =>
        steps
            .Select(step => string.Equals(step.StepId, stepId, StringComparison.Ordinal)
                ? step with { Status = status }
                : step)
            .ToArray();
}
