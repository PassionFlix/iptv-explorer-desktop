using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using IPTVExplorer.Core;
using Microsoft.Win32;

namespace IPTVExplorer.Desktop;

public sealed class MediaActionBridge(IProviderRepository providers, IProviderClientFactory clients)
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly ConcurrentDictionary<string, DownloadOperation> _downloads = new(StringComparer.Ordinal);

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
        catch (Exception)
        {
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

        var id = Guid.NewGuid().ToString("N");
        var operation = new DownloadOperation(id, Path.GetFileName(destination), destination, destination + ".part-" + id);
        if (!_downloads.TryAdd(id, operation)) throw new InvalidOperationException("Impossible de créer le téléchargement.");

        _ = RunDownloadAsync(operation, media);
        return new { started = true, downloadId = id, fileName = operation.FileName };
    }

    private object DownloadStatus(DownloadActionRequest input)
    {
        var operation = RequiredDownload(input.DownloadId);
        return Snapshot(operation);
    }

    private object CancelDownload(DownloadActionRequest input)
    {
        var operation = RequiredDownload(input.DownloadId);
        lock (operation.Gate)
        {
            if (operation.Status is "completed" or "failed" or "cancelled")
                return new { cancelled = operation.Status == "cancelled" };
        }

        try
        {
            operation.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        return new { cancelled = true };
    }

    private async Task RunDownloadAsync(DownloadOperation operation, ResolvedMedia media)
    {
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

            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operation.Cancellation.Token);
            response.EnsureSuccessStatusCode();

            lock (operation.Gate)
            {
                operation.TotalBytes = response.Content.Headers.ContentLength;
                operation.Status = "running";
            }

            await using var source = await response.Content.ReadAsStreamAsync(operation.Cancellation.Token);
            await using var target = new FileStream(
                operation.TemporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                useAsync: true);

            var buffer = new byte[1024 * 128];
            while (true)
            {
                var read = await source.ReadAsync(buffer, operation.Cancellation.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), operation.Cancellation.Token);
                lock (operation.Gate) operation.BytesReceived += read;
            }
            await target.FlushAsync(operation.Cancellation.Token);

            File.Move(operation.TemporaryPath, operation.DestinationPath, true);
            lock (operation.Gate)
            {
                operation.Status = "completed";
                if (operation.TotalBytes is null) operation.TotalBytes = operation.BytesReceived;
            }
        }
        catch (OperationCanceledException)
        {
            lock (operation.Gate) operation.Status = "cancelled";
        }
        catch (Exception)
        {
            lock (operation.Gate)
            {
                operation.Status = "failed";
                operation.Error = "Le téléchargement a échoué.";
            }
        }
        finally
        {
            if (File.Exists(operation.TemporaryPath))
            {
                try { File.Delete(operation.TemporaryPath); } catch { }
            }
        }
    }

    private static object Snapshot(DownloadOperation operation)
    {
        lock (operation.Gate)
        {
            var elapsed = Math.Max(0.001, operation.Timer.Elapsed.TotalSeconds);
            var bytesPerSecond = operation.Status == "cancelled" ? 0L : (long)Math.Max(0, operation.BytesReceived / elapsed);
            return new
            {
                downloadId = operation.Id,
                fileName = operation.FileName,
                status = operation.Status,
                bytesReceived = operation.BytesReceived,
                totalBytes = operation.TotalBytes,
                bytesPerSecond,
                error = operation.Error
            };
        }
    }

    private DownloadOperation RequiredDownload(string downloadId)
    {
        if (string.IsNullOrWhiteSpace(downloadId) || downloadId.Length > 64 || !_downloads.TryGetValue(downloadId, out var operation))
            throw new InvalidOperationException("Téléchargement introuvable.");
        return operation;
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

    private static MediaActionRequest RequireMediaAction(BridgeRequest request) =>
        request.Params?.Deserialize<MediaActionRequest>(Json)
        ?? throw new InvalidOperationException("La référence média est invalide.");

    private static DownloadActionRequest RequireDownloadAction(BridgeRequest request) =>
        request.Params?.Deserialize<DownloadActionRequest>(Json)
        ?? throw new InvalidOperationException("Le téléchargement est invalide.");

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
    private sealed record DownloadActionRequest(string DownloadId);

    private sealed class DownloadOperation(string id, string fileName, string destinationPath, string temporaryPath)
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string DestinationPath { get; } = destinationPath;
        public string TemporaryPath { get; } = temporaryPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        public string Status { get; set; } = "starting";
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public string? Error { get; set; }
    }
}
