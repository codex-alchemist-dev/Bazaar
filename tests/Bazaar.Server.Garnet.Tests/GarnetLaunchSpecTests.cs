using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>The launch arguments (pure, no process) and the option rules.</summary>
public class GarnetLaunchSpecTests
{
    [Fact]
    public void Arguments_bind_loopback_require_the_password_and_enable_the_append_only_file()
    {
        var options = new GarnetCacheOptions { DataDirectory = Path.Combine("data", "garnet"), MaxMemory = "512m" };

        var spec = GarnetLaunchSpec.Build(options, 6400, "s3cret", recover: false);

        Assert.Equal("Garnet.exe", spec.Executable);
        Assert.Equal("garnet-server.exe", spec.Executable);
        Assert.Equal(
            [
                "--port", "6400",
                "--bind", "127.0.0.1",
                "--auth", "Password",
                "--password", "s3cret",
                "--memory", "512m",
                "--aof",
                "--logdir", Path.Combine("data", "garnet"),
                "--checkpointdir", Path.Combine("data", "garnet", "checkpoints"),
                "--checkpointdir", Path.Combine("data", "garnet"),
            ],
            spec.Arguments);
    }

    [Fact]
    public void Recovery_and_commit_wait_are_added_only_when_asked()
    {
        var options = new GarnetCacheOptions { AofCommitWait = true };

        var restart = GarnetLaunchSpec.Build(options, 1, "p", recover: true);
        var first = GarnetLaunchSpec.Build(new GarnetCacheOptions(), 1, "p", recover: false);

        Assert.Contains("--aof-commit-wait", restart.Arguments);
        Assert.Equal("-r", restart.Arguments[^1]);
        Assert.DoesNotContain("--aof-commit-wait", first.Arguments);
        Assert.DoesNotContain("-r", first.Arguments);
    }

    [Fact]
    public void ToString_redacts_the_password()
    {
        var spec = GarnetLaunchSpec.Build(new GarnetCacheOptions(), 1, "topsecret", recover: false);

        Assert.DoesNotContain("topsecret", spec.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_are_unmanaged_with_safe_values()
    {
        var options = new GarnetCacheOptions();

        Assert.False(options.Managed);
        Assert.Equal("Garnet.exe", options.BinaryPath);
        Assert.Equal("garnet-server.exe", options.BinaryPath);
        Assert.Equal(0, options.ManagedPort);
        Assert.Equal("256m", options.MaxMemory);
        Assert.Equal("bazaar:cache:", options.KeyPrefix);
        Assert.False(options.AofCommitWait);
        Assert.Equal("Bazaar:Cache:Garnet", GarnetCacheOptions.SectionName);
        Assert.Null(options.ValidationError());
    }

    [Theory]
    [InlineData("256m", 256L << 20)]
    [InlineData("1G", 1L << 30)]
    [InlineData("64kb", 64L << 10)]
    [InlineData("1048576", 1048576)]
    public void Size_strings_parse_to_bytes(string text, long expected)
    {
        Assert.True(GarnetSize.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("abc")]
    [InlineData("m")]
    [InlineData("99999999999t")]
    public void Bad_size_strings_are_rejected(string? text)
    {
        Assert.False(GarnetSize.TryParseBytes(text, out _));
    }

    [Fact]
    public void A_managed_server_validates_its_own_settings()
    {
        Assert.Equal("MaxMemory must be a size such as 256m or 1g", new GarnetCacheOptions { Managed = true, MaxMemory = "lots" }.ValidationError());
        Assert.Equal("ManagedPort must be between 0 and 65535", new GarnetCacheOptions { Managed = true, ManagedPort = 70000 }.ValidationError());
        Assert.Equal("MemoryWarnPercent must be between 1 and 100", new GarnetCacheOptions { Managed = true, MemoryWarnPercent = 0 }.ValidationError());
        Assert.Equal("BinaryPath must be set for a managed server", new GarnetCacheOptions { Managed = true, BinaryPath = " " }.ValidationError());
        Assert.Equal("StartWindowSeconds must be between 1 and 120", new GarnetCacheOptions { StartWindowSeconds = 0 }.ValidationError());
        Assert.Equal("KeyPrefix must not be empty", new GarnetCacheOptions { KeyPrefix = "" }.ValidationError());
        Assert.Null(new GarnetCacheOptions { MaxMemory = "lots" }.ValidationError());
    }

    [Fact]
    public async Task The_launcher_refuses_to_start_a_child_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var launcher = new GarnetProcessLauncher();
        var spec = GarnetLaunchSpec.Build(new GarnetCacheOptions(), 1, "p", recover: false);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => launcher.StartAsync(spec, TestContext.Current.CancellationToken));
    }
}
