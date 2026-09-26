using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class ProviderFactoryTests
{
    [Fact]
    public async Task ProviderFactoryCreatesXtream()
    {
        var secrets = new InMemorySecretStore(); var reference = await secrets.PutAsync(new("user-demo", "password-demo"));
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        var client = await factory.CreateAsync(new("fixture-provider", ProviderType.Xtream, "Fixture", new Uri("https://example.invalid"), reference));
        Assert.IsType<XtreamProviderClient>(client);
    }

    [Fact]
    public async Task ProviderFactoryCreatesStalker()
    {
        var secrets = new InMemorySecretStore(); var reference = await secrets.PutAsync(new(MacAddress: "00:00:00:00:00:00"));
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        var client = await factory.CreateAsync(new("fixture-provider", ProviderType.Stalker, "Fixture", new Uri("https://example.invalid"), reference));
        Assert.IsType<StalkerProviderClient>(client);
    }

    [Fact]
    public async Task ProviderFactoryReusesStalkerClientForConfigurationLifetime()
    {
        var secrets = new InMemorySecretStore();
        var reference = await secrets.PutAsync(new(MacAddress: "00:00:00:00:00:00"));
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        var provider = new ProviderRecord("fixture-provider", ProviderType.Stalker, "Fixture", new Uri("https://example.invalid"), reference);

        var first = await factory.CreateAsync(provider);
        var second = await factory.CreateAsync(provider);
        var replacementReference = await secrets.PutAsync(new(MacAddress: "00:00:00:00:00:01"));
        var replacement = await factory.CreateAsync(provider with { SecretReference = replacementReference });

        Assert.Same(first, second);
        Assert.NotSame(first, replacement);
    }

    [Theory]
    [InlineData("valid-key", true)]
    [InlineData("a", true)]
    [InlineData("Invalid Key", false)]
    [InlineData("../escape", false)]
    [InlineData("-invalid", false)]
    public void ProviderKeyValidation(string value, bool expected) => Assert.Equal(expected, ProviderKey.IsValid(value));
}
