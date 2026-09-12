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
            model,
            new ObservationRuntime(new ObservationRegistry([]))));

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
            LastQuestion = Assert.Single(request.Messages, message =>
                    message.Role == LanguageModelMessageRole.User)
                .Content;
            LastCancellationToken = cancellationToken;
            return Task.FromResult<AgentDecision>(new FinalAnswerDecision("Aegis is ready."));
        }
    }
}
