using Bazaar.Server.Hive;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Tests.Memory.Tools;

namespace Bazaar.Server.Tests.Hive;

public sealed class MemoryVaultHiveNoteReaderTests
{
    private const string _copy = "hive/procedure/retry-with-backoff.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPathAccess RealAccess() => new(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));

    private static InMemoryMemoryVault Vault()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_copy, "copy text");
        vault.Seed("projects/atlas/memory/lessons/retry.md", "atlas source note");
        vault.Seed("agents/alpha/memory/procedural/private.md", "private note");
        vault.Seed("company/memory/rule.md", "company note");
        return vault;
    }

    private sealed class DenyAll : IVaultAccess
    {
        public bool CanRead(VaultPrincipal who, VaultPath path) => false;

        public IReadOnlyList<VaultPath> FilterVisible(VaultPrincipal who, IEnumerable<VaultPath> paths) => [];

        public AccessDecision Explain(VaultPrincipal who, VaultPath path) => AccessDecision.Deny("test");

        public bool CanResolveLink(VaultPrincipal who, VaultPath fromPath, VaultPath toPath) => false;
    }

    [Fact]
    public async Task A_hive_copy_is_found()
    {
        var reader = new MemoryVaultHiveNoteReader(Vault(), RealAccess());

        Assert.Equal("copy text", await reader.ReadAsync(_copy, Ct));
    }

    [Fact]
    public async Task An_unknown_hive_note_is_not_found()
    {
        var reader = new MemoryVaultHiveNoteReader(Vault(), RealAccess());

        Assert.Null(await reader.ReadAsync("hive/failure/missing.md", Ct));
    }

    [Theory]
    [InlineData("projects/atlas/memory/lessons/retry.md")]
    [InlineData("agents/alpha/memory/procedural/private.md")]
    [InlineData("company/memory/rule.md")]
    [InlineData("hive")]
    [InlineData("hive/../projects/atlas/memory/lessons/retry.md")]
    [InlineData("/hive/procedure/retry-with-backoff.md")]
    [InlineData("")]
    public async Task Anything_outside_the_hive_area_or_malformed_is_not_returned_even_when_it_exists(string path)
    {
        var reader = new MemoryVaultHiveNoteReader(Vault(), RealAccess());

        Assert.Null(await reader.ReadAsync(path, Ct));
    }

    [Fact]
    public async Task A_refused_read_answers_not_found_and_never_touches_the_vault()
    {
        var reader = new MemoryVaultHiveNoteReader(Vault(), new DenyAll());

        Assert.Null(await reader.ReadAsync(_copy, Ct));
    }

    [Fact]
    public async Task The_real_access_rules_let_maintenance_read_hive_and_nothing_else_here()
    {
        var access = RealAccess();

        Assert.True(access.CanRead(VaultPrincipal.Maintenance(), VaultPath.Parse(_copy)));
        Assert.False(access.CanRead(VaultPrincipal.Agent("alpha"), VaultPath.Parse(_copy)));
    }
}
