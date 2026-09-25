using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

public sealed class DpapiSecretStore(AppPaths paths) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPTVExplorer.Desktop/v1");

    public async Task<string> PutAsync(ProviderSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI secret storage requires Windows.");
        paths.EnsureCreated();
        var reference = Guid.NewGuid().ToString("N");
        var clear = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            var destination = SecretPath(reference);
            var temporary = destination + ".tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
            File.Move(temporary, destination, true);
            return reference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task<ProviderSecret?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI secret storage requires Windows.");
        var path = SecretPath(reference);
        if (!File.Exists(path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<ProviderSecret>(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        File.Delete(SecretPath(reference));
        return Task.CompletedTask;
    }

    private string SecretPath(string reference) => Path.Combine(paths.Secrets, reference + ".bin");
    private static void ValidateReference(string reference)
    {
        if (reference.Length != 32 || reference.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid secret reference.", nameof(reference));
    }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, ProviderSecret> _values = new(StringComparer.Ordinal);
    public Task<string> PutAsync(ProviderSecret secret, CancellationToken cancellationToken = default)
    {
        var reference = Guid.NewGuid().ToString("N");
        _values[reference] = secret;
        return Task.FromResult(reference);
    }
    public Task<ProviderSecret?> GetAsync(string reference, CancellationToken cancellationToken = default) => Task.FromResult(_values.TryGetValue(reference, out var secret) ? secret : null);
    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) { _values.TryRemove(reference, out _); return Task.CompletedTask; }
}
