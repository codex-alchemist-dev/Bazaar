using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Agents.Turns.Tools;

/// <summary>Policy engine fake: returns a fixed decision and records every context it was asked about.</summary>
internal sealed class FakePolicyEngine(PolicyDecision decision) : IToolPolicyEngine
{
    public List<DispatchContext> Seen { get; } = [];

    public PolicyDecision Evaluate(DispatchContext context)
    {
        Seen.Add(context);
        return decision;
    }
}

/// <summary>Taint store fake: returns one taint for every session.</summary>
internal sealed class FakeSessionTaintStore(SessionTaint taint) : ISessionTaintStore
{
    public Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(taint);
}

/// <summary>Registry fake built from a name-to-tool map. Unknown names return false.</summary>
internal sealed class FakeToolRegistry(params (string Name, ITool Tool)[] tools) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name, t => t.Tool, StringComparer.Ordinal);

    public bool TryGet(string name, [NotNullWhen(true)] out ITool? tool) => _tools.TryGetValue(name, out tool);
}

/// <summary>Tool fake that runs a delegate and counts its runs.</summary>
internal sealed class FakeTool(Func<JsonElement, Task<string>> run) : ITool
{
    public int RunCount { get; private set; }

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken cancellationToken)
    {
        RunCount++;
        return run(args);
    }
}

/// <summary>In-flight store fake. Keys are kept in a set; Complete removes one.</summary>
internal sealed class FakeInFlightCallStore : IInFlightCallStore
{
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    public Task<bool> HasUnfinishedAsync(string callKey, CancellationToken cancellationToken) =>
        Task.FromResult(_open.Contains(callKey));

    public Task BeginAsync(string callKey, CancellationToken cancellationToken)
    {
        _open.Add(callKey);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string callKey, CancellationToken cancellationToken)
    {
        _open.Remove(callKey);
        return Task.CompletedTask;
    }

    public int OpenCount => _open.Count;
}

/// <summary>Audit fake that keeps every appended entry.</summary>
internal sealed class FakeAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>A clock that always reads the same instant, so decisions and log times are deterministic.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
