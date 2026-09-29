using Aegis.Core;

namespace Aegis;

public sealed record InvestigationReportItem(
    string Text,
    IReadOnlyList<string> EvidenceStepIds);

public sealed record InvestigationReportSection(
    string Heading,
    IReadOnlyList<InvestigationReportItem> Items);

public static class InvestigationReportPresentation
{
    public static IReadOnlyList<InvestigationReportSection> BuildSections(InvestigationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return
        [
            new("Summary", [new InvestigationReportItem(report.Summary, Array.Empty<string>())]),
            new("Observed Facts", report.ObservedFacts.Select(ToItem).ToArray()),
            new("Conclusions", report.Conclusions.Select(ToItem).ToArray()),
            new("Hypotheses", report.Hypotheses.Select(ToItem).ToArray()),
            new("Uncertainty", report.Uncertainties.Select(text => new InvestigationReportItem(text, Array.Empty<string>())).ToArray()),
            new("Recommendations", report.Recommendations.Select(text => new InvestigationReportItem(text, Array.Empty<string>())).ToArray())
        ];
    }

    private static InvestigationReportItem ToItem(EvidenceStatement statement) =>
        new(statement.Text, statement.EvidenceStepIds);
}
