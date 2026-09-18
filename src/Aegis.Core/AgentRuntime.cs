namespace Aegis.Core;

public sealed class AgentRuntime
{
    private const int MaximumObservationExecutions = 3;

    private readonly IInvestigationPlanner _planner;
    private readonly ILanguageModel _languageModel;
    private readonly ObservationRuntime _observationRuntime;
    private readonly IInvestigationHistoryStore? _historyStore;
    private readonly Action<Exception> _logDiagnostic;

    public AgentRuntime(
        IInvestigationPlanner planner,
        ILanguageModel languageModel,
        ObservationRuntime observationRuntime,
        IInvestigationHistoryStore? historyStore = null,
        Action<Exception>? logDiagnostic = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _observationRuntime = observationRuntime ?? throw new ArgumentNullException(nameof(observationRuntime));
        _historyStore = historyStore;
        _logDiagnostic = logDiagnostic ?? (_ => { });
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
        var investigationId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var state = new InvestigationState(
            investigationId,
            normalizedQuestion,
            normalizedQuestion,
            createdAtUtc,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            InvestigationLifecycleStatus.Created,
            CurrentPlan: null,
            PlanHistory: Array.Empty<InvestigationPlanHistoryEntry>(),
            Steps: Array.Empty<InvestigationStep>(),
            StepExecutions: Array.Empty<InvestigationStepExecution>(),
            Evidence: Array.Empty<InvestigationEvidence>(),
            AvailableTools: _observationRuntime.Descriptors.ToArray(),
            Budget: new InvestigationBudget(MaximumObservationExecutions, 0),
            ReplanCount: 0,
            InvestigationExecutionPhase.Planning,
            Outcome: null);

        if (_historyStore is not null)
        {
            await _historyStore.CreateAsync(ToInvestigation(state), cancellationToken);
        }

        var running = false;
        var terminalCommitAttempted = false;

        try
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            if (_historyStore is not null)
            {
                await _historyStore.MarkRunningAsync(
                    investigationId,
                    startedAtUtc,
                    cancellationToken);
            }

            state = state with
            {
                StartedAtUtc = startedAtUtc,
                LifecycleStatus = InvestigationLifecycleStatus.Running
            };
            running = true;

            var initialPlan = await CreateAndValidatePlanAsync(state, cancellationToken);
            state = await ApplyPlanAsync(state, initialPlan, cancellationToken);
            state = await ExecutePlanAsync(state, initialPlan, cancellationToken);

            if (state.Budget.ObservationsUsed < state.Budget.MaximumObservationExecutions)
            {
                state = state with
                {
                    ReplanCount = 1,
                    ExecutionPhase = InvestigationExecutionPhase.Replanning
                };
                var revisedPlan = await CreateAndValidatePlanAsync(state, cancellationToken);
                state = await ApplyPlanAsync(state, revisedPlan, cancellationToken);
                state = await ExecutePlanAsync(state, revisedPlan, cancellationToken);
            }

            var answer = await FinalizeAsync(
                state with { ExecutionPhase = InvestigationExecutionPhase.Finalizing },
                cancellationToken);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var completedOutcome = new InvestigationOutcome(FinalAnswer: answer);
            state = state with
            {
                CompletedAtUtc = completedAtUtc,
                LifecycleStatus = InvestigationLifecycleStatus.Completed,
                Outcome = completedOutcome
            };

            terminalCommitAttempted = true;
            await CommitTerminalOutcomeAsync(
                state,
                InvestigationLifecycleStatus.Completed,
                completedOutcome,
                completedAtUtc);

            return new AgentRunResult(investigationId, answer);
        }
        catch (OperationCanceledException)
        {
            if (running && !terminalCommitAttempted && _historyStore is not null)
            {
                terminalCommitAttempted = true;
                try
                {
                    await CommitTerminalOutcomeAsync(
                        state,
                        InvestigationLifecycleStatus.Cancelled,
                        new InvestigationOutcome(FailureCode: "cancelled", FailureMessage: "The investigation was cancelled."),
                        DateTimeOffset.UtcNow);
                }
                catch (Exception persistenceException)
                {
                    LogDiagnostic(persistenceException);
                }
            }

            throw;
        }
        catch (Exception exception)
        {
            if (running && !terminalCommitAttempted && _historyStore is not null)
            {
                terminalCommitAttempted = true;
                try
                {
                    await CommitTerminalOutcomeAsync(
                        state,
                        InvestigationLifecycleStatus.Failed,
                        new InvestigationOutcome(
                            FailureCode: "investigation_failed",
                            FailureMessage: "The investigation could not be completed."),
                        DateTimeOffset.UtcNow);
                }
                catch (InvestigationPersistenceException persistenceException)
                {
                    throw new InvestigationPersistenceException(
                        "The investigation failed and its terminal outcome could not be persisted.",
                        persistenceException,
                        exception);
                }
            }

            throw;
        }
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

