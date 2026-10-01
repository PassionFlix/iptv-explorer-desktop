using System.IO;
using System.Text.Json;
using System.Windows;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IPTVExplorer.Desktop;

public sealed class MediaActionBridge(
    IProviderRepository providers,
    IProviderClientFactory clients,
    MediaDownloadManager downloads,
    ILogger<MediaActionBridge> logger)
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

        if (request.Method is not ("media.copyLink" or "media.download.start" or "media.download.status" or "media.download.cancel"))
            return null;

        try
        {
            object result = request.Method switch
            {
                "media.copyLink" => await CopyLinkRequestAsync(RequireMediaAction(request), cancellationToken),
                "media.download.start" => await StartDownloadRequestAsync(RequireMediaAction(request), cancellationToken),
                "media.download.status" => DownloadStatus(RequireDownloadAction(request)),
                "media.download.cancel" => CancelDownload(RequireDownloadAction(request)),
                _ => throw new InvalidOperationException("Action média inconnue.")
            };

            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, result));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Media action {Method} failed: {SafeError}", request.Method, LogRedactor.Redact(exception.Message));
            var error = request.Method.StartsWith("media.download", StringComparison.Ordinal)
                ? "Impossible de télécharger ce média."
                : "Impossible de copier le lien de ce média.";
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: error));
        }
    }

    private async Task<object> CopyLinkRequestAsync(MediaActionRequest input, CancellationToken cancellationToken)
    {
        Validate(input);
        var media = await ResolveAsync(input, cancellationToken);
        await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetText(media.Uri.AbsoluteUri));
        return new { copied = true };
    }

    private async Task<object> StartDownloadRequestAsync(MediaActionRequest input, CancellationToken cancellationToken)
    {
        Validate(input);
        var media = await ResolveAsync(input, cancellationToken);
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

        var operation = downloads.Start(Path.GetFileName(destination), destination, media);
        return new { started = true, downloadId = operation.DownloadId, fileName = operation.FileName };
    }

    private object DownloadStatus(DownloadActionRequest input)
    {
        return downloads.Get(input.DownloadId) ?? throw new InvalidOperationException("Téléchargement introuvable.");
    }

    private object CancelDownload(DownloadActionRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.DownloadId) || input.DownloadId.Length > 64 || downloads.Get(input.DownloadId) is null)
            throw new InvalidOperationException("Téléchargement introuvable.");
        return new { cancelled = downloads.Cancel(input.DownloadId) };
    }

    private async Task<ResolvedMedia> ResolveAsync(MediaActionRequest input, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(input.ProviderKey, cancellationToken)
            ?? throw new InvalidOperationException("Fournisseur introuvable.");
        if (!provider.Enabled) throw new InvalidOperationException("Fournisseur désactivé.");

        var catalog = (input.MediaType ?? "vod").Trim().ToLowerInvariant() switch
        {
            "vod" => CatalogType.Vod,
            "series" => CatalogType.Series,
            _ => throw new InvalidOperationException("Type de média invalide.")
        };
        var playbackId = catalog == CatalogType.Series ? input.EpisodeId! : input.MediaId;
        var seriesId = catalog == CatalogType.Series ? input.MediaId : null;

        var client = await clients.CreateAsync(provider, cancellationToken);
        return await client.ResolveMediaAsync(
            new MediaRequest(catalog, playbackId, seriesId, NormalizeExtension(input.Extension)),
            cancellationToken);
    }

    private static MediaActionRequest RequireMediaAction(BridgeRequest request) =>
        request.Params?.Deserialize<MediaActionRequest>(Json)
        ?? throw new InvalidOperationException("La référence média est invalide.");

    private static DownloadActionRequest RequireDownloadAction(BridgeRequest request) =>
        request.Params?.Deserialize<DownloadActionRequest>(Json)
        ?? throw new InvalidOperationException("Le téléchargement est invalide.");

    private static void Validate(MediaActionRequest input)
    {
        if (!ProviderKey.IsValid(input.ProviderKey)) throw new InvalidOperationException("Fournisseur invalide.");
        ValidateOpaqueId(input.MediaId);
        var type = (input.MediaType ?? "vod").Trim().ToLowerInvariant();
        if (type is not ("vod" or "series")) throw new InvalidOperationException("Type de média invalide.");
        if (type == "series") ValidateOpaqueId(input.EpisodeId);
    }

    private static void ValidateOpaqueId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _))
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

    private sealed record MediaActionRequest(
        string ProviderKey,
        string MediaId,
        string? MediaType = null,
        string? EpisodeId = null,
        string? Extension = null,
        string? SuggestedName = null);
    private sealed record DownloadActionRequest(string DownloadId);

}
