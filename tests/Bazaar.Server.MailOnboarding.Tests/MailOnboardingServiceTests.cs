using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailOnboarding.Tests;

internal sealed class FakeMailboxStore : IMailboxPersonStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Guid?> _boxes = [];

    public int SetCalls { get; private set; }

    public void Add(Guid mailboxId, Guid? person = null)
    {
        lock (_gate)
        {
            _boxes[mailboxId] = person;
        }
    }

    public Guid? PersonOf(Guid mailboxId)
    {
        lock (_gate)
        {
            return _boxes[mailboxId];
        }
    }

    public Task<MailboxPersonSetResult> SetPersonAsync(Guid mailboxId, Guid agentId, CancellationToken ct)
    {
        lock (_gate)
        {
            SetCalls++;
            if (!_boxes.TryGetValue(mailboxId, out var current))
            {
                return Task.FromResult(MailboxPersonSetResult.NotFound);
            }

            if (current is null)
            {
                _boxes[mailboxId] = agentId;
                return Task.FromResult(MailboxPersonSetResult.Set);
            }

            return Task.FromResult(current == agentId ? MailboxPersonSetResult.AlreadySet : MailboxPersonSetResult.OwnedByOther);
        }
    }

    private readonly HashSet<Guid> _closed = [];

    public bool IsClosed(Guid mailboxId)
    {
        lock (_gate)
        {
            return _closed.Contains(mailboxId);
        }
    }

    public Task<MailboxCloseResult> CloseAsync(Guid mailboxId, Guid agentId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_boxes.TryGetValue(mailboxId, out var person))
            {
                return Task.FromResult(MailboxCloseResult.NotFound);
            }

            if (person != agentId)
            {
                return Task.FromResult(MailboxCloseResult.NotOwner);
            }

            return Task.FromResult(_closed.Add(mailboxId) ? MailboxCloseResult.Closed : MailboxCloseResult.AlreadyClosed);
        }
    }

    public Task<MailboxOwner> GetOwnerAsync(Guid mailboxId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_boxes.TryGetValue(mailboxId, out var p) ? new MailboxOwner(true, p) : new MailboxOwner(false, null));
        }
    }
}

internal sealed class FakeInboxGcPort(bool available = true, bool throws = false) : IInboxGcPort
{
    private readonly object _gate = new();

    /// <summary>Distinct GC ids, as an idempotent adapter would hold them.</summary>
    public HashSet<Guid> Gcs { get; } = [];

    public int Calls { get; private set; }

    public async Task<bool> EnsureInboxGcAsync(Guid gcId, Guid agentId, Guid mailboxId, string title, CancellationToken ct)
    {
        await Task.Yield();
        lock (_gate)
        {
            Calls++;
            if (throws)
            {
                throw new InvalidOperationException("boom");
            }

            if (!available)
            {
                return false;
            }

            Gcs.Add(gcId);
            return true;
        }
    }

    public HashSet<Guid> Archived { get; } = [];

    public Task<InboxGcArchiveResult> ArchiveInboxGcAsync(Guid gcId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (throws)
            {
                throw new InvalidOperationException("boom");
            }

            if (!available || !Gcs.Contains(gcId))
            {
                return Task.FromResult(InboxGcArchiveResult.Failed);
            }

            return Task.FromResult(Archived.Add(gcId) ? InboxGcArchiveResult.Archived : InboxGcArchiveResult.AlreadyArchived);
        }
    }
}

internal sealed class RecordingAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        lock (Entries)
        {
            Entries.Add(e);
        }

        return Task.CompletedTask;
    }
}

