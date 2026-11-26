namespace Bazaar.Server.CodingWorkspace.Tests;

public sealed class RepoSourceTests
{
    private static CodingRepoOptions Entry(string clone, string id = "bazaar") => new()
    {
        Id = id,
        DefaultBranch = "main",
        ClonePath = clone,
        InstallationId = 11,
        Owner = "codex-alchemist-dev",
        Name = "Bazaar",
        RepositoryId = 99,
    };

    private static string Clone(Fixture fx, string name = "clone")
    {
        var path = Path.Combine(fx.Base, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Known_repository_is_returned_with_its_binding_data()
    {
        using var fx = new Fixture();
        var clone = Clone(fx);
        fx.Options.Repos.Add(Entry(clone));

        var record = await new ConfiguredCodingRepoSource(fx.Options).FindAsync("bazaar", CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal("bazaar", record.Repo.Id);
        Assert.Equal("main", record.Repo.DefaultBranch);
        Assert.Equal(Path.GetFullPath(clone), record.ClonePath);
        Assert.Equal(11, record.InstallationId);
        Assert.Equal(99, record.RepositoryId);
        Assert.Equal("Bazaar", record.Name);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("a/b")]
    public async Task Unknown_or_unsafe_id_gives_null(string id)
    {
        using var fx = new Fixture();
        fx.Options.Repos.Add(Entry(Clone(fx)));

        Assert.Null(await new ConfiguredCodingRepoSource(fx.Options).FindAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task A_clone_that_has_gone_gives_null()
    {
        using var fx = new Fixture();
        var clone = Clone(fx);
        fx.Options.Repos.Add(Entry(clone));
        var source = new ConfiguredCodingRepoSource(fx.Options);

        Directory.Delete(clone);

        Assert.Null(await source.FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public void Duplicate_ids_are_refused_even_with_different_case()
    {
        using var fx = new Fixture();
        var clone = Clone(fx);
        fx.Options.Repos.Add(Entry(clone, "bazaar"));
        fx.Options.Repos.Add(Entry(clone, "BAZAAR"));

        Assert.Throws<InvalidOperationException>(() => new ConfiguredCodingRepoSource(fx.Options));
    }

    [Fact]
    public void A_clone_inside_the_work_share_root_is_refused()
    {
        using var fx = new Fixture();
        fx.Options.Repos.Add(Entry(Path.Combine(fx.Root, "agents", "charles", "work", "repo")));

        Assert.Throws<InvalidOperationException>(() => new ConfiguredCodingRepoSource(fx.Options));
    }

    [Fact]
    public void A_clone_that_holds_the_work_share_root_is_refused()
    {
        using var fx = new Fixture();
        fx.Options.Repos.Add(Entry(fx.Base));

        Assert.Throws<InvalidOperationException>(() => new ConfiguredCodingRepoSource(fx.Options));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("clone")]
    [InlineData("owner")]
    [InlineData("name")]
    [InlineData("installation")]
    [InlineData("repository")]
    [InlineData("branch")]
    public void Invalid_entries_are_refused_at_construction(string field)
    {
        using var fx = new Fixture();
        var entry = Entry(Clone(fx));
        switch (field)
        {
            case "id": entry.Id = "../x"; break;
            case "clone": entry.ClonePath = "relative/clone"; break;
            case "owner": entry.Owner = "a/b"; break;
            case "name": entry.Name = null; break;
            case "installation": entry.InstallationId = 0; break;
            case "repository": entry.RepositoryId = -1; break;
            default: entry.DefaultBranch = "-x"; break;
        }

        fx.Options.Repos.Add(entry);
        Assert.Throws<InvalidOperationException>(() => new ConfiguredCodingRepoSource(fx.Options));
    }

    [Fact]
    public async Task Cancelled_token_is_honoured()
    {
        using var fx = new Fixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new ConfiguredCodingRepoSource(fx.Options).FindAsync("bazaar", cts.Token));
    }
}
