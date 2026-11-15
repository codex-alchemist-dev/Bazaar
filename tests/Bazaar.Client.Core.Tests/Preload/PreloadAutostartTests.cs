using Bazaar.Client.Core.Preload;

namespace Bazaar.Client.Core.Tests.Preload;

public class PreloadAutostartTests
{
    [Fact]
    public void IsPreload_detects_the_flag_only_exactly()
    {
        Assert.True(PreloadArguments.IsPreload(["--panes", "--preload"]));
        Assert.False(PreloadArguments.IsPreload(["--preload-now"]));
        Assert.False(PreloadArguments.IsPreload([]));
    }

    [Fact]
    public void CommandLine_quotes_the_executable_and_adds_the_flag()
    {
        Assert.Equal("\"/opt/bazaar bin/Bazaar\" --preload", PreloadArguments.CommandLine("/opt/bazaar bin/Bazaar"));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("line\nbreak")]
    [InlineData("")]
    public void CommandLine_rejects_paths_that_would_break_quoting(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => PreloadArguments.CommandLine(path));
    }

    [Fact]
    public void SystemdUserUnit_starts_the_warm_process_for_the_default_target()
    {
        var unit = SystemdUserUnit.Render("/home/u/bin/Bazaar.Client.Linux");

        Assert.Contains("ExecStart=\"/home/u/bin/Bazaar.Client.Linux\" --preload\n", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);
        Assert.Equal("bazaar-preloader.service", SystemdUserUnit.FileName);
    }

    [Fact]
    public void RunEntry_uses_the_per_user_run_key_and_the_flag()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", PreloadRunEntry.RunKeyPath);
        Assert.Equal("\"C:\\Bazaar\\Bazaar.exe\" --preload", PreloadRunEntry.Command(@"C:\Bazaar\Bazaar.exe"));
    }
}
