using Aegis.Core;
using Aegis.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private CancellationTokenSource? _activeInvestigationCancellation;

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
        if (_activeInvestigationCancellation is not null)
        {
            return;
        }

        QuestionTextBox.Text = string.Empty;
        StatusTextBlock.Text = string.Empty;
        ResultCard.Visibility = Visibility.Collapsed;
        InvestigationResultSectionsPanel.Children.Clear();
        RootNavigationView.SelectedItem = InvestigateNavigationItem;
        QuestionTextBox.Focus(FocusState.Programmatic);
    }

    private async void InvestigateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeInvestigationCancellation is not null)
        {
            return;
        }

        var investigationCancellation = new CancellationTokenSource();
        _activeInvestigationCancellation = investigationCancellation;
        InvestigateButton.IsEnabled = false;
        NewInvestigationButton.IsEnabled = false;
        QuestionTextBox.IsEnabled = false;
        CancelInvestigationButton.IsEnabled = true;
        CancelInvestigationButton.Content = "Cancel";
        BusyIndicator.IsActive = true;
        StatusTextBlock.Text = "Investigating…";
        ResultCard.Visibility = Visibility.Collapsed;

        try
        {
            var response = await _investigationService.InvestigateAsync(
                QuestionTextBox.Text,
                investigationCancellation.Token);
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
            _logger.LogWarning(
                "Investigation request failed. ExceptionType={ExceptionType} Category={Category} InnerExceptionType={InnerExceptionType} ProviderStatusCode={ProviderStatusCode}.",
                exception.GetType().Name,
                exception.Category,
                exception.InnerException?.GetType().Name ?? "none",
                exception.ProviderStatusCode?.ToString() ?? "none");
            StatusTextBlock.Text = exception.Category switch
            {
                LanguageModelFailureCategory.ProviderNotConfigured =>
                    "OpenAI is not configured. Set AEGIS_OPENAI_API_KEY for local development.",
                LanguageModelFailureCategory.AuthenticationRejected =>
                    "OpenAI authentication was rejected.",
                LanguageModelFailureCategory.ProviderRejected =>
                    "OpenAI rejected the investigation request. See History for the recorded failure stage.",
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
        catch (AgentRuntimeException exception)
        {
            _logger.LogWarning(
                "Investigation request failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
        }
        catch (InvestigationPersistenceException)
        {
            _logger.LogWarning("Investigation history could not be persisted.");
            StatusTextBlock.Text = "Investigation history is unavailable right now.";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Unexpected investigation failure. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
        }
        finally
        {
            if (ReferenceEquals(_activeInvestigationCancellation, investigationCancellation))
            {
                _activeInvestigationCancellation = null;
            }

            investigationCancellation.Dispose();
            BusyIndicator.IsActive = false;
            InvestigateButton.IsEnabled = true;
            NewInvestigationButton.IsEnabled = true;
            QuestionTextBox.IsEnabled = true;
            CancelInvestigationButton.IsEnabled = false;
            CancelInvestigationButton.Content = "Cancel";
            await RefreshHistoryAsync();
        }
    }

    private void CancelInvestigationButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = _activeInvestigationCancellation;
        if (cancellation is null)
        {
            return;
        }

        CancelInvestigationButton.IsEnabled = false;
        CancelInvestigationButton.Content = "Cancelling…";
        StatusTextBlock.Text = "Cancelling…";
        cancellation.Cancel();
    }

    private void QuestionTextBox_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetQuestionTextBoxBackground("AegisQuestionHoverBrush");
    }

    private void QuestionTextBox_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetQuestionTextBoxBackground("AegisQuestionBackgroundBrush");
    }

    private void SetQuestionTextBoxBackground(string resourceKey)
    {
        if (QuestionTextBox.IsEnabled && GetBrush(resourceKey) is { } brush)
        {
            QuestionTextBox.Background = brush;
        }
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshHistoryAsync();

    private async void DeleteInvestigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInvestigation is null)
        {
            return;
        }

        var investigation = _selectedInvestigation.Investigation;
        if (!await ConfirmDestructiveActionAsync(
                "Delete investigation",
                "This deletes the selected investigation and its saved Aegis history. Baselines are never deleted.",
                "Delete"))
        {
            return;
        }

        DeleteInvestigationButton.IsEnabled = false;
        try
        {
            var result = await _historyService.DeleteInvestigationAsync(investigation.InvestigationId);
            var status = result.Status switch
            {
                InvestigationDeletionStatus.Deleted => "Investigation deleted.",
                InvestigationDeletionStatus.NotFound => "That investigation was already deleted.",
                InvestigationDeletionStatus.NotTerminal =>
                    "This investigation cannot be deleted until it reaches a terminal state.",
                InvestigationDeletionStatus.BaselineProtected =>
                    "This investigation is required as provenance for a baseline and was preserved.",
                _ => "The investigation deletion result was not recognized."
            };

            var refreshed = await RefreshHistoryAsync();
            if (result.Status == InvestigationDeletionStatus.Deleted)
            {
                HistoryListView.SelectedItem = null;
                ClearHistoryDetails();
            }

            HistoryActionStatusTextBlock.Text = refreshed
                ? status
                : $"{status} History could not be refreshed.";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Investigation deletion failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            var refreshed = await RefreshHistoryAsync();
            HistoryActionStatusTextBlock.Text = refreshed
                ? "Investigation deletion failed. History was refreshed from persistence."
                : "Investigation deletion failed, and current history could not be refreshed.";
        }
        finally
        {
            DeleteInvestigationButton.IsEnabled = _selectedInvestigation is not null;
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDestructiveActionAsync(
                "Clear history",
                "This deletes all terminal investigation history that is not required as baseline provenance. Baselines, active investigations, and their provenance are preserved.",
                "Clear history"))
        {
            return;
        }

        ClearHistoryButton.IsEnabled = false;
        DeleteInvestigationButton.IsEnabled = false;
        try
        {
            var result = await _historyService.ClearHistoryAsync();
            var preservedCount = result.BaselineProtectedCount + result.NonTerminalPreservedCount;
            var refreshed = await RefreshHistoryAsync();
            HistoryListView.SelectedItem = null;
            ClearHistoryDetails();
            HistoryActionStatusTextBlock.Text = refreshed
                ? $"Deleted {result.DeletedCount} investigation{(result.DeletedCount == 1 ? "" : "s")}. " +
                  $"Preserved {preservedCount} ({result.BaselineProtectedCount} baseline-protected, " +
                  $"{result.NonTerminalPreservedCount} non-terminal)."
                : "Clear history completed, but History could not be refreshed.";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Clear history failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            var refreshed = await RefreshHistoryAsync();
            HistoryActionStatusTextBlock.Text = refreshed
                ? "Clear history failed. History was refreshed from persistence."
                : "Clear history failed, and current history could not be refreshed.";
        }
        finally
        {
            ClearHistoryButton.IsEnabled = true;
            DeleteInvestigationButton.IsEnabled = _selectedInvestigation is not null;
        }
    }

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
            DeleteInvestigationButton.IsEnabled = true;

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
            _logger.LogError(
                "Investigation history could not be loaded. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
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
        DeleteInvestigationButton.IsEnabled = false;
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
            DeleteBaselineButton.IsEnabled = false;
            return;
        }

        BaselineEmptyDetailTextBlock.Visibility = Visibility.Collapsed;
        DeleteBaselineButton.IsEnabled = true;
        BaselineDetailTextBlock.Text =
            $"{baseline.Platform} {baseline.OsVersion} (build {baseline.Build?.ToString() ?? "unknown"}), " +
            $"{baseline.Architecture}" + Environment.NewLine +
            $"Source investigation: {baseline.SourceInvestigationId}" + Environment.NewLine +
            $"Created: {baseline.CreatedAtUtc.LocalDateTime:g}" + Environment.NewLine +
            $"Tool: {baseline.ToolId}";
    }

    private async void DeleteBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (BaselineListView.SelectedItem is not BaselineSummary baseline)
        {
            return;
        }

        if (!await ConfirmDestructiveActionAsync(
                "Delete baseline",
                "This deletes the selected baseline only. Its source investigation will not be deleted automatically.",
                "Delete baseline"))
        {
            return;
        }

        DeleteBaselineButton.IsEnabled = false;
        try
        {
            var result = await _baselineService.DeleteBaselineAsync(baseline.BaselineId);
            var refreshed = await RefreshHistoryAsync();
            if (result.Status is BaselineDeletionStatus.Deleted or BaselineDeletionStatus.NotFound)
            {
                BaselineListView.SelectedItem = null;
            }

            BaselineActionStatusTextBlock.Text = refreshed
                ? result.Status switch
                {
                    BaselineDeletionStatus.Deleted =>
                        "Baseline deleted. Its source investigation was preserved.",
                    BaselineDeletionStatus.NotFound =>
                        "That baseline was already deleted.",
                    _ => "The baseline deletion result was not recognized."
                }
                : "Baseline deletion completed, but current saved data could not be fully refreshed.";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Baseline deletion failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            var refreshed = await RefreshHistoryAsync();
            BaselineActionStatusTextBlock.Text = refreshed
                ? "Baseline deletion failed. Saved data was refreshed from persistence."
                : "Baseline deletion failed, and current saved data could not be fully refreshed.";
        }
        finally
        {
            DeleteBaselineButton.IsEnabled = BaselineListView.SelectedItem is BaselineSummary;
        }
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
            _logger.LogWarning(
                "Baseline creation failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            BaselineStatusTextBlock.Text = "The selected observation is not eligible for a baseline.";
        }
    }

    private async void ClearSavedHistoryAndBaselinesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDestructiveActionAsync(
                "Clear saved history and baselines",
                "All saved baselines and all terminal investigation history will be deleted. Created and Running investigations will remain. This removes Aegis-saved records, not Windows or system data.",
                "Clear saved data"))
        {
            return;
        }

        ClearSavedHistoryAndBaselinesButton.IsEnabled = false;
        ClearHistoryButton.IsEnabled = false;
        DeleteBaselineButton.IsEnabled = false;
        DeleteInvestigationButton.IsEnabled = false;
        try
        {
            var result = await _historyService.ClearSavedHistoryAndBaselinesAsync();
            var refreshed = await RefreshHistoryAsync();
            HistoryListView.SelectedItem = null;
            ClearHistoryDetails();
            BaselineListView.SelectedItem = null;
            SavedDataStatusTextBlock.Text = refreshed
                ? $"Deleted {result.BaselinesDeletedCount} baseline{(result.BaselinesDeletedCount == 1 ? "" : "s")} and " +
                  $"{result.InvestigationsDeletedCount} terminal investigation{(result.InvestigationsDeletedCount == 1 ? "" : "s")}. " +
                  $"Preserved {result.NonTerminalPreservedCount} non-terminal investigation{(result.NonTerminalPreservedCount == 1 ? "" : "s")}."
                : "Saved-data cleanup completed, but current saved data could not be fully refreshed.";
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Saved history and baseline cleanup failed. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            var refreshed = await RefreshHistoryAsync();
            SavedDataStatusTextBlock.Text = refreshed
                ? "Saved-data cleanup failed. Current saved data was refreshed from persistence."
                : "Saved-data cleanup failed, and current saved data could not be fully refreshed.";
        }
        finally
        {
            ClearSavedHistoryAndBaselinesButton.IsEnabled = true;
            ClearHistoryButton.IsEnabled = true;
            DeleteBaselineButton.IsEnabled = BaselineListView.SelectedItem is BaselineSummary;
            DeleteInvestigationButton.IsEnabled = _selectedInvestigation is not null;
        }
    }

    private async Task<bool> RefreshHistoryAsync()
    {
        try
        {
            HistoryListView.ItemsSource = await _historyService.ListAsync();
            return await RefreshBaselinesAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "History could not be loaded. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            HistoryEmptyDetailTextBlock.Text = "History is unavailable right now.";
            return false;
        }
    }

    private async Task<bool> ConfirmDestructiveActionAsync(
        string title,
        string message,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            XamlRoot = RootNavigationView.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> RefreshBaselinesAsync()
    {
        try
        {
            var baselines = await _baselineService.ListAsync();
            BaselineListView.ItemsSource = baselines;
            BaselineEmptyStatePanel.Visibility = baselines.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Baselines could not be loaded. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType}.",
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none");
            BaselineEmptyStatePanel.Visibility = Visibility.Visible;
            BaselineEmptyDetailTextBlock.Text = "Baselines are unavailable right now.";
            return false;
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
                Style = GetStyle("AegisSectionHeadingTextStyle"),
                Foreground = GetBrush("AegisAccentBrush", "AccentTextFillColorPrimaryBrush")
            });

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
                    Style = GetStyle("AegisBodyTextStyle"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = GetBrush("AegisPrimaryTextBrush", "TextFillColorPrimaryBrush")
                });
                if (item.EvidenceStepIds.Count > 0)
                {
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = $"Evidence: {string.Join(", ", item.EvidenceStepIds.Select(PresentationFormatting.FormatEvidenceStepId))}",
                        Style = GetStyle("AegisEvidenceTextStyle"),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                sectionPanel.Children.Add(itemPanel);
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
                Style = GetStyle("AegisBodyTextStyle"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetBrush("AegisSecondaryTextBrush", "TextFillColorSecondaryBrush")
            }
        });
    }

    private static Brush? GetCardBrush() =>
        GetBrush("AegisCardBackgroundBrush", "CardBackgroundFillColorDefaultBrush");

    private static Style? GetStyle(string key) =>
        Application.Current.Resources.ContainsKey(key)
            ? Application.Current.Resources[key] as Style
            : null;

    private static Brush? GetSectionBrush() =>
        GetBrush("AegisSubtleSurfaceBrush", "LayerFillColorDefaultBrush") ?? GetCardBrush();

    private static Brush? GetCardStrokeBrush() =>
        GetBrush("AegisBorderBrush", "CardStrokeColorDefaultBrush");

    private static Brush? GetBrush(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Application.Current.Resources.ContainsKey(key) &&
                Application.Current.Resources[key] is Brush brush)
            {
                return brush;
            }
        }

        return null;
    }

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
