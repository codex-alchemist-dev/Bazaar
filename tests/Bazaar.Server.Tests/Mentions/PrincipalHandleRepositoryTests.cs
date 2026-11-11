using Bazaar.Contracts.Gc;
using Bazaar.Server.Mentions;
using Bazaar.Server.Tests.Gc;
using Npgsql;

namespace Bazaar.Server.Tests.Mentions;

/// <summary><see cref="PrincipalHandleRepository"/> against the real schema (all namespaces, including <c>mentions</c>).</summary>
public sealed class PrincipalHandleRepositoryTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PrincipalHandleRepository Repo => new(db.DataSource);

    /// <summary>A handle no other test in this class uses: the class shares one database.</summary>
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private async Task<Guid> NewAgentAsync(string name)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, $2, 'active')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<string> SqlStateOfAsync(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        return ex.SqlState;
    }

    [Fact]
    public async Task A_principal_without_a_handle_has_none()
    {
        GcTestSkip.WithoutDatabase();

        Assert.Null(await Repo.GetAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task A_set_handle_is_read_back_by_principal_and_by_handle()
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();
        var handle = Unique("readback");

        var stored = await Repo.SetAsync(principal, PrincipalKind.Human, handle, Ct);

        var expected = new PrincipalHandle(principal, PrincipalKind.Human, handle);
        Assert.Equal(expected, stored);
        Assert.Equal(expected, await Repo.GetAsync(principal, Ct));
        Assert.Equal(expected, await Repo.FindByHandleAsync(handle, Ct));
    }

    [Theory]
    [InlineData(PrincipalKind.Owner, "owner")]
    [InlineData(PrincipalKind.Human, "human")]
    [InlineData(PrincipalKind.Agent, "agent")]
    public async Task Each_principal_kind_is_stored_under_its_wire_name(PrincipalKind kind, string text)
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();

        await Repo.SetAsync(principal, kind, Unique("kind"), Ct);

        Assert.Equal(text, await db.ScalarAsync<string>("SELECT principal_kind FROM principal_handle WHERE principal_id = $1", principal));
        Assert.Equal(kind, (await Repo.GetAsync(principal, Ct))!.Kind);
    }

    [Fact]
    public async Task A_handle_is_stored_lowercase_and_found_in_any_case()
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();
        var handle = Unique("mixed");

        var stored = await Repo.SetAsync(principal, PrincipalKind.Agent, "  " + handle.ToUpperInvariant() + " ", Ct);

        Assert.Equal(handle, stored.Handle);
        Assert.Equal(handle, await db.ScalarAsync<string>("SELECT handle FROM principal_handle WHERE principal_id = $1", principal));
        Assert.Equal(principal, (await Repo.FindByHandleAsync(handle.ToUpperInvariant(), Ct))!.PrincipalId);
    }

    [Fact]
    public async Task A_handle_another_principal_holds_is_refused()
    {
        GcTestSkip.WithoutDatabase();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var handle = Unique("taken");
        await Repo.SetAsync(first, PrincipalKind.Agent, handle, Ct);

        var ex = await Assert.ThrowsAsync<HandleTakenException>(() => Repo.SetAsync(second, PrincipalKind.Agent, handle, Ct));

        Assert.Equal(handle, ex.Handle);
        Assert.Null(await Repo.GetAsync(second, Ct));
        Assert.Equal(first, (await Repo.FindByHandleAsync(handle, Ct))!.PrincipalId);
    }

    [Fact]
    public async Task A_handle_that_differs_only_in_case_is_the_same_handle()
    {
        GcTestSkip.WithoutDatabase();
        var handle = Unique("case");
        await Repo.SetAsync(Guid.CreateVersion7(), PrincipalKind.Agent, handle, Ct);

        await Assert.ThrowsAsync<HandleTakenException>(
            () => Repo.SetAsync(Guid.CreateVersion7(), PrincipalKind.Human, handle.ToUpperInvariant(), Ct));
    }

    [Fact]
    public async Task Setting_again_replaces_the_handle_and_frees_the_old_one()
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();
        var old = Unique("old");
        var replacement = Unique("new");
        await Repo.SetAsync(principal, PrincipalKind.Agent, old, Ct);

        await Repo.SetAsync(principal, PrincipalKind.Agent, replacement, Ct);

        Assert.Equal(replacement, (await Repo.GetAsync(principal, Ct))!.Handle);
        Assert.Null(await Repo.FindByHandleAsync(old, Ct));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM principal_handle WHERE principal_id = $1", principal));

        // The freed handle can go to someone else.
        var other = Guid.CreateVersion7();
        await Repo.SetAsync(other, PrincipalKind.Agent, old, Ct);
        Assert.Equal(other, (await Repo.FindByHandleAsync(old, Ct))!.PrincipalId);
    }

    [Fact]
    public async Task Setting_the_handle_a_principal_already_holds_is_accepted()
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();
        var handle = Unique("same");
        await Repo.SetAsync(principal, PrincipalKind.Agent, handle, Ct);

        var again = await Repo.SetAsync(principal, PrincipalKind.Agent, handle, Ct);

        Assert.Equal(handle, again.Handle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("-ana")]
    [InlineData("an a")]
    [InlineData("zoë")]
    [InlineData("here")]
    [InlineData("ALL")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task An_invalid_handle_is_refused_before_any_write(string handle)
    {
        GcTestSkip.WithoutDatabase();
        var principal = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidHandleException>(() => Repo.SetAsync(principal, PrincipalKind.Agent, handle, Ct));

        Assert.Null(await Repo.GetAsync(principal, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a handle")]
    [InlineData("here")]
    public async Task Looking_up_a_text_that_cannot_be_a_handle_finds_nothing(string handle)
    {
        GcTestSkip.WithoutDatabase();

        Assert.Null(await Repo.FindByHandleAsync(handle, Ct));
    }

    [Theory]
    [InlineData("Ana")]
    [InlineData("a")]
    [InlineData("-ana")]
    [InlineData("an a")]
    [InlineData("here")]
    [InlineData("all")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task The_table_itself_refuses_a_bad_handle(string handle)
    {
        GcTestSkip.WithoutDatabase();

        var state = await SqlStateOfAsync(
            "INSERT INTO principal_handle (principal_id, principal_kind, handle) VALUES ($1, 'agent', $2)",
            Guid.CreateVersion7(),
            handle);

        Assert.Equal(PostgresErrorCodes.CheckViolation, state);
    }

    [Fact]
    public async Task The_table_itself_refuses_an_unknown_principal_kind()
    {
        GcTestSkip.WithoutDatabase();

        var state = await SqlStateOfAsync(
            "INSERT INTO principal_handle (principal_id, principal_kind, handle) VALUES ($1, 'system', $2)",
            Guid.CreateVersion7(),
            Unique("kind"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, state);
    }

    [Fact]
    public async Task Ensure_gives_an_agent_the_slug_of_its_name()
    {
        GcTestSkip.WithoutDatabase();
        var name = "Ensure " + Guid.NewGuid().ToString("N")[..12];
        var agent = await NewAgentAsync(name);

        var handle = await Repo.EnsureAgentHandleAsync(agent, name, Ct);

        Assert.Equal(HandleSlug.FromName(name), handle);
        Assert.Equal(new PrincipalHandle(agent, PrincipalKind.Agent, handle!), await Repo.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task Ensure_keeps_the_handle_an_agent_already_has()
    {
        GcTestSkip.WithoutDatabase();
        var agent = await NewAgentAsync("Keeper");
        var chosen = Unique("chosen");
        await Repo.SetAsync(agent, PrincipalKind.Agent, chosen, Ct);

        var handle = await Repo.EnsureAgentHandleAsync(agent, "Some Other Name " + Guid.NewGuid().ToString("N")[..8], Ct);

        Assert.Equal(chosen, handle);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM principal_handle WHERE principal_id = $1", agent));
    }

    [Fact]
    public async Task Ensure_twice_returns_the_same_handle()
    {
        GcTestSkip.WithoutDatabase();
        var name = "Twice " + Guid.NewGuid().ToString("N")[..12];
        var agent = await NewAgentAsync(name);

        var first = await Repo.EnsureAgentHandleAsync(agent, name, Ct);
        var second = await Repo.EnsureAgentHandleAsync(agent, name, Ct);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Ensure_adds_a_numeric_suffix_when_the_slug_is_taken()
    {
        GcTestSkip.WithoutDatabase();
        var name = "Suffix " + Guid.NewGuid().ToString("N")[..12];
        var slug = HandleSlug.FromName(name)!;
        var first = await NewAgentAsync(name);
        var second = await NewAgentAsync(name);
        var third = await NewAgentAsync(name);

        Assert.Equal(slug, await Repo.EnsureAgentHandleAsync(first, name, Ct));
        Assert.Equal(slug + "-2", await Repo.EnsureAgentHandleAsync(second, name, Ct));
        Assert.Equal(slug + "-3", await Repo.EnsureAgentHandleAsync(third, name, Ct));
    }

    [Fact]
    public async Task Ensure_keeps_a_suffixed_handle_within_the_maximum_length()
    {
        GcTestSkip.WithoutDatabase();
        var name = Guid.NewGuid().ToString("N") + "tail";
        var slug = HandleSlug.FromName(name)!;
        Assert.Equal(HandleSlug.MaxLength, slug.Length);
        await Repo.EnsureAgentHandleAsync(await NewAgentAsync(name), name, Ct);

        var handle = await Repo.EnsureAgentHandleAsync(await NewAgentAsync(name), name, Ct);

        Assert.Equal(slug[..30] + "-2", handle);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("x")]
    [InlineData("日本語")]
    public async Task Ensure_leaves_an_agent_without_a_handle_when_the_name_gives_no_slug(string name)
    {
        GcTestSkip.WithoutDatabase();
        var agent = await NewAgentAsync(name);

        Assert.Null(await Repo.EnsureAgentHandleAsync(agent, name, Ct));
        Assert.Null(await Repo.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task Ensure_never_hands_out_a_broadcast_word()
    {
        GcTestSkip.WithoutDatabase();
        var agent = await NewAgentAsync("Here");

        var handle = await Repo.EnsureAgentHandleAsync(agent, "Here", Ct);

        // Another test class cannot take "here-2" here: the database belongs to this class, and no other test uses the name.
        Assert.Equal("here-2", handle);
    }

    [Fact]
    public async Task Concurrent_ensures_for_agents_with_one_name_give_distinct_handles()
    {
        GcTestSkip.WithoutDatabase();
        const int count = 8;
        var name = "Twin " + Guid.NewGuid().ToString("N")[..12];
        var slug = HandleSlug.FromName(name)!;
        var agents = new List<Guid>();
        for (var i = 0; i < count; i++)
            agents.Add(await NewAgentAsync(name));

        var handles = await Task.WhenAll(agents.Select(a => Repo.EnsureAgentHandleAsync(a, name, Ct)));

        var expected = Enumerable.Range(1, count).Select(n => HandleSlug.WithSuffix(slug, n)).Order(StringComparer.Ordinal);
        Assert.Equal(expected, handles.Select(h => h!).Order(StringComparer.Ordinal));
        Assert.Equal((long)count, await db.ScalarAsync<long>("SELECT count(*) FROM principal_handle WHERE principal_id = ANY($1)", agents.ToArray()));
    }

    [Fact]
    public async Task Concurrent_ensures_for_one_agent_give_one_handle()
    {
        GcTestSkip.WithoutDatabase();
        var name = "Solo " + Guid.NewGuid().ToString("N")[..12];
        var agent = await NewAgentAsync(name);

        var handles = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Repo.EnsureAgentHandleAsync(agent, name, Ct)));

        Assert.Single(handles.Distinct());
        Assert.Equal(HandleSlug.FromName(name), handles[0]);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM principal_handle WHERE principal_id = $1", agent));
    }
}
