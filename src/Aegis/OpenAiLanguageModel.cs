using Aegis.Core;
using OpenAI.Chat;

namespace Aegis;

internal interface IOpenAiChatClient
{
    Task<string> CompleteAsync(
        IReadOnlyList<OpenAiMessage> messages,
        CancellationToken cancellationToken = default);
}

internal sealed record OpenAiMessage(string Role, string Content);

internal sealed class SdkOpenAiChatClient : IOpenAiChatClient
{
    private readonly ChatClient _chatClient;

    public SdkOpenAiChatClient(ChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<string> CompleteAsync(
        IReadOnlyList<OpenAiMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var chatMessages = messages
            .Select(message => new UserChatMessage(message.Content))
            .ToArray();
        var completion = await _chatClient.CompleteChatAsync(
            chatMessages,
            cancellationToken: cancellationToken);

        return completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
    }
}

internal sealed class OpenAiLanguageModel : ILanguageModel
{
    private readonly IOpenAiChatClient _chatClient;

    public OpenAiLanguageModel(IOpenAiChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<LanguageModelResponse> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await _chatClient.CompleteAsync(
                [new OpenAiMessage("user", request.Question)],
                cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
            {
                throw new LanguageModelException("The language model returned an empty answer.");
            }

            return new LanguageModelResponse(answer);
        }
        catch (LanguageModelException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LanguageModelException("The language model request failed.", exception);
        }
    }
}
