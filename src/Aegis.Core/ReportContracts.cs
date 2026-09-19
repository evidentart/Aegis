namespace Aegis.Core;

public sealed record EvidenceStatement(
    string Text,
    IReadOnlyList<string> EvidenceStepIds);

public sealed record InvestigationReport(
    string Summary,
    IReadOnlyList<EvidenceStatement> ObservedFacts,
    IReadOnlyList<EvidenceStatement> Conclusions,
    IReadOnlyList<EvidenceStatement> Hypotheses,
    IReadOnlyList<string> Uncertainties,
    IReadOnlyList<string> Recommendations)
{
    public static InvestigationReport FromLegacySummary(string summary) =>
        new(
            summary,
            Array.Empty<EvidenceStatement>(),
            Array.Empty<EvidenceStatement>(),
            Array.Empty<EvidenceStatement>(),
            Array.Empty<string>(),
            Array.Empty<string>());
}

public static class InvestigationReportValidator
{
    public const int MaximumEntriesPerSection = 8;
    public const int MaximumEvidenceReferencesPerStatement = 4;
    public const int MaximumSummaryLength = 4000;
    public const int MaximumStatementLength = 4000;
    public const int MaximumListItemLength = 2000;

    public static void ValidateShape(InvestigationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        RequireText(report.Summary, MaximumSummaryLength, "The report summary");
        ValidateStatements(report.ObservedFacts, "observed facts", requireEvidence: true, evidenceStepIds: null);
        ValidateStatements(report.Conclusions, "conclusions", requireEvidence: true, evidenceStepIds: null);
        ValidateStatements(report.Hypotheses, "hypotheses", requireEvidence: true, evidenceStepIds: null);
        ValidateTextList(report.Uncertainties, "uncertainties");
        ValidateTextList(report.Recommendations, "recommendations");
    }

    public static void Validate(
        InvestigationReport report,
        IReadOnlySet<string> collectedEvidenceStepIds)
    {
        ArgumentNullException.ThrowIfNull(collectedEvidenceStepIds);
        ValidateShape(report);
        ValidateStatements(report.ObservedFacts, "observed facts", requireEvidence: true, collectedEvidenceStepIds);
        ValidateStatements(report.Conclusions, "conclusions", requireEvidence: true, collectedEvidenceStepIds);
        ValidateStatements(report.Hypotheses, "hypotheses", requireEvidence: true, collectedEvidenceStepIds);
    }

    private static void ValidateStatements(
        IReadOnlyList<EvidenceStatement> statements,
        string sectionName,
        bool requireEvidence,
        IReadOnlySet<string>? evidenceStepIds)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count > MaximumEntriesPerSection)
        {
            throw new InvalidOperationException($"The {sectionName} section exceeds the supported bound.");
        }

        foreach (var statement in statements)
        {
            ArgumentNullException.ThrowIfNull(statement);
            RequireText(statement.Text, MaximumStatementLength, $"A {sectionName} statement");
            ArgumentNullException.ThrowIfNull(statement.EvidenceStepIds);
            if (requireEvidence && statement.EvidenceStepIds.Count == 0)
            {
                throw new InvalidOperationException($"A {sectionName} statement must cite collected evidence.");
            }

            if (statement.EvidenceStepIds.Count > MaximumEvidenceReferencesPerStatement)
            {
                throw new InvalidOperationException(
                    $"A {sectionName} statement exceeds the supported evidence-reference bound.");
            }

            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidenceStepId in statement.EvidenceStepIds)
            {
                RequireText(evidenceStepId, MaximumStatementLength, "An evidence step ID");
                if (!string.Equals(evidenceStepId, evidenceStepId.Trim(), StringComparison.Ordinal) ||
                    !references.Add(evidenceStepId))
                {
                    throw new InvalidOperationException("A report statement contains a duplicate or invalid evidence reference.");
                }

                if (evidenceStepIds is not null && !evidenceStepIds.Contains(evidenceStepId))
                {
                    throw new InvalidOperationException(
                        $"The report references an evidence step '{evidenceStepId}' that was not collected.");
                }
            }
        }
    }

    private static void ValidateTextList(
        IReadOnlyList<string> values,
        string sectionName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumEntriesPerSection)
        {
            throw new InvalidOperationException($"The {sectionName} section exceeds the supported bound.");
        }

        foreach (var value in values)
        {
            RequireText(value, MaximumListItemLength, $"A {sectionName} item");
        }
    }

    private static void RequireText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidOperationException($"{name} must be non-empty and within the supported length.");
        }
    }
}
