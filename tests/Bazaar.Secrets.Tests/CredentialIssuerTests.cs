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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            issuer.IssueAsync(new AgentId(agent), SecretScope.Installation, TimeSpan.FromMinutes(5), CancellationToken.None));

        Assert.Equal("no credential issuer is configured", ex.Message);
    }

    [Fact]
    public async Task FailClosedIssuerRevokeCompletesBecauseNothingWasIssued()
    {
        FailClosedAgentCredentialIssuer issuer = new();

        await issuer.RevokeAsync(_alpha, "issuance-1", CancellationToken.None);
        await issuer.RevokeAsync(_beta, "issuance-1", CancellationToken.None);
    }

    [Fact]
    public void AgentCredentialRedactsTheTokenInItsStringForm()
    {
        using var token = SecretValue.FromText("super-secret-token");
        var credential = new AgentCredential("issuance-1", token, SecretScope.Installation, DateTimeOffset.UnixEpoch);

        var text = credential.ToString();

        Assert.DoesNotContain("super-secret-token", text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }
}
