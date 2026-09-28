using Aegis.Core;
using Aegis.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aegis;

public sealed partial class MainWindow : Window
{
    private readonly InvestigationService _investigationService;
    private readonly ILogger _logger;
    private readonly InvestigationHistoryService _historyService;
    private readonly BaselineService _baselineService;
    private InvestigationDetails? _selectedInvestigation;

    public MainWindow(
        InvestigationService investigationService,
        ILogger logger,
        InvestigationHistoryService historyService,
        BaselineService baselineService)
    {
        _investigationService = investigationService ?? throw new ArgumentNullException(nameof(investigationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _baselineService = baselineService ?? throw new ArgumentNullException(nameof(baselineService));
        InitializeComponent();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e) =>
        await RefreshHistoryAsync();

    private async void InvestigateButton_Click(object sender, RoutedEventArgs e)
    {
        InvestigateButton.IsEnabled = false;
        BusyIndicator.IsActive = true;
        StatusTextBlock.Text = "Investigating…";
        AnswerTextBlock.Visibility = Visibility.Collapsed;

        try
        {
            var response = await _investigationService.InvestigateAsync(QuestionTextBox.Text);
            AnswerTextBlock.Text = FormatReport(response.Report);
            AnswerTextBlock.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"Investigation {response.InvestigationId} completed.";
        }
        catch (ArgumentException exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        catch (LanguageModelException exception)
        {
            _logger.LogWarning("Investigation request failed.");
            StatusTextBlock.Text = exception.Category switch
            {
                LanguageModelFailureCategory.ProviderNotConfigured =>
                    "OpenAI is not configured. Set AEGIS_OPENAI_API_KEY for local development.",
                LanguageModelFailureCategory.AuthenticationRejected =>
                    "OpenAI authentication was rejected.",
                LanguageModelFailureCategory.ProviderUnavailable =>
                    "OpenAI could not be reached right now.",
                LanguageModelFailureCategory.StructuredOutputFailure =>
                    "OpenAI did not accept the requested response format.",
                LanguageModelFailureCategory.InvalidModelResponse =>
                    "OpenAI returned an unusable investigation response.",
                _ => "Investigation is unavailable right now."
            };
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Investigation cancelled.";
        }
        catch (AgentRuntimeException)
        {
            _logger.LogWarning("Investigation request failed.");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
        }
        catch (InvestigationPersistenceException)
        {
            _logger.LogWarning("Investigation history could not be persisted.");
            StatusTextBlock.Text = "Investigation history is unavailable right now.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected investigation failure.");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
        }
        finally
        {
            BusyIndicator.IsActive = false;
            InvestigateButton.IsEnabled = true;
            await RefreshHistoryAsync();
        }
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshHistoryAsync();

    private async void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListView.SelectedItem is not InvestigationSummary summary)
        {
            _selectedInvestigation = null;
            ObservationListView.ItemsSource = null;
            ObservationDetailTextBlock.Text = string.Empty;
            CreateBaselineButton.IsEnabled = false;
            HistoryDetailTextBlock.Text = string.Empty;
            return;
        }

        try
        {
            _selectedInvestigation = await _historyService.GetAsync(summary.InvestigationId);
            if (_selectedInvestigation is null)
            {
                ObservationDetailTextBlock.Text = string.Empty;
                HistoryDetailTextBlock.Text = "Investigation details are unavailable.";
                return;
            }

            var investigation = _selectedInvestigation.Investigation;
            var outcome = investigation.Outcome?.Report is { } report
                ? FormatReport(report)
                : investigation.Outcome?.FailureMessage ??
                  investigation.Outcome?.FinalAnswer ??
                  "No final outcome was recorded.";
            var plans = string.Join(
                Environment.NewLine,
                investigation.Plans.Select(plan =>
                    $"Plan {plan.PlanSequence}: " +
                    string.Join(", ", plan.Plan.Steps.Select(step => step.ToolId))));
            HistoryDetailTextBlock.Text =
                $"{investigation.LifecycleStatus} · {investigation.CreatedAtUtc.LocalDateTime:g}{Environment.NewLine}" +
                $"Question: {investigation.Question}{Environment.NewLine}" +
                $"Outcome:{Environment.NewLine}{outcome}{Environment.NewLine}" +
                $"Plans:{Environment.NewLine}{plans}";
            ObservationListView.ItemsSource = investigation.StepExecutions;
            ObservationDetailTextBlock.Text = string.Empty;
            CreateBaselineButton.IsEnabled = false;
            BaselineStatusTextBlock.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Investigation history could not be loaded.");
            ObservationDetailTextBlock.Text = string.Empty;
            HistoryDetailTextBlock.Text = "Investigation details are unavailable.";
        }
    }

    private void ObservationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObservationListView.SelectedItem is not InvestigationStepExecution execution ||
            execution.Result is null)
        {
            ObservationDetailTextBlock.Text = string.Empty;
            CreateBaselineButton.IsEnabled = false;
            return;
        }

