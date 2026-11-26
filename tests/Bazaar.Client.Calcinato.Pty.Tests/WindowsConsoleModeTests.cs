namespace Bazaar.Client.Calcinato.Pty.Tests;

public sealed class WindowsConsoleModeTests
{
    [Fact]
    public void Enabling_virtual_terminal_never_throws_whether_or_not_output_is_a_console()
    {
        var exception = Record.Exception(() => WindowsConsoleMode.EnableVirtualTerminal());

        Assert.Null(exception);
    }
}
