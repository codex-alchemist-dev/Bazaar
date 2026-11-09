using System.Collections.Concurrent;

namespace Bazaar.Server.Fun.Mirror;

/// <summary>
/// In-memory implementation of <see cref="IMirrorStore"/> for fast headless testing and local execution.
/// </summary>
public sealed class InMemoryMirrorStore : IMirrorStore
{
    private readonly ConcurrentBag<MirrorSignal> _signals = [];
    private readonly ConcurrentDictionary<int, MirrorProfile> _profiles = new();
    private readonly ConcurrentDictionary<string, MirrorRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public Task RecordSignalAsync(MirrorSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _signals.Add(signal);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MirrorSignal>> ListSignalsAsync(string? kind = null, CancellationToken ct = default)
    {
        var query = _signals.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(s => s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.OrderBy(s => s.CreatedAt).ToList();
        return Task.FromResult<IReadOnlyList<MirrorSignal>>(result);
    }

    public Task SaveProfileAsync(MirrorProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Version] = profile;

        foreach (var rule in profile.Rules)
        {
            _rules[rule.RuleId] = rule;
        }

        return Task.CompletedTask;
    }

    public Task<MirrorProfile?> GetProfileAsync(int? version = null, CancellationToken ct = default)
    {
        if (version.HasValue)
        {
            _profiles.TryGetValue(version.Value, out var found);
            return Task.FromResult(found);
        }

        var latest = _profiles.Values.OrderByDescending(p => p.Version).FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<int> GetNextProfileVersionAsync(CancellationToken ct = default)
    {
        var max = _profiles.Keys.DefaultIfEmpty(0).Max();
        return Task.FromResult(max + 1);
    }

    public Task WipeProfileAndEmbeddingsAsync(CancellationToken ct = default)
    {
        _signals.Clear();
        _profiles.Clear();
        _rules.Clear();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        return Task.FromResult(_rules.TryRemove(ruleId, out _));
    }
}