public sealed class MailOnboardingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Happy_path_ensures_the_gc_then_binds_the_mailbox()
    {
        var store = new FakeMailboxStore();
        var gc = new FakeInboxGcPort();
        var audit = new RecordingAudit();
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box);

        var result = await new MailOnboardingService(store, gc, audit).OnboardAsync(agent, box, Ct);

        Assert.Equal(MailOnboardingStatus.Onboarded, result.Status);
        Assert.Equal(InboxGcId.For(agent), result.InboxGcId);
        Assert.Equal(agent, store.PersonOf(box));
        Assert.Contains(InboxGcId.For(agent), gc.Gcs);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("mail.onboarding.onboarded", entry.Kind);
        Assert.DoesNotContain("@", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_call_is_idempotent()
    {
        var store = new FakeMailboxStore();
        var gc = new FakeInboxGcPort();
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box);
        var service = new MailOnboardingService(store, gc);

        await service.OnboardAsync(agent, box, Ct);
        var again = await service.OnboardAsync(agent, box, Ct);

        Assert.Equal(MailOnboardingStatus.AlreadyOnboarded, again.Status);
        Assert.Equal(agent, store.PersonOf(box));
        Assert.Single(gc.Gcs);
    }

    [Fact]
    public async Task Mailbox_owned_by_another_agent_is_refused_and_no_gc_is_made()
    {
        var store = new FakeMailboxStore();
        var gc = new FakeInboxGcPort();
        var owner = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box, owner);

        var result = await new MailOnboardingService(store, gc).OnboardAsync(Guid.NewGuid(), box, Ct);

        Assert.Equal(MailOnboardingStatus.MailboxOwnedByOtherAgent, result.Status);
        Assert.Equal(owner, store.PersonOf(box));
        Assert.Equal(0, gc.Calls);
    }

    [Fact]
    public async Task Missing_mailbox_is_reported_and_no_gc_is_made()
    {
        var gc = new FakeInboxGcPort();

        var result = await new MailOnboardingService(new FakeMailboxStore(), gc).OnboardAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);

        Assert.Equal(MailOnboardingStatus.MailboxNotFound, result.Status);
        Assert.Equal(0, gc.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Gc_port_unavailable_or_failing_leaves_person_unset(bool available, bool throws)
    {
        var store = new FakeMailboxStore();
        var box = Guid.NewGuid();
        store.Add(box);

        var result = await new MailOnboardingService(store, new FakeInboxGcPort(available, throws)).OnboardAsync(Guid.NewGuid(), box, Ct);

        Assert.Equal(MailOnboardingStatus.InboxGcUnavailable, result.Status);
        Assert.Null(store.PersonOf(box));
        Assert.Equal(0, store.SetCalls);
    }

    [Fact]
    public async Task Default_fail_closed_port_leaves_person_unset_and_a_later_retry_succeeds()
    {
        var store = new FakeMailboxStore();
        var box = Guid.NewGuid();
        var agent = Guid.NewGuid();
        store.Add(box);

        var closed = await new MailOnboardingService(store, new FailClosedInboxGcPort()).OnboardAsync(agent, box, Ct);
        var retried = await new MailOnboardingService(store, new FakeInboxGcPort()).OnboardAsync(agent, box, Ct);

        Assert.Equal(MailOnboardingStatus.InboxGcUnavailable, closed.Status);
        Assert.Equal(MailOnboardingStatus.Onboarded, retried.Status);
        Assert.Equal(agent, store.PersonOf(box));
    }

    [Fact]
    public async Task Concurrent_double_call_converges_on_one_gc_and_one_owner()
    {
        var store = new FakeMailboxStore();
        var gc = new FakeInboxGcPort();
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box);
        var service = new MailOnboardingService(store, gc);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.OnboardAsync(agent, box, Ct), Ct)));

        Assert.Single(gc.Gcs);
        Assert.Equal(agent, store.PersonOf(box));
        Assert.Equal(1, results.Count(r => r.Status == MailOnboardingStatus.Onboarded));
        Assert.All(results, r => Assert.True(r.Status is MailOnboardingStatus.Onboarded or MailOnboardingStatus.AlreadyOnboarded));
    }

    [Fact]
    public async Task Empty_agent_id_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new MailOnboardingService(new FakeMailboxStore(), new FakeInboxGcPort()).OnboardAsync(Guid.Empty, Guid.NewGuid(), Ct));
    }

    [Fact]
    public void Inbox_gc_id_is_stable_and_differs_per_agent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(InboxGcId.For(a), InboxGcId.For(a));
        Assert.NotEqual(InboxGcId.For(a), InboxGcId.For(b));
        Assert.NotEqual(a, InboxGcId.For(a));

        // Pinned: a changed derivation would orphan every existing inbox GC.
        var fixedAgent = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(InboxGcId.For(fixedAgent), InboxGcId.For(fixedAgent));
        var bytes = InboxGcId.For(fixedAgent).ToString("D");
        Assert.Equal('5', bytes[14]);
        Assert.Contains(bytes[19], "89ab");
    }

    [Fact]
    public async Task Di_registration_is_try_add_and_defaults_fail_closed()
    {
        var custom = new FakeInboxGcPort();
        var services = new ServiceCollection();
        services.AddSingleton<IInboxGcPort>(custom);
        services.AddSingleton<IMailboxPersonStore>(new FakeMailboxStore());

        services.AddBazaarMailOnboarding();
        services.AddBazaarMailOnboarding();

        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<IInboxGcPort>());
        Assert.IsType<MailOnboardingService>(provider.GetRequiredService<IMailOnboarding>());
        Assert.Single(services, d => d.ServiceType == typeof(IMailOnboarding));

        var bare = new ServiceCollection().AddBazaarMailOnboarding();
        await using var bareProvider = bare.BuildServiceProvider();
        Assert.IsType<FailClosedInboxGcPort>(bareProvider.GetRequiredService<IInboxGcPort>());
        var owner = bareProvider.GetRequiredService<IMailOwnerDirectory>();
        Assert.IsType<FailClosedMailOwnerDirectory>(owner);
        Assert.Null(await owner.GetOwnerAsync(Ct));
    }

    private static async Task<(FakeMailboxStore Store, FakeInboxGcPort Gc, MailOnboardingService Service, Guid Agent, Guid Box)> OnboardedAsync(RecordingAudit? audit = null)
    {
        var store = new FakeMailboxStore();
        var gc = new FakeInboxGcPort();
        var service = new MailOnboardingService(store, gc, audit);
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box);
        await service.OnboardAsync(agent, box, Ct);
        return (store, gc, service, agent, box);
    }

    [Fact]
    public async Task Offboard_closes_the_mailbox_archives_the_gc_and_keeps_person_id()
    {
        var audit = new RecordingAudit();
        var (store, gc, service, agent, box) = await OnboardedAsync(audit);

        var result = await service.OffboardAsync(agent, box, Ct);

        Assert.Equal(MailOffboardingStatus.Offboarded, result.Status);
        Assert.Equal(InboxGcId.For(agent), result.InboxGcId);
        Assert.True(store.IsClosed(box));
        Assert.Equal(agent, store.PersonOf(box));
        Assert.Contains(InboxGcId.For(agent), gc.Archived);
        Assert.Contains(audit.Entries, e => e.Kind == "mail.offboarding.offboarded" && !e.Payload.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offboard_twice_reports_already_offboarded()
    {
        var (store, _, service, agent, box) = await OnboardedAsync();

        await service.OffboardAsync(agent, box, Ct);
        var again = await service.OffboardAsync(agent, box, Ct);

        Assert.Equal(MailOffboardingStatus.AlreadyOffboarded, again.Status);
        Assert.Equal(agent, store.PersonOf(box));
    }

    [Fact]
    public async Task Offboard_by_a_different_agent_or_of_a_missing_mailbox_changes_nothing()
    {
        var (store, gc, service, agent, box) = await OnboardedAsync();

        var other = await service.OffboardAsync(Guid.NewGuid(), box, Ct);
        var missing = await service.OffboardAsync(agent, Guid.NewGuid(), Ct);

        Assert.Equal(MailOffboardingStatus.NotOwner, other.Status);
        Assert.Equal(MailOffboardingStatus.MailboxNotFound, missing.Status);
        Assert.False(store.IsClosed(box));
        Assert.Empty(gc.Archived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offboard_with_a_failing_gc_port_closes_the_mailbox_and_a_retry_finishes(bool throws)
    {
        var store = new FakeMailboxStore();
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box, agent);

        var failed = await new MailOnboardingService(store, new FakeInboxGcPort(available: false, throws: throws)).OffboardAsync(agent, box, Ct);
        var gc = new FakeInboxGcPort();
        gc.Gcs.Add(InboxGcId.For(agent));
        var retried = await new MailOnboardingService(store, gc).OffboardAsync(agent, box, Ct);

        Assert.Equal(MailOffboardingStatus.GcUnavailable, failed.Status);
        Assert.Equal(MailOffboardingStatus.Offboarded, retried.Status);
        Assert.True(store.IsClosed(box));
        Assert.Contains(InboxGcId.For(agent), gc.Archived);
    }

    [Fact]
    public async Task Offboard_with_the_default_fail_closed_port_reports_gc_unavailable()
    {
        var store = new FakeMailboxStore();
        var agent = Guid.NewGuid();
        var box = Guid.NewGuid();
        store.Add(box, agent);

        var result = await new MailOnboardingService(store, new FailClosedInboxGcPort()).OffboardAsync(agent, box, Ct);

        Assert.Equal(MailOffboardingStatus.GcUnavailable, result.Status);
    }

    [Fact]
    public async Task Offboard_rejects_an_empty_agent_id()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new MailOnboardingService(new FakeMailboxStore(), new FakeInboxGcPort()).OffboardAsync(Guid.Empty, Guid.NewGuid(), Ct));
    }
}
