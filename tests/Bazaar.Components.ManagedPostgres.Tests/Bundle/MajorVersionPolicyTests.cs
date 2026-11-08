using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Tests.Bundle;

public sealed class MajorVersionPolicyTests
{
    [Fact]
    public void Same_major_is_allowed()
    {
        var decision = MajorVersionPolicy.Evaluate(19, 19);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Message);
    }

    [Fact]
    public void No_cluster_yet_is_allowed()
    {
        var decision = MajorVersionPolicy.Evaluate(19, null);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Message);
    }

    [Fact]
    public void Older_cluster_is_refused_with_the_upgrade_command()
    {
        var decision = MajorVersionPolicy.Evaluate(19, 18);

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Message);
        Assert.Contains("PostgreSQL 18", decision.Message);
        Assert.Contains("PostgreSQL 19", decision.Message);
        Assert.Contains("bazaar db upgrade", decision.Message);
    }

    [Fact]
    public void Newer_cluster_is_refused_and_names_the_bundle_to_use()
    {
        var decision = MajorVersionPolicy.Evaluate(18, 19);

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Message);
        Assert.Contains("Downgrading is not supported", decision.Message);
        Assert.Contains("PostgreSQL 19 bundle", decision.Message);
        Assert.DoesNotContain("bazaar db upgrade", decision.Message);
    }
}
