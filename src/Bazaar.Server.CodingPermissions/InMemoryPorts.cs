using System.Collections.Concurrent;
using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingPermissions;

/// <summary>Scripted generic policy for tests: returns a fixed decision, or one chosen per class.</summary>
public sealed class FakeGenericToolPolicy : IGenericToolPolicy
{
    private readonly Func<ToolCallContext, GenericPolicyDecision> _decide;

    /// <summary>Creates a policy from a function.</summary>
    public FakeGenericToolPolicy(Func<ToolCallContext, GenericPolicyDecision> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        _decide = decide;
    }

    /// <summary>A policy that allows everything.</summary>
    public static FakeGenericToolPolicy AllowAll() => new(_ => new GenericPolicyDecision(PermissionOutcome.Allow, "allowed"));

    /// <inheritdoc />
    public GenericPolicyDecision Evaluate(ToolCallContext context) => _decide(context);
}

/// <summary>In-memory append-only trace. <see cref="FailWrites"/> simulates a broken store.</summary>
public sealed class InMemoryToolCallTrace : IToolCallTrace
{
    private readonly ConcurrentQueue<ToolCallRecord> _rows = new();

    /// <summary>When true, <see cref="AppendAsync"/> throws.</summary>
    public bool FailWrites { get; set; }

    /// <summary>The rows written so far, in order.</summary>
    public IReadOnlyList<ToolCallRecord> Rows => [.. _rows];

    /// <inheritdoc />
    public ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("Trace store unavailable.");
        }

        _rows.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory freeze port that remembers who was frozen and why (the owner notification is the host's job).</summary>
public sealed class InMemoryAgentFreeze : IAgentFreeze
{
    private readonly ConcurrentDictionary<string, string> _frozen = new(StringComparer.Ordinal);

    /// <summary>Agents frozen so far, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Frozen => _frozen;

    /// <inheritdoc />
    public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_frozen.ContainsKey(agentId));

    /// <inheritdoc />
    public ValueTask FreezeAsync(string agentId, string reason, CancellationToken cancellationToken)
    {
        _frozen[agentId] = reason;
        return ValueTask.CompletedTask;
    }
}
