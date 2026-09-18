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
        var state = CreateState();

        await planner.CreatePlanAsync(state);

        var systemInstruction = Assert.Single(
                model.LastRequest!.Messages,
                message => message.Role == LanguageModelMessageRole.System)
            .Content;
        Assert.Contains("windows.performance.system", systemInstruction);
        Assert.Contains("windows.performance.top_processes", systemInstruction);
        Assert.DoesNotContain("Do not request commands, files, paths, registry access, processes,", systemInstruction);
        Assert.Contains("Process observations are permitted only through exact registered read-only observation tools", systemInstruction);
        Assert.Contains("arbitrary process targeting", systemInstruction);
        Assert.Contains("caller-supplied PIDs", systemInstruction);
        Assert.Contains("process control", systemInstruction);
        Assert.Contains("exact registered ToolIds", systemInstruction);
        Assert.Contains("runtime decides", systemInstruction);
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
                    "Reads bounded rankings for accessible processes.")
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