        ObservationDetailTextBlock.Text = FormatObservation(execution.Result);
        CreateBaselineButton.IsEnabled =
            execution.Status == InvestigationStepStatus.Completed &&
            execution.Result.Status == ObservationStatus.Succeeded &&
            string.Equals(execution.ToolId, WindowsSystemInfoObservationTool.ToolId, StringComparison.Ordinal) &&
            execution.Result.Data is WindowsSystemInfo;
    }

    private void BaselineListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BaselineListView.SelectedItem is not BaselineSummary baseline)
        {
            BaselineDetailTextBlock.Text = string.Empty;
            return;
        }

        BaselineDetailTextBlock.Text =
            $"{baseline.Platform} {baseline.OsVersion} (build {baseline.Build?.ToString() ?? "unknown"}), " +
            $"{baseline.Architecture}{Environment.NewLine}" +
            $"Source investigation: {baseline.SourceInvestigationId}";
    }

    private async void CreateBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInvestigation is null ||
            ObservationListView.SelectedItem is not InvestigationStepExecution execution)
        {
            return;
        }

        try
        {
            await _baselineService.CreateFromStepAsync(
                _selectedInvestigation.Investigation.InvestigationId,
                execution.StepId);
            BaselineStatusTextBlock.Text = "Baseline created.";
            await RefreshBaselinesAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Baseline creation failed.");
            BaselineStatusTextBlock.Text = "The selected observation is not eligible for a baseline.";
        }
    }

    private async Task RefreshHistoryAsync()
    {
        try
        {
            HistoryListView.ItemsSource = await _historyService.ListAsync();
            await RefreshBaselinesAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "History could not be loaded.");
            HistoryDetailTextBlock.Text = "History is unavailable right now.";
        }
    }

    private async Task RefreshBaselinesAsync()
    {
        BaselineListView.ItemsSource = await _baselineService.ListAsync();
    }

    private static string FormatObservation(ObservationResult result) => result.Data switch
    {
        WindowsSystemInfo data =>
            $"System: {data.Platform} {data.OsVersion} build {data.Build?.ToString() ?? "unknown"}, {data.Architecture}",
        WindowsPerformanceSystem data =>
            $"System performance at {result.ObservedAtUtc.LocalDateTime:g}: CPU {data.CpuUtilizationPercent:F1}%, " +
            $"memory {data.MemoryLoadPercent}% ({FormatBytes(data.PhysicalMemoryAvailableBytes)} available of " +
            $"{FormatBytes(data.PhysicalMemoryTotalBytes)}), sample {data.SampleDuration.TotalMilliseconds:F0} ms.",
        WindowsPerformanceTopProcesses data =>
            $"Top accessible processes at {result.ObservedAtUtc.LocalDateTime:g}, sample " +
            $"{data.SampleDuration.TotalMilliseconds:F0} ms. CPU: " +
            string.Join(", ", data.TopCpuProcesses.Select(FormatProcess)) + ". Memory: " +
            string.Join(", ", data.TopMemoryProcesses.Select(FormatProcess)) + ".",
        WindowsRecentErrorEvents data => FormatRecentErrorEvents(data),
        _ => "The observation returned no displayable typed details."
    };

    private static string FormatRecentErrorEvents(WindowsRecentErrorEvents data)
    {
        var lines = new List<string>
        {
            $"Recent Windows Critical/Error events — last 48 hours ({data.Events.Count} retained)."
        };

        foreach (var diagnosticEvent in data.Events)
        {
            lines.Add(
                $"{diagnosticEvent.OccurredAtUtc.LocalDateTime:g}  {diagnosticEvent.Channel}  " +
                $"{diagnosticEvent.ProviderName}  Event {diagnosticEvent.EventId}  {diagnosticEvent.Severity}");
        }

        if (data.IsTruncated)
        {
            lines.Add("Additional matching events were omitted because the observation reached its bounded limit.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatReport(InvestigationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string> { $"Summary: {report.Summary}" };
        AppendEvidenceStatements(lines, "Observed facts", report.ObservedFacts);
        AppendEvidenceStatements(lines, "Conclusions", report.Conclusions);
        AppendEvidenceStatements(lines, "Hypotheses", report.Hypotheses);
        AppendTextStatements(lines, "Uncertainty", report.Uncertainties);
        AppendTextStatements(lines, "Recommendations", report.Recommendations);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendEvidenceStatements(
        ICollection<string> lines,
        string heading,
        IReadOnlyList<EvidenceStatement> statements)
    {
        if (statements.Count == 0)
        {
            return;
        }

        lines.Add($"{heading}:");
        foreach (var statement in statements)
        {
            lines.Add($"- {statement.Text}");
            lines.Add($"  Evidence: {string.Join(", ", statement.EvidenceStepIds)}");
        }
    }

    private static void AppendTextStatements(
        ICollection<string> lines,
        string heading,
        IReadOnlyList<string> statements)
    {
        if (statements.Count == 0)
        {
            return;
        }

        lines.Add($"{heading}:");
        foreach (var statement in statements)
        {
            lines.Add($"- {statement}");
        }
    }

    private static string FormatProcess(WindowsPerformanceProcess process) =>
        $"{process.ProcessName} (PID {process.ProcessId}, CPU {process.CpuUtilizationPercent:F1}%, " +
        $"working set {FormatBytes(process.WorkingSetBytes)})";

    private static string FormatBytes(ulong bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        return bytes switch
        {
            >= (ulong)gigabyte => $"{bytes / gigabyte:F1} GB",
            >= (ulong)megabyte => $"{bytes / megabyte:F1} MB",
            >= (ulong)kilobyte => $"{bytes / kilobyte:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
