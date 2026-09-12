using Aegis.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Aegis;

public sealed partial class MainWindow : Window
{
    private readonly InvestigationService _investigationService;
    private readonly ILogger _logger;

    public MainWindow(InvestigationService investigationService, ILogger logger)
    {
        _investigationService = investigationService;
        _logger = logger;
        InitializeComponent();
    }

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
            StatusTextBlock.Text = string.Empty;
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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected investigation failure.");
            StatusTextBlock.Text = "Investigation is unavailable right now.";
        }
        finally
        {
            BusyIndicator.IsActive = false;
            InvestigateButton.IsEnabled = true;
        }
    }
}
