using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class PlanningContextTests
{
    [Fact]
    public async Task PlanningContextAllowsApprovedProcessObservationWithoutAllowingArbitraryProcessAccess()
    {
        var model = new CapturingLanguageModel();
        var planner = new LanguageModelInvestigationPlanner(model);
        var state = CreateState() with
        {
            Question = "User question wording",
            Objective = "Authoritative runtime objective"
        };

        await planner.CreatePlanAsync(state);

        var messages = model.LastRequest!.Messages;
        var systemInstruction = Assert.Single(
                messages,
                message => message.Role == LanguageModelMessageRole.System)
            .Content;
        Assert.Contains("windows.performance.system", systemInstruction);
        Assert.Contains("windows.performance.top_processes", systemInstruction);
        Assert.Contains("windows.events.recent_errors", systemInstruction);
        Assert.DoesNotContain("Do not request commands, files, paths, registry access, processes,", systemInstruction);
        Assert.Contains("Process observations are permitted only through exact registered read-only observation tools", systemInstruction);
        Assert.Contains("arbitrary process targeting", systemInstruction);
        Assert.Contains("caller-supplied PIDs", systemInstruction);
        Assert.Contains("process control", systemInstruction);
        Assert.Contains("exact registered ToolIds", systemInstruction);
        Assert.Contains("runtime decides", systemInstruction);
        Assert.Contains("The first User message is the runtime-owned authoritative investigation objective.", systemInstruction);
        Assert.Contains("Copy the first User message exactly", systemInstruction);
        Assert.Contains("MaximumObservationExecutions - ObservationsUsed = 3", systemInstruction);
        Assert.Contains("Return a plan with at least one step. Its step count must not exceed 3.", systemInstruction);
        Assert.DoesNotContain("Authoritative runtime objective", systemInstruction);
        Assert.Equal(LanguageModelMessageRole.User, messages[1].Role);
        Assert.Equal("Authoritative runtime objective", messages[1].Content);
    }

    [Fact]
    public async Task ReplanningContextRepeatsTheAuthoritativeObjectiveExactly()
    {
        var model = new CapturingLanguageModel();
        var planner = new LanguageModelInvestigationPlanner(model);
        var state = CreateState() with
        {
            Question = "Replan question wording",
            Objective = "Replan authoritative runtime objective",
            Budget = new InvestigationBudget(3, 2),
            ReplanCount = 1,
            ExecutionPhase = InvestigationExecutionPhase.Replanning
        };

        await planner.CreatePlanAsync(state);

        Assert.Equal(LanguageModelCallPhase.Replanning, model.LastRequest!.Phase);
        var messages = model.LastRequest.Messages;
        var systemInstruction = Assert.Single(
                messages,
                message => message.Role == LanguageModelMessageRole.System)
            .Content;
        Assert.Contains("The first User message is the runtime-owned authoritative investigation objective.", systemInstruction);
        Assert.Contains("Copy the first User message exactly", systemInstruction);
        Assert.Contains("MaximumObservationExecutions - ObservationsUsed = 1", systemInstruction);
        Assert.Contains("Return a plan with at least one step. Its step count must not exceed 1.", systemInstruction);
        Assert.DoesNotContain("Replan authoritative runtime objective", systemInstruction);
        Assert.Equal(LanguageModelMessageRole.User, messages[1].Role);
        Assert.Equal("Replan authoritative runtime objective", messages[1].Content);
    }

    private static InvestigationState CreateState() =>
        new(
            Guid.NewGuid(),
            "Why is this PC slow?",
            "Why is this PC slow?",
            DateTimeOffset.UtcNow,
            null,
            null,
            InvestigationLifecycleStatus.Created,
            null,
            [],
            [],
            [],
            [],
            [
                new ObservationToolDescriptor(
                    "windows.performance.system",
                    "System performance snapshot",
                    "Reads a bounded system performance snapshot."),
                new ObservationToolDescriptor(
                    "windows.performance.top_processes",
                    "Top process performance snapshot",
                    "Reads bounded rankings for accessible processes."),
                new ObservationToolDescriptor(
                    "windows.events.recent_errors",
                    "Recent Windows error events",
                    "Reads bounded Critical and Error metadata from System and Application.")
            ],
            new InvestigationBudget(3, 0),
            0,
            InvestigationExecutionPhase.Planning,
            null);

    private sealed class CapturingLanguageModel : ILanguageModel
    {
        public LanguageModelRequest? LastRequest { get; private set; }

        public Task<AgentDecision> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<AgentDecision>(
                new InvestigationPlanDecision(new InvestigationPlan("Why is this PC slow?", [])));
        }
    }
}
