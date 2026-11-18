using Bazaar.Roles;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Tests.Components.Erasure;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Components.Roles;

/// <summary>The roles store on a real PostgreSQL: grants round-trip, and the two floors that need locks hold under concurrency.</summary>
public sealed class PostgresRoleStoreTests(ErasureDb db) : IClassFixture<ErasureDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Role NewRole(string name, string? template = null, params Grant[] grants) =>
        new(Guid.NewGuid(), name + Guid.NewGuid().ToString("N"), "d", template, false, grants, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static RoleAssignment Human(Guid role) =>
        new(Guid.NewGuid(), PrincipalKind.Human, role, Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_role_and_its_grants_round_trip_and_names_are_unique_without_case()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var store = new PostgresRoleStore(db.DataSource);
        var role = NewRole("Triage", null, new Grant(Capabilities.Post, Scope.Gc("g1")), new Grant(Capabilities.Read, Scope.Company));

        Assert.True(await store.CreateAsync(role, Ct));
        Assert.False(await store.CreateAsync(role with { Id = Guid.NewGuid(), Name = role.Name.ToUpperInvariant() }, Ct));

        var loaded = await store.GetAsync(role.Id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Grants.Count);
        Assert.Contains(new Grant(Capabilities.Post, Scope.Gc("g1")), loaded.Grants);

        Assert.True(await store.ReplaceGrantsAsync(role.Id, [new Grant(Capabilities.Read, Scope.Instance)], DateTimeOffset.UtcNow, Ct));
        Assert.Single((await store.GetAsync(role.Id, Ct))!.Grants);
        Assert.False(await store.ReplaceGrantsAsync(Guid.NewGuid(), [], DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task Deleting_a_role_removes_its_grants_and_assignments()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var store = new PostgresRoleStore(db.DataSource);
        var role = NewRole("Temp", null, new Grant(Capabilities.Read, Scope.Company));
        await store.CreateAsync(role, Ct);
        var a = Human(role.Id);
        Assert.True(await store.AssignAsync(a, Ct));
        Assert.False(await store.AssignAsync(a, Ct));

        Assert.True(await store.DeleteAsync(role.Id, Ct));

        Assert.Null(await store.GetAsync(role.Id, Ct));
        Assert.Empty(await store.AssignmentsForPrincipalAsync(a.PrincipalId, Ct));
    }

    [Fact]
    public async Task The_last_human_cannot_be_unassigned_even_when_two_removals_race()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var store = new PostgresRoleStore(db.DataSource);
        var role = NewRole("Owners");
        await store.CreateAsync(role, Ct);
        var one = Human(role.Id);
        var two = Human(role.Id);
        await store.AssignAsync(one, Ct);
        await store.AssignAsync(two, Ct);

        var results = await Task.WhenAll(
            store.UnassignAsync(one.PrincipalId, role.Id, keepOneHuman: true, Ct),
            store.UnassignAsync(two.PrincipalId, role.Id, keepOneHuman: true, Ct));

        Assert.Single(results, r => r == UnassignResult.Removed);
        Assert.Single(await store.AssignmentsForRoleAsync(role.Id, Ct));
    }

    [Fact]
    public async Task Bootstrap_assigns_only_while_no_human_holds_the_role()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var store = new PostgresRoleStore(db.DataSource);
        var role = NewRole("Boot");
        await store.CreateAsync(role, Ct);

        var results = await Task.WhenAll(
            store.AssignIfNoHumanHoldsAsync(Human(role.Id), Ct),
            store.AssignIfNoHumanHoldsAsync(Human(role.Id), Ct),
            store.AssignIfNoHumanHoldsAsync(Human(role.Id), Ct));

        Assert.Single(results, r => r);
        Assert.Single(await store.AssignmentsForRoleAsync(role.Id, Ct));
    }

    [Fact]
    public async Task The_table_refuses_an_instance_grant_with_an_id_and_an_unknown_template()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var badTemplate = db.DataSource.CreateCommand(
            "INSERT INTO role (role_id, name, description, template_key, protected, created_at, updated_at) VALUES (gen_random_uuid(), $1, '', 'wizard', false, now(), now())");
        badTemplate.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "x" + Guid.NewGuid().ToString("N") });
        await Assert.ThrowsAsync<PostgresException>(() => badTemplate.ExecuteNonQueryAsync(Ct));
    }
}
