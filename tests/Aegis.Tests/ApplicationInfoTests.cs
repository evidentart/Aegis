using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void ApplicationMetadataIsDefined()
    {
        Assert.Equal("Aegis", ApplicationInfo.Name);
        Assert.Equal("0.2.0", ApplicationInfo.Version);
    }

    [Fact]
    public async Task InvestigationServiceTrimsQuestionAndReturnsAnswer()
    {
        var model = new FakeLanguageModel();
        var service = CreateService(model);

        var response = await service.InvestigateAsync("  What is Aegis?  ");

        Assert.Equal("What is Aegis?", model.LastQuestion);
        Assert.Equal("Aegis is ready.", response.Answer);
    }

    [Fact]
    public async Task InvestigationServiceRejectsEmptyQuestion()
    {
        var service = CreateService(new FakeLanguageModel());

        await Assert.ThrowsAsync<ArgumentException>(() => service.InvestigateAsync("  "));
    }

    private static InvestigationService CreateService(ILanguageModel model) =>
        new(new AgentRuntime(
            new FixedPlanner(),
            model,
            new ObservationRuntime(new ObservationRegistry([new FixedObservationTool()]))));

    [Fact]
    public async Task InvestigationServiceForwardsCancellationToLanguageModel()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new FakeLanguageModel();
        var service = CreateService(model);

        await service.InvestigateAsync("Question", cancellation.Token);

        Assert.Equal(cancellation.Token, model.LastCancellationToken);
    }

    [Fact]
    public async Task InvestigationServiceUsesLanguageModelContract()
    {
        ILanguageModel model = new FakeLanguageModel();
        var service = CreateService(model);

        var response = await service.InvestigateAsync("Question");

        Assert.Equal("Aegis is ready.", response.Answer);
    }

    private sealed class FakeLanguageModel : ILanguageModel
    {
        public string? LastQuestion { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<AgentDecision> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            LastQuestion = request.Messages.First(message =>
                    message.Role == LanguageModelMessageRole.User &&
                    message.Content is "What is Aegis?" or "Question" or "Question.")
                .Content;
            LastCancellationToken = cancellationToken;
            return Task.FromResult<AgentDecision>(new FinalAnswerDecision("Aegis is ready."));
        }
    }

    private sealed class FixedPlanner : IInvestigationPlanner
    {
        public Task<InvestigationPlanDecision> CreatePlanAsync(
            InvestigationState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvestigationPlanDecision(new InvestigationPlan(
                state.Objective,
                [
                    new InvestigationStep("step-1", "fixed.tool"),
                    new InvestigationStep("step-2", "fixed.tool"),
                    new InvestigationStep("step-3", "fixed.tool")
                ])));
    }

    private sealed class FixedObservationTool : IObservationTool
    {
        public ObservationToolDescriptor Descriptor { get; } =
            new("fixed.tool", "Fixed tool", "Test observation tool.");

        public Task<ObservationResult> ObserveAsync(
            ObservationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObservationResult(
                request.RequestId,
                request.ToolId,
                DateTimeOffset.UtcNow,
                ObservationStatus.Succeeded));
    }
}
