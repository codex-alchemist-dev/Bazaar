using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty.Tests;

/// <summary>
/// ConPTY checks from the S11 Windows harness. They run on the Windows lane only (doc 52, section 8). This container is
/// Linux, so these tests are skipped here and have not been run in this environment.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("ConPty")]
public sealed class ConPtyBackendTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private static void RequireConPty()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            Assert.Skip("ConPTY tests need Windows 10 build 17763 or later; this host is not.");
        }
    }

    [Fact]
    public void Backend_reports_supported_on_windows_10_1809_or_later()
    {
        RequireConPty();

        Assert.True(new ConPtyBackend().IsSupported);
    }

    [Fact]
    public async Task Marker_echo_reaches_the_output_in_cmd()
    {
        RequireConPty();
        await using var session = new ConPtyBackend().Open(new TerminalSpec("cmd.exe", 80, 25));
        await using var transcript = new PtyTranscript(session);

        // At the prompt %X% is expanded before set runs, so "call echo %^X%" defers it. The typed text never contains the result.
        await session.WriteAsync("set /a X=6*7 & call echo S11_RESULT_%^X%\r\n"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("S11_RESULT_42", _wait), transcript.Text);
    }

    [Fact]
    public async Task Resize_is_seen_by_cmd()
    {
        RequireConPty();
        await using var session = new ConPtyBackend().Open(new TerminalSpec("cmd.exe", 80, 25));
        await using var transcript = new PtyTranscript(session);

        session.Resize(100, 30);
        await session.WriteAsync("mode con\r\n"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForMatchAsync(@"Columns:\s+100", _wait), transcript.Text);
    }

    [Fact]
    public async Task Ctrl_c_stops_ping_and_the_shell_stays_alive()
    {
        RequireConPty();
        await using var session = new ConPtyBackend().Open(new TerminalSpec("cmd.exe", 80, 25));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync("ping -t 127.0.0.1\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await session.WriteAsync(new byte[] { 0x03 }, TestContext.Current.CancellationToken);
        await session.WriteAsync("set /a X=1+1 & call echo CTRLC_%^X%\r\n"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("CTRLC_2", _wait), transcript.Text);
    }

    [Fact]
    public async Task Dispose_ends_the_shell()
    {
        RequireConPty();
        var session = new ConPtyBackend().Open(new TerminalSpec("cmd.exe", 80, 25));

        await session.DisposeAsync();

        var code = await session.Exited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(code >= -1);
    }
}
