using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Tests.Bundle;

public sealed class ServerVersionProbeTests
{
    [Fact]
    public void Parses_a_release_version()
    {
        var version = ServerVersionProbe.Parse("postgres (PostgreSQL) 18.6");

        Assert.Equal(new ServerVersion(18, 6, null), version);
    }

    [Fact]
    public void Parses_a_pre_release_version_with_minor_zero()
    {
        var version = ServerVersionProbe.Parse("postgres (PostgreSQL) 19beta1");

        Assert.Equal(new ServerVersion(19, 0, "beta1"), version);
    }

    [Fact]
    public void Parses_a_release_candidate_with_trailing_newline()
    {
        var version = ServerVersionProbe.Parse("postgres (PostgreSQL) 19rc1\n");

        Assert.Equal(new ServerVersion(19, 0, "rc1"), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("psql (PostgreSQL) 18.6")]
    [InlineData("postgres (PostgreSQL) eighteen")]
    [InlineData("postgres 18.6")]
    [InlineData("postgres (PostgreSQL) 18.6 extra text")]
    public void Rejects_malformed_output(string output)
    {
        var ex = Assert.Throws<ServerVersionParseException>(() => ServerVersionProbe.Parse(output));

        Assert.Equal(output, ex.Output);
    }

    [Fact]
    public void Parse_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ServerVersionProbe.Parse(null!));
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(19, true)]
    [InlineData(17, false)]
    [InlineData(20, false)]
    public void IsSupportedMajor_checks_the_bundle_set(int major, bool expected)
    {
        Assert.Equal(expected, ServerVersionProbe.IsSupportedMajor(major));
    }
}
