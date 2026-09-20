using Aegis.Core;
using Aegis.Persistence;
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
    private readonly InvestigationHistoryService _historyService;
    private readonly BaselineService _baselineService;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        _logger = _loggerFactory.CreateLogger<App>();
        var observationRegistry = new ObservationRegistry(
            [
                new WindowsSystemInfoObservationTool(),
                new WindowsPerformanceSystemObservationTool(),
                new WindowsPerformanceTopProcessesObservationTool(),
                new WindowsRecentErrorEventsObservationTool()
            ]);
        _observationRuntime = new ObservationRuntime(
            observationRegistry,
            exception => _logger.LogError(exception, "Observation tool execution failed."));

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aegis",
            "aegis.db");
        var persistenceStore = new SqliteInvestigationStore(new SqliteDatabase(databasePath));
        try
        {
            persistenceStore.Initialize();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Aegis local history storage could not be initialized.");
        }

        _historyService = new InvestigationHistoryService(persistenceStore);
        _baselineService = new BaselineService(
            persistenceStore,
            persistenceStore,
            observationRegistry);

        var apiKey = Environment.GetEnvironmentVariable("AEGIS_OPENAI_API_KEY");
        var model = Environment.GetEnvironmentVariable("AEGIS_OPENAI_MODEL");
        model = string.IsNullOrWhiteSpace(model) ? DefaultOpenAiModel : model.Trim();
        ILanguageModel languageModel;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("AEGIS_OPENAI_API_KEY is not configured; investigations will be unavailable.");
            languageModel = new UnavailableLanguageModel();
        }
        else
        {
            languageModel = new OpenAiLanguageModel(
                new SdkOpenAiChatClient(new ChatClient(model, apiKey)));
        }

        _investigationService = new InvestigationService(
            new AgentRuntime(
                new LanguageModelInvestigationPlanner(languageModel),
                languageModel,
                _observationRuntime,
                persistenceStore,
                exception => _logger.LogError(exception, "Investigation persistence failed.")));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInformation("Starting {ApplicationName} {ApplicationVersion}.", Aegis.Core.ApplicationInfo.Name, Aegis.Core.ApplicationInfo.Version);
        _window = new MainWindow(
            _investigationService,
            _logger,
            _historyService,
            _baselineService);
        _window.Activate();
    }

    private sealed class UnavailableLanguageModel : Aegis.Core.ILanguageModel
    {
        public Task<Aegis.Core.AgentDecision> CompleteAsync(
            Aegis.Core.LanguageModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new Aegis.Core.LanguageModelException("Configure AEGIS_OPENAI_API_KEY for local development.");
    }
}
