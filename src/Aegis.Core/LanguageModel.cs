namespace Aegis.Core;

public interface ILanguageModel
{
    Task<LanguageModelResponse> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LanguageModelRequest(string Question);

public sealed record LanguageModelResponse(string Answer);

public sealed class LanguageModelException : Exception
{
    public LanguageModelException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class InvestigationService
{
    private readonly ILanguageModel _languageModel;

    public InvestigationService(ILanguageModel languageModel)
    {
        _languageModel = languageModel;
    }

    public Task<LanguageModelResponse> InvestigateAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A question is required.", nameof(question));
        }

        return _languageModel.CompleteAsync(
            new LanguageModelRequest(question.Trim()),
            cancellationToken);
    }
}
