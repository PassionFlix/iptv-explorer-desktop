using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed record ProviderDraftInput(string Name, string ProviderType, string ServerUrl, string? Username, string? Password, string? MacAddress);
public sealed record OnboardingDiagnosticView(string Type, bool Authenticated, string Account, int Live, int Vod, int Series, long LatencyMs);
public sealed record ProviderDraftView(string Id, string Name, string RequestedType, string ServerUrl, ProviderType? DetectedType, string CredentialStatus, string? Message, OnboardingDiagnosticView? Diagnostic, IReadOnlyDictionary<string, IReadOnlyList<ProviderCategory>> Categories);
public sealed record CategoryPolicyInput(string Mode, IReadOnlyList<string> SelectedIds);
public sealed record ProviderSaveOptions(bool Enable, IReadOnlyDictionary<string, CategoryPolicyInput>? Policies = null);

public sealed partial class ProviderOnboardingService(ISecretStore secrets, IProviderRepository providers, IProviderClientFactory clients)
{
    private sealed record Draft(
        string Id,
        string Name,
        string RequestedType,
        Uri ServerUri,
        ProviderSecret Secret,
        ProviderType? DetectedType = null,
        string PortalPath = "/portal.php",
        string? Message = null,
        OnboardingDiagnosticView? Diagnostic = null,
        IReadOnlyDictionary<CatalogType, IReadOnlyList<ProviderCategory>>? Categories = null);

    private readonly ConcurrentDictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private static readonly string[] StandardPortalPaths = ["/portal.php", "/server/load.php", "/stalker_portal/server/load.php"];

