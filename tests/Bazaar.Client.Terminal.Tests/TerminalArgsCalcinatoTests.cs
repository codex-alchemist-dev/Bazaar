using Bazaar.Client.Terminal;

namespace Bazaar.Client.Terminal.Tests;

public sealed class TerminalArgsCalcinatoTests
{
    private const string _gcText = "6f1c2b0e-3d4a-4b8e-9c1f-2a3b4c5d6e7f";

    [Theory]
    [InlineData("--calcinato")]
    [InlineData("calcinato", "--shell", "/bin/sh")]
    [InlineData("--shell", "/bin/sh", "--calcinato")]
    public void RecognisesTheCalcinatoFlagAndSubcommand(params string[] args)
    {
        Assert.True(TerminalArgs.IsCalcinato(args));
    }

    [Theory]
    [InlineData]
    [InlineData("--server", "ws://h/ws", "--gc", _gcText, "--token", "t")]
    [InlineData("--shell", "/bin/sh")]
    public void ChatArgumentsDoNotSelectCalcinato(params string[] args)
    {
        Assert.False(TerminalArgs.IsCalcinato(args));
    }

    [Fact]
    public void ShellIsOptionalAndDefaultsToNull()
    {
        Assert.Null(TerminalArgs.ParseCalcinato(["--calcinato"]).Shell);
        Assert.Null(TerminalArgs.ParseCalcinato(["calcinato"]).Shell);
    }

    [Fact]
    public void ShellAcceptsSpaceAndEqualsForms()
    {
        Assert.Equal("/bin/zsh", TerminalArgs.ParseCalcinato(["--calcinato", "--shell", "/bin/zsh"]).Shell);
        Assert.Equal("/bin/bash", TerminalArgs.ParseCalcinato(["calcinato", "--shell=/bin/bash"]).Shell);
        Assert.Equal("pwsh", TerminalArgs.ParseCalcinato(["--calcinato", "--shell", "  pwsh  "]).Shell);
    }

    [Fact]
    public void ShellWithoutAValueIsRejected()
    {
        var missing = Assert.Throws<ArgumentException>(() => TerminalArgs.ParseCalcinato(["--calcinato", "--shell"]));
        Assert.Contains("--shell needs a path", missing.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TerminalArgs.ParseCalcinato(["--calcinato", "--shell", "   "]));
    }

    [Fact]
    public void ChatOptionsAreRejectedInCalcinatoMode()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TerminalArgs.ParseCalcinato(["--calcinato", "--server", "ws://h/ws"]));

        Assert.Contains("'--server'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrayArgumentsAreRejectedInCalcinatoMode()
    {
        var ex = Assert.Throws<ArgumentException>(() => TerminalArgs.ParseCalcinato(["--calcinato", "stray"]));

        Assert.Contains("Unexpected argument 'stray'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageDocumentsTheCalcinatoFlags()
    {
        Assert.Contains("--calcinato", TerminalArgs.Usage, StringComparison.Ordinal);
        Assert.Contains("--shell <path>", TerminalArgs.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatParsingIsUnchangedWhenCalcinatoIsNotRequested()
    {
        var o = TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "t", "--gc", _gcText], _ => null);

        Assert.Equal(Guid.Parse(_gcText), o.GcId);
    }
}
