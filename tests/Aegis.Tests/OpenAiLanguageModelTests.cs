using Aegis;
using Xunit;

namespace Aegis.Tests;

public sealed class OpenAiLanguageModelTests
{
    [Fact]
    public async Task MapsQuestionToUserMessageAndConvertsAnswer()
    {
        var client = new FakeOpenAiChatClient("The answer.");
        var model = new OpenAiLanguageModel(client);

        var response = await model.CompleteAsync(new Aegis.Core.LanguageModelRequest("What is Aegis?"));

        var message = Assert.Single(client.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("What is Aegis?", message.Content);
        Assert.Equal("The answer.", response.Answer);
    }

    [Fact]
    public async Task ConvertsProviderFailureToLanguageModelException()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(exception: new InvalidOperationException("provider failure")));

        var exception = await Assert.ThrowsAsync<Aegis.Core.LanguageModelException>(() =>
            model.CompleteAsync(new Aegis.Core.LanguageModelRequest("Question")));

        Assert.Equal("The language model request failed.", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task RejectsMalformedEmptyProviderResponse()
    {
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(string.Empty));

        var exception = await Assert.ThrowsAsync<Aegis.Core.LanguageModelException>(() =>
            model.CompleteAsync(new Aegis.Core.LanguageModelRequest("Question")));

        Assert.Equal("The language model returned an empty answer.", exception.Message);
    }

    [Fact]
    public async Task PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = new OpenAiLanguageModel(new FakeOpenAiChatClient(cancellationToken: cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            model.CompleteAsync(new Aegis.Core.LanguageModelRequest("Question"), cancellation.Token));
    }

    private sealed class FakeOpenAiChatClient : IOpenAiChatClient
    {
        private readonly string? _answer;
        private readonly Exception? _exception;
        private readonly CancellationToken? _cancellationToken;

        public FakeOpenAiChatClient(
            string? answer = null,
            Exception? exception = null,
            CancellationToken? cancellationToken = null)
        {
            _answer = answer;
            _exception = exception;
            _cancellationToken = cancellationToken;
        }

        public List<OpenAiMessage> Messages { get; } = [];

        public Task<string> CompleteAsync(
            IReadOnlyList<OpenAiMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            if (_exception is not null)
            {
                throw _exception;
            }

            if (_cancellationToken is { } token)
            {
                return Task.FromCanceled<string>(token);
            }

            return Task.FromResult(_answer ?? string.Empty);
        }
    }
}
