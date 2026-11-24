using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Bazaar.Server.Identity.Kek.OpenBao.Tests;

/// <summary>A flat key/value configuration for tests. Keys use the <c>:</c> separator, as configuration does.</summary>
public sealed class FakeConfiguration(IReadOnlyDictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.TryGetValue(key, out var value) ? value : null;
        set => throw new NotSupportedException();
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => throw new NotSupportedException();

    public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
}
