using System.Net;
using System.Text.Json;
using System.Windows;
using IPTVExplorer.Core;
using Microsoft.Win32;

namespace IPTVExplorer.Desktop;

public sealed class MediaActionBridge(IProviderRepository providers, IProviderClientFactory clients)
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public async Task<string?> TryHandleAsync(string message, CancellationToken cancellationToken = default)
    {
        BridgeRequest request;
        try
        {
            request = BridgeProtocol.Parse(message);
        }
        catch (FormatException)
        {
            return null;
        }

        if (request.Method is not ("media.copyLink" or "media.download")) return null;

        try
        {
            var input = request.Params?.Deserialize<MediaActionRequest>(Json)
                ?? throw new InvalidOperationException("La référence média est invalide.");
            Validate(input);
            var media = await ResolveAsync(input, cancellationToken);

            object result = request.Method switch
            {
                "media.copyLink" => await CopyLinkAsync(media),
                "media.download" => await DownloadAsync(media, input, cancellationToken),
                _ => throw new InvalidOperationException("Action média inconnue.")
            };

            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, result));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch (Exception)
        {
            var error = request.Method == "media.download"
                ? "Impossible de télécharger ce média."
                : "Impossible de copier le lien de ce média.";
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: error));
        }
    }

    private async Task<ResolvedMedia> ResolveAsync(MediaActionRequest input, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(input.ProviderKey, cancellationToken)
            ?? throw new InvalidOperationException("Fournisseur introuvable.");
        if (!provider.Enabled) throw new InvalidOperationException("Fournisseur désactivé.");

        var client = await clients.CreateAsync(provider, cancellationToken);
        return await client.ResolveMediaAsync(
            new MediaRequest(CatalogType.Vod, input.MediaId, Extension: NormalizeExtension(input.Extension)),
            cancellationToken);
    }

    private static async Task<object> CopyLinkAsync(ResolvedMedia media)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetText(media.Uri.AbsoluteUri));
        return new { copied = true };
    }

    private static async Task<object> DownloadAsync(ResolvedMedia media, MediaActionRequest input, CancellationToken cancellationToken)
    {
        var extension = NormalizeExtension(input.Extension) ?? ExtensionFromUri(media.Uri) ?? "mp4";
        var baseName = SanitizeFileName(input.SuggestedName);
        var fileName = baseName.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase)
            ? baseName
            : $"{baseName}.{extension}";

        var destination = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = fileName,
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = $"Fichier média (*.{extension})|*.{extension}|Tous les fichiers (*.*)|*.*"
            };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        });

        if (string.IsNullOrWhiteSpace(destination)) return new { cancelled = true };

        var temporary = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                AllowAutoRedirect = true,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20)
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, media.Uri);
            if (media.Headers is not null)
            {
                foreach (var (name, value) in media.Headers)
                    request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(temporary, destination, true);
            return new { saved = true, fileName = Path.GetFileName(destination) };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(MediaActionRequest input)
    {
        if (!ProviderKey.IsValid(input.ProviderKey)) throw new InvalidOperationException("Fournisseur invalide.");
        if (string.IsNullOrWhiteSpace(input.MediaId) || input.MediaId.Length > 256)
            throw new InvalidOperationException("Média invalide.");
    }

    private static string SanitizeFileName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "video" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = source.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        if (result.Length > 140) result = result[..140].TrimEnd();
        return string.IsNullOrWhiteSpace(result) ? "video" : result;
    }

    private static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var extension = value.Trim().TrimStart('.').ToLowerInvariant();
        return extension.Length is > 0 and <= 12 && extension.All(char.IsLetterOrDigit) ? extension : null;
    }

    private static string? ExtensionFromUri(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return NormalizeExtension(extension);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record MediaActionRequest(string ProviderKey, string MediaId, string? Extension = null, string? SuggestedName = null);
}
