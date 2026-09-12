namespace Aegis.Core;

public interface ILanguageModel
{
    Task<AgentDecision> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default);
}

public enum LanguageModelMessageRole
{
    System,
    User,
    Assistant,
    Observation
}

public sealed record LanguageModelMessage(
    LanguageModelMessageRole Role,
    string Content);

public sealed record LanguageModelRequest(
    IReadOnlyList<LanguageModelMessage> Messages);

public sealed class LanguageModelException : Exception
{
    public LanguageModelException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class InvestigationService
{
    private readonly AgentRuntime _agentRuntime;

    public InvestigationService(AgentRuntime agentRuntime)
    {
        _agentRuntime = agentRuntime;
    }

    public Task<AgentRunResult> InvestigateAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A question is required.", nameof(question));
        }

        return _agentRuntime.RunAsync(
            question.Trim(),
            cancellationToken);
    }
}
