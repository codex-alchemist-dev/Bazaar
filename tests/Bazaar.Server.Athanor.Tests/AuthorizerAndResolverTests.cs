using Bazaar.Roles.Abstractions;
using Bazaar.Server.Git;

namespace Bazaar.Server.Athanor.Tests;

public sealed class AuthorizerAndResolverTests
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vault_write_asks_for_vault_write_over_the_company_scope_as_a_human()
    {
        var checker = new RecordingChecker(allow: true);
        var auth = new RolesEditorAuthorizer(checker);

        var allowed = await auth.IsAllowedAsync(
            new EditorCaller(_user.ToString()), new EditorWorkspaceInfo("vault", EditorWorkspaceKind.Vault, false), EditorAccess.Write, Ct);

        Assert.True(allowed);
        var call = Assert.Single(checker.Calls);
        Assert.Equal(_user, call.Principal);
        Assert.Equal(PrincipalKind.Human, call.Kind);
        Assert.Equal("vault.write", call.Capability.Value);
        Assert.Equal(Scope.Company, call.Scope);
    }

    [Fact]
    public async Task Worktree_write_asks_for_git_worktree_write_over_the_repository_scope()
    {
        var checker = new RecordingChecker(allow: true);
        var auth = new RolesEditorAuthorizer(checker);

        await auth.IsAllowedAsync(
            new EditorCaller(_user.ToString()), new EditorWorkspaceInfo("git:repo-1", EditorWorkspaceKind.GitWorktree, false), EditorAccess.Write, Ct);

        var call = Assert.Single(checker.Calls);
        Assert.Equal("git.worktree.write", call.Capability.Value);
        Assert.Equal(new Scope(ScopeKind.Repository, "git:repo-1"), call.Scope);
    }

    [Theory]
    [InlineData(EditorWorkspaceKind.Vault)]
    [InlineData(EditorWorkspaceKind.GitWorktree)]
    public async Task Read_asks_for_the_read_capability(EditorWorkspaceKind kind)
    {
        var checker = new RecordingChecker(allow: true);

        await new RolesEditorAuthorizer(checker).IsAllowedAsync(
            new EditorCaller(_user.ToString()), new EditorWorkspaceInfo("w", kind, false), EditorAccess.Read, Ct);

        Assert.Equal("read", Assert.Single(checker.Calls).Capability.Value);
    }

    [Fact]
    public async Task A_denied_check_is_denied()
    {
        var auth = new RolesEditorAuthorizer(new RecordingChecker(allow: false));

        Assert.False(await auth.IsAllowedAsync(
            new EditorCaller(_user.ToString()), new EditorWorkspaceInfo("vault", EditorWorkspaceKind.Vault, false), EditorAccess.Write, Ct));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_user_id_that_is_not_a_real_guid_is_refused_without_asking_the_checker(string userId)
    {
        var checker = new RecordingChecker(allow: true);

        var allowed = await new RolesEditorAuthorizer(checker).IsAllowedAsync(
            new EditorCaller(userId), new EditorWorkspaceInfo("vault", EditorWorkspaceKind.Vault, false), EditorAccess.Read, Ct);

        Assert.False(allowed);
        Assert.Empty(checker.Calls);
    }

    [Fact]
    public async Task Configured_resolver_returns_entries_and_skips_bad_ones()
    {
        var root = Path.GetTempPath();
        var resolver = new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
        {
            Workspaces =
            [
                new EditorWorkspaceEntry { Id = "vault", Kind = EditorWorkspaceKind.Vault, Path = root },
                new EditorWorkspaceEntry { Id = "vault", Kind = EditorWorkspaceKind.GitWorktree, Path = root },
                new EditorWorkspaceEntry { Id = "rel", Path = "relative/path" },
                new EditorWorkspaceEntry { Id = " ", Path = root },
                new EditorWorkspaceEntry { Id = "ro", Path = root, ReadOnly = true },
            ],
        });

        var vault = await resolver.ResolveAsync("vault", Ct);
        Assert.Equal(EditorWorkspaceKind.Vault, vault!.Kind);
        Assert.False(vault.ReadOnly);
        Assert.True((await resolver.ResolveAsync("ro", Ct))!.ReadOnly);
        Assert.Null(await resolver.ResolveAsync("rel", Ct));
        Assert.Null(await resolver.ResolveAsync("VAULT", Ct));
    }

    [Fact]
    public async Task Git_resolver_maps_git_prefixed_ids_to_the_clone_and_honours_the_read_only_option()
    {
        var clone = Path.GetTempPath();
        var directory = new FakeDirectory(new GitRepositoryInfo("r1", new RepoRef("github", "o/n"), clone, null, null));

        var rw = new GitRepositoryEditorWorkspaceResolver(directory, new AthanorWorkspaceOptions());
        var found = await rw.ResolveAsync("git:r1", Ct);
        Assert.Equal(new EditorWorkspaceRoot("git:r1", EditorWorkspaceKind.GitWorktree, clone, false), found);
        Assert.Null(await rw.ResolveAsync("git:other", Ct));
        Assert.Null(await rw.ResolveAsync("git:", Ct));
        Assert.Null(await rw.ResolveAsync("r1", Ct));

        var ro = new GitRepositoryEditorWorkspaceResolver(directory, new AthanorWorkspaceOptions { GitRepositoriesReadOnly = true });
        Assert.True((await ro.ResolveAsync("git:r1", Ct))!.ReadOnly);
    }

    [Fact]
    public async Task Composite_resolver_takes_the_first_resolver_that_knows_the_id()
    {
        var a = new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
        {
            Workspaces = [new EditorWorkspaceEntry { Id = "x", Kind = EditorWorkspaceKind.Vault, Path = Path.GetTempPath() }],
        });
        var b = new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
        {
            Workspaces =
            [
                new EditorWorkspaceEntry { Id = "x", Kind = EditorWorkspaceKind.GitWorktree, Path = Path.GetTempPath() },
                new EditorWorkspaceEntry { Id = "y", Kind = EditorWorkspaceKind.GitWorktree, Path = Path.GetTempPath() },
            ],
        });
        var composite = new CompositeEditorWorkspaceResolver([a, b]);

        Assert.Equal(EditorWorkspaceKind.Vault, (await composite.ResolveAsync("x", Ct))!.Kind);
        Assert.Equal(EditorWorkspaceKind.GitWorktree, (await composite.ResolveAsync("y", Ct))!.Kind);
        Assert.Null(await composite.ResolveAsync("z", Ct));
    }

    private sealed class FakeDirectory(GitRepositoryInfo repo) : IGitRepositoryDirectory
    {
        public Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(repositoryId == repo.RepositoryId ? repo : null);

        public Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GitRepositoryInfo>>([repo]);
    }

    private sealed class RecordingChecker(bool allow) : IPermissionChecker
    {
        public List<(Guid Principal, PrincipalKind Kind, Capability Capability, Scope Scope)> Calls { get; } = [];

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
        {
            Calls.Add((principalId, kind, capability, scope));
            return Task.FromResult(allow);
        }
    }
}
