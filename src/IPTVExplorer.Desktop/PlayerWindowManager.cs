using System.Windows;
using System.Windows.Threading;
using IPTVExplorer.Core;
using IPTVExplorer.Player;

namespace IPTVExplorer.Desktop;

public sealed class PlayerWindowManager(IPlayerService player, LiveChannelDisplayNameCache liveTitles) : IPlayerWindowManager
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private PlayerWindow? _window;

    public Task<nint> ShowAsync(CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess()) return ShowWindowCoreAsync(cancellationToken);
        return _dispatcher.InvokeAsync(() => ShowWindowCoreAsync(cancellationToken), DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public Task<nint> ShowAsync(MediaReference reference, CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess()) return ShowForReferenceCoreAsync(reference, cancellationToken);
        return _dispatcher.InvokeAsync(() => ShowForReferenceCoreAsync(reference, cancellationToken), DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public Task SetLiveSurfaceVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess()) return SetLiveSurfaceVisibleCoreAsync(visible, cancellationToken);
        return _dispatcher.InvokeAsync(() => SetLiveSurfaceVisibleCoreAsync(visible, cancellationToken), DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public void ConfigureEpisodes(
        PlayerSeriesContext? context,
        Func<PlayerEpisodeOption, CancellationToken, Task>? selectionHandler)
    {
        if (_dispatcher.CheckAccess())
        {
            _window?.ConfigureEpisodes(context, selectionHandler);
            return;
        }

        _dispatcher.BeginInvoke(() => _window?.ConfigureEpisodes(context, selectionHandler), DispatcherPriority.Normal);
    }

    private async Task<nint> ShowForReferenceCoreAsync(MediaReference reference, CancellationToken cancellationToken)
    {
        if (reference.MediaType == CatalogType.Live)
        {
            if (_window is not null)
            {
                _window.Close();
                _window = null;
            }
            if (Application.Current.MainWindow is not MainWindow mainWindow)
                throw new InvalidOperationException("La surface Live intégrée n’est pas disponible.");
            return await mainWindow.ShowIntegratedLivePlayerAsync(liveTitles.Find(reference), cancellationToken);
        }

        if (Application.Current.MainWindow is MainWindow main)
            main.SetIntegratedLivePlayerVisible(false, stopPlayback: false);

        return await ShowWindowCoreAsync(cancellationToken);
    }

    private Task SetLiveSurfaceVisibleCoreAsync(bool visible, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Application.Current.MainWindow is MainWindow main)
            main.SetIntegratedLivePlayerVisible(visible, stopPlayback: !visible);
        return Task.CompletedTask;
    }

    private async Task<nint> ShowWindowCoreAsync(CancellationToken cancellationToken)
    {
        if (_window is null)
        {
            _window = new PlayerWindow(player);
            if (Application.Current.MainWindow is { } owner && owner != _window)
            {
                _window.Owner = owner;
            }
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        else if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        return await _window.WaitForRenderHandleAsync(cancellationToken);
    }
}