    public ProviderDraftView AddDraft(ProviderDraftInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80) throw new ArgumentException("Provider name must contain 1 to 80 characters.");
        var requested = input.ProviderType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (requested is not ("auto" or "xtream" or "stalker")) throw new ArgumentException("Provider type must be auto, xtream or stalker.");
        var uri = NormalizeServerUri(input.ServerUrl);
        var secret = new ProviderSecret(input.Username?.Trim(), input.Password, input.MacAddress?.Trim().ToUpperInvariant());
        if (secret.Username?.Length > 256 || secret.Password?.Length > 1024 || secret.MacAddress?.Length > 32) throw new ArgumentException("Provider credentials exceed the supported length.");
        if (requested == "xtream" && !HasXtream(secret)) throw new ArgumentException("Username and password are required for Xtream.");
        if (requested == "stalker" && !ValidMac().IsMatch(secret.MacAddress ?? string.Empty)) throw new ArgumentException("A valid MAC is required for Stalker.");
        if (requested == "auto" && !HasXtream(secret) && !ValidMac().IsMatch(secret.MacAddress ?? string.Empty)) throw new ArgumentException("Provide one supported credential set for automatic detection.");
        var draft = new Draft(Guid.NewGuid().ToString("N"), name, requested, uri, secret);
        _drafts[draft.Id] = draft;
        return View(draft);
    }

    public async Task<ProviderDraftView> TestAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var draft = GetDraft(draftId);
        var attempts = new List<(ProviderType Type, string Portal)>();
        var failedAttempts = new List<string>();
        if (draft.RequestedType is "auto" or "xtream" && HasXtream(draft.Secret)) attempts.Add((ProviderType.Xtream, "/portal.php"));
        if (draft.RequestedType is "auto" or "stalker" && ValidMac().IsMatch(draft.Secret.MacAddress ?? string.Empty)) attempts.AddRange(StandardPortalPaths.Select(path => (ProviderType.Stalker, path)));
        foreach (var attempt in attempts)
        {
            var temporaryReference = await secrets.PutAsync(draft.Secret, cancellationToken);
            try
            {
                var candidate = new ProviderRecord("probe-provider", attempt.Type, draft.Name, draft.ServerUri, temporaryReference, attempt.Portal);
                var client = await clients.CreateAsync(candidate, cancellationToken);
                var stopwatch = Stopwatch.StartNew();
                var result = await client.TestConnectionAsync(cancellationToken);
                if (!result.Success)
                {
                    if (attempt.Type == ProviderType.Stalker) failedAttempts.Add($"{attempt.Portal} → {result.Message}");
                    continue;
                }
                var account = await client.GetAccountInfoAsync(cancellationToken);
                var live = (await client.GetLiveCategoriesAsync(cancellationToken)).ToArray();
                var vod = (await client.GetVodCategoriesAsync(cancellationToken)).ToArray();
                var series = (await client.GetSeriesCategoriesAsync(cancellationToken)).ToArray();
                stopwatch.Stop();
                var categories = new Dictionary<CatalogType, IReadOnlyList<ProviderCategory>>
                {
                    [CatalogType.Live] = live,
                    [CatalogType.Vod] = vod,
                    [CatalogType.Series] = series
                };
                var diagnostic = new OnboardingDiagnosticView(attempt.Type == ProviderType.Xtream ? "Xtream" : "Stalker / MAG", account.Authenticated, account.Status ?? "Active", live.Count(c => !c.Technical), vod.Count(c => !c.Technical), series.Count(c => !c.Technical), stopwatch.ElapsedMilliseconds);
                draft = draft with { DetectedType = attempt.Type, PortalPath = attempt.Portal, Message = "Connection successful.", Diagnostic = diagnostic, Categories = categories };
                _drafts[draft.Id] = draft;
                return View(draft);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { if (attempt.Type == ProviderType.Stalker) failedAttempts.Add($"{attempt.Portal} → connexion HTTP interrompue après le profil"); }
            catch (System.Text.Json.JsonException) { if (attempt.Type == ProviderType.Stalker) failedAttempts.Add($"{attempt.Portal} → JSON incompatible après le profil"); }
            catch (InvalidDataException) { if (attempt.Type == ProviderType.Stalker) failedAttempts.Add($"{attempt.Portal} → réponse incompatible après le profil"); }
            finally { await secrets.DeleteAsync(temporaryReference, CancellationToken.None); }
        }
        var failure = draft.RequestedType switch
        {
            "stalker" => "Impossible d’établir une session Stalker/MAG avec ce portail. Aucun endpoint Stalker standard compatible n’a répondu.",
            "auto" when ValidMac().IsMatch(draft.Secret.MacAddress ?? string.Empty) => "Impossible d’établir une session compatible avec ce portail. Aucun endpoint Xtream ou Stalker/MAG standard n’a accepté les identifiants fournis.",
            _ => "No compatible endpoint accepted the supplied credentials."
        };
        if (failedAttempts.Count > 0) failure += $" Détails techniques non sensibles : {string.Join(" ; ", failedAttempts)}.";
        draft = draft with { DetectedType = null, Message = failure, Diagnostic = null, Categories = null };
        _drafts[draft.Id] = draft;
        return View(draft);
    }

    public async Task<ProviderRecord> SaveAsync(string draftId, ProviderSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var draft = GetDraft(draftId);
        if (draft.DetectedType is null || draft.Categories is null) throw new InvalidOperationException("Test the provider successfully before saving.");
        options ??= new ProviderSaveOptions(false);
        var key = await UniqueKeyAsync(ProviderKey.FromName(draft.Name), cancellationToken);
        var secretReference = await secrets.PutAsync(draft.Secret, cancellationToken);
        var provider = new ProviderRecord(key, draft.DetectedType.Value, draft.Name, draft.ServerUri, secretReference, draft.PortalPath, options.Enable);
        try
        {
            await providers.AddAsync(provider, cancellationToken);
            foreach (var pair in draft.Categories)
            {
                await providers.SyncCategoriesAsync(key, pair.Key, pair.Value, cancellationToken);
                var input = PolicyFor(options, pair.Key);
                if (!Enum.TryParse<CategoryPolicyMode>(input.Mode, true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Invalid category policy mode.");
                var allowed = pair.Value.Where(category => !category.Technical).Select(category => category.RemoteId).ToHashSet(StringComparer.Ordinal);
                if (input.SelectedIds.Count > 5000 || input.SelectedIds.Any(id => !allowed.Contains(id))) throw new ArgumentException("Category selection contains an unknown id.");
                await providers.SaveCategoryPolicyAsync(key, pair.Key, new CategoryPolicy(mode, input.SelectedIds.ToHashSet(StringComparer.Ordinal)), cancellationToken);
            }
        }
        catch
        {
            try { if (await providers.GetAsync(key, CancellationToken.None) is not null) await providers.DeleteAsync(key, CancellationToken.None); } catch { }
            await secrets.DeleteAsync(secretReference, CancellationToken.None);
            throw;
        }
        _drafts.TryRemove(draft.Id, out _);
        return provider;
    }

    private static CategoryPolicyInput PolicyFor(ProviderSaveOptions options, CatalogType catalog)
    {
        if (options.Policies is not null && options.Policies.TryGetValue(catalog.ToString().ToLowerInvariant(), out var input)) return input;
        return new CategoryPolicyInput("all", []);
    }
    private Draft GetDraft(string id) => id is not null && _drafts.TryGetValue(id, out var draft) ? draft : throw new KeyNotFoundException("Onboarding draft was not found.");
    private static ProviderDraftView View(Draft draft)
    {
        var categories = draft.Categories?.ToDictionary(pair => pair.Key.ToString().ToLowerInvariant(), pair => (IReadOnlyList<ProviderCategory>)pair.Value.Where(category => !category.Technical).ToArray(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyList<ProviderCategory>>(StringComparer.OrdinalIgnoreCase);
        var credential = draft.DetectedType == ProviderType.Stalker || draft.RequestedType == "stalker" ? "MAC configured" : "Password configured";
        return new ProviderDraftView(draft.Id, draft.Name, draft.RequestedType, draft.ServerUri.ToString().TrimEnd('/'), draft.DetectedType, credential, draft.Message, draft.Diagnostic, categories);
    }
    private static bool HasXtream(ProviderSecret secret) => !string.IsNullOrWhiteSpace(secret.Username) && !string.IsNullOrEmpty(secret.Password);
    private async Task<string> UniqueKeyAsync(string seed, CancellationToken cancellationToken) { var key = seed; for (var i = 2; await providers.GetAsync(key, cancellationToken) is not null; i++) key = $"{seed[..Math.Min(seed.Length, 60)]}-{i}"; return key; }
    internal static Uri NormalizeServerUri(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || value.Length > 500) throw new ArgumentException("A valid HTTP(S) server URL without embedded credentials is required.");
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/player_api.php", StringComparison.OrdinalIgnoreCase)) path = path[..^15].TrimEnd('/');
        if (path.EndsWith("/c", StringComparison.OrdinalIgnoreCase)) path = path[..^2].TrimEnd('/');
        builder.Path = path.Length == 0 ? "/" : path;
        return new Uri(builder.Uri.ToString().TrimEnd('/'));
    }
    [GeneratedRegex("^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$")] internal static partial Regex ValidMac();
}
