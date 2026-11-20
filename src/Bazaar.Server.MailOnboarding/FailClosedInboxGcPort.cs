namespace Bazaar.Server.MailOnboarding;

/// <summary>
/// Default <see cref="IInboxGcPort"/> when the host registers no GC adapter: it creates and archives
/// nothing, so onboarding reports <see cref="MailOnboardingStatus.InboxGcUnavailable"/> and leaves <c>person_id</c> unset.
/// </summary>
public sealed class FailClosedInboxGcPort : IInboxGcPort, IFailClosedMailOnboardingPort
{
    public Task<bool> EnsureInboxGcAsync(Guid gcId, Guid agentId, Guid mailboxId, string title, CancellationToken ct) => Task.FromResult(false);

    public Task<InboxGcArchiveResult> ArchiveInboxGcAsync(Guid gcId, CancellationToken ct) => Task.FromResult(InboxGcArchiveResult.Failed);
}

/// <summary>Default <see cref="IMailOwnerDirectory"/>: knows no owner, so the adapter that adds the owner to every inbox GC creates nothing.</summary>
public sealed class FailClosedMailOwnerDirectory : IMailOwnerDirectory, IFailClosedMailOnboardingPort
{
    public Task<MailOwner?> GetOwnerAsync(CancellationToken ct) => Task.FromResult<MailOwner?>(null);
}
