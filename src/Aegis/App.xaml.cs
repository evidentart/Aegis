using Aegis.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using OpenAI.Chat;

namespace Aegis;

public partial class App : Application
{
    private const string DefaultOpenAiModel = "gpt-5-mini";
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<App> _logger;
    private readonly InvestigationService _investigationService;
    private readonly ObservationRuntime _observationRuntime;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        _logger = _loggerFactory.CreateLogger<App>();
        var observationRegistry = new ObservationRegistry(
            [new WindowsSystemInfoObservationTool()]);
        _observationRuntime = new ObservationRuntime(
            observationRegistry,
            exception => _logger.LogError(exception, "Observation tool execution failed."));

        var apiKey = Environment.GetEnvironmentVariable("AEGIS_OPENAI_API_KEY");
        var model = Environment.GetEnvironmentVariable("AEGIS_OPENAI_MODEL");
        model = string.IsNullOrWhiteSpace(model) ? DefaultOpenAiModel : model.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("AEGIS_OPENAI_API_KEY is not configured; investigations will be unavailable.");
            _investigationService = new InvestigationService(new UnavailableLanguageModel());
        }
        else
        {
            _investigationService = new InvestigationService(
                new OpenAiLanguageModel(new SdkOpenAiChatClient(new ChatClient(model, apiKey))));
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInformation("Starting {ApplicationName} {ApplicationVersion}.", Aegis.Core.ApplicationInfo.Name, Aegis.Core.ApplicationInfo.Version);
        _window = new MainWindow(_investigationService, _logger);
        _window.Activate();
    }

    private sealed class UnavailableLanguageModel : Aegis.Core.ILanguageModel
    {
        public Task<Aegis.Core.LanguageModelResponse> CompleteAsync(
            Aegis.Core.LanguageModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new Aegis.Core.LanguageModelException("Configure AEGIS_OPENAI_API_KEY for local development.");
    }
}
