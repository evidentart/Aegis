namespace Aegis.Core;

public abstract record AgentDecision;

public sealed record FinalAnswerDecision(InvestigationReport Report) : AgentDecision
{
    public FinalAnswerDecision(string answer)
        : this(InvestigationReport.FromLegacySummary(answer))
    {
    }
}

public sealed record InvestigationPlanDecision(InvestigationPlan Plan) : AgentDecision;

public abstract record InvestigationReplanDecision : AgentDecision
{
    private InvestigationReplanDecision()
    {
    }

    public sealed record RevisedPlan(InvestigationPlan Plan) : InvestigationReplanDecision;

    public sealed record FinalizeNow : InvestigationReplanDecision;
}

public sealed record AgentRunResult(Guid InvestigationId, InvestigationReport Report)
{
    public string Answer => Report.Summary;
}

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
