using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.SignIn;
using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

public sealed class GitGuiCompositionTests
{
    private static PaneModuleContext Compose() =>
        new PaneModuleContext().WithGitGui(new ServerEndpoint(new Uri("https://bazaar.example/"), "token"));

    [Fact]
    public void Injects_what_the_git_panes_read()
    {
        var context = Compose();

        Assert.IsType<ServerGitGuiClient>(context.Get<IGitGuiClient>());
        Assert.NotNull(context.Get<IGitGuiSettings>());
        Assert.NotNull(context.Get<DiffSurfaceRegistry>());
        var providers = context.Get<IGitProviderUiRegistry>();
        Assert.NotNull(providers);
        Assert.NotNull(providers.Find("github"));
        Assert.NotNull(providers.Find("gitlab"));
    }

    [Fact]
    public void Does_not_inject_the_merge_approver_or_repo_source()
    {
        var context = Compose();

        Assert.Null(context.Get<IOwnerMergeApprover>());
        Assert.Null(context.Get<IOwnerMergeIdentity>());
        Assert.Null(context.Get<IGitRepoSource>());
    }

    private sealed class FixedProfile(string? line) : IMergeProfileStore
    {
        public string? GetSignOff(Guid humanId) => line;
    }

    private static readonly MergePrincipal _ada = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Ada Lovelace", "ada@example.org");

    [Fact]
    public async Task Registers_the_identity_for_a_signed_in_person()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new ClientSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)), TestContext.Current.CancellationToken);

        var context = await new PaneModuleContext().WithOwnerMergeIdentityAsync(
            store, (_, _) => Task.FromResult<MergePrincipal?>(_ada), cancellationToken: TestContext.Current.CancellationToken);

        var identity = context.Get<IOwnerMergeIdentity>();
        Assert.NotNull(identity);
        Assert.Equal(_ada.HumanId, identity.HumanId);
        Assert.Equal("Signed-off-by: Ada Lovelace <ada@example.org>", identity.SignOff);
    }

    [Fact]
    public async Task Leaves_the_identity_absent_when_signed_out()
    {
        var resolved = false;
        var context = await new PaneModuleContext().WithOwnerMergeIdentityAsync(
            new InMemorySessionStore(), (_, _) => { resolved = true; return Task.FromResult<MergePrincipal?>(_ada); },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(context.Get<IOwnerMergeIdentity>());
        Assert.False(resolved);
    }

    [Fact]
    public void Profile_override_wins_and_is_registered()
    {
        var context = new PaneModuleContext().WithOwnerMergeIdentity(_ada, new FixedProfile("Signed-off-by: Ada L. <ada@work.example>"));

        Assert.Equal("Signed-off-by: Ada L. <ada@work.example>", context.Get<IOwnerMergeIdentity>()!.SignOff);
    }
}
