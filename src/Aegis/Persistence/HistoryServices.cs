using Aegis.Core;

namespace Aegis.Persistence;

public sealed class InvestigationHistoryService
{
    private readonly IInvestigationHistoryStore _store;

    public InvestigationHistoryService(IInvestigationHistoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IReadOnlyList<InvestigationSummary>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _store.ListAsync(cancellationToken);

    public Task<InvestigationDetails?> GetAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default) =>
        _store.GetAsync(investigationId, cancellationToken);

    public Task<InvestigationDeletionResult> DeleteInvestigationAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default) =>
        _store.DeleteInvestigationAsync(investigationId, cancellationToken);

    public Task<ClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default) =>
        _store.ClearHistoryAsync(cancellationToken);

    public Task<ClearSavedHistoryAndBaselinesResult> ClearSavedHistoryAndBaselinesAsync(
        CancellationToken cancellationToken = default) =>
        _store.ClearSavedHistoryAndBaselinesAsync(cancellationToken);
}

public sealed class BaselineService
{
    private readonly IInvestigationHistoryStore _historyStore;
    private readonly IBaselineStore _baselineStore;
    private readonly WindowsSystemInfoBaselineMapper _mapper;

    public BaselineService(
        IInvestigationHistoryStore historyStore,
        IBaselineStore baselineStore,
        ObservationRegistry observationRegistry)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _baselineStore = baselineStore ?? throw new ArgumentNullException(nameof(baselineStore));
        _mapper = new WindowsSystemInfoBaselineMapper(observationRegistry);
    }

    public Task<IReadOnlyList<BaselineSummary>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _baselineStore.ListBaselinesAsync(cancellationToken);

    public Task<BaselineDeletionResult> DeleteBaselineAsync(
        Guid baselineId,
        CancellationToken cancellationToken = default) =>
        _baselineStore.DeleteBaselineAsync(baselineId, cancellationToken);

    public async Task CreateFromStepAsync(
        Guid investigationId,
        string stepId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            throw new ArgumentException("A step ID is required.", nameof(stepId));
        }

        var details = await _historyStore.GetAsync(investigationId, cancellationToken);
        if (details is null)
        {
            throw new InvalidOperationException("The investigation could not be found.");
        }

        if (details.Investigation.LifecycleStatus != InvestigationLifecycleStatus.Completed)
        {
            throw new InvalidOperationException("Only completed investigations can create baselines.");
        }

        var execution = details.Investigation.StepExecutions
            .SingleOrDefault(candidate => string.Equals(candidate.StepId, stepId, StringComparison.Ordinal));
        if (execution is null ||
            execution.Status != InvestigationStepStatus.Completed ||
            execution.Result is null ||
            !details.Investigation.Plans.Any(plan =>
                plan.PlanSequence == execution.PlanSequence &&
                plan.Plan.Steps.Any(step =>
                    string.Equals(step.StepId, execution.StepId, StringComparison.Ordinal) &&
                    string.Equals(step.ToolId, execution.ToolId, StringComparison.Ordinal) &&
                    step.Status == InvestigationStepStatus.Completed)))
        {
            throw new InvalidOperationException(
                "The selected step has no completed persisted observation execution.");
        }

        await _baselineStore.CreateAsync(
            _mapper.Map(investigationId, execution),
            cancellationToken);
    }
}

public sealed class WindowsSystemInfoBaselineMapper
{
    private readonly ObservationRegistry _observationRegistry;

    public WindowsSystemInfoBaselineMapper(ObservationRegistry observationRegistry)
    {
        _observationRegistry = observationRegistry ?? throw new ArgumentNullException(nameof(observationRegistry));
    }

    public Baseline Map(Guid investigationId, InvestigationStepExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Status != InvestigationStepStatus.Completed ||
            execution.Result is not { } result ||
            !_observationRegistry.TryGet(result.ToolId, out var tool) ||
            tool is null ||
            !tool.Descriptor.BaselineEligible)
        {
            throw new InvalidOperationException("The selected observation is not baseline-eligible.");
        }

        if (!string.Equals(result.ToolId, WindowsSystemInfoObservationTool.ToolId, StringComparison.Ordinal) ||
            result.Status != ObservationStatus.Succeeded ||
            result.Data is not WindowsSystemInfo)
        {
            throw new InvalidOperationException("Only approved Windows system information can be baselined.");
        }

        return new Baseline(
            Guid.NewGuid(),
            investigationId,
            execution.StepId,
            result.ToolId,
            DateTimeOffset.UtcNow,
            result);
    }
}
