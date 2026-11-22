using Bazaar.Server.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Hosting;

public class BazaarHostOptionsTests
{
    private static readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "bazaar");

    [Fact]
    public void Valid_configuration_binds_the_host_section()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _dataRoot,
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:BAZAAR_DB",
        });

        var options = provider.GetRequiredService<IOptions<BazaarHostOptions>>().Value;

        Assert.Equal(_dataRoot, options.DataRoot);
        Assert.Equal("env:BAZAAR_DB", options.DatabaseConnectionReference);
        Assert.Null(options.KekReference);
    }

    [Fact]
    public async Task Valid_configuration_starts_without_error()
    {
        using var provider = BuildProvider(ValidConfiguration());

        await StartHostedServicesAsync(provider);
    }

    [Fact]
    public async Task Missing_data_root_fails_start_with_the_key_name()
    {
        var config = ValidConfiguration();
        config.Remove("Bazaar:Host:DataRoot");
        using var provider = BuildProvider(config);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartHostedServicesAsync(provider));

        Assert.Contains("Bazaar:Host:DataRoot is required.", ex.Message);
    }

    [Fact]
    public async Task Relative_data_root_fails_start()
    {
        var config = ValidConfiguration();
        config["Bazaar:Host:DataRoot"] = "relative/bazaar";
        using var provider = BuildProvider(config);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartHostedServicesAsync(provider));

        Assert.Contains("Bazaar:Host:DataRoot must be an absolute path.", ex.Message);
    }

    [Fact]
    public async Task Blank_database_reference_fails_start()
    {
        var config = ValidConfiguration();
        config["Bazaar:Host:DatabaseConnectionReference"] = "   ";
        using var provider = BuildProvider(config);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartHostedServicesAsync(provider));

        Assert.Contains("Bazaar:Host:DatabaseConnectionReference is required", ex.Message);
    }

    [Fact]
    public async Task Blank_kek_reference_fails_start_but_absent_kek_is_allowed()
    {
        var config = ValidConfiguration();
        config["Bazaar:Host:KekReference"] = " ";
        using var blank = BuildProvider(config);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartHostedServicesAsync(blank));
        Assert.Contains("Bazaar:Host:KekReference must be omitted or non-empty.", ex.Message);

        using var absent = BuildProvider(ValidConfiguration());
        await StartHostedServicesAsync(absent);
    }

    [Fact]
    public async Task A_registered_kek_provider_is_opened_and_reported_configured_at_start()
    {
        var logger = new CapturingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<BazaarHostOptionsLogger>>(logger);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBazaarHostOptions(new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build());
        var opened = 0;
        services.AddSingleton<Bazaar.Server.Identity.Kek.IKekProvider>(_ =>
        {
            opened++;
            return new NoopKek();
        });
        using var provider = services.BuildServiceProvider();

        await StartHostedServicesAsync(provider);

        Assert.Equal(1, opened);
        Assert.Contains("KekConfigured=True", logger.Messages);
    }

    [Fact]
    public async Task Without_any_kek_the_start_reports_not_configured()
    {
        var logger = new CapturingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<BazaarHostOptionsLogger>>(logger);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBazaarHostOptions(new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build());
        using var provider = services.BuildServiceProvider();

        await StartHostedServicesAsync(provider);

        Assert.Contains("KekConfigured=False", logger.Messages);
    }

    private sealed class NoopKek : Bazaar.Server.Identity.Kek.IKekProvider
    {
        public string KekId => "test";

        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) => ValueTask.FromResult(dek.ToArray());

        public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) => ValueTask.FromResult(wrappedDek.ToArray());
    }

    private sealed class CapturingLogger : ILogger<BazaarHostOptionsLogger>
    {
        public string Messages { get; private set; } = "";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages += formatter(state, exception) + "\n";
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["Bazaar:Host:DataRoot"] = _dataRoot,
        ["Bazaar:Host:DatabaseConnectionReference"] = "env:BAZAAR_DB",
    };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBazaarHostOptions(configuration);
        return services.BuildServiceProvider();
    }

    // The host starts hosted services in order; ValidateOnStart fails the first start that runs validation.
    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }
    }
}
