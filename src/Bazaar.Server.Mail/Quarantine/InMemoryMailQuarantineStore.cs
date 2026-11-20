namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// An in-memory <see cref="IMailQuarantineStore"/> for tests and for hosts without Postgres. It applies the same state
/// machine as any other store. Thread-safe.
/// </summary>
public sealed class InMemoryMailQuarantineStore : IMailQuarantineStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, QuarantineItem> _items = [];

    public Task<QuarantineTransitionResult> AdmitAsync(QuarantineItem item, QuarantineActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!QuarantineStateMachine.CanAdmit(actor))
        {
            return Task.FromResult(new QuarantineTransitionResult(
                actor.Kind == QuarantineActorKind.Agent ? QuarantineTransitionOutcome.AgentCannotChange : QuarantineTransitionOutcome.NotOwner, null));
        }

        var held = item with { State = QuarantineState.Held };
        lock (_gate)
        {
            // Admitting an id again never resets an item that has moved on.
            if (_items.TryGetValue(held.Id, out var existing))
            {
                return Task.FromResult(new QuarantineTransitionResult(QuarantineTransitionOutcome.IllegalTransition, existing));
            }

            _items[held.Id] = held;
        }

        return Task.FromResult(new QuarantineTransitionResult(QuarantineTransitionOutcome.Allowed, held));
    }

    public Task<QuarantineItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<QuarantineItem>> ListAsync(QuarantineState? state, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<QuarantineItem> result = _items.Values
                .Where(i => state is null || i.State == state)
                .OrderBy(i => i.ReceivedAt)
                .ThenBy(i => i.Id)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<QuarantineTransitionResult> TransitionAsync(
        Guid id, QuarantineState target, QuarantineActor actor, QuarantineTransitionContext context, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item))
            {
                return Task.FromResult(new QuarantineTransitionResult(QuarantineTransitionOutcome.NotFound, null));
            }

            var result = QuarantineStateMachine.Apply(item, target, actor, context);
            if (result.IsAllowed)
            {
                _items[id] = result.Item!;
            }

            return Task.FromResult(result);
        }
    }

    public Task<int> CountHeldAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Values.Count(i => i.State == QuarantineState.Held));
        }
    }
}
