namespace IPTVExplorer.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPTV Explorer");
        Data = Path.Combine(Root, "data");
        Cache = Path.Combine(Root, "cache");
        Indexes = Path.Combine(Root, "indexes");
        Logs = Path.Combine(Root, "logs");
        Secrets = Path.Combine(Data, "secrets");
        Database = Path.Combine(Data, "iptv-explorer.sqlite");
    }

    public string Root { get; }
    public string Data { get; }
    public string Cache { get; }
    public string Indexes { get; }
    public string Logs { get; }
    public string Secrets { get; }
    public string Database { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Indexes);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Secrets);
    }

    public string SearchIndex(string providerKey)
    {
        if (!Core.ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        return Path.Combine(Indexes, $"search-{providerKey}.sqlite");
    }
}
