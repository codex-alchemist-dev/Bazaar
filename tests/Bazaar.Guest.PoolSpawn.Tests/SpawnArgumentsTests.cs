using Bazaar.Guest.PoolSpawn;

namespace Bazaar.Guest.PoolSpawn.Tests;

public sealed class SpawnArgumentsTests
{
    private static bool Parse(out SpawnArguments? result, out SpawnParseError error, params string[] args) =>
        SpawnArguments.TryParse(args, out result, out error);

    [Fact]
    public void A_full_command_line_parses_and_the_program_is_argv_zero()
    {
        Assert.True(Parse(out var r, out var error, "--fd3", "/run/p/a", "--fd4", "/run/p/b", "--", "/usr/bin/setpriv", "--reuid", "1", "--", "/x"));
        Assert.Equal(SpawnParseError.None, error);
        Assert.NotNull(r);
        Assert.Equal("/run/p/a", r.Fd3Path);
        Assert.Equal("/run/p/b", r.Fd4Path);
        Assert.Equal("/usr/bin/setpriv", r.Program);
        Assert.Equal(["/usr/bin/setpriv", "--reuid", "1", "--", "/x"], r.Argv);
    }

    [Fact]
    public void The_options_may_come_in_either_order()
    {
        Assert.True(Parse(out var r, out _, "--fd4", "/b", "--fd3", "/a", "--", "/bin/true"));
        Assert.Equal("/a", r!.Fd3Path);
        Assert.Equal("/b", r.Fd4Path);
    }

    [Fact]
    public void Arguments_after_the_separator_are_passed_through_untouched()
    {
        Assert.True(Parse(out var r, out _, "--fd3", "/a", "--fd4", "/b", "--", "/bin/echo", "--fd3", "../x", "a b;$HOME", string.Empty));
        Assert.Equal(["/bin/echo", "--fd3", "../x", "a b;$HOME", string.Empty], r!.Argv);
    }

    public static TheoryData<string[], SpawnParseError> Refusals => new()
    {
        { [], SpawnParseError.MissingSeparator },
        { ["--fd3", "/a", "--fd4", "/b"], SpawnParseError.MissingSeparator },
        { ["--", "/bin/true"], SpawnParseError.MissingOption },
        { ["--fd3", "/a", "--", "/bin/true"], SpawnParseError.MissingOption },
        { ["--fd4", "/b", "--", "/bin/true"], SpawnParseError.MissingOption },
        { ["--fd3", "/a", "--fd4", "/b", "--"], SpawnParseError.MissingProgram },
        { ["--fd3", "/a", "--fd4", "/b", "--", "bin/true"], SpawnParseError.BadProgram },
        { ["--fd3", "/a", "--fd4", "/b", "--", "/usr/../bin/true"], SpawnParseError.BadProgram },
        { ["--fd3", "/a", "--fd4", "/b", "--", "true"], SpawnParseError.BadProgram },
        { ["--fd3", "/a", "--fd4", "/b", "--", ""], SpawnParseError.BadProgram },
        { ["--fd3", "a", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a/../b", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a", "--fd4", "/b/..", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a//c", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a/./c", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a/", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a\nb", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a", "--fd4", "/b", "--fd4", "/c", "--", "/bin/true"], SpawnParseError.DuplicateOption },
        { ["--fd3", "/a", "--fd3", "/c", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.DuplicateOption },
        { ["--fd3", "/a", "--fd4", "/a", "--", "/bin/true"], SpawnParseError.SamePath },
        { ["--fd3", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.BadPath },
        { ["--fd3", "/a", "--fd4", "--", "/bin/true"], SpawnParseError.MissingValue },
        { ["--fd3"], SpawnParseError.MissingValue },
        { ["--fd5", "/a", "--fd3", "/a", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.UnknownOption },
        { ["--fd3=/a", "--fd4", "/b", "--", "/bin/true"], SpawnParseError.UnknownOption },
        { ["/bin/true"], SpawnParseError.UnknownOption },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Anything_else_is_refused_with_a_reason(string[] args, SpawnParseError expected)
    {
        Assert.False(SpawnArguments.TryParse(args, out var result, out var error));
        Assert.Null(result);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void An_overlong_path_is_refused()
    {
        var long1 = "/" + new string('a', SpawnArguments.MaxPathLength);
        Assert.False(SpawnArguments.IsAcceptablePath(long1));
        Assert.True(SpawnArguments.IsAcceptablePath("/" + new string('a', SpawnArguments.MaxPathLength - 1)));
    }

    [Fact]
    public void Messages_never_contain_argument_text()
    {
        const string marker = "TOPSECRETVALUE";
        foreach (var args in new[]
        {
            new[] { "--fd3", "/a/../" + marker, "--fd4", "/b", "--", "/bin/true" },
            new[] { "--" + marker, "/a" },
            new[] { "--fd3", "/a", "--fd4", "/b", "--", marker },
        })
        {
            Assert.False(SpawnArguments.TryParse(args, out _, out var error));
            Assert.DoesNotContain(marker, SpawnArguments.Describe(error), StringComparison.Ordinal);
        }

        foreach (var e in Enum.GetValues<SpawnParseError>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SpawnArguments.Describe(e)));
        }
    }

    [Fact]
    public void The_usage_exit_code_is_64()
    {
        Assert.Equal(64, SpawnArguments.UsageExitCode);
    }
}
