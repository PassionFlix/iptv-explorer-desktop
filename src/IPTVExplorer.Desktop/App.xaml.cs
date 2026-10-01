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
            WindowAppearance.EnableForAllWindows();

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
            builder.Services.AddSingleton<IProviderLocalData, ProviderLocalDataStore>();
            builder.Services.AddSingleton<SeriesArtworkRepository>();
            builder.Services.AddSingleton<RecentSeriesArtwork>();
            builder.Services.AddSingleton<CatalogSnapshotRepository>();
            builder.Services.AddSingleton<IStalkerLiveCatalogStore>(sp => sp.GetRequiredService<CatalogSnapshotRepository>());
            builder.Services.AddSingleton<IPlaybackDiagnosticTrace, SafePlaybackDiagnosticTrace>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<CatalogRefreshService>();
            builder.Services.AddSingleton<MediaDetailService>();
            builder.Services.AddSingleton<PlaybackHistoryRepository>();
            builder.Services.AddSingleton<IPlaybackHistoryRepository>(sp => sp.GetRequiredService<PlaybackHistoryRepository>());
            builder.Services.AddSingleton<IRemoteProviderClientFactory, ProviderClientFactory>();
            builder.Services.AddSingleton<IProviderClientFactory, LocalProviderClientFactory>();
            // Only explicit setup/settings operations receive the remote factory.
            builder.Services.AddSingleton(sp => new ProviderOnboardingService(sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<IProviderRepository>(), sp.GetRequiredService<IRemoteProviderClientFactory>()));
            builder.Services.AddSingleton(sp => new ProviderManagementService(sp.GetRequiredService<IProviderRepository>(),
                sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<IRemoteProviderClientFactory>(),
                sp.GetRequiredService<IProviderLocalData>()));
            builder.Services.AddSingleton<IPlayerService, LibMpvPlayerService>();
            builder.Services.AddSingleton<IPlayerWindowManager, PlayerWindowManager>();
            builder.Services.AddSingleton<PlaybackCoordinator>();
            builder.Services.AddSingleton<BridgeRouter>();
            builder.Services.AddSingleton<MediaDownloadManager>();
            builder.Services.AddSingleton<MediaActionBridge>();
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
            var window = new MainWindow(
                _host.Services.GetRequiredService<BridgeRouter>(),
                _host.Services.GetRequiredService<MediaActionBridge>());
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
            await _host.Services.GetRequiredService<MediaDownloadManager>().ShutdownAsync(TimeSpan.FromSeconds(5));
            await _host.Services.GetRequiredService<PlaybackCoordinator>().FlushAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            else _host.Dispose();
        }
        base.OnExit(e);
    }
}
