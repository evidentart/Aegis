using Aegis;
using Aegis.Core;
using Xunit;

namespace Aegis.Tests;

public sealed class InvestigationReportPresentationTests
{
    [Fact]
    public void BuildsStructuredSectionsWithoutDroppingEvidenceReferences()
    {
        var report = new InvestigationReport(
            "Summary",
            [new EvidenceStatement("Observed fact", ["step-1"])],
            [new EvidenceStatement("Conclusion", ["step-1", "step-2"])],
            [new EvidenceStatement("Hypothesis", ["step-2"])],
            ["Uncertainty"],
            ["Recommendation"]);

        var sections = InvestigationReportPresentation.BuildSections(report);

        Assert.Equal(
            ["Summary", "Observed Facts", "Conclusions", "Hypotheses", "Uncertainty", "Recommendations"],
            sections.Select(section => section.Heading));
        Assert.Equal("Summary", Assert.Single(sections[0].Items).Text);
        Assert.Equal("step-1", Assert.Single(sections[1].Items).EvidenceStepIds.Single());
        Assert.Equal(["step-1", "step-2"], Assert.Single(sections[2].Items).EvidenceStepIds);
        Assert.Equal("Hypothesis", Assert.Single(sections[3].Items).Text);
        Assert.Equal("Uncertainty", Assert.Single(sections[4].Items).Text);
        Assert.Equal("Recommendation", Assert.Single(sections[5].Items).Text);
    }

    [Fact]
    public void OmitsEmptyOptionalReportSections()
    {
        var sections = InvestigationReportPresentation.BuildSections(
            InvestigationReport.FromLegacySummary("Summary"));

        var section = Assert.Single(sections);
        Assert.Equal("Summary", section.Heading);
        Assert.Equal("Summary", Assert.Single(section.Items).Text);
    }

    [Fact]
    public void KeepsOnlyPopulatedOptionalSectionsInStableOrder()
    {
        var sections = InvestigationReportPresentation.BuildSections(
            new InvestigationReport(
                "Summary",
                [new EvidenceStatement("Observed fact", ["step-1"])],
                [],
                [new EvidenceStatement("Hypothesis", ["step-1"])],
                [],
                ["Recommendation"]));

        Assert.Equal(
            ["Summary", "Observed Facts", "Hypotheses", "Recommendations"],
            sections.Select(section => section.Heading));
        Assert.Equal("step-1", Assert.Single(sections[1].Items).EvidenceStepIds.Single());
        Assert.Equal("step-1", Assert.Single(sections[2].Items).EvidenceStepIds.Single());
        Assert.Equal("Recommendation", Assert.Single(sections[3].Items).Text);
    }
}
