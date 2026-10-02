using System.Windows;

namespace IPTVExplorer.Desktop;

public sealed class NativeSecretPresenter : INativeSecretPresenter
{
    public async Task ShowMacAsync(string providerName, string macAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            MessageBox.Show(
                Application.Current.MainWindow,
                $"Fournisseur : {providerName}\n\nMAC : {macAddress}\n\nCette information reste affichée uniquement dans cette fenêtre native.",
                "MAC Stalker / MAG",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }
}