    private async Task<InvestigationState> ApplyPlanAsync(
        InvestigationState state,
        InvestigationPlan plan,
        CancellationToken cancellationToken)
    {
        var planEntry = new InvestigationPlanHistoryEntry(
            state.PlanHistory.Count,
            DateTimeOffset.UtcNow,
            plan);
        var updatedState = state with
        {
            CurrentPlan = plan,
            PlanHistory = state.PlanHistory.Concat([planEntry]).ToArray(),
            Steps = state.Steps.Concat(plan.Steps).ToArray(),
            ExecutionPhase = InvestigationExecutionPhase.Executing
        };

        if (_historyStore is not null)
        {
            await _historyStore.AppendPlanAsync(
                state.InvestigationId,
                planEntry,
                cancellationToken);
        }

        return updatedState;
    }

    private async Task<InvestigationState> ExecutePlanAsync(
        InvestigationState state,
        InvestigationPlan plan,
        CancellationToken cancellationToken)
    {
        var planSequence = state.PlanHistory[^1].PlanSequence;
        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var planStep = plan.Steps[stepIndex];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                await TryMarkRemainingStepsSkippedAsync(state, plan, planSequence, stepIndex);
                throw;
            }

            if (state.Budget.ObservationsUsed >= state.Budget.MaximumObservationExecutions)
            {
                throw new AgentRuntimeException("The observation budget has been exhausted.");
            }

