using Bazaar.Client.Terminal;

namespace Bazaar.Client.Terminal.Tests;

public sealed class TerminalArgsTests
{
    private const string _gcText = "6f1c2b0e-3d4a-4b8e-9c1f-2a3b4c5d6e7f";

    private static readonly Guid _agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void ParsesAllOptionsFromArguments()
    {
        var o = TerminalArgs.Parse(
            ["--server", "ws://localhost:5000/ws", "--token", "tok", "--gc", _gcText, "--name", "Owner"],
            NoEnv);

        Assert.Equal(new Uri("ws://localhost:5000/ws"), o.Server);
        Assert.Equal("tok", o.Token);
        Assert.Equal(Guid.Parse(_gcText), o.GcId);
        Assert.Equal("Owner", o.Name);
        Assert.Empty(o.Members);
    }

    [Fact]
    public void AcceptsEqualsFormAndWss()
    {
        var o = TerminalArgs.Parse([$"--server=wss://example.test/ws", "--token=t", $"--gc={_gcText}"], NoEnv);

        Assert.Equal("wss", o.Server.Scheme);
        Assert.Equal("you", o.Name);
    }

    [Fact]
    public void FallsBackToEnvironmentAndArgumentsWin()
    {
        var env = Env(("BAZAAR_SERVER", "ws://env/ws"), ("BAZAAR_TOKEN", "env-token"), ("BAZAAR_GC", _gcText));

        var fromEnv = TerminalArgs.Parse([], env);
        Assert.Equal("ws://env/ws", fromEnv.Server.ToString());
        Assert.Equal("env-token", fromEnv.Token);

        var overridden = TerminalArgs.Parse(["--server", "ws://arg/ws", "--token", "arg-token"], env);
        Assert.Equal("ws://arg/ws", overridden.Server.ToString());
        Assert.Equal("arg-token", overridden.Token);
    }

    [Fact]
    public void ParsesMembersAndMarksAgents()
    {
        var o = TerminalArgs.Parse(
            ["--server", "ws://h/ws", "--token", "t", "--gc", _gcText,
             "--member", "aaaaaaaa-0000-0000-0000-000000000001=Dana",
             $"--agent={_agentId}=Charles"],
            NoEnv);

        Assert.Equal(new Speaker("Dana", IsAgent: false), o.Members[Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")]);
        Assert.Equal(new Speaker("Charles", IsAgent: true), o.Members[_agentId]);
    }

    [Theory]
    [InlineData("http://h/ws")]
    [InlineData("ws-but-not-a-uri")]
    [InlineData("/relative/ws")]
    public void RejectsNonWebSocketServers(string server)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TerminalArgs.Parse(["--server", server, "--token", "t", "--gc", _gcText], NoEnv));

        Assert.Contains("ws://", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresServerTokenAndGc()
    {
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["--token", "t", "--gc", _gcText], NoEnv));
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["--server", "ws://h/ws", "--gc", _gcText], NoEnv));
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "  ", "--gc", _gcText], NoEnv));
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "t"], NoEnv));
    }

    [Fact]
    public void RejectsBadGcId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "t", "--gc", "not-a-guid"], NoEnv));

        Assert.Contains("GUID", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus", "x")]
    [InlineData("--member", "not-a-guid=Name")]
    [InlineData("--member", "aaaaaaaa-0000-0000-0000-000000000001=")]
    [InlineData("--member", "aaaaaaaa-0000-0000-0000-000000000001")]
    public void RejectsUnknownOrMalformedOptions(string option, string value)
    {
        Assert.Throws<ArgumentException>(() =>
            TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "t", "--gc", _gcText, option, value], NoEnv));
    }

    [Fact]
    public void RejectsOptionWithoutValueAndStrayArguments()
    {
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["--server", "ws://h/ws", "--token"], NoEnv));
        Assert.Throws<ArgumentException>(() => TerminalArgs.Parse(["stray", "--server", "ws://h/ws"], NoEnv));
    }

    [Fact]
    public void BlankNameFallsBackToYou()
    {
        var o = TerminalArgs.Parse(["--server", "ws://h/ws", "--token", "t", "--gc", _gcText, "--name", "   "], NoEnv);

        Assert.Equal("you", o.Name);
    }
}
