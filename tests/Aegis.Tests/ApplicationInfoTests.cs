using Aegis;
using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void ApplicationMetadataIsDefined()
    {
        Assert.Equal("Aegis", ApplicationInfo.Name);
        Assert.Equal("1.0.0", ApplicationInfo.Version);
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

    [Fact]
    public async Task InvestigationServiceAcceptsMaximumQuestionLength()
    {
        var model = new FakeLanguageModel();
        var service = CreateService(model);
        var question = new string('q', InvestigationInputValidator.MaximumQuestionLength);

        await service.InvestigateAsync(question);

        Assert.Equal(question, model.LastQuestion);
        Assert.Equal(1, model.CompleteCallCount);
    }

    [Fact]
    public async Task InvestigationServiceRejectsOversizedQuestionBeforeProviderUse()
    {
        var model = new FakeLanguageModel();
        var service = CreateService(model);
        var question = new string('q', InvestigationInputValidator.MaximumQuestionLength + 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.InvestigateAsync(question));

        Assert.Contains("2000 characters or fewer", exception.Message);
        Assert.Equal(0, model.CompleteCallCount);
    }

    [Fact]
    public async Task MissingProviderIsReportedWithoutExecutingAnything()
    {
        var exception = await Assert.ThrowsAsync<LanguageModelException>(() =>
            new UnavailableLanguageModel().CompleteAsync(new LanguageModelRequest([])));

        Assert.Equal(LanguageModelFailureCategory.ProviderNotConfigured, exception.Category);
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

        public int CompleteCallCount { get; private set; }

        public Task<AgentDecision> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CompleteCallCount++;
            LastQuestion = request.Messages.First(message => message.Role == LanguageModelMessageRole.User).Content;
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

        public Task<InvestigationReplanDecision> CreateReplanDecisionAsync(
            InvestigationState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<InvestigationReplanDecision>(
                new InvestigationReplanDecision.FinalizeNow());
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