            var requestedAtUtc = DateTimeOffset.UtcNow;
            var request = new ObservationRequest(
                Guid.NewGuid(),
                planStep.ToolId,
                requestedAtUtc);
            ObservationResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = state with
                {
                    Budget = state.Budget with
                    {
                        ObservationsUsed = state.Budget.ObservationsUsed + 1
                    }
                };
                result = await _observationRuntime.ObserveAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var cancelledExecution = CreateExecution(
                    state,
                    planSequence,
                    planStep,
                    request,
                    InvestigationStepStatus.Cancelled,
                    result: null);
                state = ApplyExecution(state, cancelledExecution);
                await TryPersistCancelledExecutionAsync(state.InvestigationId, cancelledExecution);
                await TryMarkRemainingStepsSkippedAsync(state, plan, planSequence, stepIndex + 1);
                throw;
            }
            catch (UnknownObservationToolException exception)
            {
                var failedExecution = CreateExecution(
                    state,
                    planSequence,
                    planStep,
                    request,
                    InvestigationStepStatus.Failed,
                    result: null);
                state = ApplyExecution(state, failedExecution);
                await PersistExecutionAsync(state.InvestigationId, failedExecution, CancellationToken.None);
                await TryMarkRemainingStepsSkippedAsync(state, plan, planSequence, stepIndex + 1);
                throw new AgentRuntimeException(
                    "The requested observation tool is not available.",
                    exception);
            }

            var status = result.Status == ObservationStatus.Succeeded
                ? InvestigationStepStatus.Completed
                : InvestigationStepStatus.Failed;
            var execution = CreateExecution(
                state,
                planSequence,
                planStep,
                request,
                status,
                result);
            state = ApplyExecution(state, execution);
            state = state with
            {
                Evidence = state.Evidence
                    .Concat([new InvestigationEvidence(planStep.StepId, result)])
                    .ToArray()
            };
            await PersistExecutionAsync(state.InvestigationId, execution, CancellationToken.None);
        }

        return state;
    }

    private async Task<string> FinalizeAsync(
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

        return finalAnswer.Answer.Trim();
    }

    private async Task CommitTerminalOutcomeAsync(
        InvestigationState state,
        InvestigationLifecycleStatus terminalStatus,
        InvestigationOutcome outcome,
        DateTimeOffset completedAtUtc)
    {
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            var committed = await _historyStore.CommitTerminalOutcomeAsync(
                state.InvestigationId,
                terminalStatus,
                outcome,
                completedAtUtc,
                CancellationToken.None);
            if (!committed)
            {
                throw new InvalidOperationException("The investigation was not in a running state.");
            }
        }
        catch (InvestigationPersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvestigationPersistenceException(
                "The investigation terminal outcome could not be persisted.",
                exception);
        }
    }

    private async Task PersistExecutionAsync(
        Guid investigationId,
        InvestigationStepExecution execution,
        CancellationToken cancellationToken)
    {
        if (_historyStore is not null)
        {
            await _historyStore.AppendStepExecutionAsync(
                investigationId,
                execution,
                cancellationToken);
        }
    }

    private async Task TryPersistCancelledExecutionAsync(
        Guid investigationId,
        InvestigationStepExecution execution)
    {
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            await _historyStore.AppendStepExecutionAsync(
                    investigationId,
                    execution,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDiagnostic(exception);
        }
    }

    private async Task TryMarkRemainingStepsSkippedAsync(
        InvestigationState state,
        InvestigationPlan plan,
        int planSequence,
        int firstRemainingIndex)
    {
        var remainingStepIds = plan.Steps
            .Skip(firstRemainingIndex)
            .Select(step => step.StepId)
            .ToArray();
        if (remainingStepIds.Length == 0 || _historyStore is null)
        {
            return;
        }

        try
        {
            await _historyStore.MarkStepsSkippedAsync(
                state.InvestigationId,
                planSequence,
                remainingStepIds,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDiagnostic(exception);
        }
    }

    private InvestigationStepExecution CreateExecution(
        InvestigationState state,
        int planSequence,
        InvestigationStep step,
        ObservationRequest request,
        InvestigationStepStatus status,
        ObservationResult? result) =>
        new(
            planSequence,
            step.StepId,
            step.ToolId,
            request.RequestId,
            request.RequestedAtUtc ?? DateTimeOffset.UtcNow,
            result?.ObservedAtUtc,
            _observationRuntime.Descriptors
                .First(descriptor => string.Equals(descriptor.Id, step.ToolId, StringComparison.Ordinal))
                .ContractVersion,
            status,
            result);

    private static InvestigationState ApplyExecution(
        InvestigationState state,
        InvestigationStepExecution execution)
    {
        var updatedSteps = UpdateStepStatus(state.Steps, execution.StepId, execution.Status);
        var updatedCurrentPlan = state.CurrentPlan is null
            ? null
            : state.CurrentPlan with
            {
                Steps = UpdateStepStatus(state.CurrentPlan.Steps, execution.StepId, execution.Status)
            };
        return state with
        {
            CurrentPlan = updatedCurrentPlan,
            Steps = updatedSteps,
            StepExecutions = state.StepExecutions.Concat([execution]).ToArray()
        };
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

    private static Investigation ToInvestigation(InvestigationState state) =>
        new(
            state.InvestigationId,
            state.Question,
            state.Objective,
            state.CreatedAtUtc,
            state.StartedAtUtc,
            state.CompletedAtUtc,
            state.LifecycleStatus,
            state.Outcome,
            state.PlanHistory,
            state.StepExecutions);

    private void LogDiagnostic(Exception exception)
    {
        try
        {
            _logDiagnostic(exception);
        }
        catch
        {
            // Diagnostics must never replace the investigation outcome.
        }
    }

}
