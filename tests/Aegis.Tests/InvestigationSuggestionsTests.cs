using Aegis;
using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class InvestigationSuggestionsTests
{
    [Theory]
    [InlineData("Performance")]
    [InlineData("SystemErrors")]
    [InlineData("SystemInformation")]
    [InlineData("SystemHealth")]
    [InlineData("TopProcesses")]
    [InlineData("Reliability")]
    public void SupportedSuggestionsProduceValidInvestigationQuestions(string suggestion)
    {
        var question = InvestigationSuggestions.GetQuestion(suggestion);
        Assert.NotNull(question);
        Assert.Equal(question, InvestigationInputValidator.NormalizeQuestion(question));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Startup")]
    [InlineData("performance")]
    public void UnknownSuggestionsAreIgnored(string? suggestion) =>
        Assert.Null(InvestigationSuggestions.GetQuestion(suggestion));
}
