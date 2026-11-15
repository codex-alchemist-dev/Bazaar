namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgHbaRulesTests
{
    private const string _initDbTrust =
        "# TYPE  DATABASE        USER            ADDRESS                 METHOD\n" +
        "local   all             all                                     trust\n" +
        "host    all             all             127.0.0.1/32            trust\n" +
        "host    all             all             ::1/128                 trust\n" +
        "local   replication     all                                     trust\n" +
        "# host  replication     all             127.0.0.1/32            trust\n";

    [Fact]
    public void Switching_removes_every_active_trust_rule()
    {
        var switched = PgHbaRules.SwitchTrustToScram(_initDbTrust);

        Assert.False(PgHbaRules.HasTrustRule(switched));
        Assert.Equal(4, switched.Split('\n').Count(line => line.EndsWith("scram-sha-256", StringComparison.Ordinal)));
    }

    [Fact]
    public void Comments_are_kept_as_they_were()
    {
        var switched = PgHbaRules.SwitchTrustToScram(_initDbTrust);

        Assert.Contains("# host  replication     all             127.0.0.1/32            trust", switched, StringComparison.Ordinal);
    }

    [Fact]
    public void Crlf_line_endings_survive_the_switch()
    {
        var crlf = "local   all   all   trust\r\nhost    all   all   127.0.0.1/32   trust\r\n";

        var switched = PgHbaRules.SwitchTrustToScram(crlf);

        Assert.Equal("local   all   all   scram-sha-256\r\nhost    all   all   127.0.0.1/32   scram-sha-256\r\n", switched);
    }

    [Fact]
    public void A_method_that_only_ends_in_trust_is_not_a_trust_rule()
    {
        Assert.False(PgHbaRules.HasTrustRule("host all all 10.0.0.0/8 distrust\n"));
        Assert.True(PgHbaRules.HasTrustRule("host all all 10.0.0.0/8 trust\n"));
    }
}
