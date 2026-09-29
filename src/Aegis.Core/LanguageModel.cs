namespace Aegis.Core;

public interface ILanguageModel
{
    Task<AgentDecision> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default);
}

public enum LanguageModelCallPhase
{
    Unknown,
    InitialPlanning,
    Replanning,
    Finalization
}

public enum LanguageModelFailureCategory
{
    Unknown,
    ProviderNotConfigured,
    AuthenticationRejected,
    ProviderRejected,
    ProviderUnavailable,
    InvalidModelResponse,
    StructuredOutputFailure,
    Cancelled
}

public enum LanguageModelCallOutcome
{
    Succeeded,
    Failed
}

public sealed record LanguageModelCallDiagnostics(
    LanguageModelCallPhase Phase,
    string Model,
    LanguageModelCallOutcome Outcome,
    LanguageModelFailureCategory? FailureCategory,
    string? FinishReason,
    int? InputTokenCount,
    int? OutputTokenCount,
    int? TotalTokenCount,
    int? ProviderStatusCode,
    TimeSpan Elapsed);

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
    IReadOnlyList<LanguageModelMessage> Messages,
    LanguageModelCallPhase Phase = LanguageModelCallPhase.Unknown);

public sealed class LanguageModelException : Exception
{
    public LanguageModelException(string message, Exception? innerException = null)
        : this(message, LanguageModelFailureCategory.Unknown, innerException, providerStatusCode: null)
    {
    }

    public LanguageModelException(
        string message,
        LanguageModelFailureCategory category,
        Exception? innerException = null,
        int? providerStatusCode = null)
        : base(message, innerException)
    {
        Category = category;
        ProviderStatusCode = providerStatusCode is 0 ? null : providerStatusCode;
    }

    public LanguageModelFailureCategory Category { get; }

    public int? ProviderStatusCode { get; }
}

public static class InvestigationInputValidator
{
    public const int MaximumQuestionLength = 2000;

    public static string NormalizeQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("An investigation question is required.", nameof(question));
        }

        var normalizedQuestion = question.Trim();
        if (normalizedQuestion.Length > MaximumQuestionLength)
        {
            throw new ArgumentException(
                $"The investigation question must be {MaximumQuestionLength} characters or fewer.",
                nameof(question));
        }

        return normalizedQuestion;
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
        var normalizedQuestion = InvestigationInputValidator.NormalizeQuestion(question);
        return _agentRuntime.RunAsync(
            normalizedQuestion,
            cancellationToken);
    }
}
