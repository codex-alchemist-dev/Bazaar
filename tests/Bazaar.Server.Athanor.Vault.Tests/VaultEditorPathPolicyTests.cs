using Bazaar.Server.Athanor;
using Bazaar.Server.Athanor.Vault;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Athanor.Vault.Tests;

/// <summary>Doc 18 "Access" for the editor: the owner reaches everything, a member only ownerless files in their areas.</summary>
public sealed class VaultEditorPathPolicyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string _ulid = "01HZZZZZZZZZZZZZZZZZZZZZZZ";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "athanor-policy-" + Guid.NewGuid().ToString("N"));
    private readonly VaultEditorPathPolicy _policy;
    private static readonly EditorWorkspaceInfo _vault = new("vault", EditorWorkspaceKind.Vault, ReadOnly: false);
    private static readonly EditorCaller _owner = new("owner-1", IsOwner: true);
    private static readonly EditorCaller _member = new("member1");

    public VaultEditorPathPolicyTests()
    {
        Write("company/plan.md", Doc("plan", null));
        Write("company/owned.md", Doc("memory", "agent:abc"));
        Write("company/broken.md", "---\nid: nope\n---\nbody\n");
        Write("shared/note.md", "no front matter\n");
        Write("projects/alpha/spec.md", Doc("plan", null));
        Write("projects/beta/spec.md", Doc("plan", null));
        Write("hive/x.md", Doc("plan", null));
        Write("decisions/0001-x.md", Doc("plan", null));

        var options = new VaultEditorOptions("vault", _root, _root + ".wt", new GitIdentity("a", "a@example.test"));
        var limits = new Limits();
        var store = new DirectoryEditorWorkspaceStore(
            new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions { Workspaces = [new EditorWorkspaceEntry { Id = "vault", Kind = EditorWorkspaceKind.Vault, Path = _root }] }),
            limits);
        var membership = new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            ["alpha"] = new(new HashSet<string>(), new HashSet<string> { "member1" }),
            ["beta"] = new(new HashSet<string>(), new HashSet<string> { "someone-else" }),
        });
        _policy = new VaultEditorPathPolicy(options, store, membership, AllowAllEditorPathPolicy.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Doc(string type, string? owner) =>
        $"---\nid: {_ulid}\ntype: {type}\ntitle: Something\n{(owner is null ? string.Empty : "owner: " + owner + "\n")}---\nbody\n";

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private ValueTask<bool> Check(EditorCaller caller, string path, EditorAccess access = EditorAccess.Read) =>
        _policy.IsAllowedAsync(caller, _vault, path, access, Ct);

    [Theory]
    [InlineData("company/plan.md")]
    [InlineData("hive/x.md")]
    [InlineData("decisions/0001-x.md")]
    [InlineData("company/owned.md")]
    [InlineData("projects/beta/spec.md")]
    public async Task The_owner_reaches_every_file(string path) => Assert.True(await Check(_owner, path));

    [Theory]
    [InlineData("company/plan.md")]
    [InlineData("shared/note.md")]
    [InlineData("projects/alpha/spec.md")]
    [InlineData("company/missing.md")]
    public async Task A_member_reaches_ownerless_files_in_company_shared_and_their_projects(string path) =>
        Assert.True(await Check(_member, path));

    [Theory]
    [InlineData("company/owned.md")]
    [InlineData("company/broken.md")]
    [InlineData("projects/beta/spec.md")]
    [InlineData("hive/x.md")]
    [InlineData("decisions/0001-x.md")]
    [InlineData("Index.md")]
    public async Task A_member_is_refused_owned_unreadable_and_out_of_area_files(string path)
    {
        Assert.False(await Check(_member, path));
        Assert.False(await Check(_member, path, EditorAccess.Write));
    }

    [Fact]
    public async Task Other_workspaces_go_to_the_next_policy()
    {
        var other = new EditorWorkspaceInfo("git:x", EditorWorkspaceKind.GitWorktree, ReadOnly: false);

        Assert.True(await _policy.IsAllowedAsync(_member, other, "hive/x.md", EditorAccess.Read, Ct));
    }

    private sealed class Limits : IAthanorLimits
    {
        public long MaxOpenBytes => 1_000_000;
        public long MaxSaveBytes => 1_000_000;
        public int SaveRatePerMinute => 60;
    }
}
