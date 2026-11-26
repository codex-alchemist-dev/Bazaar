using Bazaar.CompanyIdentity;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.CompanyBuild.State;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.CompanyBuild;

public sealed class CompanyBuildExecutorTests : IDisposable
{
    private static readonly string _pin = new('a', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-exec-" + Guid.NewGuid().ToString("N"));

    private static CompanyBuildPlan Plan() => new("acme", [], []);

    private CompanyBuildExecutor Create(Func<CompanyBuildTarget, string, CancellationToken, Task> step, int attempts = 1) =>
        new(
            CompanyServerIdentity.Create(new Uri("https://studio.example"), _pin),
            [new CompanyBuildTarget("one", "/p/one.csproj"), new CompanyBuildTarget("two", "/p/two.csproj")],
            Path.Combine(_root, "out"),
            Path.Combine(_root, "identity"),
            attempts,
            step);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Success_builds_every_target_and_writes_under_the_configured_root()
    {
        var executor = Create((_, dir, ct) => File.WriteAllTextAsync(Path.Combine(dir, "app.bin"), "x", ct));

        var report = await executor.RunAsync(Plan(), TestContext.Current.CancellationToken);

        Assert.True(report.Complete);
        Assert.False(report.IsSigned);
        Assert.Equal(2, report.Targets.Count);
        Assert.True(File.Exists(Path.Combine(_root, "out", "acme", "one", "app.bin")));
        Assert.True(File.Exists(Path.Combine(_root, "identity", "CompanyBuildIdentity.cs")));
    }

    [Fact]
    public async Task Incomplete_set_is_reported_without_paths_or_exception_text()
    {
        var executor = Create((t, dir, ct) => t.Id == "two"
            ? throw new InvalidOperationException("boom at /secret/path")
            : File.WriteAllTextAsync(Path.Combine(dir, "a"), "x", ct));

        var report = await executor.RunAsync(Plan(), TestContext.Current.CancellationToken);

        Assert.False(report.Complete);
        Assert.Equal(string.Empty, report.OutputDirectory);
        var failed = Assert.Single(report.Targets, t => !t.Report.Succeeded);
        Assert.Equal("two", failed.TargetId);
        Assert.All(failed.Report.FailureMessages, m => Assert.DoesNotContain("secret", m));
        Assert.Equal(string.Empty, failed.Report.IdentityPath);
        Assert.True(File.Exists(Path.Combine(_root, "out", "acme", "INCOMPLETE")));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var executor = Create(async (_, _, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RunAsync(Plan(), cts.Token));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("relative/out")]
    public void Unsafe_output_folder_is_refused(string folder) =>
        Assert.Throws<ArgumentException>(() => new CompanyBuildExecutor(
            CompanyServerIdentity.Create(new Uri("https://studio.example"), _pin), [new CompanyBuildTarget("one", "/p")], folder, Path.Combine(_root, "i")));

    [Fact]
    public void Executor_is_not_registered_until_configured()
    {
        var empty = new ServiceCollection().AddBazaarCompanyBuildExecutor(new ConfigurationBuilder().Build());
        Assert.DoesNotContain(empty, d => d.ServiceType == typeof(ICompanyBuildExecutor));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            ["Bazaar:CompanyBuild:ServerAddress"] = "https://studio.example",
            ["Bazaar:CompanyBuild:ServerKeyPin"] = _pin,
            ["Bazaar:CompanyBuild:Targets:win"] = "/p/win.csproj",
        }).Build();
        var services = new ServiceCollection().AddBazaarCompanyBuildExecutor(config);
        Assert.NotNull(services.BuildServiceProvider().GetService<ICompanyBuildExecutor>());
    }

    [Fact]
    public void Invalid_identity_setting_fails_at_start_without_echoing_the_value()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            ["Bazaar:CompanyBuild:ServerAddress"] = "http://remote.example",
            ["Bazaar:CompanyBuild:ServerKeyPin"] = _pin,
            ["Bazaar:CompanyBuild:Targets:win"] = "/p/win.csproj",
        }).Build();
        var e = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBazaarCompanyBuildExecutor(config));
        Assert.DoesNotContain("remote.example", e.Message);
    }
}
