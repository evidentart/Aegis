namespace Aegis.Core;

public abstract record AgentDecision;

public sealed record FinalAnswerDecision(string Answer) : AgentDecision;

public sealed record InvestigationPlanDecision(InvestigationPlan Plan) : AgentDecision;

public sealed record AgentRunResult(Guid InvestigationId, string Answer);

public sealed class AgentRuntimeException : Exception
{
    public AgentRuntimeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class InvestigationPersistenceException : Exception
{
    public InvestigationPersistenceException(
        string message,
        Exception? innerException = null,
        Exception? originalException = null)
        : base(message, innerException)
    {
        OriginalException = originalException;
    }

    public Exception? OriginalException { get; }
}
