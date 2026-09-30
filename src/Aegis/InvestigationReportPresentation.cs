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

        var sections = new List<InvestigationReportSection>
        {
            new("Summary", [new InvestigationReportItem(report.Summary, Array.Empty<string>())])
        };

        AddSectionIfPopulated(sections, "Observed Facts", report.ObservedFacts.Select(ToItem));
        AddSectionIfPopulated(sections, "Conclusions", report.Conclusions.Select(ToItem));
        AddSectionIfPopulated(sections, "Hypotheses", report.Hypotheses.Select(ToItem));
        AddSectionIfPopulated(
            sections,
            "Uncertainty",
            report.Uncertainties.Select(text => new InvestigationReportItem(text, Array.Empty<string>())));
        AddSectionIfPopulated(
            sections,
            "Recommendations",
            report.Recommendations.Select(text => new InvestigationReportItem(text, Array.Empty<string>())));

        return sections;
    }

    private static InvestigationReportItem ToItem(EvidenceStatement statement) =>
        new(statement.Text, statement.EvidenceStepIds);

    private static void AddSectionIfPopulated(
        ICollection<InvestigationReportSection> sections,
        string heading,
        IEnumerable<InvestigationReportItem> items)
    {
        var materializedItems = items.ToArray();
        if (materializedItems.Length > 0)
        {
            sections.Add(new InvestigationReportSection(heading, materializedItems));
        }
    }
}
