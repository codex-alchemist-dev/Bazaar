using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.SignIn;
using Bazaar.Client.Windows;
using Bazaar.DeviceApproval;

namespace Bazaar.Client.Windows.Tests;

public sealed class MergeApprovalCompositionTests
{
    private static readonly Guid _human = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _device = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly SignedInPerson _ada = new(_human, "Ada Lovelace", "owner", _device);

    private sealed class FakeWhoAmI(WhoAmIResult result) : IWhoAmIClient
    {
        public int Calls { get; private set; }

        public Task<WhoAmIResult> WhoAmIAsync(ClientSession session, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakePlatform(bool enrolled) : IDeviceApprovalPlatform
    {
        public int Opened { get; private set; }

        public IPresenceVerifier Presence { get; } = new FakePresence();

        public bool HasEnrolledKey(string keyName) => enrolled && keyName == DeviceApprovalComposition.ApprovalKeyName;

        public IApprovalSigner OpenSigner(string keyName)
        {
            Opened++;
            throw new ApprovalRefusedException(ApprovalRefusal.NoTpm, "fake");
        }
    }

    private sealed class FakePresence : IPresenceVerifier
    {
        public ValueTask<PresenceOutcome> VerifyAsync(string reason, CancellationToken cancellationToken) => ValueTask.FromResult(PresenceOutcome.Verified);
    }

    private static async Task<ISessionStore> SignedIn(bool signedIn = true)
    {
        var store = new InMemorySessionStore();
        if (signedIn)
        {
            await store.SaveAsync(new ClientSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)), TestContext.Current.CancellationToken);
        }

        return store;
    }

    private static readonly MergeApprovalRequest _request = new("github", "o/r", 7, new string('a', 40), "Title (#7)");

    [Fact]
    public async Task Whoami_200_registers_identity_and_approver_with_the_interim_email()
    {
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new FakeWhoAmI(new WhoAmIResult(WhoAmIOutcome.Found, _ada)), new FakePlatform(true),
            cancellationToken: TestContext.Current.CancellationToken);

        var identity = context.Get<IOwnerMergeIdentity>();
        Assert.NotNull(identity);
        Assert.Equal(_human, identity.HumanId);
        Assert.Equal($"Signed-off-by: Ada Lovelace <{_human:N}@{MergeSessionResolver.InterimEmailDomain}>", identity.SignOff);
        Assert.NotNull(context.Get<IOwnerMergeApprover>());
    }

    [Theory]
    [InlineData(WhoAmIOutcome.InvalidToken)]
    [InlineData(WhoAmIOutcome.Forbidden)]
    [InlineData(WhoAmIOutcome.Unavailable)]
    public async Task Any_whoami_refusal_leaves_identity_and_approver_out(WhoAmIOutcome outcome)
    {
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new FakeWhoAmI(new WhoAmIResult(outcome)), new FakePlatform(true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(context.Get<IOwnerMergeIdentity>());
        Assert.Null(context.Get<IOwnerMergeApprover>());
    }

    [Fact]
    public async Task Signed_out_asks_nothing_and_registers_nothing()
    {
        var whoAmI = new FakeWhoAmI(new WhoAmIResult(WhoAmIOutcome.Found, _ada));

        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(false), whoAmI, new FakePlatform(true), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, whoAmI.Calls);
        Assert.Null(context.Get<IOwnerMergeIdentity>());
        Assert.Null(context.Get<IOwnerMergeApprover>());
    }

    [Fact]
    public async Task A_thrown_http_failure_means_identity_absent()
    {
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new ThrowingWhoAmI(), new FakePlatform(true), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(context.Get<IOwnerMergeIdentity>());
    }

    private sealed class ThrowingWhoAmI : IWhoAmIClient
    {
        public Task<WhoAmIResult> WhoAmIAsync(ClientSession session, CancellationToken cancellationToken = default) => throw new HttpRequestException("down");
    }

    [Fact]
    public async Task No_platform_registers_no_approver_but_keeps_the_identity()
    {
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new FakeWhoAmI(new WhoAmIResult(WhoAmIOutcome.Found, _ada)), platform: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(context.Get<IOwnerMergeIdentity>());
        Assert.Null(context.Get<IOwnerMergeApprover>());
    }

    [Fact]
    public async Task Without_an_enrolled_key_the_approver_is_unavailable_and_creates_no_key()
    {
        var platform = new FakePlatform(enrolled: false);
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new FakeWhoAmI(new WhoAmIResult(WhoAmIOutcome.Found, _ada)), platform,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await context.Get<IOwnerMergeApprover>()!.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Merge);
        Assert.Equal(0, platform.Opened);
    }

    [Fact]
    public async Task A_refused_key_open_is_unavailable()
    {
        var platform = new FakePlatform(enrolled: true);
        var context = await MergeSessionResolver.ComposeAsync(
            new PaneModuleContext(), await SignedIn(), new FakeWhoAmI(new WhoAmIResult(WhoAmIOutcome.Found, _ada)), platform,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await context.Get<IOwnerMergeApprover>()!.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Unavailable, result.Outcome);
        Assert.Equal(1, platform.Opened);
    }

    [Fact]
    public void The_interim_email_is_isolated_in_one_shape()
    {
        Assert.Equal($"{_human:N}@users.noreply.github.com", MergeSessionResolver.InterimEmail(_human));
    }

    [Fact]
    public void The_approver_needs_identity_and_a_device_id()
    {
        var platform = new FakePlatform(true);
        var identity = SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_human, "Ada", "a@example.org"));

        Assert.Null(new PaneModuleContext().WithOwnerMergeApprover(platform, null, _device).Get<IOwnerMergeApprover>());
        Assert.Null(new PaneModuleContext().WithOwnerMergeApprover(platform, identity, null).Get<IOwnerMergeApprover>());
        Assert.Null(new PaneModuleContext().WithOwnerMergeApprover(platform, identity, Guid.Empty).Get<IOwnerMergeApprover>());
        Assert.NotNull(new PaneModuleContext().WithOwnerMergeApprover(platform, identity, _device).Get<IOwnerMergeApprover>());
    }
}
