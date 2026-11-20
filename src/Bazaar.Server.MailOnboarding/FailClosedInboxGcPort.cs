namespace Bazaar.Server.MailOnboarding;

/// <summary>
/// Default <see cref="IInboxGcPort"/> until GC publishes <c>IGcProvisioner</c> and <c>IGcDirectory</c> in <c>Bazaar.GC.Abstractions</c>: it creates
/// nothing, so onboarding reports <see cref="MailOnboardingStatus.InboxGcUnavailable"/> and leaves <c>person_id</c> unset.
/// </summary>
public sealed class FailClosedInboxGcPort : IInboxGcPort, IFailClosedMailOnboardingPort
{
    public Task<bool> EnsureInboxGcAsync(Guid gcId, Guid agentId, Guid mailboxId, string title, CancellationToken ct) => Task.FromResult(false);
}
