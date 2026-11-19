using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// Chaos findings (crash-only campaign): a second host on the same database and a stop that overran the shutdown timeout both ended
/// the process with an unhandled <see cref="OperationCanceledException"/> (SIGABRT, exit 134) instead of the designed exit code.
/// These run a real generic host through <see cref="HostRun"/>, the way Program.cs does.
/// </summary>
public sealed class HostRunTests
{
    private sealed class FixedLock(bool acquire) : IInstanceLock
    {
        public bool IsHeld => acquire;

        public Task<bool> TryAcquireAsync(CancellationToken ct) => Task.FromResult(acquire);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoGate : ISchemaGate
    {
        public Task EnsureCurrentAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Like the recovery sweep: waits on the start token, so a stop requested during start cancels it.</summary>
    private sealed class HonoursStartToken : IHostedService
    {
        public bool Ran { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            Ran = true;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Like a listener whose loops outlive the stop budget: honours the stop token by throwing.</summary>
    private sealed class SlowStop : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("schema newer than build");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IHost Build(bool acquire, Action<IServiceCollection> more, TimeSpan? shutdownTimeout = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IInstanceLock>(new FixedLock(acquire));
        builder.Services.AddSingleton<ISchemaGate, NoGate>();
        builder.Services.AddSingleton(new ProviderCapabilityReport([ProviderSelector.Select(ProviderRole.Cache, ProviderSelector.None, Array.Empty<ICacheProvider>())]));
        builder.Services.AddOptions<BazaarReadinessOptions>();
        builder.Services.AddSingleton<HostState>();
        builder.Services.AddSingleton<HostExitCode>();
        builder.Services.AddHostedService<StartupSequence>();
        more(builder.Services);
        return builder.Build();
    }

    [Fact]
    public async Task A_second_instance_exits_with_code_1_and_the_rest_of_the_start_does_not_crash_the_process()
    {
        var later = new HonoursStartToken();
        using var host = Build(acquire: false, s => s.AddSingleton<IHostedService>(later));

        var code = await HostRun.RunAsync(host).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.False(later.Ran);
    }

    [Fact]
    public async Task A_stop_that_overruns_the_shutdown_timeout_returns_the_exit_code_instead_of_throwing()
    {
        using var host = Build(acquire: true, s => s.AddHostedService<SlowStop>(), TimeSpan.FromMilliseconds(200));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var code = await HostRun.RunAsync(host).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
    }

    // Seen live: RyuseiEventListener, GcNotifyListener and a third service all threw, and the host raised one AggregateException.
    [Fact]
    public async Task Several_stops_that_overrun_the_shutdown_timeout_return_the_exit_code_instead_of_throwing()
    {
        using var host = Build(acquire: true, s =>
        {
            s.AddSingleton<IHostedService, SlowStop>();
            s.AddSingleton<IHostedService, SlowStop>();
        }, TimeSpan.FromMilliseconds(200));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var code = await HostRun.RunAsync(host).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task A_real_start_failure_still_fails_the_run()
    {
        using var host = Build(acquire: true, s => s.AddHostedService<FailsToStart>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => HostRun.RunAsync(host).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }
}
