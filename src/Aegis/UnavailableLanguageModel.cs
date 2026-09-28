using Aegis.Core;

namespace Aegis;

internal sealed class UnavailableLanguageModel : ILanguageModel
{
    public Task<AgentDecision> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken = default) =>
        throw new LanguageModelException(
            "Configure AEGIS_OPENAI_API_KEY for local development.",
            LanguageModelFailureCategory.ProviderNotConfigured);
}
