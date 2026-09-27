using System.Windows;
using System.Windows.Threading;
using IPTVExplorer.Player;

namespace IPTVExplorer.Desktop;

public sealed class PlayerWindowManager(IPlayerService player) : IPlayerWindowManager
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private PlayerWindow? _window;

    public Task<nint> ShowAsync(CancellationToken cancellationToken = default)
    {
        if (_dispatcher.CheckAccess()) return ShowCoreAsync(cancellationToken);
        return _dispatcher.InvokeAsync(() => ShowCoreAsync(cancellationToken), DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    private async Task<nint> ShowCoreAsync(CancellationToken cancellationToken)
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
