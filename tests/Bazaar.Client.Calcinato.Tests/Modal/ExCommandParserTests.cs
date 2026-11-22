#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Tests.Modal;

public sealed class ExCommandParserTests
{
    [Theory]
    [InlineData("q", ExCommandKind.Quit)]
    [InlineData("q!", ExCommandKind.QuitForce)]
    [InlineData("vsplit", ExCommandKind.VSplit)]
    [InlineData("split", ExCommandKind.Split)]
    [InlineData("detach", ExCommandKind.Detach)]
    [InlineData("take", ExCommandKind.Take)]
    [InlineData("release", ExCommandKind.Release)]
    [InlineData("interrupt", ExCommandKind.Interrupt)]
    [InlineData("eof", ExCommandKind.Eof)]
    [InlineData("suspend", ExCommandKind.Suspend)]
    [InlineData("clear", ExCommandKind.Clear)]
    public void NoArgumentCommandsParseToTheirKind(string text, ExCommandKind kind)
    {
        var result = ExCommandParser.Parse(text);

        Assert.True(result.Succeeded);
        Assert.Equal(new ExCommand(kind), result.Command);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("new", ExCommandKind.New, null)]
    [InlineData("new bash", ExCommandKind.New, "bash")]
    [InlineData("new  /usr/bin/zsh -l ", ExCommandKind.New, "/usr/bin/zsh -l")]
    [InlineData("theme", ExCommandKind.Theme, null)]
    [InlineData("theme dark", ExCommandKind.Theme, "dark")]
    [InlineData("help", ExCommandKind.Help, null)]
    [InlineData("help set", ExCommandKind.Help, "set")]
    public void OptionalArgumentCommands(string text, ExCommandKind kind, string? argument)
    {
        var result = ExCommandParser.Parse(text);

        Assert.Equal(new ExCommand(kind, argument), result.Command);
    }

    [Theory]
    [InlineData("attach abc123", "abc123")]
    [InlineData("  attach   term-7  ", "term-7")]
    public void AttachTakesOneTerminalId(string text, string id)
    {
        var result = ExCommandParser.Parse(text);

        Assert.Equal(new ExCommand(ExCommandKind.Attach, id), result.Command);
    }

    [Theory]
    [InlineData("set scrollback", "scrollback", null)]
    [InlineData("set scrollback=5000", "scrollback", "5000")]
    [InlineData("set font-size=12", "font-size", "12")]
    [InlineData("set empty=", "empty", "")]
    [InlineData("set theme=dark mode", "theme", "dark mode")]
    public void SetSplitsOptionAndValue(string text, string option, string? value)
    {
        var result = ExCommandParser.Parse(text);

        Assert.Equal(new ExCommand(ExCommandKind.Set, option, value), result.Command);
    }

    [Theory]
    [InlineData("crash list", ExCommandKind.CrashList, null)]
    [InlineData("crash preview 1", ExCommandKind.CrashPreview, 1)]
    [InlineData("crash send 12", ExCommandKind.CrashSend, 12)]
    [InlineData("crash drop 007", ExCommandKind.CrashDrop, 7)]
    public void CrashSubcommandsParse(string text, ExCommandKind kind, int? number)
    {
        var result = ExCommandParser.Parse(text);

        Assert.Equal(new ExCommand(kind, Number: number), result.Command);
    }

    [Theory]
    [InlineData("", "No command given.")]
    [InlineData("   ", "No command given.")]
    [InlineData("foo", "E492: Not an editor command: foo")]
    [InlineData("Q", "E492: Not an editor command: Q")]
    [InlineData("qu", "E492: Not an editor command: qu")]
    [InlineData("quit", "E492: Not an editor command: quit")]
    [InlineData("q x", "q takes no arguments.")]
    [InlineData("q!x", "E492: Not an editor command: q!x")]
    [InlineData("vsplit left", "vsplit takes no arguments.")]
    [InlineData("split now", "split takes no arguments.")]
    [InlineData("detach all", "detach takes no arguments.")]
    [InlineData("take it", "take takes no arguments.")]
    [InlineData("release it", "release takes no arguments.")]
    [InlineData("clear all", "clear takes no arguments.")]
    [InlineData("interrupt now", "interrupt takes no arguments.")]
    [InlineData("eof now", "eof takes no arguments.")]
    [InlineData("suspend now", "suspend takes no arguments.")]
    [InlineData("attach", "attach needs a terminal id.")]
    [InlineData("attach a b", "attach takes one terminal id.")]
    [InlineData("set", "set needs an option name, for example set scrollback=5000.")]
    [InlineData("set =5", "Invalid option name: ")]
    [InlineData("set Scrollback=5", "Invalid option name: Scrollback")]
    [InlineData("set 1x=5", "Invalid option name: 1x")]
    [InlineData("set a_b", "Invalid option name: a_b")]
    [InlineData("crash", "crash needs list, preview N, send N or drop N.")]
    [InlineData("crash list 1", "crash list takes no number.")]
    [InlineData("crash preview", "crash preview needs one crash number.")]
    [InlineData("crash send 1 2", "crash send needs one crash number.")]
    [InlineData("crash drop 0", "The crash number must be a positive whole number.")]
    [InlineData("crash drop -1", "The crash number must be a positive whole number.")]
    [InlineData("crash preview +2", "The crash number must be a positive whole number.")]
    [InlineData("crash preview two", "The crash number must be a positive whole number.")]
    [InlineData("crash explode 1", "Unknown crash subcommand: explode")]
    public void MalformedInputIsAnErrorAndNeverACommand(string text, string error)
    {
        var result = ExCommandParser.Parse(text);

        Assert.False(result.Succeeded);
        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
        Assert.StartsWith(error, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NullInputIsRejectedByArgumentCheck()
    {
        Assert.Throws<ArgumentNullException>(() => ExCommandParser.Parse(null!));
    }
}
