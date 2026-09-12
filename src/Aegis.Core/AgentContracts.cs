namespace Aegis.Core;

public abstract record AgentDecision;

public sealed record FinalAnswerDecision(string Answer) : AgentDecision;

public sealed record ObservationRequestDecision(string ToolId) : AgentDecision;

public sealed record AgentRunResult(string Answer);

public sealed class AgentRuntimeException : Exception
{
    public AgentRuntimeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
