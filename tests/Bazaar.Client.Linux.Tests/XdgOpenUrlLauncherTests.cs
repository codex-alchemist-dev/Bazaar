using System.Diagnostics;

namespace Bazaar.Client.Linux.Tests;

public class XdgOpenUrlLauncherTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("bazaar://signin?code=1")]
    public async Task Rejects_non_web_addresses_without_starting_a_process(string address)
    {
        var started = false;
        var launcher = new XdgOpenUrlLauncher(_ => started = true);

        Assert.False(await launcher.OpenAsync(new Uri(address), TestContext.Current.CancellationToken));
        Assert.False(started);
    }

    [Fact]
    public async Task Rejects_relative_addresses()
    {
        var launcher = new XdgOpenUrlLauncher(_ => true);

        Assert.False(await launcher.OpenAsync(new Uri("/signin", UriKind.Relative), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Runs_xdg_open_with_one_argument_and_no_shell()
    {
        ProcessStartInfo? seen = null;
        var launcher = new XdgOpenUrlLauncher(info =>
        {
            seen = info;
            return true;
        });

        Assert.True(await launcher.OpenAsync(new Uri("https://example.test/signin?audience=native&x=a b"), TestContext.Current.CancellationToken));

        Assert.NotNull(seen);
        Assert.Equal("xdg-open", seen.FileName);
        Assert.False(seen.UseShellExecute);
        Assert.Equal(["https://example.test/signin?audience=native&x=a%20b"], seen.ArgumentList);
    }

    [Fact]
    public async Task A_failing_start_returns_false_instead_of_throwing()
    {
        var launcher = new XdgOpenUrlLauncher(_ => throw new System.ComponentModel.Win32Exception("not found"));

        Assert.False(await launcher.OpenAsync(new Uri("http://example.test/"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Build_arguments_accepts_only_http_and_https()
    {
        Assert.NotNull(XdgOpenUrlLauncher.BuildArguments(new Uri("http://example.test/")));
        Assert.Null(XdgOpenUrlLauncher.BuildArguments(new Uri("file:///tmp/x")));
    }
}
