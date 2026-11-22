using Bazaar.Server.Identity.GitHubApp;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class GitHubAppTokenRequestTests
{
    private static IReadOnlyDictionary<string, string> Contents => new Dictionary<string, string> { ["contents"] = "write" };

    [Fact]
    public void Valid_request_copies_and_deduplicates_repositories()
    {
        var request = new GitHubAppTokenRequest(42, ["Bazaar", "bazaar", "Docs"], Contents, TimeSpan.FromMinutes(10));

        Assert.Equal(["Bazaar", "Docs"], request.Repositories);
        Assert.Equal("write", request.Permissions["contents"]);
    }

    [Fact]
    public void Empty_repository_list_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new GitHubAppTokenRequest(42, [], Contents, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Blank_repository_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar", " "], Contents, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Lifetime_above_one_hour_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar"], Contents, TimeSpan.FromMinutes(61)));
    }

    [Fact]
    public void Lifetime_of_exactly_one_hour_is_accepted()
    {
        var request = new GitHubAppTokenRequest(42, ["Bazaar"], Contents, GitHubAppTokenRequest.MaxLifetime);

        Assert.Equal(TimeSpan.FromHours(1), request.Lifetime);
    }

    [Fact]
    public void Non_positive_lifetime_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar"], Contents, TimeSpan.Zero));
    }

    [Fact]
    public void Empty_permission_set_is_refused()
    {
        // An empty set would make GitHub grant every installation permission, which is wider than any lease.
        Assert.Throws<ArgumentException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar"], new Dictionary<string, string>(), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Permission_with_empty_level_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar"], new Dictionary<string, string> { ["contents"] = "" }, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Permission_with_empty_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new GitHubAppTokenRequest(42, ["Bazaar"], new Dictionary<string, string> { [""] = "read" }, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Non_positive_installation_id_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GitHubAppTokenRequest(0, ["Bazaar"], Contents, TimeSpan.FromMinutes(10)));
    }
}
