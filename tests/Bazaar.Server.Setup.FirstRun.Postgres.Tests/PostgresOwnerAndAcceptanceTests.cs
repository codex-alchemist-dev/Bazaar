namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>The owner lookup and the Constitution acceptance. Needs PostgreSQL.</summary>
public sealed class PostgresOwnerAndAcceptanceTests
{
    private const string _commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task The_owner_is_the_active_owner_row_or_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = new PostgresFirstRunOwner(db.DataSource);
        Assert.Null(await owner.GetOwnerPrincipalIdAsync(ct));

        var id = await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);

        Assert.Equal(id, await owner.GetOwnerPrincipalIdAsync(ct));
    }

    [Fact]
    public async Task A_suspended_owner_is_not_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct, status: "suspended");

        Assert.Null(await new PostgresFirstRunOwner(db.DataSource).GetOwnerPrincipalIdAsync(ct));
    }

    [Fact]
    public async Task With_no_constitution_commit_nothing_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);

        Assert.False(await new PostgresConstitutionAcceptance(db.DataSource).RecordOwnerAcceptanceAsync(Guid.NewGuid(), ct));
        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM constitution_acceptance", ct));
    }

    [Fact]
    public async Task The_current_commit_is_recorded_once_for_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = Guid.NewGuid();
        await using (var insert = db.DataSource.CreateCommand($"INSERT INTO vault_docs (type, head_commit) VALUES ('constitution', '{_commit}')"))
            await insert.ExecuteNonQueryAsync(ct);
        var acceptance = new PostgresConstitutionAcceptance(db.DataSource);

        Assert.True(await acceptance.RecordOwnerAcceptanceAsync(owner, ct));
        Assert.True(await acceptance.RecordOwnerAcceptanceAsync(owner, ct));

        Assert.Equal(1, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM constitution_acceptance", ct));
        Assert.Equal(_commit, await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT commit_sha FROM constitution_acceptance", ct));
    }
}
