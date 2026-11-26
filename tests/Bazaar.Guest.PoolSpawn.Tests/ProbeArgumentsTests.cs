using Bazaar.Guest.PoolSpawn;

namespace Bazaar.Guest.PoolSpawn.Tests;

public sealed class ProbeArgumentsTests
{
    private static bool Parse(out ProbeArguments? result, out ProbeParseError error, params string[] args) =>
        ProbeArguments.TryParse(args, out result, out error);

    [Fact]
    public void A_full_command_line_parses()
    {
        Assert.True(Parse(out var r, out var error, "--probe-connect", "10.231.64.5:3128", "--timeout-ms", "1000"));
        Assert.Equal(ProbeParseError.None, error);
        Assert.NotNull(r);
        Assert.Equal([10, 231, 64, 5], r.Address);
        Assert.Equal(3128, r.Port);
        Assert.Equal(1000, r.TimeoutMs);
    }

    [Fact]
    public void Probe_mode_is_chosen_by_the_first_argument_only()
    {
        Assert.True(ProbeArguments.IsProbe(["--probe-connect"]));
        Assert.True(ProbeArguments.IsProbe(["--probe-connect", "junk"]));
        Assert.False(ProbeArguments.IsProbe([]));
        Assert.False(ProbeArguments.IsProbe(["--fd3", "/a", "--probe-connect"]));
        Assert.False(ProbeArguments.IsProbe(["--fd3"]));
    }

    public static TheoryData<string[], ProbeParseError> Refusals => new()
    {
        { ["--probe-connect"], ProbeParseError.WrongShape },
        { ["--probe-connect", "10.0.0.1:80"], ProbeParseError.WrongShape },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms"], ProbeParseError.WrongShape },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "100", "extra"], ProbeParseError.WrongShape },
        { ["--probe-connect", "10.0.0.1:80", "--timeout", "100"], ProbeParseError.WrongShape },
        { ["--timeout-ms", "100", "--probe-connect", "10.0.0.1:80"], ProbeParseError.WrongShape },
        { ["--probe-connect", "example.org:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", ":80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:0", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:65536", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:-1", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.256:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "127.1:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "0x7f.0.0.1:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "[::1]:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "::1:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:80 ", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "010.0.0.1:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "1.2.3.4.5:80", "--timeout-ms", "100"], ProbeParseError.BadTarget },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "49"], ProbeParseError.BadTimeout },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "60001"], ProbeParseError.BadTimeout },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "abc"], ProbeParseError.BadTimeout },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "-100"], ProbeParseError.BadTimeout },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", string.Empty], ProbeParseError.BadTimeout },
        { ["--probe-connect", "10.0.0.1:80", "--timeout-ms", "99999999999"], ProbeParseError.BadTimeout },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Anything_else_is_refused_with_a_fixed_message(string[] args, ProbeParseError expected)
    {
        Assert.False(Parse(out var r, out var error, args));
        Assert.Null(r);
        Assert.Equal(expected, error);
        var message = ProbeArguments.Describe(error);
        Assert.NotEmpty(message);
        Assert.All(args.Where(a => a.Length > 3 && !a.StartsWith("--", StringComparison.Ordinal)), a => Assert.DoesNotContain(a, message, StringComparison.Ordinal));
    }

    [Fact]
    public void The_timeout_and_port_bounds_are_accepted()
    {
        Assert.True(Parse(out _, out _, "--probe-connect", "10.0.0.1:80", "--timeout-ms", "50"));
        Assert.True(Parse(out _, out _, "--probe-connect", "10.0.0.1:80", "--timeout-ms", "60000"));
        Assert.True(Parse(out _, out _, "--probe-connect", "10.0.0.1:1", "--timeout-ms", "100"));
        Assert.True(Parse(out _, out _, "--probe-connect", "10.0.0.1:65535", "--timeout-ms", "100"));
    }
}
