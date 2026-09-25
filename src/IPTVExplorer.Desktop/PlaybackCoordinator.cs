using IPTVExplorer.Core;
using IPTVExplorer.Player;

namespace IPTVExplorer.Desktop;

public sealed record PlaybackOpenResult(string ProviderKey, string MediaType, string MediaId, string State, string Message);

public sealed class PlaybackCoordinator(IProviderRepository providers, IProviderClientFactory clients, IPlayerService player)
{
    public async Task<PlaybackOpenResult> OpenAsync(MediaReference reference, CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(reference.ProviderKey)) throw new ArgumentException("Invalid provider key.");
        ValidateOpaqueId(reference.MediaId, nameof(reference.MediaId));
        if (reference.EpisodeId is not null) ValidateOpaqueId(reference.EpisodeId, nameof(reference.EpisodeId));
        if (reference.Extension is not null && (reference.Extension.Length is < 1 or > 20 || reference.Extension.Any(character => !char.IsAsciiLetterOrDigit(character)))) throw new ArgumentException("Invalid media extension.");
        var provider = await providers.GetAsync(reference.ProviderKey, cancellationToken) ?? throw new KeyNotFoundException("Provider was not found.");
        if (!provider.Enabled) throw new InvalidOperationException("Enable this provider before playback.");
        var client = await clients.CreateAsync(provider, cancellationToken);
        var playbackId = reference.EpisodeId ?? reference.MediaId;
        var request = new MediaRequest(reference.MediaType, playbackId, reference.MediaType == CatalogType.Series ? reference.MediaId : null, reference.Extension);
        var resolved = await client.ResolveMediaAsync(request, cancellationToken);
        await player.LoadAsync(resolved, 0, cancellationToken);
        return new PlaybackOpenResult(reference.ProviderKey, reference.MediaType.ToString().ToLowerInvariant(), playbackId, "placeholder", "Native player available in Phase 3.");
    }

    private static void ValidateOpaqueId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _)) throw new ArgumentException("Invalid opaque media reference.", parameter);
    }
}
