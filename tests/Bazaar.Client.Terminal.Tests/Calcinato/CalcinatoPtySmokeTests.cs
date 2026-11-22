using Bazaar.Client.Calcinato.Pty;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Client.Terminal.Calcinato;

namespace Bazaar.Client.Terminal.Tests.Calcinato;

/// <summary>Drives a real Unix PTY and sh through the host. Runs only on Linux, where the PTY backend exists.</summary>
public sealed class CalcinatoPtySmokeTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Needs the Linux Unix PTY backend.")]
    public async Task RealShellOutputIsDrawnInTheFrameAndCloses()
    {
        var console = new FakeConsole(new ConsoleSize(80, 6));
        var host = new TerminalHost(new UnixPtyBackend(), new NoRemoteTerminalHub());
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host, defaultCols: 80, defaultRows: 5));

        Task<int> run = calcinato.RunAsync("/bin/sh", TestContext.Current.CancellationToken);
        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");

        console.Type("i");
        console.Type("printf 'ab%s\\n' cd");
        console.Press(ConsoleKey.Enter, '\r');
        await Wait.For(() => console.Output.Contains("abcd"), "the shell's printf output is drawn in the frame");

        console.Press(ConsoleKey.Escape, '\u001b');
        console.Type(":q!");
        console.Press(ConsoleKey.Enter, '\r');
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
