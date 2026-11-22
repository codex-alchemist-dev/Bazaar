using System.ComponentModel;
using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Luau;
using Bazaar.Server.Luau.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Luau.Tests;

/// <summary>Real process runner tests. Use only standard POSIX tools on Linux and skip elsewhere where a tool is not available.</summary>
public class SystemLuauToolRunnerTests
{
    [Fact]
    public async Task A_binary_that_does_not_exist_throws_win32_exception()
    {
        var runner = new SystemLuauToolRunner();

        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(
            "bazaar-luau-no-such-binary-" + Guid.NewGuid().ToString("N"),
            [],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_process_past_its_timeout_is_killed_and_reported_timed_out()
    {
        SkipUnlessLinux();
        var runner = new SystemLuauToolRunner();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        ProcessResult result = await runner.RunAsync("sleep", ["30"], Path.GetTempPath(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Timed-out process was not killed promptly: {watch.Elapsed}.");
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_process_and_throws()
    {
        SkipUnlessLinux();
        var runner = new SystemLuauToolRunner();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("sleep", ["30"], Path.GetTempPath(), TimeSpan.FromMinutes(1), cancel.Token));
    }

    [Fact]
    public async Task Output_is_capped_at_the_runner_limit()
    {
        SkipUnlessLinux();
        var runner = new SystemLuauToolRunner(maxOutputCharacters: 1000);

        ProcessResult result = await runner.RunAsync("head", ["-c", "300000", "/dev/zero"], Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1001, result.StandardOutput.Length);
    }

    [Fact]
    public void A_non_positive_output_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemLuauToolRunner(0));
    }

    [Fact]
    public void Add_bazaar_luau_registers_the_checker_and_options()
    {
        var options = new LuauToolOptions { DefaultTimeout = TimeSpan.FromSeconds(42) };
        var services = new ServiceCollection();
        services.AddBazaarLuau(options);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<LuauChecker>(provider.GetRequiredService<ILuauChecker>());
        Assert.Same(options, provider.GetRequiredService<LuauToolOptions>());
        Assert.IsType<SystemLuauToolRunner>(provider.GetRequiredService<IProcessRunner>());
    }

    [Fact]
    public void Add_bazaar_luau_without_options_uses_defaults()
    {
        var services = new ServiceCollection();
        services.AddBazaarLuau();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal("luau-analyze", provider.GetRequiredService<LuauToolOptions>().AnalyzeBinaryPath);
    }

    private static void SkipUnlessLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("This test uses POSIX sleep and head.");
        }
    }
}
