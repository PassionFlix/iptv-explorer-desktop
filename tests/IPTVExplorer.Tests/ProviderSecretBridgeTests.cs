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
        var secrets = new InMemorySecretStore();
        const string mac = "00:11:22:33:44:55";
        var reference = await secrets.PutAsync(new ProviderSecret(MacAddress: mac));
        var provider = await database.AddProviderAsync(ProviderType.Stalker, reference);
        var presenter = new RecordingPresenter();
        var bridge = new ProviderSecretBridge(database.Repository, secrets, presenter, NullLogger<ProviderSecretBridge>.Instance);

        var response = await bridge.TryHandleAsync($$"""{"id":"m1","method":"providers.showFullMac","params":{"providerKey":"{{provider.Key}}"}}""");

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
        var secrets = new InMemorySecretStore();
        var reference = await secrets.PutAsync(new ProviderSecret("fixture-user", "fixture-password"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, reference);
        var presenter = new RecordingPresenter();
        var bridge = new ProviderSecretBridge(database.Repository, secrets, presenter, NullLogger<ProviderSecretBridge>.Instance);

        var response = await bridge.TryHandleAsync($$"""{"id":"m2","method":"providers.showFullMac","params":{"providerKey":"{{provider.Key}}"}}""");

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
        var bridge = new ProviderSecretBridge(database.Repository, new InMemorySecretStore(), new RecordingPresenter(), NullLogger<ProviderSecretBridge>.Instance);
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
}
