using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SealedPassport.Tests;

public sealed class SealedPassportComponentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-passport-di-" + Guid.NewGuid().ToString("N"));

    public SealedPassportComponentTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Registers_the_sealed_store_as_the_browser_ports_with_protective_defaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISealAuditSink>(new NullSink());
        services.AddSingleton<ISealedStore>(sp => new SealedStore(
            new SealedStoreOptions { RootPath = Path.Combine(_dir, "sealed") }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), new InMemorySealAnchor(), sp.GetRequiredService<ISealAuditSink>()));
        new SealedPassportComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<SealedPassportOptions>();
        Assert.Null(options.MountRoot);
        Assert.Contains("Cache", options.ExcludedFolders);
        Assert.IsType<SealedPassportStore>(provider.GetRequiredService<IBrowserProfileDirectory>());
        Assert.IsType<SealedBrowserCookieSource>(provider.GetRequiredService<IBrowserCookieSource>());

        var passport = provider.GetRequiredService<ISealedPassportStore>();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        // No mount root and no verifier: no plaintext folder can be opened.
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<ISealedProfileDirectories>().OpenAsync(profile.ProfileId, default));
    }

    [Fact]
    public void Reads_operator_settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:SealedPassport:MountRoot"] = "/run/bazaar/profiles",
            ["Bazaar:SealedPassport:LeaseTimeoutSeconds"] = "5",
            ["Bazaar:SealedPassport:ExcludedFolders:0"] = "Cache",
        }).Build();

        var options = SealedPassportComponent.ReadOptions(configuration.GetSection(SealedPassportOptions.SectionName));

        Assert.Equal("/run/bazaar/profiles", options.MountRoot);
        Assert.Equal(TimeSpan.FromSeconds(5), options.LeaseTimeout);
        Assert.Equal(["Cache"], options.ExcludedFolders);
    }

    private sealed class NullSink : ISealAuditSink
    {
        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
