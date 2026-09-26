using System.Net;
using System.Net.Http;
using System.Windows;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Player;
using IPTVExplorer.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            var paths = new AppPaths();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new RedactingFileLoggerProvider(paths));
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<SqliteConnectionFactory>();
            builder.Services.AddSingleton<DatabaseInitializer>();
            builder.Services.AddSingleton<ISecretStore, DpapiSecretStore>();
            builder.Services.AddSingleton<ProviderRepository>();
            builder.Services.AddSingleton<IProviderRepository>(sp => sp.GetRequiredService<ProviderRepository>());
            builder.Services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
            builder.Services.AddSingleton<RebuildJobRepository>();
            builder.Services.AddSingleton<AtomicSearchIndex>();
            builder.Services.AddSingleton<ISearchService, SearchService>();
            builder.Services.AddSingleton<IProviderClientFactory, ProviderClientFactory>();
            builder.Services.AddSingleton<ProviderOnboardingService>();
            builder.Services.AddSingleton<ProviderManagementService>();
            builder.Services.AddSingleton<IPlayerService, PlayerNotInstalledService>();
            builder.Services.AddSingleton<PlaybackCoordinator>();
            builder.Services.AddSingleton<BridgeRouter>();
            builder.Services.AddHostedService<IndexRebuildWorker>();
            builder.Services.AddHttpClient("providers", ProviderHttpRegistration.Configure)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(8),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    AllowAutoRedirect = false,
                    UseCookies = false
                });

            _host = builder.Build();
            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            await _host.StartAsync();
            var window = new MainWindow(_host.Services.GetRequiredService<BridgeRouter>());
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"IPTV Explorer Desktop could not start.\n\n{LogRedactor.Redact(exception.Message)}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
