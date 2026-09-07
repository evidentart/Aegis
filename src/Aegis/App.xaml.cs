using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Aegis;

public partial class App : Application
{
    private readonly ILogger<App> _logger;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        using var factory = LoggerFactory.Create(builder => builder.AddDebug());
        _logger = factory.CreateLogger<App>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInformation("Starting {ApplicationName} {ApplicationVersion}.", Aegis.Core.ApplicationInfo.Name, Aegis.Core.ApplicationInfo.Version);
        _window = new MainWindow();
        _window.Activate();
    }
}
