using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Client.Terminal.Calcinato;

namespace Bazaar.Client.Terminal.Tests.Calcinato;

public sealed class CalcinatoHostTests
{
    private const string _leaveAlternateScreen = "\u001b[?1049l";
    private const string _enterAlternateScreen = "\u001b[?1049h";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TypingReachesTheShellAndOutputAppearsInTheFrame()
    {
        var console = new FakeConsole(new ConsoleSize(120, 8));
        var host = new FakeHost();
        var manager = new TerminalSessionManager(host, defaultCols: 120, defaultRows: 7);
        await using var calcinato = new CalcinatoHost(console, manager);

        Task<int> run = calcinato.RunAsync("/bin/sh", TestContext.Current.CancellationToken);

        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");
        FakeShell shell = Assert.Single(host.Shells);
        Assert.Equal("/bin/sh", Assert.Single(host.Specs).Shell);
        Assert.Equal(_enterAlternateScreen, console.Output[.._enterAlternateScreen.Length]);

        shell.Emit("$ ");
        await Wait.For(() => console.Output.Contains("$ "), "shell prompt is drawn");

        console.Type("i");
        await Wait.For(() => console.Output.Contains(" INSERT "), "Insert mode is shown");

        console.Type("echo hi");
        console.Press(ConsoleKey.Enter, '\r');
        await Wait.For(() => shell.Written == "echo hi\r", "typed text and Enter reach the shell");

        shell.Emit("hello from pty");
        await Wait.For(() => console.Output.Contains("hello from pty"), "shell output is drawn in the frame");

        int mark = console.Output.Length;
        console.Press(ConsoleKey.Escape, '\u001b');
        await Wait.For(() => console.Output[mark..].Contains(" NORMAL "), "Esc returns to Normal mode");

        mark = console.Output.Length;
        console.Type(":q");
        console.Press(ConsoleKey.Enter, '\r');
        await Wait.For(
            () => console.Output[mark..].Contains("The shell is still running"),
            ":q asks for confirmation while the shell runs");
        Assert.Single(manager.Sessions);

        console.Type(":q!");
        console.Press(ConsoleKey.Enter, '\r');

        int exitCode = await run.WaitAsync(_timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.True(shell.Disposed);
        Assert.Empty(manager.Sessions);
        Assert.EndsWith(_leaveAlternateScreen, console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlCReachesTheShellInsteadOfStoppingTheHost()
    {
        var console = new FakeConsole(new ConsoleSize(80, 6));
        var host = new FakeHost();
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host, defaultCols: 80, defaultRows: 5));

        Task<int> run = calcinato.RunAsync(null, TestContext.Current.CancellationToken);
        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");
        Assert.True(console.TreatControlCAsInput);

        FakeShell shell = host.Shells[0];
        console.Type("i");
        await Wait.For(() => console.Output.Contains(" INSERT "), "Insert mode is shown");
        console.Press(ConsoleKey.C, '\u0003', control: true);
        await Wait.For(() => shell.Written == "\u0003", "Ctrl+C is sent to the shell as byte 0x03");
        Assert.False(run.IsCompleted);

        console.Press(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        console.Type(":q!");
        console.Press(ConsoleKey.Enter, '\r');
        Assert.Equal(0, await run.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.False(console.TreatControlCAsInput);
    }

    [Fact]
    public async Task ConsoleResizeResizesTheShellAndTheFrame()
    {
        var console = new FakeConsole(new ConsoleSize(120, 8));
        var host = new FakeHost();
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host, defaultCols: 120, defaultRows: 7));

        Task<int> run = calcinato.RunAsync(null, TestContext.Current.CancellationToken);
        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");
        FakeShell shell = host.Shells[0];
        await Wait.For(() => shell.Resizes.Contains((120, 7)), "the shell starts at the console size minus the status row");

        console.Resize(new ConsoleSize(60, 6));
        await Wait.For(() => shell.Resizes.Contains((60, 5)), "a size change reaches the shell within the poll interval");

        console.Type(":q!");
        console.Press(ConsoleKey.Enter, '\r');
        Assert.Equal(0, await run.WaitAsync(_timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConsoleInputFailureRestoresTheConsoleAndThrows()
    {
        var console = new FakeConsole(new ConsoleSize(80, 6));
        var host = new FakeHost();
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host, defaultCols: 80, defaultRows: 5));

        Task<int> run = calcinato.RunAsync(null, TestContext.Current.CancellationToken);
        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");

        console.EndInput();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.EndsWith(_leaveAlternateScreen, console.Output, StringComparison.Ordinal);
        Assert.False(console.TreatControlCAsInput);
    }

    [Fact]
    public async Task CancellationReturnsZeroRestoresTheConsoleAndEndsTheShell()
    {
        var console = new FakeConsole(new ConsoleSize(80, 6));
        var host = new FakeHost();
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host, defaultCols: 80, defaultRows: 5));
        using var cts = new CancellationTokenSource();

        Task<int> run = calcinato.RunAsync(null, cts.Token);
        await Wait.For(() => console.Output.Contains(" NORMAL "), "the first frame shows NORMAL mode");
        cts.Cancel();

        Assert.Equal(0, await run.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.True(host.Shells[0].Disposed);
        Assert.EndsWith(_leaveAlternateScreen, console.Output, StringComparison.Ordinal);
        Assert.False(console.TreatControlCAsInput);
        console.EndInput();
    }

    [Fact]
    public async Task WithoutALocalBackendTheScreenIsNeverTaken()
    {
        var console = new FakeConsole(new ConsoleSize(80, 6));
        var host = new FakeHost { SupportsLocal = false };
        await using var calcinato = new CalcinatoHost(console, new TerminalSessionManager(host));

        await Assert.ThrowsAsync<NotSupportedException>(() => calcinato.RunAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, console.Output);
        Assert.False(console.TreatControlCAsInput);
    }

    [Fact]
    public async Task RemoteAttachWithoutAServerFailsWithAClearMessage()
    {
        await using var manager = new TerminalSessionManager(new TerminalHost(null, new NoRemoteTerminalHub()));

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => manager.AttachAsync("server-terminal", TestContext.Current.CancellationToken));

        Assert.Contains("server connection", ex.Message, StringComparison.Ordinal);
    }
}
