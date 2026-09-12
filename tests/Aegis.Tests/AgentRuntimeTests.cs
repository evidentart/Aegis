using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class AgentRuntimeTests
{
    [Fact]
    public async Task InitialPlanningUsesOnlyRegisteredCapabilityContext()
    {
        var model = new FakeLanguageModel(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")),
            new FinalAnswerDecision("Final answer."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(new LanguageModelInvestigationPlanner(model), model, tool);

        await runtime.RunAsync("Question");

        var firstMessage = model.Requests[0].Messages[0];
        Assert.Equal(LanguageModelMessageRole.System, firstMessage.Role);
        Assert.Contains("tool.one", firstMessage.Content);
        Assert.Contains("Test observation tool.", firstMessage.Content);
        Assert.DoesNotContain("ExecuteCommand", firstMessage.Content);
        Assert.DoesNotContain("PowerShell", firstMessage.Content);
        Assert.DoesNotContain("RunPowerShell", firstMessage.Content);
    }

    [Fact]
    public async Task ExecutesOneBoundedAdaptiveCycleWithPriorEvidence()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationPlanDecision(CreatePlan("step-2")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("The revised answer."));
        var tool = new FakeObservationTool(
            "tool.one",
            data: new TestObservationData("observed value"));
        var runtime = CreateRuntime(planner, model, tool);

        var result = await runtime.RunAsync("Question");

        Assert.Equal("The revised answer.", result.Answer);
        Assert.Equal(2, planner.States.Count);
        Assert.Equal("Question", planner.States[0].Objective);
        Assert.Empty(planner.States[0].Evidence);
        Assert.Equal(0, planner.States[0].Budget.ObservationsUsed);
        Assert.Equal(0, planner.States[0].ReplanCount);
        Assert.Single(planner.States[1].Evidence);
        Assert.Equal(1, planner.States[1].Budget.ObservationsUsed);
        Assert.Equal(1, planner.States[1].ReplanCount);
        Assert.Equal("Question", planner.States[1].Objective);
        Assert.Equal(InvestigationStepStatus.Completed, planner.States[1].Steps[0].Status);
        Assert.Equal(2, tool.InvocationCount);
        Assert.Single(model.Requests);
        Assert.Contains(model.Requests[0].Messages, message =>
            message.Role == LanguageModelMessageRole.Observation &&
            message.Content.Contains("observed value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutesExactRegisteredToolsInOrder()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan(
                ["step-1", "step-2", "step-3"],
                "windows.system.info")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Final answer."));
        var tool = new FakeObservationTool("windows.system.info");
        var runtime = CreateRuntime(planner, model, tool);

        await runtime.RunAsync("Question");

        Assert.Equal(3, tool.InvocationCount);
        Assert.All(tool.Requests, request =>
            Assert.Equal("windows.system.info", request.ToolId));
        Assert.Equal(1, tool.MaximumConcurrentInvocations);
    }

    [Fact]
    public async Task RejectsUnknownToolWithoutInvokingAnyTool()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan(["step-1"], "unknown.tool")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("windows.system.info");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task RejectsEmptyPlanWithoutInvokingAnyTool()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(new InvestigationPlan("Question", [])));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RejectsPlanWithDifferentObjective()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(new InvestigationPlan(
                "Different objective",
                [new InvestigationStep("step-1", "tool.one")] )));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RejectsDuplicateStepIdsWithoutPartialExecution()
    {
        var plan = new InvestigationPlan(
            "Question",
            [
                new InvestigationStep("step-1", "tool.one"),
                new InvestigationStep("step-1", "tool.one")
            ]);
        var planner = new FakePlanner(new InvestigationPlanDecision(plan));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RejectsPlanThatExceedsRemainingBudget()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3", "step-4")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RejectsPlannerSuppliedStepStatus()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(new InvestigationPlan(
                "Question",
                [new InvestigationStep("step-1", "tool.one", InvestigationStepStatus.Completed)])));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task ReturnsObservationFailureAsEvidenceDuringReplan()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationPlanDecision(CreatePlan("step-2")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("The observation was unavailable."));
        var tool = new FakeObservationTool(
            "tool.one",
            result: new ObservationResult(
                Guid.Empty,
                "tool.one",
                DateTimeOffset.UtcNow,
                ObservationStatus.Failed,
                Failure: new ObservationFailure("observation_failed", "Safe failure.")));
        var runtime = CreateRuntime(planner, model, tool);

        await runtime.RunAsync("Question");

        Assert.Single(planner.States[1].Evidence);
        Assert.Equal(ObservationStatus.Failed, planner.States[1].Evidence[0].Result.Status);
        Assert.Equal(InvestigationStepStatus.Failed, planner.States[1].Steps[0].Status);
        Assert.Contains(model.Requests[0].Messages, message =>
            message.Role == LanguageModelMessageRole.Observation &&
            message.Content.Contains("Safe failure.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplanCannotResetSharedBudget()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan(["step-1", "step-2"])),
            new InvestigationPlanDecision(CreatePlan(["step-3", "step-4"])));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(2, planner.States.Count);
        Assert.Equal(2, planner.States[1].Budget.ObservationsUsed);
        Assert.Equal(2, tool.InvocationCount);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task SkipsReplanAfterThreeInitialObservations()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Bounded final answer."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        var result = await runtime.RunAsync("Question");

        Assert.Equal("Bounded final answer.", result.Answer);
        Assert.Single(planner.States);
        Assert.Equal(3, tool.InvocationCount);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task NeverExecutesAPlanReturnedDuringFinalization()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(
            new InvestigationPlanDecision(CreatePlan("step-4")));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(3, tool.InvocationCount);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task EvidenceSerializationFailureBecomesSafeRuntimeFailure()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool(
            "tool.one",
            data: new ThrowingObservationData());
        var runtime = CreateRuntime(planner, model, tool);

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() =>
            runtime.RunAsync("Question"));

        Assert.Equal(
            "The observation result could not be prepared for model reasoning.",
            exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(3, tool.InvocationCount);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task PropagatesPlannerProviderFailure()
    {
        var model = new FakeLanguageModel(
            exception: new LanguageModelException("provider failure"));
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"));

        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            runtime.RunAsync("Question"));

        Assert.Equal("provider failure", exception.Message);
    }

    [Fact]
    public async Task RejectsNonPlanDecisionFromModelBackedPlanner()
    {
        var model = new FakeLanguageModel(new FinalAnswerDecision("Not a plan."));
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"));

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task PropagatesCancellationDuringInitialPlanning()
    {
        using var cancellation = new CancellationTokenSource();
        var planner = new FakePlanner(cancellationToken: cancellation.Token);
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var runtime = CreateRuntime(planner, model, new FakeObservationTool("tool.one"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunAsync("Question", cancellation.Token));
    }

    [Fact]
    public async Task PropagatesCancellationDuringReplanning()
    {
        using var cancellation = new CancellationTokenSource();
        var planner = new FakePlanner(
            cancellation.Token,
            new InvestigationPlanDecision(CreatePlan("step-1")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunAsync("Question", cancellation.Token));

        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(2, planner.States.Count);
    }

    [Fact]
    public async Task PropagatesCancellationDuringObservationExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool(
            "tool.one",
            cancellationAction: token => throw new OperationCanceledException(token));
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunAsync("Question", cancellation.Token));
    }

    private static AgentRuntime CreateRuntime(
        IInvestigationPlanner planner,
        ILanguageModel model,
        FakeObservationTool tool) =>
        new(
            planner,
            model,
            new ObservationRuntime(new ObservationRegistry([tool])));

    private static InvestigationPlan CreatePlan(
        params string[] stepIds) =>
        CreatePlan(stepIds, "tool.one");

    private static InvestigationPlan CreatePlan(
        IReadOnlyList<string> stepIds,
        string toolId = "tool.one") =>
        new(
            "Question",
            stepIds.Select(stepId => new InvestigationStep(stepId, toolId)).ToArray());

    private sealed record TestObservationData(string Value) : IObservationData;

    private sealed class ThrowingObservationData : IObservationData
    {
        public string Value => throw new InvalidOperationException("secret serialization detail");
    }

    private sealed class FakePlanner : IInvestigationPlanner
    {
        private readonly Queue<InvestigationPlanDecision> _decisions;
        private readonly CancellationToken? _cancellationToken;

        public FakePlanner(params InvestigationPlanDecision[] decisions)
        {
            _decisions = new Queue<InvestigationPlanDecision>(decisions);
        }

        public FakePlanner(CancellationToken cancellationToken)
        {
            _decisions = [];
            _cancellationToken = cancellationToken;
        }

        public FakePlanner(
            CancellationToken cancellationToken,
            params InvestigationPlanDecision[] decisions)
        {
            _decisions = new Queue<InvestigationPlanDecision>(decisions);
            _cancellationToken = cancellationToken;
        }

        public List<InvestigationState> States { get; } = [];

        public Task<InvestigationPlanDecision> CreatePlanAsync(
            InvestigationState state,
            CancellationToken cancellationToken = default)
        {
            States.Add(state);
            cancellationToken.ThrowIfCancellationRequested();
            if (_cancellationToken is { } token &&
                (_decisions.Count == 0 || States.Count > 1))
            {
                throw new OperationCanceledException(token);
            }

            if (_decisions.Count == 0)
            {
                throw new InvalidOperationException("No fake plan is available.");
            }

            return Task.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class FakeLanguageModel : ILanguageModel
    {
        private readonly Queue<AgentDecision> _decisions;
        private readonly Exception? _exception;

        public FakeLanguageModel(params AgentDecision[] decisions)
        {
            _decisions = new Queue<AgentDecision>(decisions);
        }

        public FakeLanguageModel(Exception exception)
        {
            _decisions = [];
            _exception = exception;
        }

        public List<LanguageModelRequest> Requests { get; } = [];

        public Task<AgentDecision> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null)
            {
                throw _exception;
            }

            if (_decisions.Count == 0)
            {
                throw new InvalidOperationException("No fake decision is available.");
            }

            return Task.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class FakeObservationTool : IObservationTool
    {
        private readonly ObservationResult? _result;
        private readonly IObservationData? _data;
        private readonly Action<CancellationToken>? _cancellationAction;
        private int _activeInvocations;

        public FakeObservationTool(
            string id,
            ObservationResult? result = null,
            IObservationData? data = null,
            Action<CancellationToken>? cancellationAction = null)
        {
            Descriptor = new ObservationToolDescriptor(id, id, "Test observation tool.");
            _result = result;
            _data = data;
            _cancellationAction = cancellationAction;
        }

        public ObservationToolDescriptor Descriptor { get; }

        public int InvocationCount { get; private set; }

        public int MaximumConcurrentInvocations { get; private set; }

        public List<ObservationRequest> Requests { get; } = [];

        public async Task<ObservationResult> ObserveAsync(
            ObservationRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            Requests.Add(request);
            var active = Interlocked.Increment(ref _activeInvocations);
            MaximumConcurrentInvocations = Math.Max(MaximumConcurrentInvocations, active);
            try
            {
                _cancellationAction?.Invoke(cancellationToken);
                await Task.Yield();
                var result = _result ?? new ObservationResult(
                    request.RequestId,
                    request.ToolId,
                    DateTimeOffset.UtcNow,
                    ObservationStatus.Succeeded,
                    _data);
                return result.RequestId == Guid.Empty
                    ? result with { RequestId = request.RequestId }
                    : result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeInvocations);
            }
        }
    }
}
