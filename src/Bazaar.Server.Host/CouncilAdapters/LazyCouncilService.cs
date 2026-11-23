using Bazaar.Server.Council;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Builds the real <see cref="ICouncilService"/> on first use. The Council options come from the settings store, which needs
/// PostgreSQL; building the service when the host graph is created (a hosted service depends on it) would reach the database
/// before the data volume guard has had its say (doc 63 section 5). The first call pays the read instead.
/// </summary>
internal sealed class LazyCouncilService(Func<ICouncilService> create) : ICouncilService
{
    private readonly Lazy<ICouncilService> _inner = new(create, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct) => _inner.Value.OpenAsync(callerId, motion, ct);

    public Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct) => _inner.Value.SubmitBallotAsync(voteId, voterId, ballot, ct);

    public Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct) => _inner.Value.ResolveEscalationAsync(voteId, optionChoice, reasoning, ct);

    public Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct) => _inner.Value.ProcessDeadlineAsync(voteId, ct);

    public Task RedriveDecisionAsync(Guid voteId, CancellationToken ct) => _inner.Value.RedriveDecisionAsync(voteId, ct);

    public Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct) => _inner.Value.WithdrawAsync(voteId, actorId, ct);
}
