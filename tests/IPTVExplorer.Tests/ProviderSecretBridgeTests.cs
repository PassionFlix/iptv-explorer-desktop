using System.Collections.Concurrent;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Desktop;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class ProviderSecretBridgeTests
{
    [Fact]
    public async Task StalkerMacIsShownOnlyThroughNativePresenter()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new RecordingSecretStore();
        const string mac = "00:11:22:33:44:55";
        var reference = await secrets.PutAsync(new ProviderSecret(MacAddress: mac));
        var provider = await database.AddProviderAsync(ProviderType.Stalker, reference);
        var presenter = new RecordingPresenter();
        var bridge = new ProviderSecretBridge(database.Repository, secrets, presenter, NullLogger<ProviderSecretBridge>.Instance);
        var request = JsonSerializer.Serialize(new
        {
            id = "m1",
            method = "providers.showFullMac",
            @params = new { providerKey = provider.Key }
        });

        var response = await bridge.TryHandleAsync(request);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(provider.Name, presenter.ProviderName);
        Assert.Equal(mac, presenter.MacAddress);
        Assert.DoesNotContain(mac, response, StringComparison.Ordinal);
        Assert.DoesNotContain("macAddress", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task XtreamProviderCannotExposeNativeMacAction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new RecordingSecretStore();
        var reference = await secrets.PutAsync(new ProviderSecret("fixture-user", "fixture-password"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, reference);
        var presenter = new RecordingPresenter();
        var bridge = new ProviderSecretBridge(database.Repository, secrets, presenter, NullLogger<ProviderSecretBridge>.Instance);
        var request = JsonSerializer.Serialize(new
        {
            id = "m2",
            method = "providers.showFullMac",
            @params = new { providerKey = provider.Key }
        });

        var response = await bridge.TryHandleAsync(request);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(presenter.MacAddress);
        Assert.DoesNotContain("fixture-user", response, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedBridgeMethodIsIgnored()
    {
        await using var database = await TestDatabase.CreateAsync();
        var bridge = new ProviderSecretBridge(database.Repository, new RecordingSecretStore(), new RecordingPresenter(), NullLogger<ProviderSecretBridge>.Instance);
        Assert.Null(await bridge.TryHandleAsync("""{"id":"x","method":"app.getState","params":{}}"""));
    }

    private sealed class RecordingPresenter : INativeSecretPresenter
    {
        public string? ProviderName { get; private set; }
        public string? MacAddress { get; private set; }

        public Task ShowMacAsync(string providerName, string macAddress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderName = providerName;
            MacAddress = macAddress;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, ProviderSecret> _values = new(StringComparer.Ordinal);

        public Task<string> PutAsync(ProviderSecret secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"secret-{Guid.NewGuid():N}";
            _values[key] = secret;
            return Task.FromResult(key);
        }

        public Task<ProviderSecret?> GetAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(reference, out var secret);
            return Task.FromResult(secret);
        }

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(reference, out _);
            return Task.CompletedTask;
        }
    }
}
