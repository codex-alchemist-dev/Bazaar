using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.MailOnboarding;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// <see cref="IInboxGcPort"/> over GC's <see cref="IGcProvisioner"/> (docs/design/22-email-subsystem.md, "Mail onboarding (doc 06)"). The inbox GC is created
/// with the agent as its owner and sole starting member, tagged with <see cref="InboxGcId.MailboxMetadataKey"/>, and the owner is then added: agent work is
/// owner-visible, so every inbox GC has the owner. Both steps are idempotent. No owner means nothing is created (fail closed), so a GC never exists without
/// the owner in it. Failures return false or <see cref="InboxGcArchiveResult.Failed"/> and carry no detail out of this class.
/// </summary>
public sealed class GcInboxGcPort(IGcProvisioner provisioner, IMailOwnerDirectory owners) : IInboxGcPort
{
    // The provisioner stores only the id; the name is for display on a card and is not a handle.
    private const string _agentDisplayName = "Agent";

    public async Task<bool> EnsureInboxGcAsync(Guid gcId, Guid agentId, Guid mailboxId, string title, CancellationToken ct)
    {
        try
        {
            var owner = await owners.GetOwnerAsync(ct).ConfigureAwait(false);
            if (owner is null)
            {
                return false;
            }

            var agent = new GcPrincipal(agentId, PrincipalKind.Agent, _agentDisplayName);
            var metadata = new Dictionary<string, string> { [InboxGcId.MailboxMetadataKey] = mailboxId.ToString("D") };
            await provisioner.CreateAsync(gcId, InboxGcId.TypeKey, title, agent, metadata, ct).ConfigureAwait(false);
            await provisioner.AddMemberAsync(gcId, new GcPrincipal(owner.Id, PrincipalKind.Owner, owner.DisplayName), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<InboxGcArchiveResult> ArchiveInboxGcAsync(Guid gcId, CancellationToken ct)
    {
        try
        {
            return await provisioner.ArchiveAsync(gcId, ct).ConfigureAwait(false) ? InboxGcArchiveResult.Archived : InboxGcArchiveResult.AlreadyArchived;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return InboxGcArchiveResult.Failed;
        }
    }
}
