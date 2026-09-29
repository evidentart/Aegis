using Aegis.Core;
using Aegis.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Aegis;

public sealed partial class MainWindow : Window
{
    private readonly InvestigationService _investigationService;
    private readonly ILogger _logger;
    private readonly InvestigationHistoryService _historyService;
    private readonly BaselineService _baselineService;
    private InvestigationDetails? _selectedInvestigation;

    private sealed record ObservationExecutionListItem(
        InvestigationStepExecution Execution,
        string ToolId,
        InvestigationStepStatus Status,
        string EligibilityText);

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
        SettingsVersionTextBlock.Text = $"Application version {ApplicationInfo.Version}";
        RootNavigationView.SelectedItem = InvestigateNavigationItem;
        ConfigureDefaultWindowSize();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e) =>
        await RefreshHistoryAsync();

    private void ConfigureDefaultWindowSize()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(1180, 760));
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string page)
        {
            return;
        }

        ShowPage(page);
        if (string.Equals(page, "History", StringComparison.Ordinal))
        {
            _ = RefreshHistoryAsync();
        }
        else if (string.Equals(page, "Baselines", StringComparison.Ordinal))
        {
            _ = RefreshBaselinesAsync();
        }
    }

    private void ShowPage(string page)
    {
        InvestigatePage.Visibility = string.Equals(page, "Investigate", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryPage.Visibility = string.Equals(page, "History", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        BaselinesPage.Visibility = string.Equals(page, "Baselines", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPage.Visibility = string.Equals(page, "Settings", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NewInvestigationButton_Click(object sender, RoutedEventArgs e)
    {
        QuestionTextBox.Text = string.Empty;
        StatusTextBlock.Text = string.Empty;
        ResultCard.Visibility = Visibility.Collapsed;
        InvestigationResultSectionsPanel.Children.Clear();
        RootNavigationView.SelectedItem = InvestigateNavigationItem;
        QuestionTextBox.Focus(FocusState.Programmatic);
    }

    private async void InvestigateButton_Click(object sender, RoutedEventArgs e)
    {
        InvestigateButton.IsEnabled = false;
        NewInvestigationButton.IsEnabled = false;
        BusyIndicator.IsActive = true;
        StatusTextBlock.Text = "Investigating…";
        ResultCard.Visibility = Visibility.Collapsed;

        try
        {
            var response = await _investigationService.InvestigateAsync(QuestionTextBox.Text);
            RenderReport(InvestigationResultSectionsPanel, response.Report);
            ResultCard.Visibility = Visibility.Visible;
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
            NewInvestigationButton.IsEnabled = true;
            await RefreshHistoryAsync();
        }
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshHistoryAsync();

    private async void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListView.SelectedItem is not InvestigationSummary summary)
        {
            ClearHistoryDetails();
            return;
        }

        try
        {
            _selectedInvestigation = await _historyService.GetAsync(summary.InvestigationId);
            if (_selectedInvestigation is null)
            {
                ClearHistoryDetails();
                HistoryEmptyDetailTextBlock.Text = "Investigation details are unavailable.";
                return;
            }

            var investigation = _selectedInvestigation.Investigation;
            HistoryEmptyDetailTextBlock.Visibility = Visibility.Collapsed;
            HistoryDetailQuestionTextBlock.Visibility = Visibility.Visible;
            HistoryDetailQuestionTextBlock.Text = investigation.Question;
            HistoryDetailMetadataTextBlock.Visibility = Visibility.Visible;
            HistoryDetailMetadataTextBlock.Text =
                $"{investigation.LifecycleStatus} | {investigation.CreatedAtUtc.LocalDateTime:g}";

            HistoryResultSectionsPanel.Children.Clear();
            if (investigation.Outcome?.Report is { } report)
            {
                RenderReport(HistoryResultSectionsPanel, report);
            }
            else
            {
                AddMessageCard(
                    HistoryResultSectionsPanel,
                    investigation.Outcome?.FailureMessage ??
                    investigation.Outcome?.FinalAnswer ??
                    "No final outcome was recorded.");
            }

            HistoryPlansTextBlock.Text = string.Join(
                Environment.NewLine,
                investigation.Plans.Select(plan =>
                    $"Plan {plan.PlanSequence}: {string.Join(", ", plan.Plan.Steps.Select(step => step.ToolId))}"));
            HistoryPlansHeadingTextBlock.Visibility = investigation.Plans.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            ObservationListView.ItemsSource = investigation.StepExecutions
                .Select(execution => CreateObservationExecutionListItem(investigation, execution))
                .ToArray();
            ObservationDetailTextBlock.Text = string.Empty;
            ObservationListView.SelectedItem = null;
            UpdateBaselineSelectionState(null);
            BaselineStatusTextBlock.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Investigation history could not be loaded.");
            ClearHistoryDetails();
            HistoryEmptyDetailTextBlock.Text = "Investigation details are unavailable.";
        }
    }

    private void ClearHistoryDetails()
    {
        _selectedInvestigation = null;
        HistoryEmptyDetailTextBlock.Visibility = Visibility.Visible;
        HistoryEmptyDetailTextBlock.Text = "Select an investigation to view its reconstructed result.";
        HistoryDetailQuestionTextBlock.Visibility = Visibility.Collapsed;
        HistoryDetailQuestionTextBlock.Text = string.Empty;
        HistoryDetailMetadataTextBlock.Visibility = Visibility.Collapsed;
        HistoryDetailMetadataTextBlock.Text = string.Empty;
        HistoryResultSectionsPanel.Children.Clear();
        HistoryPlansHeadingTextBlock.Visibility = Visibility.Collapsed;
        HistoryPlansTextBlock.Text = string.Empty;
        ObservationListView.ItemsSource = null;
        ObservationDetailTextBlock.Text = string.Empty;
        UpdateBaselineSelectionState(null);
        BaselineStatusTextBlock.Text = string.Empty;
    }

    private void ObservationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObservationListView.SelectedItem is not ObservationExecutionListItem item ||
            item.Execution.Result is null)
        {
            ObservationDetailTextBlock.Text = string.Empty;
            UpdateBaselineSelectionState(null);
            return;
        }

        var execution = item.Execution;
        ObservationDetailTextBlock.Text = FormatObservation(execution.Result);
        UpdateBaselineSelectionState(execution);
    }

    private static ObservationExecutionListItem CreateObservationExecutionListItem(
        Investigation investigation,
        InvestigationStepExecution execution) =>
        new(
            execution,
            execution.ToolId,
            execution.Status,
            IsBaselineEligible(investigation, execution) ? "Baseline eligible" : string.Empty);

    private static bool IsBaselineEligible(
        Investigation investigation,
        InvestigationStepExecution execution) =>
        investigation.LifecycleStatus == InvestigationLifecycleStatus.Completed &&
        execution.Result is not null &&
        execution.Status == InvestigationStepStatus.Completed &&
        execution.Result.Status == ObservationStatus.Succeeded &&
        string.Equals(execution.ToolId, WindowsSystemInfoObservationTool.ToolId, StringComparison.Ordinal) &&
        execution.Result.Data is WindowsSystemInfo &&
        investigation.Plans.Any(plan =>
            plan.PlanSequence == execution.PlanSequence &&
            plan.Plan.Steps.Any(step =>
                string.Equals(step.StepId, execution.StepId, StringComparison.Ordinal) &&
                string.Equals(step.ToolId, execution.ToolId, StringComparison.Ordinal) &&
                step.Status == InvestigationStepStatus.Completed));

    private void UpdateBaselineSelectionState(InvestigationStepExecution? execution)
    {
        var investigation = _selectedInvestigation?.Investigation;
        var isEligible = investigation is not null &&
            execution is not null &&
            IsBaselineEligible(investigation, execution);
        CreateBaselineButton.IsEnabled = isEligible;

        if (execution is null)
        {
            var hasEligibleObservation = ObservationListView.Items
                .OfType<ObservationExecutionListItem>()
                .Any(item => investigation is not null && IsBaselineEligible(investigation, item.Execution));
            BaselineGuidanceTextBlock.Text = hasEligibleObservation
                ? "Select a completed system-information observation to create a baseline."
                : "No baseline-eligible system-information observation is available in this investigation.";
        }
        else
        {
            BaselineGuidanceTextBlock.Text = isEligible
                ? "Selected observation is eligible for a baseline."
                : "Select a completed system-information observation to create a baseline.";
        }
    }

    private void BaselineListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BaselineListView.SelectedItem is not BaselineSummary baseline)
        {
            BaselineEmptyDetailTextBlock.Visibility = Visibility.Visible;
            BaselineDetailTextBlock.Text = string.Empty;
            return;
        }

        BaselineEmptyDetailTextBlock.Visibility = Visibility.Collapsed;
        BaselineDetailTextBlock.Text =
            $"{baseline.Platform} {baseline.OsVersion} (build {baseline.Build?.ToString() ?? "unknown"}), " +
            $"{baseline.Architecture}" + Environment.NewLine +
            $"Source investigation: {baseline.SourceInvestigationId}" + Environment.NewLine +
            $"Created: {baseline.CreatedAtUtc.LocalDateTime:g}" + Environment.NewLine +
            $"Tool: {baseline.ToolId}";
    }

    private async void CreateBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInvestigation is null ||
            ObservationListView.SelectedItem is not ObservationExecutionListItem item)
        {
            return;
        }

        var execution = item.Execution;

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
            HistoryEmptyDetailTextBlock.Text = "History is unavailable right now.";
        }
    }

    private async Task RefreshBaselinesAsync()
    {
        try
        {
            var baselines = await _baselineService.ListAsync();
            BaselineListView.ItemsSource = baselines;
            BaselineEmptyStatePanel.Visibility = baselines.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Baselines could not be loaded.");
            BaselineEmptyStatePanel.Visibility = Visibility.Visible;
            BaselineEmptyDetailTextBlock.Text = "Baselines are unavailable right now.";
        }
    }

    private static void RenderReport(StackPanel target, InvestigationReport report)
    {
        target.Children.Clear();
        var sections = InvestigationReportPresentation.BuildSections(report);
        foreach (var section in sections)
        {
            var sectionPanel = new StackPanel
            {
                Spacing = 14,
                MaxWidth = 840,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0)
            };
            sectionPanel.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GetBrush("AccentTextFillColorPrimaryBrush")
            });

            if (section.Items.Count == 0)
            {
                sectionPanel.Children.Add(new TextBlock
                {
                    Text = "None recorded.",
                    Foreground = GetBrush("TextFillColorSecondaryBrush")
                });
            }
            else
            {
                foreach (var item in section.Items)
                {
                    var itemPanel = new StackPanel
                    {
                        Spacing = 8,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                        LineHeight = 22,
                        Foreground = GetBrush("TextFillColorPrimaryBrush")
                    });
                    if (item.EvidenceStepIds.Count > 0)
                    {
                        itemPanel.Children.Add(new TextBlock
                        {
                            Text = $"Evidence: {string.Join(", ", item.EvidenceStepIds.Select(PresentationFormatting.FormatEvidenceStepId))}",
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                            Foreground = GetBrush("TextFillColorTertiaryBrush")
                        });
                    }

                    sectionPanel.Children.Add(itemPanel);
                }
            }

            target.Children.Add(new Border
            {
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(12),
                Background = GetSectionBrush(),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = sectionPanel
            });
        }
    }

    private static void AddMessageCard(StackPanel target, string message)
    {
        target.Children.Add(new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = GetSectionBrush(),
            BorderBrush = GetCardStrokeBrush(),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Foreground = GetBrush("TextFillColorSecondaryBrush")
            }
        });
    }

    private static Brush? GetCardBrush() => GetBrush("CardBackgroundFillColorDefaultBrush");

    private static Brush? GetSectionBrush() =>
        GetBrush("LayerFillColorDefaultBrush") ?? GetCardBrush();

    private static Brush? GetCardStrokeBrush() => GetBrush("CardStrokeColorDefaultBrush");

    private static Brush? GetBrush(string key) =>
        Application.Current.Resources.ContainsKey(key)
            ? Application.Current.Resources[key] as Brush
            : null;

    private static string FormatObservation(ObservationResult result) => result.Data switch
    {
        WindowsSystemInfo data =>
            $"System: {data.Platform} {data.OsVersion} build {data.Build?.ToString() ?? "unknown"}, {data.Architecture}",
        WindowsPerformanceSystem data =>
            $"System performance at {result.ObservedAtUtc.LocalDateTime:g}: CPU " +
            $"{PresentationFormatting.FormatPercentage(data.CpuUtilizationPercent)}, " +
            $"memory {data.MemoryLoadPercent}% ({PresentationFormatting.FormatBytes(data.PhysicalMemoryAvailableBytes)} available of " +
            $"{PresentationFormatting.FormatBytes(data.PhysicalMemoryTotalBytes)}), sample {data.SampleDuration.TotalMilliseconds:F0} ms.",
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

    private static string FormatProcess(WindowsPerformanceProcess process) =>
        $"{process.ProcessName} (PID {process.ProcessId}, CPU " +
        $"{PresentationFormatting.FormatPercentage(process.CpuUtilizationPercent)}, " +
        $"working set {PresentationFormatting.FormatBytes(process.WorkingSetBytes)})";
}
