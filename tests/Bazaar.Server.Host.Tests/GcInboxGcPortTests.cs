using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.MailOnboarding;

namespace Bazaar.Server.Host.Tests;

public sealed class GcInboxGcPortTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeProvisioner : IGcProvisioner
    {
        public Dictionary<Guid, (string Type, string Title, GcPrincipal Owner, IReadOnlyDictionary<string, string>? Meta)> Created { get; } = [];
        public Dictionary<Guid, List<GcPrincipal>> Members { get; } = [];
        public HashSet<Guid> Archived { get; } = [];
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnAdd { get; set; }

        public Task<bool> CreateProjectGcAsync(Guid gcId, string title, GcPrincipal owner, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CreateAsync(Guid gcId, string typeKey, string title, GcPrincipal owner, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
        {
            if (ThrowOnCreate)
            {
                throw new InvalidOperationException("secret detail");
            }

            if (Created.ContainsKey(gcId))
            {
                return Task.FromResult(false);
            }

            Created[gcId] = (typeKey, title, owner, metadata);
            Members[gcId] = [owner];
            return Task.FromResult(true);
        }

        public Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> AddMemberAsync(Guid gcId, GcPrincipal member, CancellationToken ct)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("secret detail");
            }

            if (Members[gcId].Any(m => m.Id == member.Id))
            {
                return Task.FromResult(false);
            }

            Members[gcId].Add(member);
            return Task.FromResult(true);
        }

        public Task<bool> ArchiveAsync(Guid gcId, CancellationToken ct)
        {
            if (!Created.ContainsKey(gcId))
            {
                throw new InvalidOperationException("missing");
            }

            return Task.FromResult(Archived.Add(gcId));
        }
    }

    private sealed class FixedOwner(MailOwner? owner) : IMailOwnerDirectory
    {
        public Task<MailOwner?> GetOwnerAsync(CancellationToken ct) => Task.FromResult(owner);
    }

    private static readonly MailOwner _owner = new(Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb"), "The Owner");

    [Fact]
    public async Task Ensure_creates_an_inbox_gc_owned_by_the_agent_and_adds_the_owner()
    {
        var provisioner = new FakeProvisioner();
        var agent = Guid.NewGuid();
        var mailbox = Guid.NewGuid();
        var gcId = InboxGcId.For(agent);

        Assert.True(await new GcInboxGcPort(provisioner, new FixedOwner(_owner)).EnsureInboxGcAsync(gcId, agent, mailbox, "Inbox", Ct));

        var created = Assert.Single(provisioner.Created).Value;
        Assert.Equal("inbox", created.Type);
        Assert.Equal(PrincipalKind.Agent, created.Owner.Kind);
        Assert.Equal(agent, created.Owner.Id);
        Assert.Equal(mailbox.ToString("D"), created.Meta!["mail.mailbox_id"]);
        Assert.Contains(provisioner.Members[gcId], m => m.Id == _owner.Id && m.Kind == PrincipalKind.Owner);
        Assert.Contains(provisioner.Members[gcId], m => m.Id == agent);
    }

    [Fact]
    public async Task Ensure_is_idempotent()
    {
        var provisioner = new FakeProvisioner();
        var port = new GcInboxGcPort(provisioner, new FixedOwner(_owner));
        var agent = Guid.NewGuid();
        var gcId = InboxGcId.For(agent);

        Assert.True(await port.EnsureInboxGcAsync(gcId, agent, Guid.NewGuid(), "Inbox", Ct));
        Assert.True(await port.EnsureInboxGcAsync(gcId, agent, Guid.NewGuid(), "Inbox", Ct));

        Assert.Single(provisioner.Created);
        Assert.Equal(2, provisioner.Members[gcId].Count);
    }

    [Fact]
    public async Task Ensure_without_an_owner_creates_nothing()
    {
        var provisioner = new FakeProvisioner();
        var agent = Guid.NewGuid();

        Assert.False(await new GcInboxGcPort(provisioner, new FixedOwner(null)).EnsureInboxGcAsync(InboxGcId.For(agent), agent, Guid.NewGuid(), "Inbox", Ct));

        Assert.Empty(provisioner.Created);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Ensure_turns_a_provisioner_failure_into_false(bool failCreate, bool failAdd)
    {
        var provisioner = new FakeProvisioner { ThrowOnCreate = failCreate, ThrowOnAdd = failAdd };
        var agent = Guid.NewGuid();

        Assert.False(await new GcInboxGcPort(provisioner, new FixedOwner(_owner)).EnsureInboxGcAsync(InboxGcId.For(agent), agent, Guid.NewGuid(), "Inbox", Ct));
    }

    [Fact]
    public async Task Archive_reports_archived_then_already_archived_and_failure_for_an_unknown_gc()
    {
        var provisioner = new FakeProvisioner();
        var port = new GcInboxGcPort(provisioner, new FixedOwner(_owner));
        var agent = Guid.NewGuid();
        var gcId = InboxGcId.For(agent);
        await port.EnsureInboxGcAsync(gcId, agent, Guid.NewGuid(), "Inbox", Ct);

        Assert.Equal(InboxGcArchiveResult.Archived, await port.ArchiveInboxGcAsync(gcId, Ct));
        Assert.Equal(InboxGcArchiveResult.AlreadyArchived, await port.ArchiveInboxGcAsync(gcId, Ct));
        Assert.Equal(InboxGcArchiveResult.Failed, await port.ArchiveInboxGcAsync(Guid.NewGuid(), Ct));
    }
}
