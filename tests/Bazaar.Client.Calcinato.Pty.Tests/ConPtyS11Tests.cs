using System.Diagnostics;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty.Tests;

/// <summary>
/// The Windows half of spike S11, for both cmd and pwsh: colour SGR passthrough, resize, Ctrl-C and clean shutdown.
/// Windows lane only; skipped elsewhere.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("ConPty")]
public sealed class ConPtyS11Tests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private static void RequireConPty(string shell)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            Assert.Skip("ConPTY tests need Windows 10 build 17763 or later; this host is not.");
        }

        if (shell == "pwsh.exe" && !OnPath("pwsh.exe"))
        {
            Assert.Skip("pwsh.exe is not installed on this host.");
        }
    }

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, exe)));

    private static byte[] Line(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\r");

    // Each marker is typed in a form that does not contain the text it prints, so the echo of the input never matches.
    private static string ColourCommand(string shell) => shell == "cmd.exe"
        ? "for /f \"delims=\" %E in ('echo prompt $E^| cmd') do @echo %E[31mRED16%E[0m %E[38;5;196mRED256%E[0m %E[38;2;255;0;0mREDRGB%E[0m"
        : "$PSStyle.OutputRendering='Ansi'; $e=[char]27; Write-Host \"$e[31mRED16$e[0m $e[38;5;196mRED256$e[0m $e[38;2;255;0;0mREDRGB$e[0m\"";

    private static string SizeCommand(string shell) => shell == "cmd.exe"
        ? "mode con"
        : "\"W=$($Host.UI.RawUI.WindowSize.Width)x$($Host.UI.RawUI.WindowSize.Height)\"";

    private static string AliveCommand(string shell) => shell == "cmd.exe" ? "echo ALI^VE" : "('ALI'+'VE')";

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("pwsh.exe")]
    public async Task Colour_sgr_sequences_reach_the_output(string shell)
    {
        RequireConPty(shell);
        await using var session = new ConPtyBackend().Open(new TerminalSpec(shell, 100, 30));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync(Line(ColourCommand(shell)), TestContext.Current.CancellationToken);

        // ConPTY re-renders attributes, so accept the SGR forms it emits: basic, 256-colour or true colour red.
        Assert.True(await transcript.WaitForMatchAsync(@"\x1b\[(?:[0-9;]*;)?(?:31|91)m[^\x1b]*RED16", _wait), transcript.Text);
        Assert.True(await transcript.WaitForMatchAsync(@"\x1b\[(?:[0-9;]*;)?(?:38;5;196|38;2;255;0;0|31|91)m[^\x1b]*RED256", _wait), transcript.Text);
        Assert.True(await transcript.WaitForMatchAsync(@"\x1b\[(?:[0-9;]*;)?(?:38;2;255;0;0|38;5;196|31|91)m[^\x1b]*REDRGB", _wait), transcript.Text);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("pwsh.exe")]
    public async Task Resize_is_seen_by_the_shell(string shell)
    {
        RequireConPty(shell);
        await using var session = new ConPtyBackend().Open(new TerminalSpec(shell, 80, 25));
        await using var transcript = new PtyTranscript(session);

        session.Resize(100, 30);
        await session.WriteAsync(Line(SizeCommand(shell)), TestContext.Current.CancellationToken);

        var pattern = shell == "cmd.exe" ? @"Columns:\s+100" : @"W=100x30";
        Assert.True(await transcript.WaitForMatchAsync(pattern, _wait), transcript.Text);
        if (shell == "cmd.exe")
        {
            Assert.Matches(@"Lines:\s+30", transcript.Text);
        }
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("pwsh.exe")]
    public async Task Ctrl_c_stops_ping_then_the_shell_answers(string shell)
    {
        RequireConPty(shell);
        await using var session = new ConPtyBackend().Open(new TerminalSpec(shell, 100, 30));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync(Line("ping -t 127.0.0.1"), TestContext.Current.CancellationToken);
        Assert.True(await transcript.WaitForAsync("Reply from", _wait), transcript.Text);

        await session.WriteAsync(new byte[] { 0x03 }, TestContext.Current.CancellationToken);

        // Ping prints its statistics only once it has stopped. Typing before the shell has redrawn its prompt loses
        // the input on a slow runner (the console flushes pending input while it handles Ctrl-C), so wait for the
        // statistics and then give the shell a moment to redraw before sending the next command.
        Assert.True(await transcript.WaitForAsync("Ping statistics", _wait), transcript.Text);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await session.WriteAsync(Line(AliveCommand(shell)), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("ALIVE", _wait), transcript.Text);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("pwsh.exe")]
    public async Task Dispose_leaves_no_shell_or_ping_process_behind(string shell)
    {
        RequireConPty(shell);
        var before = Snapshot();
        var session = new ConPtyBackend().Open(new TerminalSpec(shell, 100, 30));
        await using (var transcript = new PtyTranscript(session))
        {
            await session.WriteAsync(Line("ping -t 127.0.0.1"), TestContext.Current.CancellationToken);
            Assert.True(await transcript.WaitForAsync("Reply from", _wait), transcript.Text);

            await session.DisposeAsync();
            await session.Exited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        var clock = Stopwatch.StartNew();
        HashSet<int> leftover;
        do
        {
            leftover = Snapshot();
            leftover.ExceptWith(before);
            if (leftover.Count == 0)
            {
                break;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        while (clock.Elapsed < TimeSpan.FromSeconds(5));

        Assert.Empty(leftover);
    }

    private static HashSet<int> Snapshot()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "cmd", "pwsh", "PING" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                ids.Add(process.Id);
                process.Dispose();
            }
        }

        return ids;
    }
}
