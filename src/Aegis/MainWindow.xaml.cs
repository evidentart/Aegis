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
            AnswerTextBlock.Text = response.Answer;
            AnswerTextBlock.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"Investigation {response.InvestigationId} completed.";
        }
        catch (ArgumentException exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        catch (LanguageModelException)
        {
            _logger.LogWarning("Investigation request failed.");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
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
            CreateBaselineButton.IsEnabled = false;
            HistoryDetailTextBlock.Text = string.Empty;
            return;
        }

        try
        {
            _selectedInvestigation = await _historyService.GetAsync(summary.InvestigationId);
            if (_selectedInvestigation is null)
            {
                HistoryDetailTextBlock.Text = "Investigation details are unavailable.";
                return;
            }

            var investigation = _selectedInvestigation.Investigation;
            var outcome = investigation.Outcome?.FinalAnswer ??
                          investigation.Outcome?.FailureMessage ??
                          "No final outcome was recorded.";
            var plans = string.Join(
                Environment.NewLine,
                investigation.Plans.Select(plan =>
                    $"Plan {plan.PlanSequence}: " +
                    string.Join(", ", plan.Plan.Steps.Select(step => step.ToolId))));
            HistoryDetailTextBlock.Text =
                $"{investigation.LifecycleStatus} · {investigation.CreatedAtUtc.LocalDateTime:g}{Environment.NewLine}" +
                $"Question: {investigation.Question}{Environment.NewLine}" +
                $"Outcome: {outcome}{Environment.NewLine}" +
                $"Plans:{Environment.NewLine}{plans}";
            ObservationListView.ItemsSource = investigation.StepExecutions;
            CreateBaselineButton.IsEnabled = false;
            BaselineStatusTextBlock.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Investigation history could not be loaded.");
            HistoryDetailTextBlock.Text = "Investigation details are unavailable.";
        }
    }

    private void ObservationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CreateBaselineButton.IsEnabled =
            _selectedInvestigation is not null &&
            ObservationListView.SelectedItem is InvestigationStepExecution execution &&
            execution.Result is not null;
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
}
