using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void ApplicationMetadataIsDefined()
    {
        Assert.Equal("Aegis", ApplicationInfo.Name);
        Assert.Equal("0.1.0", ApplicationInfo.Version);
    }

    [Fact]
    public async Task InvestigationServiceTrimsQuestionAndReturnsAnswer()
    {
        var model = new FakeLanguageModel();
        var service = new InvestigationService(model);

        var response = await service.InvestigateAsync("  What is Aegis?  ");

        Assert.Equal("What is Aegis?", model.LastQuestion);
        Assert.Equal("Aegis is ready.", response.Answer);
    }

    [Fact]
    public async Task InvestigationServiceRejectsEmptyQuestion()
    {
        var service = new InvestigationService(new FakeLanguageModel());

        await Assert.ThrowsAsync<ArgumentException>(() => service.InvestigateAsync("  "));
    }

    private sealed class FakeLanguageModel : ILanguageModel
    {
        public string? LastQuestion { get; private set; }

        public Task<LanguageModelResponse> CompleteAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken = default)
        {
            LastQuestion = request.Question;
            return Task.FromResult(new LanguageModelResponse("Aegis is ready."));
        }
    }
}
