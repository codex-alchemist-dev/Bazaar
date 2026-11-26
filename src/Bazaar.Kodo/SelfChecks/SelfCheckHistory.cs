namespace Bazaar.Kodo.SelfChecks;

/// <summary>
/// A bounded history of results (doc 46 section 17.6): the newest <see cref="CapacityPerCheck"/> results for each definition, and at most
/// <see cref="MaxDefinitions"/> definitions. Thread safe. In memory only; the journal copy is the host's job.
/// </summary>
public sealed class SelfCheckHistory
{
    /// <summary>The most definitions tracked. A result for a further definition is dropped.</summary>
    public const int MaxDefinitions = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<SelfCheckResult>> _byDefinition = new(StringComparer.Ordinal);

    /// <summary>Creates a history. Throws <see cref="ArgumentOutOfRangeException"/> unless the capacity is from 1 to 1000.</summary>
    public SelfCheckHistory(int capacityPerCheck = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPerCheck, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacityPerCheck, 1000);
        CapacityPerCheck = capacityPerCheck;
    }

    /// <summary>The most results kept for one definition.</summary>
    public int CapacityPerCheck { get; }

    /// <summary>Adds a result, dropping the oldest one for that definition when full. Returns false when the definition limit dropped it.</summary>
    public bool Add(SelfCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!_byDefinition.TryGetValue(result.DefinitionId, out List<SelfCheckResult>? list))
            {
                if (_byDefinition.Count >= MaxDefinitions)
                {
                    return false;
                }

                list = [];
                _byDefinition[result.DefinitionId] = list;
            }

            list.Add(result);
            if (list.Count > CapacityPerCheck)
            {
                list.RemoveAt(0);
            }

            return true;
        }
    }

    /// <summary>The results for a definition, oldest first. Empty when there are none.</summary>
    public IReadOnlyList<SelfCheckResult> Recent(string definitionId)
    {
        lock (_gate)
        {
            return _byDefinition.TryGetValue(definitionId, out List<SelfCheckResult>? list) ? [.. list] : [];
        }
    }

    /// <summary>The newest result for a definition, or null.</summary>
    public SelfCheckResult? Latest(string definitionId)
    {
        lock (_gate)
        {
            return _byDefinition.TryGetValue(definitionId, out List<SelfCheckResult>? list) && list.Count > 0 ? list[^1] : null;
        }
    }
}
