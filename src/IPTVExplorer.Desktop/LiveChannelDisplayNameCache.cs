using System.Collections.Concurrent;
using IPTVExplorer.Core;

namespace IPTVExplorer.Desktop;

public sealed class LiveChannelDisplayNameCache
{
    private readonly ConcurrentDictionary<(string ProviderKey, string CategoryId, string MediaId), string> _titles = new();

    public void Store(string providerKey, string categoryId, IEnumerable<CatalogItem> items)
    {
        foreach (var key in _titles.Keys.Where(key => key.ProviderKey == providerKey && key.CategoryId == categoryId))
            _titles.TryRemove(key, out _);
        foreach (var item in items)
        {
            var title = Clean(item.Title);
            if (title.Length > 0) _titles[(providerKey, categoryId, item.Id)] = title;
        }
    }

    public string Find(MediaReference reference)
    {
        if (reference.CategoryId is { Length: > 0 } categoryId &&
            _titles.TryGetValue((reference.ProviderKey, categoryId, reference.MediaId), out var title))
            return title;
        return $"Chaîne #{Clean(reference.MediaId)}";
    }

    private static string Clean(string value)
    {
        var cleaned = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length > 180 ? cleaned[..180].TrimEnd() : cleaned;
    }
}
