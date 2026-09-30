using Aegis;
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
    public async Task AcceptsEvidenceAttributedFinalReport()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision(CreateReport("step-1")));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        var result = await runtime.RunAsync("Question");

        Assert.Equal("Summary", result.Report.Summary);
        var fact = Assert.Single(result.Report.ObservedFacts);
        Assert.Equal("step-1", Assert.Single(fact.EvidenceStepIds));
        var finalSystemInstruction = model.Requests.Single().Messages[0].Content;
        Assert.Contains("observed_facts", finalSystemInstruction);
        Assert.Contains("evidence_step_ids", finalSystemInstruction);
        Assert.Contains("structural citations", finalSystemInstruction);
        Assert.Contains("single performance snapshot cannot prove sustained behavior", finalSystemInstruction);
        Assert.Contains("Do not infer Windows marketing or product names", finalSystemInstruction);
        Assert.Contains("Report the observed platform, version, and build literally instead", finalSystemInstruction);
        Assert.Contains("overall memory pressure or usage as high merely because individual processes have large working sets", finalSystemInstruction);
        Assert.Contains("observed aggregate memory-load metric", finalSystemInstruction);
        Assert.Contains("Keep per-process memory usage distinct from system-wide memory pressure", finalSystemInstruction);
        Assert.Contains("leave conclusions, hypotheses, uncertainties, and recommendations empty", finalSystemInstruction);
        Assert.Contains("recommendations only when they materially help the user", finalSystemInstruction);
        Assert.Contains("Do not recommend commands, registry inspection, systeminfo, or additional evidence collection", finalSystemInstruction);
        Assert.DoesNotContain("\"answer\"", finalSystemInstruction);
    }

    [Theory]
    [InlineData("unknown-step")]
    [InlineData("")]
    public async Task RejectsFinalReportWithoutCollectedEvidence(string evidenceStepId)
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var report = CreateReport(evidenceStepId);
        var model = new FakeLanguageModel(new FinalAnswerDecision(report));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task RejectsDuplicateEvidenceReferences()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision(CreateReport("step-1", "step-1")));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task RejectsObservedFactWithoutEvidenceReferences()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var report = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Observed fact", [])],
            [],
            [],
            [],
            []);
        var model = new FakeLanguageModel(new FinalAnswerDecision(report));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task RejectsFactualCitationToFailedObservation()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision(CreateReport("step-1")));
        var tool = new FakeObservationTool(
            "tool.one",
            result: new ObservationResult(
                Guid.Empty,
                "tool.one",
                DateTimeOffset.UtcNow,
                ObservationStatus.Failed,
                Failure: new ObservationFailure("observation_failed", "Unavailable.")));
        var runtime = CreateRuntime(planner, model, tool);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task RejectsEvidenceFreeConclusionAndHypothesis()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var report = new InvestigationReport(
            "Summary",
            [],
            [new EvidenceStatement("Conclusion", [])],
            [new EvidenceStatement("Hypothesis", [])],
            [],
            []);
        var model = new FakeLanguageModel(new FinalAnswerDecision(report));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));
    }

    [Fact]
    public async Task RecommendationsRemainTextAndDoNotCreateExecutionPath()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var report = CreateReport("step-1") with
        {
            Recommendations = ["Review the accessible evidence."]
        };
        var model = new FakeLanguageModel(new FinalAnswerDecision(report));
        var tool = new FakeObservationTool("tool.one", data: new TestObservationData("observed"));
        var runtime = CreateRuntime(planner, model, tool);

        var result = await runtime.RunAsync("Question");

        Assert.Equal(["Review the accessible evidence."], result.Report.Recommendations);
        Assert.Equal(3, tool.InvocationCount);
    }

    [Fact]
    public void EnforcesReportSectionAndEvidenceReferenceBounds()
    {
        var tooManyRecommendations = new InvestigationReport(
            "Summary",
            [],
            [],
            [],
            [],
            Enumerable.Range(0, InvestigationReportValidator.MaximumEntriesPerSection + 1)
                .Select(index => $"Recommendation {index}")
                .ToArray());
        var tooManyReferences = CreateReport(
            Enumerable.Range(0, InvestigationReportValidator.MaximumEvidenceReferencesPerStatement + 1)
                .Select(index => $"step-{index}")
                .ToArray());

        Assert.Throws<InvalidOperationException>(() =>
            InvestigationReportValidator.ValidateShape(tooManyRecommendations));
        Assert.Throws<InvalidOperationException>(() =>
            InvestigationReportValidator.ValidateShape(tooManyReferences));
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
    public async Task FinalizeNowCompletesFromPlanZeroEvidenceWithoutPlanOne()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationReplanDecision.FinalizeNow());
        var model = new FakeLanguageModel(new FinalAnswerDecision(CreateReport("step-1")));
        var store = new RecordingHistoryStore();
        var tool = new FakeObservationTool(
            "tool.one",
            data: new TestObservationData("observed"));
        var runtime = CreateRuntime(planner, model, tool, store);

        var result = await runtime.RunAsync("Question");

        Assert.Equal("Summary", result.Report.Summary);
        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(2, planner.States.Count);
        Assert.Equal(1, planner.States[1].Budget.ObservationsUsed);
        Assert.Equal(1, planner.States[1].ReplanCount);
        Assert.Single(model.Requests);
        Assert.Contains("plan:0", store.Events);
        Assert.Contains("step:step-1", store.Events);
        Assert.DoesNotContain("plan:1", store.Events);
        Assert.Equal("terminal:Completed", store.Events[^1]);
    }

    [Fact]
    public async Task FinalizeNowStillValidatesFinalReportEvidence()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationReplanDecision.FinalizeNow());
        var invalidReport = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Observed fact", [])],
            [],
            [],
            [],
            []);
        var model = new FakeLanguageModel(new FinalAnswerDecision(invalidReport));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")),
            store);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(1, store.TerminalCommitCount);
        Assert.Equal("terminal:Failed", store.Events[^1]);
        Assert.Equal(
            "investigation_failed_agent_runtime_finalizing",
            store.LastTerminalOutcome?.FailureCode);
        Assert.DoesNotContain("plan:1", store.Events);
    }

    [Fact]
    public async Task RevisedPlanWithFreshStepIdsPersistsAndExecutesPlanOne()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationPlanDecision(CreatePlan("step-2")));
        var model = new FakeLanguageModel(new FinalAnswerDecision(CreateReport("step-2")));
        var store = new RecordingHistoryStore();
        var tool = new FakeObservationTool("tool.one", data: new TestObservationData("observed"));
        var runtime = CreateRuntime(planner, model, tool, store);

        await runtime.RunAsync("Question");

        Assert.Equal(2, tool.InvocationCount);
        Assert.Contains("plan:0", store.Events);
        Assert.Contains("plan:1", store.Events);
        Assert.Contains("step:step-2", store.Events);
        Assert.Equal("terminal:Completed", store.Events[^1]);
    }

    [Fact]
    public async Task ReplanReusingPlanZeroStepIdStillFailsClosed()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationReplanDecision.RevisedPlan(CreatePlan("step-1")));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            new FakeLanguageModel(new FinalAnswerDecision("Never reached.")),
            new FakeObservationTool("tool.one"),
            store);

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(
            "The investigation plan contains an invalid or duplicate step ID.",
            exception.Message);
        Assert.DoesNotContain("plan:1", store.Events);
        Assert.Equal("investigation_failed_agent_runtime_replanning", store.LastTerminalOutcome?.FailureCode);
    }

    [Fact]
    public async Task ReplanExceedingRemainingBudgetStillFailsClosed()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2")),
            new InvestigationReplanDecision.RevisedPlan(CreatePlan("step-3", "step-4")));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            new FakeLanguageModel(new FinalAnswerDecision("Never reached.")),
            new FakeObservationTool("tool.one"),
            store);

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal("The investigation plan exceeds the remaining observation budget.", exception.Message);
        Assert.DoesNotContain("plan:1", store.Events);
    }

    [Fact]
    public async Task ReplanObjectiveMismatchStillFailsClosed()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationReplanDecision.RevisedPlan(
                new InvestigationPlan("Different objective", [new InvestigationStep("step-2", "tool.one")])));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            new FakeLanguageModel(new FinalAnswerDecision("Never reached.")),
            new FakeObservationTool("tool.one"),
            store);

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(
            "The investigation plan objective must match the authoritative investigation objective.",
            exception.Message);
        Assert.DoesNotContain("plan:1", store.Events);
    }

    [Fact]
    public async Task ReplanUnknownToolIdStillFailsClosed()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationReplanDecision.RevisedPlan(CreatePlan(["step-2"], "unknown.tool")));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            new FakeLanguageModel(new FinalAnswerDecision("Never reached.")),
            new FakeObservationTool("tool.one"),
            store);

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Contains("unavailable observation tool", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("plan:1", store.Events);
    }

    [Fact]
    public async Task DirectFinalAnswerDecisionDuringReplanningIsRejected()
    {
        var model = new FakeLanguageModel(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new FinalAnswerDecision("Must not be persisted."));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"),
            store);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal("investigation_failed_agent_runtime_replanning", store.LastTerminalOutcome?.FailureCode);
        Assert.DoesNotContain("plan:1", store.Events);
        Assert.Null(store.LastTerminalOutcome?.FinalAnswer);
        Assert.Null(store.LastTerminalOutcome?.Report);
    }

    [Fact]
    public async Task SerializesAuthoritativeStepIdsForRepeatedToolEvidence()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationPlanDecision(CreatePlan("step-2")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Final answer."));
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")));

        await runtime.RunAsync("Question");

        var evidence = model.Requests.Single().Messages
            .Where(message => message.Role == LanguageModelMessageRole.Observation)
            .Select(message => message.Content)
            .ToArray();
        Assert.Equal(2, evidence.Length);
        Assert.Contains(evidence, content => content.Contains("\"step_id\":\"step-1\"", StringComparison.Ordinal));
        Assert.Contains(evidence, content => content.Contains("\"step_id\":\"step-2\"", StringComparison.Ordinal));
        Assert.All(evidence, content => Assert.Contains("\"tool_id\":\"tool.one\"", content));
    }

    [Fact]
    public async Task SerializesRecentEventEnumValuesAsNamesInModelEvidence()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1")),
            new InvestigationPlanDecision(CreatePlan("step-2")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Final answer."));
        var end = DateTimeOffset.UtcNow;
        var data = WindowsRecentErrorEventsValidation.Build(
            end - WindowsRecentErrorEventsValidation.ObservationWindow,
            end,
            [new WindowsDiagnosticEvent(
                end,
                WindowsEventChannelKind.Application,
                "Application.Provider",
                100,
                WindowsEventSeverity.Critical)]);
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: data));

        await runtime.RunAsync("Question");

        var evidenceMessages = model.Requests.Single().Messages
            .Where(message => message.Role == LanguageModelMessageRole.Observation)
            .ToArray();
        Assert.Equal(2, evidenceMessages.Length);
        Assert.All(evidenceMessages, evidence =>
        {
            Assert.Contains("\"Channel\":\"Application\"", evidence.Content);
            Assert.Contains("\"Severity\":\"Critical\"", evidence.Content);
            Assert.DoesNotContain("\"Channel\":1", evidence.Content);
            Assert.DoesNotContain("\"Severity\":0", evidence.Content);
        });
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

        var exception = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(
            "The investigation plan objective must match the authoritative investigation objective.",
            exception.Message);
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
    public async Task InitialPlanningFailurePersistsBoundedLanguageModelClassification()
    {
        const string sensitiveProviderBody = "SENSITIVE_PROVIDER_BODY_MUST_NOT_PERSIST";
        var model = new FakeLanguageModel(
            new LanguageModelException(
                sensitiveProviderBody,
                LanguageModelFailureCategory.ProviderRejected,
                providerStatusCode: 400));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"),
            store);

        await Assert.ThrowsAsync<LanguageModelException>(() => runtime.RunAsync("Question"));

        Assert.NotNull(store.LastTerminalOutcome);
        var outcome = store.LastTerminalOutcome!;
        Assert.Equal(
            "investigation_failed_language_model_planning_provider_rejected",
            outcome.FailureCode);
        Assert.Equal(
            "Investigation failed during initial planning because of language-model category provider rejected. Provider status: 400.",
            outcome.FailureMessage);
        Assert.DoesNotContain(sensitiveProviderBody, outcome.FailureCode);
        Assert.DoesNotContain(sensitiveProviderBody, outcome.FailureMessage);
    }

    [Fact]
    public async Task ReplanningFailurePersistsBoundedLanguageModelClassification()
    {
        var providerFailure = new LanguageModelException(
            "provider failure",
            LanguageModelFailureCategory.ProviderUnavailable);
        var model = new FailingAfterInitialPlanLanguageModel(
            CreatePlan("step-1"),
            providerFailure);
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"),
            store);

        await Assert.ThrowsAsync<LanguageModelException>(() => runtime.RunAsync("Question"));

        Assert.NotNull(store.LastTerminalOutcome);
        var outcome = store.LastTerminalOutcome!;
        Assert.Equal(
            "investigation_failed_language_model_replanning_provider_unavailable",
            outcome.FailureCode);
        Assert.Equal(
            "Investigation failed during replanning because of language-model category provider unavailable.",
            outcome.FailureMessage);
    }

    [Fact]
    public async Task FinalizationFailurePersistsBoundedLanguageModelClassification()
    {
        var providerFailure = new LanguageModelException(
            "provider failure",
            LanguageModelFailureCategory.Unknown);
        var model = new FailingAfterInitialPlanLanguageModel(
            CreatePlan("step-1", "step-2", "step-3"),
            providerFailure);
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            new LanguageModelInvestigationPlanner(model),
            model,
            new FakeObservationTool("tool.one"),
            store);

        await Assert.ThrowsAsync<LanguageModelException>(() => runtime.RunAsync("Question"));

        Assert.NotNull(store.LastTerminalOutcome);
        var outcome = store.LastTerminalOutcome!;
        Assert.Equal(
            "investigation_failed_language_model_finalizing_unknown",
            outcome.FailureCode);
        Assert.Equal(
            "Investigation failed during finalization because of language-model category unknown.",
            outcome.FailureMessage);
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

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
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

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
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

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.RunAsync("Question", cancellation.Token));
    }

    [Fact]
    public async Task PersistsOneTerminalOutcomeAfterIncrementalFacts()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Final answer."));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(planner, model, new FakeObservationTool("tool.one"), store);

        var result = await runtime.RunAsync("Question");

        Assert.NotEqual(Guid.Empty, result.InvestigationId);
        Assert.Equal(
            ["create", "running", "plan:0", "step:step-1", "step:step-2", "step:step-3", "terminal:Completed"],
            store.Events);
        Assert.Equal(1, store.TerminalCommitCount);
    }

    [Fact]
    public async Task InvalidFinalReportNeverCommitsCompletedAndFailsOnce()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var report = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Observed fact", [])],
            [],
            [],
            [],
            []);
        var model = new FakeLanguageModel(new FinalAnswerDecision(report));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one", data: new TestObservationData("observed")),
            store);

        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.RunAsync("Question"));

        Assert.Equal(1, store.TerminalCommitCount);
        Assert.Equal(1, store.Events.Count(eventName => eventName == "terminal:Failed"));
        Assert.DoesNotContain("terminal:Completed", store.Events);
        Assert.Equal("terminal:Failed", store.Events[^1]);
        Assert.Equal(
            "investigation_failed_agent_runtime_finalizing",
            store.LastTerminalOutcome?.FailureCode);
    }

    [Fact]
    public async Task UnexpectedExecutionFailurePersistsOnlyBoundedClassification()
    {
        const string sensitiveExceptionMessage = "SENSITIVE_PROVIDER_BODY_MUST_NOT_PERSIST";
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var store = new RecordingHistoryStore
        {
            ExecutionException = new InvalidOperationException(sensitiveExceptionMessage)
        };
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool("tool.one"),
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync("Question"));

        Assert.NotNull(store.LastTerminalOutcome);
        var outcome = store.LastTerminalOutcome!;
        Assert.Equal("investigation_failed_unexpected_executing", outcome.FailureCode);
        Assert.Equal(
            "Investigation failed during observation execution because of an unexpected runtime error.",
            outcome.FailureMessage);
        Assert.DoesNotContain(sensitiveExceptionMessage, outcome.FailureCode);
        Assert.DoesNotContain(sensitiveExceptionMessage, outcome.FailureMessage);
        Assert.Equal(1, store.TerminalCommitCount);
        Assert.Equal("terminal:Failed", store.Events[^1]);
    }

    [Fact]
    public async Task DoesNotRetryTerminalCommitAndDoesNotReturnAnswerAfterPersistenceFailure()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var providerFailure = new LanguageModelException("provider failure");
        var model = new FakeLanguageModel(providerFailure);
        var store = new RecordingHistoryStore { TerminalException = new InvalidOperationException("write failed") };
        var runtime = CreateRuntime(planner, model, new FakeObservationTool("tool.one"), store);

        var exception = await Assert.ThrowsAsync<InvestigationPersistenceException>(() =>
            runtime.RunAsync("Question"));

        Assert.Same(providerFailure, exception.OriginalException);
        Assert.Equal(1, store.TerminalCommitCount);
        Assert.DoesNotContain("terminal:Completed", store.Events);
        Assert.Equal("terminal:Failed", store.Events[^1]);
    }

    [Fact]
    public async Task DoesNotExecuteObservationsWhenCreationFails()
    {
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var tool = new FakeObservationTool("tool.one");
        var store = new RecordingHistoryStore { CreateException = new InvalidOperationException("create failed") };
        var runtime = CreateRuntime(planner, model, tool, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync("Question"));

        Assert.Equal(0, tool.InvocationCount);
        Assert.Equal(0, store.TerminalCommitCount);
    }

    [Fact]
    public async Task CancellationCommitsCancelledExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var planner = new FakePlanner(
            new InvestigationPlanDecision(CreatePlan("step-1", "step-2", "step-3")));
        var model = new FakeLanguageModel(new FinalAnswerDecision("Never reached."));
        var store = new RecordingHistoryStore();
        var runtime = CreateRuntime(
            planner,
            model,
            new FakeObservationTool(
                "tool.one",
                cancellationAction: token => throw new OperationCanceledException(token)),
            store);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.RunAsync("Question", cancellation.Token));

        Assert.Equal(1, store.TerminalCommitCount);
        Assert.Equal("terminal:Cancelled", store.Events[^1]);
    }

    private static AgentRuntime CreateRuntime(
        IInvestigationPlanner planner,
        ILanguageModel model,
        FakeObservationTool tool,
        IInvestigationHistoryStore? historyStore = null) =>
        new(
            planner,
            model,
            new ObservationRuntime(new ObservationRegistry([tool])),
            historyStore);

    private static InvestigationPlan CreatePlan(
        params string[] stepIds) =>
        CreatePlan(stepIds, "tool.one");

    private static InvestigationPlan CreatePlan(
        IReadOnlyList<string> stepIds,
        string toolId = "tool.one") =>
        new(
            "Question",
            stepIds.Select(stepId => new InvestigationStep(stepId, toolId)).ToArray());

    private static InvestigationReport CreateReport(params string[] evidenceStepIds) =>
        new(
            "Summary",
            [new EvidenceStatement("Observed fact", evidenceStepIds)],
            [],
            [],
            [],
            []);

    private sealed record TestObservationData(string Value) : IObservationData;

    private sealed class ThrowingObservationData : IObservationData
    {
        public string Value => throw new InvalidOperationException("secret serialization detail");
    }

    private sealed class FakePlanner : IInvestigationPlanner
    {
        private readonly Queue<InvestigationPlanDecision> _decisions;
        private readonly CancellationToken? _cancellationToken;
        private readonly InvestigationReplanDecision? _replanDecision;

        public FakePlanner(params InvestigationPlanDecision[] decisions)
        {
            _decisions = new Queue<InvestigationPlanDecision>(decisions);
        }

        public FakePlanner(
            InvestigationPlanDecision initialDecision,
            InvestigationReplanDecision replanDecision)
        {
            _decisions = new Queue<InvestigationPlanDecision>([initialDecision]);
            _replanDecision = replanDecision;
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

        public Task<InvestigationReplanDecision> CreateReplanDecisionAsync(
            InvestigationState state,
            CancellationToken cancellationToken = default)
        {
            States.Add(state);
            cancellationToken.ThrowIfCancellationRequested();
            if (_replanDecision is not null)
            {
                return Task.FromResult(_replanDecision);
            }

            if (_cancellationToken is { } token &&
                (_decisions.Count == 0 || States.Count > 1))
            {
                throw new OperationCanceledException(token);
            }

            if (_decisions.Count == 0)
            {
                throw new InvalidOperationException("No fake replan decision is available.");
            }

            return Task.FromResult<InvestigationReplanDecision>(
                new InvestigationReplanDecision.RevisedPlan(_decisions.Dequeue().Plan));
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

    private sealed class FailingAfterInitialPlanLanguageModel : ILanguageModel
    {
        private readonly InvestigationPlan _initialPlan;
        private readonly Exception _exception;
        private int _callCount;

        public FailingAfterInitialPlanLanguageModel(
            InvestigationPlan initialPlan,
            Exception exception)
        {
            _initialPlan = initialPlan;
            _exception = exception;
        }

        public Task<AgentDecision> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return Task.FromResult<AgentDecision>(new InvestigationPlanDecision(_initialPlan));
            }

            throw _exception;
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

    private sealed class RecordingHistoryStore : IInvestigationHistoryStore
    {
        public List<string> Events { get; } = [];
        public int TerminalCommitCount { get; private set; }
        public Exception? CreateException { get; init; }
        public Exception? TerminalException { get; init; }
        public Exception? ExecutionException { get; init; }
        public InvestigationOutcome? LastTerminalOutcome { get; private set; }

        public Task CreateAsync(Investigation investigation, CancellationToken cancellationToken = default)
        {
            if (CreateException is not null)
            {
                throw CreateException;
            }

            Events.Add("create");
            return Task.CompletedTask;
        }

        public Task MarkRunningAsync(Guid investigationId, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
        {
            Events.Add("running");
            return Task.CompletedTask;
        }

        public Task AppendPlanAsync(Guid investigationId, InvestigationPlanHistoryEntry plan, CancellationToken cancellationToken = default)
        {
            Events.Add($"plan:{plan.PlanSequence}");
            return Task.CompletedTask;
        }

        public Task AppendStepExecutionAsync(Guid investigationId, InvestigationStepExecution execution, CancellationToken cancellationToken = default)
        {
            if (ExecutionException is not null)
            {
                throw ExecutionException;
            }

            Events.Add($"step:{execution.StepId}");
            return Task.CompletedTask;
        }

        public Task MarkStepsSkippedAsync(Guid investigationId, int planSequence, IReadOnlyList<string> stepIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> CommitTerminalOutcomeAsync(
            Guid investigationId,
            InvestigationLifecycleStatus terminalStatus,
            InvestigationOutcome outcome,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            TerminalCommitCount++;
            Events.Add($"terminal:{terminalStatus}");
            LastTerminalOutcome = outcome;
            if (TerminalException is not null)
            {
                throw TerminalException;
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InvestigationSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InvestigationSummary>>([]);

        public Task<InvestigationDetails?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvestigationDetails?>(null);

        public Task<InvestigationDeletionResult> DeleteInvestigationAsync(
            Guid investigationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvestigationDeletionResult(
                investigationId,
                InvestigationDeletionStatus.NotFound));

        public Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClearHistoryResult(0, 0, 0));

        public Task<ClearSavedHistoryAndBaselinesResult> ClearSavedHistoryAndBaselinesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClearSavedHistoryAndBaselinesResult(0, 0, 0));
    }
}
