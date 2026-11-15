using System.Reflection;

namespace Bazaar.Secrets.Tests;

public sealed class CredentialIssuerTests
{
    private static readonly AgentId _alpha = new("alpha");
    private static readonly AgentId _beta = new("beta-2");

    [Theory]
    [InlineData("alpha")]
    [InlineData("beta-2")]
    [InlineData("0")]
    public async Task FailClosedIssuerNeverIssuesForAnyAgent(string agent)
    {
        FailClosedAgentCredentialIssuer issuer = new();

        CredentialIssueResult result = await issuer.IssueAsync(new AgentId(agent), CancellationToken.None);

        Assert.Equal(CredentialIssueOutcome.NotAvailable, result.Outcome);
        Assert.Equal("no credential issuer is configured", result.Detail);
    }

    [Fact]
    public async Task FailClosedIssuerNeverReturnsIssuedForDistinctAgents()
    {
        FailClosedAgentCredentialIssuer issuer = new();

        CredentialIssueResult first = await issuer.IssueAsync(_alpha, CancellationToken.None);
        CredentialIssueResult second = await issuer.IssueAsync(_beta, CancellationToken.None);

        Assert.NotEqual(CredentialIssueOutcome.Issued, first.Outcome);
        Assert.NotEqual(CredentialIssueOutcome.Issued, second.Outcome);
    }

    [Fact]
    public void CredentialIssueResultCarriesOnlyOutcomeAndDetail()
    {
        string[] properties = typeof(CredentialIssueResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Detail", "Outcome"], properties);
    }
}
