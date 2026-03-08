using NjuPrepaidStatus.Services;
using NjuPrepaidStatus.UI;

namespace NjuPrepaidStatus;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var logger = new FileLogger();
        var configService = new ConfigService(logger);
        var credentialStore = new CredentialStore();
        var parser = new NjuHtmlParser();
        var autostartService = new AutostartService();
        var app = new TrayApplicationContext(credentialStore, parser, autostartService, logger, configService);
        Application.Run(app);
    }
}
